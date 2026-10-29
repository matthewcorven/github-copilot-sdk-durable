#!/usr/bin/env python3
"""Attaches to the deterministic replay runtime over stdio and completes one
recorded turn. Standard library only: no pip install required.

Usage: python3 attach.py <runtime-command> [runtime-args...]
The runtime command is supplied by run-all.sh so this check never guesses how
the runtime is launched.
"""

import json
import subprocess
import sys

SESSION_ID = "session-replay-1"
EXPECTED_MESSAGE_ID = "msg-0001"
EXPECTED_CONTENT = "Hello from the recorded transcript."


class AttachError(Exception):
    pass


class Runtime:
    """A JSON-RPC client speaking the runtime's LSP-style Content-Length framing."""

    def __init__(self, argv):
        self._process = subprocess.Popen(
            argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self._next_id = 1
        self.events = []

    def _write_frame(self, message):
        body = json.dumps(message).encode("utf-8")
        # The runtime reads nothing without the header, so every write goes
        # through this framing.
        self._process.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body))
        self._process.stdin.write(body)
        self._process.stdin.flush()

    def _read_frame(self):
        content_length = None
        while True:
            line = self._process.stdout.readline()
            if not line:
                raise AttachError("runtime closed the stream while reading headers")
            line = line.rstrip(b"\r\n")
            if not line:
                break
            name, _, value = line.partition(b":")
            if name.strip().lower() == b"content-length":
                content_length = int(value.strip())

        if content_length is None:
            raise AttachError("frame is missing a Content-Length header")

        body = self._process.stdout.read(content_length)
        if len(body) != content_length:
            raise AttachError("runtime closed the stream while reading the body")
        return json.loads(body.decode("utf-8"))

    def request(self, method, params):
        request_id = self._next_id
        self._next_id += 1
        self._write_frame(
            {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        )

        while True:
            message = self._read_frame()
            if message.get("method") == "session.event":
                self.events.append(message["params"]["event"])
                continue
            if message.get("id") != request_id:
                continue
            if "error" in message:
                error = message["error"]
                raise AttachError(f"{error['code']}: {error['message']}")
            return message["result"]

    def close(self):
        try:
            self._process.stdin.close()
        except OSError:
            pass
        try:
            self._process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self._process.kill()
            raise AttachError("runtime did not exit after runtime.shutdown")


def main(argv):
    if not argv:
        print("usage: python3 attach.py <runtime-command> [runtime-args...]", file=sys.stderr)
        return 2

    runtime = Runtime(argv)
    try:
        connect = runtime.request("connect", {})
        if connect.get("ok") is not True:
            raise AttachError(f"connect did not report ok: {connect}")

        created = runtime.request("session.create", {"sessionId": SESSION_ID})
        if created.get("sessionId") != SESSION_ID:
            raise AttachError(
                f"session.create returned {created.get('sessionId')!r}, expected {SESSION_ID!r}"
            )

        sent = runtime.request(
            "session.sendMessages",
            {"sessionId": SESSION_ID, "messages": [{"role": "user", "content": "Hello"}]},
        )
        if sent.get("messageIds") != [EXPECTED_MESSAGE_ID]:
            raise AttachError(
                f"session.sendMessages returned {sent}, expected ['{EXPECTED_MESSAGE_ID}']"
            )

        message = next((e for e in runtime.events if e["type"] == "assistant.message"), None)
        if message is None:
            raise AttachError("no assistant.message event was emitted")
        if message["data"]["content"] != EXPECTED_CONTENT:
            raise AttachError(f"assistant.message content was {message['data']['content']!r}")

        # getMessages must replay the same transcript the events carried.
        transcript = runtime.request("session.getMessages", {"sessionId": SESSION_ID})
        replayed = next(
            (e for e in transcript["events"] if e["type"] == "assistant.message"), None
        )
        if replayed is None or replayed["data"]["content"] != EXPECTED_CONTENT:
            raise AttachError("session.getMessages did not replay the recorded assistant message")

        runtime.request("runtime.shutdown", {})
    finally:
        runtime.close()

    print(f"python: PASS ({EXPECTED_MESSAGE_ID}: {EXPECTED_CONTENT})")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except AttachError as error:
        print(f"python: {error}", file=sys.stderr)
        sys.exit(1)
