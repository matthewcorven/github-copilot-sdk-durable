"""A JSON-RPC gate in front of the Copilot CLI, which is the deterministic injection point for Band B.

Both Band B variants reach a model through the Copilot SDK, and the SDK resolves its runtime from
`COPILOT_CLI_PATH`. That variable is the only seam either variant has: the baseline has no command
line for a runtime, and the Durable variant's `DURABLE_RUNTIME_PATH` says the same thing by another
name. The harness points both at this gate, which `exec`s the runtime the SDK shipped
(`bin/<variant>/runtimes/*/native/copilot`) with the arguments the SDK passed. So the process tree is

    app  ── stdout/stdin frames ──▶  gate  ── stdout/stdin frames ──▶  copilot runtime

and a fault script can act on an exact frame instead of sleeping and hoping.

Four faults need four things from this gate:

* **pause-in**  — hold a client→runtime request. `restart-after-checkpoint` stops the process there,
  which is the moment between two iterations: the previous turn has returned, the next has not been
  sent.
* **pause-out** — hold a runtime→client notification. `kill-mid-turn` kills the process there, which is
  the moment a turn is in flight: the model has already asked for a tool.
* **tear**      — cut the stream to the child, so a `store-outage` can be observed as the application's
  own reaction rather than as a harness guess.
* **duplicate** — send one client→runtime frame twice. `duplicate-delivery` re-delivers a draw.

The gate never invents a frame, never edits one, and never reorders the two directions. Everything it
holds or repeats is named by an environment variable, and the log records every frame with a monotonic
timestamp, so a result can point at the exact moment the fault landed.

Frames are relayed as bytes. The prompt this band sends contains `━` and `…`, so a text-mode reader
that honoured `Content-Length` as a character count would truncate the frame and the runtime would
exit on a malformed message — a harness that breaks the run it means to observe.

    cli_gate.py <logfile> <marker-dir> -- <runtime> [args...]

Environment:

    FAULTS_HOLD   `<direction>:<selector>`, e.g. `out:tool.execution_start` or `in:session.send`.
                  Direction is `in` (client→runtime) or `out` (runtime→client). The selector is a
                  dotted path whose last part is the value compared, so `out:tool.execution_start`
                  matches the notification whose `data.type` or `type` is `tool.execution_start`.
    FAULTS_COUNT  1-based occurrence to hold. 0 means hold nothing (a plain relay).
    FAULTS_REPEAT 1 holds *every* matching occurrence from FAULTS_COUNT on, releasing each time the
                  caller touches `release` and removing both markers before the next hold. That is what
                  lets a caller wait for the frame it actually wants: a between-iterations seam is a
                  frame whose occurrence number is not stable across runs, but whose *state* is, so the
                  caller inspects and either kills here or releases and waits for the next one.
    FAULTS_DUPLICATE  `<direction>:<selector>`; the matching frame is written twice, the second copy
                  tagged in the log. Unset means no duplication.
    FAULTS_DUPLICATE_COUNT  1-based occurrence for the duplicate (default 1).
    FAULTS_TEAR_AFTER  `<direction>:<selector>`; after that frame is forwarded, the gate closes the
                  runtime's stdin and stops relaying. That is a runtime that went away under a live
                  application, which is what a store outage looks like from inside a conversation.
    FAULTS_TEAR_COUNT  1-based occurrence for the tear (default 1).
"""

import json
import os
import subprocess
import sys
import threading
import time

SEPARATOR = sys.argv.index("--")
LOG = sys.argv[1]
MARKER_DIR = sys.argv[2]
COMMAND = sys.argv[SEPARATOR + 1 :]

APP_PID = os.getppid()
# Wall-clock seconds, not a per-process offset: two gates in a concurrent run have to be comparable on
# one axis for "they overlapped" to be a measurement rather than an assumption.
STARTED = time.time()

log_handle = open(LOG, "a", encoding="utf-8")
log_lock = threading.Lock()


def note(text):
    with log_lock:
        log_handle.write("%.3f %s\n" % (time.time(), text))
        log_handle.flush()
    ORIGINAL_STDERR.write(text + "\n")


def hold_spec():
    raw = os.environ.get("FAULTS_HOLD", "").strip()
    if not raw or ":" not in raw:
        return None, None
    direction, selector = raw.split(":", 1)
    return direction, selector


def duplicate_spec():
    raw = os.environ.get("FAULTS_DUPLICATE", "").strip()
    if not raw or ":" not in raw:
        return None, None
    direction, selector = raw.split(":", 1)
    return direction, selector


def tear_spec():
    raw = os.environ.get("FAULTS_TEAR_AFTER", "").strip()
    if not raw or ":" not in raw:
        return None, None
    direction, selector = raw.split(":", 1)
    return direction, selector


def positive(name, default="1"):
    try:
        value = int(os.environ.get(name) or default)
    except ValueError:
        return 0
    return value


HOLD_DIRECTION, HOLD_SELECTOR = hold_spec()
HOLD_COUNT = positive("FAULTS_COUNT")
HOLD_REPEAT = os.environ.get("FAULTS_REPEAT", "") == "1"
DUP_DIRECTION, DUP_SELECTOR = duplicate_spec()
DUP_COUNT = positive("FAULTS_DUPLICATE_COUNT")
TEAR_DIRECTION, TEAR_SELECTOR = tear_spec()
TEAR_COUNT = positive("FAULTS_TEAR_COUNT")

# One counter per (direction, purpose), so holding a frame and duplicating a frame never consume each
# other's occurrence numbers. That matters because a script may arm both.
_seen = {"in": 0, "out": 0}


def as_text(body):
    return body.decode("utf-8", "replace")


def matches(body, selector):
    """True when a frame's own content names `selector`.

    The selector is matched against the frame's `method` and, for a `session.event` notification,
    against the event type it carries. Nothing else is inspected: a match must be a field the
    runtime itself wrote, not a substring that happened to appear in a prompt.
    """
    try:
        message = json.loads(as_text(body))
    except ValueError:
        return False
    if not isinstance(message, dict):
        return False
    if message.get("method") == selector:
        return True
    if message.get("method") != "session.event":
        return False
    params = message.get("params")
    if not isinstance(params, dict):
        return False
    data = params.get("data")
    for candidate in (params, data):
        if not isinstance(candidate, dict):
            continue
        for field in ("event", "type", "eventType"):
            value = candidate.get(field)
            # `params.event` is the whole event, whose `type` names it; a bare string is accepted too,
            # because the recorded runtime in Band A wrote one.
            if isinstance(value, dict):
                value = value.get("type")
            if value == selector:
                return True
    return False


# The original stderr is kept before the descriptor is replaced, so the runtime's own diagnostics still
# reach the caller while a second copy goes into the gate's log. A gate is not a diagnostic dead end.
ORIGINAL_STDERR = os.fdopen(os.dup(2), "w", buffering=1)
STDERR_LOG = open(LOG, "a", encoding="utf-8")

CHILD = subprocess.Popen(
    COMMAND,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=STDERR_LOG,
    bufsize=0,
)

if MARKER_DIR:
    try:
        with open(os.path.join(MARKER_DIR, "servers"), "a", encoding="utf-8") as handle:
            handle.write("%d %d %d %s\n" % (APP_PID, os.getpid(), CHILD.pid, COMMAND[-1]))
    except OSError:
        pass

note("[gate %d] app pid %d, gate pid %d, runtime pid %d, hold=%r/%s dup=%r/%s"
     % (os.getpid(), APP_PID, os.getpid(), CHILD.pid,
        HOLD_DIRECTION, HOLD_SELECTOR or "-", DUP_DIRECTION, DUP_SELECTOR or "-"))


def read_frame(stream):
    """One JSON-RPC frame, as bytes: `Content-Length` counts bytes, so the read must too."""
    length = None
    while True:
        line = stream.readline()
        if line == b"":
            return None
        line = line.rstrip(b"\r\n")
        if line == b"":
            break
        if line.lower().startswith(b"content-length:"):
            try:
                length = int(line.split(b":", 1)[1].strip())
            except ValueError:
                return None
    if length is None:
        return None
    chunks = []
    remaining = length
    while remaining > 0:
        chunk = stream.read(remaining)
        if not chunk:
            return None
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def write_frame(stream, body):
    if stream.closed:
        return False
    header = b"Content-Length: %d\r\n\r\n" % len(body)
    try:
        stream.write(header + body)
        stream.flush()
        return True
    except (BrokenPipeError, ValueError, OSError):
        return False


def marker(name, text):
    if not MARKER_DIR:
        return
    try:
        with open(os.path.join(MARKER_DIR, name), "a", encoding="utf-8") as handle:
            handle.write(text + "\n")
    except OSError:
        pass


TEARING = {"done": False}


def release_wait():
    if not MARKER_DIR:
        return
    path = os.path.join(MARKER_DIR, "release")
    while not os.path.exists(path):
        time.sleep(0.05)


def pump_runtime():
    """runtime → client. Holds, and tears, on the outbound side."""
    while True:
        body = read_frame(CHILD.stdout)
        if body is None:
            break
        note("[gate] OUT %s" % describe(body))
        _seen["out"] += 1
        label = describe(body)
        # `matches` is evaluated once per frame per arming, so the occurrence counters count frames,
        # not the number of clauses that looked at one.
        if HOLD_DIRECTION == "out" and HOLD_SELECTOR and HOLD_COUNT > 0 and matches(body, HOLD_SELECTOR):
            if reached("out", HOLD_SELECTOR, HOLD_COUNT, HOLD_REPEAT):
                marker("inflight", "out %s %d %d" % (HOLD_SELECTOR, HOLD_COUNT, APP_PID))
                note("[gate] holding OUT %s until release" % label)
                release_wait()
                note("[gate] released OUT %s" % label)
                clear_hold()
        if TEAR_DIRECTION == "out" and TEAR_SELECTOR and matches(body, TEAR_SELECTOR) and reached_tear("out", TEAR_SELECTOR):
            note("[gate] tearing after OUT %s: closing the runtime's input" % label)
            marker("torn", "out %s %d" % (TEAR_SELECTOR, TEAR_COUNT))
            try:
                CHILD.stdin.close()
            except Exception:
                pass
            TEARING["done"] = True
            break
        if not write_frame(sys.stdout.buffer, body):
            break
    note("[gate] runtime side closed")


_occurrence = {}
_tear_occurrence = {}
_duplicate_occurrence = {}


def reached(direction, selector, count, repeat=False):
    """The Nth match, and no earlier one, is the one held — or, with `repeat`, every match from the Nth."""
    key = (direction, selector)
    _occurrence[key] = _occurrence.get(key, 0) + 1
    return _occurrence[key] >= count if repeat else _occurrence[key] == count


def clear_hold():
    """Removes the markers a repeated hold leaves, so the next hold is a new observation and a caller
    waiting for `inflight` cannot be satisfied by the previous one."""
    if not MARKER_DIR:
        return
    for name in ("inflight", "release"):
        try:
            os.remove(os.path.join(MARKER_DIR, name))
        except OSError:
            pass


def reached_tear(direction, selector):
    key = (direction, selector)
    _tear_occurrence[key] = _tear_occurrence.get(key, 0) + 1
    return _tear_occurrence[key] == TEAR_COUNT


def reached_dup(direction, selector):
    key = (direction, selector)
    _duplicate_occurrence[key] = _duplicate_occurrence.get(key, 0) + 1
    return _duplicate_occurrence[key] == DUP_COUNT


DUPLICATE_ID_BASE = 1_000_000


def with_fresh_id(body):
    try:
        message = json.loads(as_text(body))
    except ValueError:
        return body
    if not isinstance(message, dict) or "id" not in message:
        return body
    with_fresh_id.next_id = getattr(with_fresh_id, "next_id", DUPLICATE_ID_BASE) + 1
    message["id"] = with_fresh_id.next_id
    return json.dumps(message).encode("utf-8")


def describe(body):
    try:
        message = json.loads(as_text(body))
    except ValueError:
        return "?"
    if not isinstance(message, dict):
        return "?"
    method = message.get("method") or "response"
    params = message.get("params")
    if not isinstance(params, dict):
        return method
    # The CLI names an event with `params.event` (and, on some frames, `params.type`); the Durable
    # layer's recorded runtime used `params.data.type`. All three are read, so a selector names the
    # event rather than the envelope.
    event = params.get("event") or params.get("type")
    data = params.get("data")
    if isinstance(event, dict):
        event = event.get("type")
    if not event and isinstance(data, dict):
        event = data.get("type") or data.get("event")
    return "%s:%s" % (method, event) if isinstance(event, str) and event else method


threading.Thread(target=pump_runtime, daemon=True).start()

while True:
    body = read_frame(sys.stdin.buffer)
    if body is None:
        break
    _seen["in"] += 1
    label = describe(body)
    note("[gate] IN %s" % label)

    if (
        HOLD_DIRECTION == "in"
        and HOLD_SELECTOR
        and HOLD_COUNT > 0
        and matches(body, HOLD_SELECTOR)
        and reached("in", HOLD_SELECTOR, HOLD_COUNT, HOLD_REPEAT)
    ):
        marker("inflight", "in %s %d %d" % (HOLD_SELECTOR, HOLD_COUNT, APP_PID))
        note("[gate] holding IN %s until release" % label)
        release_wait()
        note("[gate] released IN %s" % label)
        clear_hold()

    if DUP_DIRECTION == "in" and DUP_SELECTOR and matches(body, DUP_SELECTOR) and reached_dup("in", DUP_SELECTOR):
        # A re-delivery is the same message arriving a second time as a delivery of its own: the body is
        # unchanged and the request id gets the next value in a range the gate reserves, because a
        # duplicate id is a protocol violation on the wire and would measure the parser rather than the
        # application. Nothing the application can see differs except that the work was asked for twice.
        note("[gate] re-delivering IN %s (the same message, a second delivery)" % label)
        marker("duplicated", "in %s %d" % (DUP_SELECTOR, DUP_COUNT))
        write_frame(CHILD.stdin, with_fresh_id(body))

    if TEAR_DIRECTION == "in" and TEAR_SELECTOR and matches(body, TEAR_SELECTOR) and reached_tear("in", TEAR_SELECTOR):
        note("[gate] tearing after IN %s: closing the runtime's input" % label)
        marker("torn", "in %s %d" % (TEAR_SELECTOR, TEAR_COUNT))
        write_frame(CHILD.stdin, body)
        try:
            CHILD.stdin.close()
        except Exception:
            pass
        TEARING["done"] = True
        break

    if not write_frame(CHILD.stdin, body):
        break

note("[gate] client side closed")
try:
    if not CHILD.stdin.closed:
        CHILD.stdin.close()
except Exception:
    pass
if not TEARING["done"]:
    try:
        CHILD.wait(timeout=10)
    except Exception:
        CHILD.kill()
elif TEARING["done"]:
    # A torn runtime is left to notice its input closed. It is killed after a short grace so the run
    # cannot be held open by a child that never decides to exit.
    deadline = time.monotonic() + 10
    while CHILD.poll() is None and time.monotonic() < deadline:
        time.sleep(0.1)
    if CHILD.poll() is None:
        CHILD.kill()
note("[gate] exiting")
