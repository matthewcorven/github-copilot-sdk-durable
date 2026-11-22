"""Records the baseline's model traffic once, so the fault harness can replay it forever.

The Band G baseline needs a model. A fault harness that needs a live model is a harness nobody can run
twice the same way — and a comparison whose numbers move with a sampler is not a comparison. So the
traffic is recorded once, against a real endpoint, and replayed by `lib/baseline_endpoint.py`.

This script is a proxy: it sits in front of `UPSTREAM`, forwards every `/v1/chat/completions` call
unchanged, and appends `{path, request, response}` per line. Point the baseline at it, run the two
processes of the band's own flow, and the result is the fixture:

    python3 lib/record_transcript.py --port 5342 --out fixtures/baseline-transcript.jsonl &
    export OPENAI_BASE_URL=http://127.0.0.1:5342/v1 OPENAI_MODEL=...
    ...run the band's run.sh + resume.sh...

The fixture is a recording, not a curated set of answers: what is in it is what the model said.
"""

import argparse
import json
import os
import sys
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Recorder(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        return

    def do_GET(self):
        self._raw(404, b"{}")

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
        upstream = self.server.upstream
        request = urllib.request.Request(
            upstream + self.path,
            data=raw,
            method="POST",
            headers={k: v for k, v in self.headers.items() if k.lower() not in ("host", "content-length")},
        )
        status, payload = 502, b"{}"
        try:
            with urllib.request.urlopen(request) as response:
                status, payload = response.status, response.read()
        except Exception as error:  # noqa: BLE001 - the recording is only as good as the upstream
            self.server.note("upstream failed: %s" % error)

        if status == 200:
            try:
                record = {"path": self.path, "request": json.loads(raw), "response": json.loads(payload)}
                with self.server.lock, open(self.server.out, "a", encoding="utf-8") as handle:
                    handle.write(json.dumps(record, ensure_ascii=False) + "\n")
                    self.server.recorded += 1
                self.server.note("recorded call %d" % self.server.recorded)
            except ValueError:
                self.server.note("could not parse the upstream answer; not recorded")

        self._raw(status, payload)

    def _raw(self, status, body):
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def main(argv):
    parser = argparse.ArgumentParser(description="Record the OpenAI-compatible traffic of the Band G baseline.")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--upstream", default=os.environ.get("UPSTREAM", "http://127.0.0.1:11434"))
    parser.add_argument("--out", required=True)
    parser.add_argument("--log", default=None)
    args = parser.parse_args(argv)

    log_handle = open(args.log, "a", encoding="utf-8") if args.log else sys.stderr
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Recorder)
    server.daemon_threads = True
    server.upstream = args.upstream
    server.out = args.out
    server.lock = threading.Lock()
    server.recorded = 0

    def note(text):
        log_handle.write(text + "\n")
        log_handle.flush()

    server.note = note
    note("recording: 127.0.0.1:%d -> %s" % (args.port, args.upstream))
    try:
        server.serve_forever(poll_interval=0.05)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        note("recording: %d call(s) written to %s" % (server.recorded, args.out))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
