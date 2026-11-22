# Polyglot attach checks

Proves the deterministic replay runtime in `tools/ReplayRuntime/` is language-neutral.

Each check starts the replay runtime as a child process, speaks the runtime JSON-RPC
protocol over stdio, opens a session, sends one turn, and asserts the recorded
response. Five clients do this — Node, Python, Go, Rust and Java — and all five use
the **same** fixture, `tools/ReplayRuntime/fixtures/basic.json`, so a divergence
points at the client rather than at the transcript.

This is a capability check, not a port. The examples live in `examples/`; this tree
only answers "can a non-.NET SDK client attach at all?".

## Run it

```sh
tools/polyglot-attach/run-all.sh              # every available client
tools/polyglot-attach/run-all.sh node python  # only the named clients
```

The runner builds the replay runtime once, then runs each client and prints one
`PASS` / `FAIL` / `SKIP` line per language:

```
=== Building the replay runtime ===
=== Attaching from each SDK client ===
PASS     node
         node: PASS (msg-0001: Hello from the recorded transcript.)
PASS     python
         python: PASS (msg-0001: Hello from the recorded transcript.)
PASS     go
         go: PASS (msg-0001: Hello from the recorded transcript.)
PASS     rust
         rust: PASS (msg-0001: Hello from the recorded transcript.)
SKIP     java (a JDK (javac and java) is not installed)

4 passed, 0 failed, 1 skipped.
```

Exit status is non-zero **only** when an available toolchain failed. A missing
toolchain is a `SKIP`, never a failure, so a contributor without a JDK still gets a
green run.

Set `CHECK_TIMEOUT` (seconds, default `120`) to change the per-client watchdog.

## Prerequisites

| Client | Needs | Notes |
| --- | --- | --- |
| all | .NET SDK (`dotnet`) | Builds the replay runtime. Without it every client is skipped. |
| node | Node.js 18+ | Standard library only; no `npm install`. |
| python | Python 3.8+ | Standard library only; no `pip install`. |
| go | Go 1.21+ | Standard library only; no module downloads. |
| rust | `cargo` + `rustc` | No crates; the check builds with `--offline`. |
| java | JDK 17+ (`javac` and `java`) | Standard library only; no Maven or Gradle. |

A toolchain counts as available only when it actually runs. macOS ships launcher
shims at `/usr/bin/java` and `/usr/bin/javac` that exist on `PATH` but fail with
"Unable to locate a Java Runtime"; the runner probes each toolchain with its version
flag, so those shims produce a `SKIP` rather than a build failure.

## What each check asserts

Every client performs the same sequence against the same fixture:

1. `connect` reports `ok: true`.
2. `session.create` with `sessionId: "session-replay-1"` returns that session id.
3. `session.sendMessages` returns `messageIds: ["msg-0001"]`.
4. An `assistant.message` event arrives carrying
   `"Hello from the recorded transcript."`.
5. `session.getMessages` replays the same recorded message.
6. `runtime.shutdown` is acknowledged and the runtime exits.

## Framing

The runtime uses LSP-style `Content-Length` header framing, not newline-delimited
JSON:

```
Content-Length: <utf8 byte count>\r\n
\r\n
<json>
```

A client that writes bare JSON lines gets no response: the runtime reads nothing and
exits 0. Every client here writes the header, and the byte count is computed from the
UTF-8 encoding so it always matches the payload.

## Layout

```
tools/polyglot-attach/
  run-all.sh        # builds the runtime, runs each client, reports per-language results
  node/attach.mjs
  python/attach.py
  go/attach.go      # go.mod alongside
  rust/             # Cargo.toml + src/main.rs
  java/Attach.java
  target/           # runner scratch output (git-ignored)
```

Each client takes the runtime command as its arguments, so the launch contract is
identical across languages and only the client code differs. Run one directly:

```sh
node tools/polyglot-attach/node/attach.mjs \
  dotnet tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime.dll \
  --fixture tools/ReplayRuntime/fixtures/basic.json
```

## Adding a client

Add a subdirectory with a standard-library-only client that accepts the runtime
command as arguments, then add a `case` arm to `run-all.sh` that probes the toolchain
with `tool_works`, builds, and calls `run_check`. Reuse the existing fixture; do not
add a new one.
