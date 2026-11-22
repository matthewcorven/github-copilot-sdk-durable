"""A JSON-RPC gate in front of the recorded runtime.

Both Band A variants are driven through this gate:

- The baseline has no seam that names a runtime. It builds its Copilot client with the SDK's default
  connection, which resolves `copilot` from `COPILOT_CLI_PATH`; that variable is the only way to drive
  it without changing it, and changing it would be improving the baseline.
- The Durable variant can name a runtime (`DURABLE_RUNTIME_PATH`). It names this gate too, so both
  variants are faulted at the same instant by the same mechanism and the comparison is of the
  applications rather than of two different harnesses.

The gate does three things:

1. It relays every frame to `tools/ReplayRuntime`, unchanged.
2. It answers the one request the baseline's SDK client makes that the recorded runtime does not
   implement (`session.options.update`), with the empty result the SDK expects.
3. It can *pause* a named method. `FAULTS_PAUSE_ON=session.send` makes the gate write
   `<FAULTS_MARKER>/inflight` and hold the frame until `<FAULTS_MARKER>/release` exists. That is the
   deterministic injection point: a fault script waits for the marker and knows the application is
   blocked inside that request, rather than sleeping and hoping it caught the right moment.

The pause is transport-level and affects only where a frame is held. It changes no content, and a run
without `FAULTS_PAUSE_ON` behaves exactly like a plain relay.
"""

import json
import os
import subprocess
import sys
import threading
import time

RUNTIME = sys.argv[1]
FIXTURE = sys.argv[2]
LOG = sys.argv[3] if len(sys.argv) > 3 else None

# The one method the recorded runtime does not implement, with the result the SDK expects. A request
# that is not listed here is forwarded, so an unrecorded method still fails loudly in the runtime
# instead of being swallowed by the gate.
LOCAL_ANSWERS = {"session.options.update": {}}

PAUSE_ON = os.environ.get("FAULTS_PAUSE_ON", "").strip()
MARKER_DIR = os.environ.get("FAULTS_MARKER", "").strip()
# Which occurrence of the method to hold, 1-based. The first `session.send` is the queued pass, which
# has committed nothing yet; holding the second gives the run a committed phase to resume from.
PAUSE_OCCURRENCE = int(os.environ.get("FAULTS_PAUSE_COUNT") or "1")
_seen = {"n": 0}

log_handle = open(LOG, "a", encoding="utf-8") if LOG else None
log_lock = threading.Lock()


def note(text):
    if log_handle is None:
        return
    with log_lock:
        log_handle.write(text + "\n")
        log_handle.flush()


CHILD = subprocess.Popen(
    [RUNTIME, "--fixture", FIXTURE],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    encoding="utf-8",
    bufsize=1,
)

# The gate and the runtime are children of the application, so `kill -9` on the application reparents
# them instead of ending them: they would outlive the run and hold the fixture open. Recording both PIDs
# where the harness can read them is what lets the reap be by PID, without matching on a path (which would
# also match a reviewer tailing a log).
if MARKER_DIR:
    try:
        with open(os.path.join(MARKER_DIR, "pids"), "a", encoding="utf-8") as handle:
            handle.write("%d\n%d\n" % (os.getpid(), CHILD.pid))
    except OSError:
        pass


def reap_children():
    try:
        CHILD.kill()
    except Exception:
        pass


def read_frame(stream):
    length = None
    while True:
        line = stream.readline()
        if line == "":
            return None
        line = line.rstrip("\r\n")
        if line == "":
            break
        if line.lower().startswith("content-length:"):
            length = int(line.split(":", 1)[1].strip())
    if length is None:
        return None
    return stream.read(length)


def write_frame(stream, body):
    stream.write("Content-Length: %d\r\n\r\n%s" % (len(body.encode("utf-8")), body))
    stream.flush()


def pump_runtime():
    while True:
        body = read_frame(CHILD.stdout)
        if body is None:
            break
        write_frame(sys.stdout, body)


threading.Thread(target=pump_runtime, daemon=True).start()

while True:
    body = read_frame(sys.stdin)
    if body is None:
        break
    message = None
    try:
        message = json.loads(body)
    except ValueError:
        pass
    method = message.get("method") if isinstance(message, dict) else None
    if method:
        note("[gate %d pause=%r count=%d] client->runtime %s" % (os.getpid(), PAUSE_ON, PAUSE_OCCURRENCE, method))

    if method in LOCAL_ANSWERS and isinstance(message, dict) and "id" in message:
        note("gate: answered %s locally (the recorded runtime does not implement it)" % method)
        write_frame(
            sys.stdout,
            json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": LOCAL_ANSWERS[method]}),
        )
        continue

    if PAUSE_ON and method == PAUSE_ON and MARKER_DIR:
        _seen["n"] += 1
        release = os.path.join(MARKER_DIR, "release")
        # Occurrence 0 means "never hold", and a pause always requires an occurrence to name. A bare
        # `=2` with no arm is read as 2; anything else that is not a positive integer never holds.
        if PAUSE_OCCURRENCE > 0 and _seen["n"] >= PAUSE_OCCURRENCE and not os.path.exists(release):
            marker = os.path.join(MARKER_DIR, "inflight")
            note("gate: holding %s (occurrence %d) until %s appears" % (method, _seen["n"], release))
            with open(marker, "w", encoding="utf-8") as handle:
                handle.write("%s %d\n" % (method, _seen["n"]))
            while not os.path.exists(release):
                time.sleep(0.1)
            note("gate: released %s (occurrence %d)" % (method, _seen["n"]))

    write_frame(CHILD.stdin, body)

try:
    CHILD.stdin.close()
except Exception:
    pass
CHILD.wait()
