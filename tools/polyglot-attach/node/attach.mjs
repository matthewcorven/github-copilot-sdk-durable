#!/usr/bin/env node
// Attaches to the deterministic replay runtime over stdio and completes one
// recorded turn. Standard library only: no npm install required.
//
// Usage: node attach.mjs <runtime-command> [runtime-args...]
// The runtime command is supplied by run-all.sh so this check never guesses how
// the runtime is launched.

import { spawn } from "node:child_process";

const SESSION_ID = "session-replay-1";
const EXPECTED_MESSAGE_ID = "msg-0001";
const EXPECTED_CONTENT = "Hello from the recorded transcript.";

const [command, ...args] = process.argv.slice(2);
if (!command) {
  console.error("usage: node attach.mjs <runtime-command> [runtime-args...]");
  process.exit(2);
}

const child = spawn(command, args, { stdio: ["pipe", "pipe", "pipe"] });

let buffer = Buffer.alloc(0);
let nextId = 1;
const pending = new Map();
const events = [];
let stderrText = "";

child.stderr.on("data", (chunk) => {
  stderrText += chunk.toString("utf8");
});

// LSP-style framing: "Content-Length: <utf8 bytes>\r\n\r\n<json>". The runtime
// reads nothing without the header, so every write goes through writeFrame.
function writeFrame(message) {
  const body = Buffer.from(JSON.stringify(message), "utf8");
  child.stdin.write(`Content-Length: ${body.length}\r\n\r\n`);
  child.stdin.write(body);
}

function readFrames() {
  for (;;) {
    const headerEnd = buffer.indexOf("\r\n\r\n");
    if (headerEnd < 0) return;

    const header = buffer.subarray(0, headerEnd).toString("utf8");
    const match = /^content-length:\s*(\d+)$/im.exec(header);
    if (!match) {
      fail(`frame is missing a Content-Length header: ${JSON.stringify(header)}`);
      return;
    }

    const length = Number(match[1]);
    const bodyStart = headerEnd + 4;
    if (buffer.length < bodyStart + length) return;

    const body = buffer.subarray(bodyStart, bodyStart + length).toString("utf8");
    buffer = buffer.subarray(bodyStart + length);
    handleMessage(JSON.parse(body));
  }
}

function handleMessage(message) {
  if (message.method === "session.event") {
    events.push(message.params.event);
    return;
  }

  const waiter = pending.get(message.id);
  if (!waiter) return;
  pending.delete(message.id);
  if (message.error) {
    waiter.reject(new Error(`${message.error.code}: ${message.error.message}`));
  } else {
    waiter.resolve(message.result);
  }
}

function request(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    writeFrame({ jsonrpc: "2.0", id, method, params });
  });
}

function fail(message) {
  console.error(`node: ${message}`);
  if (stderrText.trim()) console.error(stderrText.trim());
  child.kill();
  process.exit(1);
}

child.stdout.on("data", (chunk) => {
  buffer = Buffer.concat([buffer, chunk]);
  readFrames();
});

child.on("exit", (code) => {
  if (pending.size > 0) {
    fail(`runtime exited with code ${code} while ${pending.size} request(s) were outstanding`);
  }
});

async function main() {
  const connect = await request("connect", {});
  if (connect.ok !== true) fail(`connect did not report ok: ${JSON.stringify(connect)}`);

  const created = await request("session.create", { sessionId: SESSION_ID });
  if (created.sessionId !== SESSION_ID) {
    fail(`session.create returned '${created.sessionId}', expected '${SESSION_ID}'`);
  }

  const sent = await request("session.sendMessages", {
    sessionId: SESSION_ID,
    messages: [{ role: "user", content: "Hello" }],
  });
  if (!Array.isArray(sent.messageIds) || sent.messageIds[0] !== EXPECTED_MESSAGE_ID) {
    fail(`session.sendMessages returned ${JSON.stringify(sent)}, expected ['${EXPECTED_MESSAGE_ID}']`);
  }

  const message = events.find((e) => e.type === "assistant.message");
  if (!message) fail("no assistant.message event was emitted");
  if (message.data.content !== EXPECTED_CONTENT) {
    fail(`assistant.message content was ${JSON.stringify(message.data.content)}`);
  }

  // getMessages must replay the same transcript the events carried.
  const transcript = await request("session.getMessages", { sessionId: SESSION_ID });
  const replayed = transcript.events.find((e) => e.type === "assistant.message");
  if (!replayed || replayed.data.content !== EXPECTED_CONTENT) {
    fail("session.getMessages did not replay the recorded assistant message");
  }

  await request("runtime.shutdown", {});
  child.stdin.end();
  console.log(`node: PASS (${EXPECTED_MESSAGE_ID}: ${EXPECTED_CONTENT})`);
}

main().catch((error) => fail(error.message));
