"""A JSON-RPC relay in front of the recorded runtime, with a pause that makes a kill deterministic.

The Durable variant names its runtime with `DURABLE_RUNTIME_PATH`, so pointing that at this script is
how a fault script gets an injection point without changing the variant: the relay forwards every frame
unchanged, and when `FAULT_PAUSE_ON` names a method it holds that frame and writes `<marker>/inflight`.

Holding a frame means the application is blocked *inside* that request. A caller that waits for the
marker therefore knows, rather than hopes, that the process is in the middle of a model call — which is
the instant a `kill -9` is measuring something.

The relay is not involved in the comparison. With no pause armed it is a transparent pipe, and the
variant's own output is unchanged; the frame contents are never inspected beyond the method name.
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

PAUSE_ON = os.environ.get("FAULT_PAUSE_ON", "").strip()
PAUSE_OCCURRENCE = int(os.environ.get("FAULT_PAUSE_COUNT") or "1")
MARKER_DIR = os.environ.get("FAULT_MARKER", "").strip()
_seen = {"n": 0}

log_handle = open(LOG, "a", encoding="utf-8") if LOG else None
log_lock = threading.Lock()


def note(text):
    if log_handle is None:
        return
    with log_lock:
        log_handle.write(text + "\n")
        log_handle.flush()


child = subprocess.Popen(
    [RUNTIME, "--fixture", FIXTURE],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    encoding="utf-8",
    bufsize=1,
)


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


def pump():
    while True:
        body = read_frame(child.stdout)
        if body is None:
            break
        write_frame(sys.stdout, body)


threading.Thread(target=pump, daemon=True).start()

while True:
    body = read_frame(sys.stdin)
    if body is None:
        break

    method = None
    try:
        message = json.loads(body)
        if isinstance(message, dict):
            method = message.get("method")
    except ValueError:
        pass

    if method:
        note("frame client->runtime %s" % method)

    if PAUSE_ON and method == PAUSE_ON and MARKER_DIR and PAUSE_OCCURRENCE > 0:
        _seen["n"] += 1
        if _seen["n"] >= PAUSE_OCCURRENCE:
            release = os.path.join(MARKER_DIR, "release")
            if not os.path.exists(release):
                note("holding %s (occurrence %d) until %s appears" % (method, _seen["n"], release))
                with open(os.path.join(MARKER_DIR, "inflight"), "w", encoding="utf-8") as handle:
                    handle.write("%s %d\n" % (method, _seen["n"]))
                while not os.path.exists(release):
                    time.sleep(0.05)
                note("released %s (occurrence %d)" % (method, _seen["n"]))

    write_frame(child.stdin, body)

child.stdin.close()
child.wait()
