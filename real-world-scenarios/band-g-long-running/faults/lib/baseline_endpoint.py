"""An OpenAI-compatible endpoint that serves a recorded transcript, and counts the calls it serves.

Why this exists
---------------
The baseline takes its model from an OpenAI-compatible endpoint (`OPENAI_BASE_URL`). Faulting it needs
three things an ordinary endpoint does not give:

1. **A run that needs no credentials and no network.** The two Band G baselines in the repository were
   captured against a hosted gateway. A fault harness that needed somebody's key would be unrunnable
   by the next reader, and one that reached a real model would produce different research and writing
   on every run, so the fault comparison would be against sampling noise.
2. **A deterministic answer to each request.** Here the endpoint replays a transcript that was recorded
   once, from this same port, by running the baseline against a real model (see `fixtures/`). It matches
   a request by the *shape* of its message list — the number of messages, each one's role, and a hash of
   each message's content — so the second process of a two-process flow gets the same answer the first
   process would have got for the same step. If a request does not match the recording it is answered
   with a fixed line and the mismatch is logged: a resume that asks for something the recording does not
   hold is a finding, not something to paper over.
3. **A count of the calls.** The count is what the comparison is about: a resume from a checkpoint that
   re-runs completed work asks again, and a resume that replays from memory does not. The count is
   written per process to `CALLS_FILE` so a fault script can read the second process's count directly.

It answers `/v1/chat/completions`, which is the only route the baseline's client uses, and it is the
route the recording was taken from. `--pause-on N` holds the Nth request and writes `MARKER_DIR/inflight`
until `MARKER_DIR/release` exists — the same deterministic injection point the Band A harness uses in
front of the recorded runtime, applied here in front of the model. A held request is a model call in
flight: the process is blocked waiting for the answer, which is the instant `kill-during-wait` needs.

Nothing here touches `baseline/` or `upstream/`. The endpoint is named by the environment variable the
baseline already reads, which is the only seam the baseline has.
"""

import argparse
import hashlib
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

UNMATCHED_ANSWER = (
    "The recorded transcript has no answer for this request; the harness answers with a fixed line "
    "and logs the mismatch."
)

FALLBACK = {
    "id": "chatcmpl-unrecorded",
    "object": "chat.completion",
    "created": 0,
    "model": "band-g-recorded",
    "choices": [
        {
            "index": 0,
            "message": {"role": "assistant", "content": UNMATCHED_ANSWER},
            "finish_reason": "stop",
        }
    ],
    "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
}


def shape(messages):
    """The identity of a request: how many messages, their roles, and a digest per message.

    This is deliberately the message list and not the prompt text: the same step in two processes is
    the same request, and a prompt that differs by a suffix is the case a naive key would collide on.
    """
    return [
        [message.get("role", ""), hashlib.sha256((message.get("content") or "").encode("utf-8")).hexdigest()]
        for message in messages
    ]


def load_transcript(path):
    entries = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            request = record.get("request") or {}
            entries.append({"shape": shape(request.get("messages") or []), "response": record["response"]})
    return entries


class Endpoint(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):  # noqa: D401 - the server's own access log is not the log we keep
        return

    def do_GET(self):
        body = json.dumps({"object": "list", "data": [{"id": "band-g-recorded", "object": "model"}]}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length)
        try:
            request = json.loads(raw)
        except ValueError:
            self._send(400, {"error": {"message": "not JSON"}})
            return

        state = self.server.state
        with state["lock"]:
            state["seen"] += 1
            occurrence = state["seen"]
        state["note"]("request %d: %s" % (occurrence, len(request.get("messages") or [])))
        if state["calls_jsonl"]:
            with state["lock"], open(state["calls_jsonl"], "a", encoding="utf-8") as handle:
                handle.write(
                    json.dumps({"n": occurrence, "shape": "|".join("%s:%s" % (role, digest[:8]) for role, digest in shape(request.get("messages") or []))})
                    + "\n"
                )

        # The pause is the injection point. It is applied before the answer is chosen, so the process is
        # blocked inside the call, which is the moment "the wait" is about.
        if state["pause_on"] and occurrence == state["pause_on"] and state["marker_dir"]:
            release = os.path.join(state["marker_dir"], "release")
            if not os.path.exists(release):
                marker = os.path.join(state["marker_dir"], "inflight")
                state["note"]("holding request %d until %s appears" % (occurrence, release))
                with open(marker, "w", encoding="utf-8") as handle:
                    handle.write("chat.completions %d\n" % occurrence)
                deadline = time.time() + state["hold_seconds"]
                while not os.path.exists(release) and time.time() < deadline:
                    time.sleep(0.05)
                state["note"]("released request %d" % occurrence)

        wanted = shape(request.get("messages") or [])
        answer = None
        matched = 0
        for candidate in state["transcript"]:
            if candidate["shape"] == wanted:
                answer = candidate["response"]
                matched += 1
        if answer is None:
            state["note"]("no recorded answer for request %d; answering with the fixed line" % occurrence)
            answer = dict(FALLBACK)
        else:
            state["note"]("request %d matched a recorded answer (%d such request(s) in the recording)" % (occurrence, matched))

        with state["lock"]:
            state["served"] += 1
        self._send(200, answer)

    def _send(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main(argv):
    parser = argparse.ArgumentParser(description="A recorded OpenAI-compatible endpoint for the Band G fault harness.")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--transcript", required=True)
    parser.add_argument("--log", default=None)
    parser.add_argument("--calls-file", default=None, help="written with the number of requests served on shutdown")
    parser.add_argument(
        "--calls-jsonl",
        default=None,
        help="one line per request served: {n, at, shapes}. Read while the endpoint is running.",
    )
    parser.add_argument("--pause-on", type=int, default=0, help="hold this 1-based request")
    parser.add_argument("--marker-dir", default="")
    parser.add_argument("--hold-seconds", type=float, default=600.0)
    args = parser.parse_args(argv)

    state = {
        "lock": threading.Lock(),
        "seen": 0,
        "served": 0,
        "pause_on": args.pause_on,
        "marker_dir": args.marker_dir,
        "hold_seconds": args.hold_seconds,
        "transcript": load_transcript(args.transcript),
        "calls_jsonl": args.calls_jsonl,
    }

    log_handle = open(args.log, "a", encoding="utf-8") if args.log else sys.stderr

    def note(text):
        log_handle.write(text + "\n")
        log_handle.flush()

    state["note"] = note

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Endpoint)
    server.state = state
    server.daemon_threads = True

    def shutdown(_signum, _frame):
        raise KeyboardInterrupt

    import signal

    signal.signal(signal.SIGTERM, shutdown)
    note("endpoint: listening on 127.0.0.1:%d with %d recorded answer(s)" % (args.port, len(state["transcript"])))

    try:
        server.serve_forever(poll_interval=0.05)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        note("endpoint: served %d request(s)" % state["served"])
        if args.calls_file:
            with open(args.calls_file, "w", encoding="utf-8") as handle:
                handle.write("%d\n" % state["served"])
        log_handle.flush()
        if log_handle is not sys.stderr:
            log_handle.close()

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
