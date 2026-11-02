"""Targeted standalone and mutation checks: python3 examples/py/parity_test_11_21.py."""
import subprocess
import sys
import unittest
import json
import os
import ast
import textwrap
import uuid
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]


class DeterministicParityTests(unittest.TestCase):
    def run_example(self, path, mutation=None, env=None, cwd=ROOT):
        if mutation is None:
            command = (["node", "--experimental-transform-types"] if path.suffix == ".ts"
                       else [sys.executable]) + [str(path)]
        elif path.suffix == ".ts":
            old, new = mutation
            self.assertIn(old, path.read_text(), f"mutation must target the actual artifact: {path}")
            script = (
                "import { createRequire } from 'node:module'; "
                f"const require = createRequire({json.dumps((ROOT / 'lib/ts/package.json').as_uri())}); "
                "const fs = require('node:fs'); "
                f"const ts = require({json.dumps(str(ROOT / 'lib/ts/node_modules/typescript'))}); "
                f"let source = fs.readFileSync({json.dumps(str(path))}, 'utf8').replace(/\\r\\n?/g, '\\n'); "
                f"source = source.replace({json.dumps(old)}, {json.dumps(new)}); "
                f"source = source.replace('../../lib/ts/durable.ts', {json.dumps((ROOT / 'lib/ts/durable.ts').as_uri())}); "
                "const code = ts.transpileModule(source, {compilerOptions: "
                "{target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext}}).outputText; "
                "import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));"
            )
            command = ["node", "--experimental-transform-types", "--input-type=module", "-e", script]
        else:
            old, new = mutation
            source = path.read_text()
            self.assertIn(old, source, f"mutation must target the actual artifact: {path}")
            command = [sys.executable, "-c",
                       "import sys; from pathlib import Path; "
                       "p = Path(sys.argv[1]); "
                       "s = p.read_text().replace(sys.argv[2], sys.argv[3], 1); "
                       "exec(compile(s, str(p), 'exec'), "
                       "{'__name__': '__main__', '__file__': str(p)})", str(path), old, new]
        return subprocess.run(command, cwd=cwd, text=True, capture_output=True, timeout=15, env=env)

    def test_tasks_standalone(self):
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                result = self.run_example(ROOT / "examples" / language / f"11-tasks.{suffix}")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("receipt: 500", result.stdout)
                self.assertIn("payments charged: 1", result.stdout)
                self.assertIn("task status: Terminal", result.stdout)

    def test_recovery_standalone(self):
        result = self.run_example(ROOT / "examples/py/12-recovery.py")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("closed; saved checkpoint: tick n=2", result.stdout)
        self.assertIn("saved memos: 2", result.stdout)
        self.assertIn("ticks printed in total: [1, 2, 3, 4, 5]", result.stdout)

    def test_typescript_recovery_standalone(self):
        result = self.run_example(ROOT / "examples/ts/12-recovery.ts")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("closed; saved checkpoint: tick n=2", result.stdout)
        self.assertIn("saved memos: 2", result.stdout)
        self.assertIn("ticks printed in total: [1, 2, 3, 4, 5]", result.stdout)

    def test_system_prompts_standalone(self):
        runtime = Path(os.environ.get("DURABLE_RUNTIME_PATH",
                                      str(ROOT / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime")))
        self.assertTrue(runtime.is_file(), "Build tools/ReplayRuntime/ReplayRuntime.csproj before running parity tests")
        env = dict(os.environ, DURABLE_RUNTIME_PATH=str(runtime),
                   DURABLE_EXAMPLE_FIXTURE=str(ROOT / "examples/ts/14-system-prompt.fixture.json"))
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                result = self.run_example(ROOT / "examples" / language / f"14-system-prompt.{suffix}", env=env)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("root system prompt:", result.stdout)
                self.assertIn("subagent system prompt:", result.stdout)
                self.assertIn("Only read; never edit files.", result.stdout)
                self.assertIn("effective model: (not reported)", result.stdout)
                # Configuration evidence only: native generated session IDs cause replay
                # to report synthesized sessions. This is not a recorded model transcript.
                self.assertIn("synthesised session", result.stderr)

    def test_fixture_print_mode_standalone(self):
        runtime = ROOT / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
        env = dict(os.environ, DURABLE_RUNTIME_PATH=str(runtime),
                   DURABLE_EXAMPLE_FIXTURE=str(ROOT / "tools/ReplayRuntime/fixtures/basic.json"))
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                result = self.run_example(ROOT / "examples" / language / f"17-print.{suffix}", env=env)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("Hello from the recorded transcript.", result.stdout.strip())
                self.assertIn("effective model: replay-model", result.stderr)
                self.assertIn("synthesised session", result.stderr)

    def test_fixture_json_mode_standalone(self):
        runtime = ROOT / "tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"
        env = dict(os.environ, DURABLE_RUNTIME_PATH=str(runtime),
                   DURABLE_EXAMPLE_FIXTURE=str(ROOT / "tools/ReplayRuntime/fixtures/basic.json"))
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                result = self.run_example(ROOT / "examples" / language / f"18-json.{suffix}", env=env)
                self.assertEqual(0, result.returncode, result.stderr)
                lines = [json.loads(line) for line in result.stdout.splitlines()]
                self.assertTrue(all(isinstance(line, dict) for line in lines))
                self.assertEqual("snapshot", lines[0]["type"])
                events = [line for line in lines if line["type"] == "event"]
                answers = [line for line in events if line["event"] == "assistant.message"]
                self.assertEqual(1, len(answers))
                self.assertEqual("Hello from the recorded transcript.", answers[0]["data"])
                self.assertIn("synthesised session", result.stderr)

    def test_failed_payment_assertion_exits_nonzero(self):
        result = self.run_example(ROOT / "examples/py/11-tasks.py",
                                  ('Check.equal(500, paid.result["receipt"]',
                                   'Check.equal(501, paid.result["receipt"]'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: the receipt holds the amount", result.stderr)

    def test_coding_tools_accept_canonical_workspace_and_reject_escape(self):
        source = ROOT / "examples/py/16-coding-tools.py"
        content = source.read_text()
        with TemporaryDirectory(prefix="durable-path-guard-") as directory:
            base = Path(directory).resolve()
            workspace = base / "workspace"
            workspace.mkdir()
            notes = workspace / "notes.txt"
            notes.write_text("hello world\n")
            alias = base / "alias"
            alias.symlink_to(workspace, target_is_directory=True)

            def load_guard(text):
                main = next(node for node in ast.parse(text).body
                            if isinstance(node, ast.AsyncFunctionDef) and node.name == "main")
                scope = next(node for node in main.body if isinstance(node, ast.With))
                nodes = [node for node in scope.body
                         if (isinstance(node, ast.Assign)
                             and any(isinstance(target, ast.Name) and target.id == "workspace"
                                     for target in node.targets))
                         or (isinstance(node, ast.FunctionDef) and node.name == "file_for")]
                self.assertEqual(2, len(nodes), "extract the actual workspace and file guard")
                namespace = {"Path": Path, "directory": str(alias)}
                exec(compile(ast.Module(body=nodes, type_ignores=[]), str(source), "exec"), namespace)
                return namespace["file_for"]

            guard = load_guard(content)
            self.assertEqual(notes, guard("notes.txt"))
            with self.assertRaisesRegex(ValueError, "outside the example workspace"):
                guard("../outside.txt")
            sibling = base / "workspace-sibling"
            sibling.mkdir()
            with self.assertRaisesRegex(ValueError, "outside the example workspace"):
                guard("../workspace-sibling/notes.txt")
            (workspace / "escape").symlink_to(sibling, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, "outside the example workspace"):
                guard("escape/notes.txt")
            old = "workspace = Path(directory).resolve()"
            self.assertIn(old, content)
            broken = load_guard(content.replace(old, "workspace = Path(directory)", 1))
            with self.assertRaisesRegex(ValueError, "outside the example workspace"):
                broken("notes.txt")

    def test_missing_memo_repeats_effect_and_fails(self):
        result = self.run_example(ROOT / "examples/py/12-recovery.py",
                                  ('if await runtime.memo_async(f"printed-{n}") is None:',
                                   'if True:'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: no tick was printed twice, and none was skipped", result.stderr)
        self.assertIn("found [1, 2, 2, 3, 4, 5]", result.stderr)

    def test_failed_typescript_payment_assertion_exits_nonzero(self):
        result = self.run_example(ROOT / "examples/ts/11-tasks.ts",
                                  ("Check.equal(500, result", "Check.equal(501, result"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: the receipt holds the amount", result.stderr)

    def test_typescript_missing_memo_repeats_effect_and_fails(self):
        result = self.run_example(ROOT / "examples/ts/12-recovery.ts",
                                  ('if (await runtime.memoAsync(`printed-${n}`) === null) {', 'if (true) {'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: no tick was printed twice, and none was skipped", result.stderr)

    def test_range_metadata_preserves_inventory_and_live_classification(self):
        stems = ["11-tasks", "12-recovery", "13-chat", "14-system-prompt", "15-real-model",
                 "16-coding-tools", "17-print", "18-json", "19-inbox", "20-late-join",
                 "21-subagent-foreground"]
        live = {13, 15, 16, 17, 18, 19, 20, 21}
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                metadata = json.loads((ROOT / "examples" / language / "parity-11-21.json").read_text())
                self.assertTrue(metadata["implementationComplete"])
                self.assertFalse(metadata["liveValidationComplete"])
                self.assertFalse(metadata["complete"])
                self.assertEqual([f"{stem}.{suffix}" for stem in stems],
                                 [item["file"] for item in metadata["examples"]])
                for item in metadata["examples"]:
                    number = int(item["file"][:2])
                    self.assertEqual(str(number), item["id"])
                    self.assertEqual(item["file"].rsplit(".", 1)[0] + ".cs", item["source"])
                    self.assertEqual(f"examples/{language}/{item['file']}", item["path"])
                    self.assertEqual("ported", item["status"])
                    self.assertEqual("not-applicable", item["sandboxMode"])
                    self.assertEqual(number in live, item["live"], item["file"])
                    if item["live"]:
                        self.assertTrue(item["liveReason"], item["file"])
                    self.assertTrue((ROOT / "examples" / language / item["file"]).is_file())
                    if number == 20:
                        self.assertEqual("live-passed", item["validationStatus"])
                        self.assertEqual("fdf325bae125768aff8d8e1b8f0eec7fc4ee2955",
                                         item["referenceCorrection"]["commit"])
                        self.assertEqual(
                            "Original joins a streaming model run after 1.5 seconds and requires live deltas after the snapshot; timing is not guaranteed by a completed recording.",
                            item["liveReason"])
                        self.assertEqual(5, len(item["requiredAssertions"]))
                        self.assertEqual(90, item["synchronization"]["deadlineClockSeconds"])
                        self.assertEqual(15, item["synchronization"]["historyDeadlineSeconds"])
                        self.assertEqual(50, item["synchronization"]["conditionalHistoryPollMilliseconds"])
                        self.assertTrue(item["synchronization"]["subscriptionBeforeRelease"])
                        self.assertFalse(item["synchronization"]["bootstrapCancellationGuaranteed"])
                        self.assertEqual("the snapshot holds the user message",
                                         item["historicalNativeEvidence"]["failedAssertion"])
                        for run in item["nativeEvidence"]["runs"]:
                            self.assertEqual(0, run["exitCode"])
                            self.assertTrue(run["snapshotUserEvent"])
                            self.assertGreater(run["deltasAfterJoin"], 0)


class LateJoinParityTests(unittest.TestCase):
    """Actual port sources + real SDK/facade + reference protocol peer, not LIVE evidence."""

    @classmethod
    def setUpClass(cls):
        cls.directory = TemporaryDirectory(prefix="durable-port-late-join-")
        cls.addClassCleanup(cls.directory.cleanup)
        runtime = Path(cls.directory.name) / "runtime"
        runtime.mkdir()
        reference = (ROOT / "tests/Durable.Tests/LateJoinExampleTests.cs").read_text()
        peer = reference.split('private const string MockRuntimeSource = """"', 1)[1].split('"""";', 1)[0]
        peer = textwrap.dedent(peer).lstrip()

        def adapt(old, new):
            nonlocal peer
            if old not in peer:
                raise AssertionError(f"extracted C# mock transport no longer contains {old!r}")
            peer = peer.replace(old, new)

        # Root restoration reads native history before the port can signal observer readiness.
        # Keep servicing RPC while the same named-pipe handshake awaits that signal. Serialize
        # whole frames because the model report now runs alongside the request loop.
        start = peer.index('    if (method == "session.create")\n')
        end = peer.index('    if (method == "session.send")\n', start)
        handshake = textwrap.dedent(peer[start:end])
        handshake = handshake[handshake.index("{") + 1:handshake.rindex("}")].strip()
        peer = (peer[:start] + '    if (method == "session.create") modelReport = ReportWhenReadyAsync();\n'
                + peer[end:])
        adapt('var output = Console.OpenStandardOutput();',
              'var output = Console.OpenStandardOutput();\n'
              'var wireGate = new SemaphoreSlim(1, 1);\n'
              'var traceGate = new object();\n'
              'Task? modelReport = null;')
        adapt('    if (method == "session.send")\n    {',
              '    if (method == "session.send")\n    {\n        await modelReport!;')
        adapt('Task ReportModelAsync() =>',
              'async Task ReportWhenReadyAsync()\n{\n' + textwrap.indent(handshake, "    ")
              + '\n}\n\nTask ReportModelAsync() =>')
        adapt('    await observerReady.WriteAsync(new byte[] { 1 });',
              '    if (scenario == "old-model-timing") await ReportModelAsync();\n'
              '    await observerReady.WriteAsync(new byte[] { 1 });')
        adapt('File.AppendAllText("wire-" + scenario, message.ToJsonString() + "\\n");',
              'lock (traceGate) File.AppendAllText("wire-" + scenario, message.ToJsonString() + "\\n");')
        adapt('    var body = Encoding.UTF8.GetBytes(message.ToJsonString());',
              '    await wireGate.WaitAsync();\n    try\n    {\n'
              '    var body = Encoding.UTF8.GetBytes(message.ToJsonString());')
        adapt('    await output.FlushAsync();',
              '    await output.FlushAsync();\n    }\n    finally { wireGate.Release(); }')
        adapt('\nasync Task ReportWhenReadyAsync()',
              '\nif (modelReport is not null) await modelReport;\n\nasync Task ReportWhenReadyAsync()')
        # These facades also read history during root restoration, before session.send.
        # Apply turn scenarios only to reads after submission, as in the C# demonstration.
        adapt("var reads = 0;", "var reads = 0;\nvar submitted = false;")
        adapt('case "session.send":', 'case "session.send":\n            submitted = true;\n            reads = 0;')
        adapt('if (scenario == "stalled-read")', 'if (submitted && scenario == "stalled-read")')
        adapt('if (scenario == "delayed-user" && reads >= 3)',
              'if (submitted && scenario == "delayed-user" && reads >= 3)')
        adapt('if (scenario == "mid-join-error")', 'if (submitted && scenario == "mid-join-error")')
        adapt('if (scenario == "gap" && !resultReceived)',
              'if (submitted && scenario == "gap" && !resultReceived)')
        adapt('await EventAsync("assistant.message", new JsonObject',
              'if (scenario != "no-answer") await EventAsync("assistant.message", new JsonObject')
        (runtime / "Program.cs").write_text(peer)
        (runtime / "Program.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '</PropertyGroup></Project>')
        build = subprocess.run(["dotnet", "build", "--nologo", "-v:q"], cwd=runtime,
                               text=True, capture_output=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.runtime = runtime / "bin/Debug/net10.0" / ("Program.exe" if os.name == "nt" else "Program")

    @staticmethod
    def instrument_readiness(source, language):
        if language == "ts":
            target = "  const report = await until(root.waitForEffectiveModelAsync(), deadline.signal);"
            instrumentation = r'''
  // Mock transport only: RootAsync returned and the actual facade observer is attached.
  const { createConnection } = await import("node:net");
  const { readFileSync } = await import("node:fs");
  const { tmpdir } = await import("node:os");
  const { join } = await import("node:path");
  const pipe = process.platform === "win32"
    ? "\\\\.\\pipe\\" + process.env.LATE_JOIN_READY_PIPE
    : join(tmpdir(), "CoreFxPipe_" + process.env.LATE_JOIN_READY_PIPE);
  await until(new Promise<void>((resolve, reject) => {
    const socket = createConnection(pipe);
    let acknowledged = false;
    const abort = () => socket.destroy(new Error("Observer readiness cancelled."));
    deadline.signal.addEventListener("abort", abort, { once: true });
    socket.once("error", reject);
    socket.once("close", () => {
      deadline.signal.removeEventListener("abort", abort);
      if (!acknowledged) reject(new Error("Observer readiness closed before acknowledgement."));
    });
    socket.once("data", (probe: Buffer) => {
      if (probe.length !== 1 || probe[0] !== 1) {
        socket.destroy(new Error("Invalid model readiness probe."));
        return;
      }
      const wire = readFileSync("wire-" + process.env.LATE_JOIN_MOCK_SCENARIO, "utf8");
      if (wire.includes('"type":"session.start"')) {
        socket.destroy(new Error("Model report preceded observer readiness."));
        return;
      }
      socket.end(Buffer.from([1]), () => { acknowledged = true; resolve(); });
    });
  }), deadline.signal);
'''
        else:
            target = "            report = await root.wait_for_effective_model_async()"
            instrumentation = r'''
            # Mock transport only: root_async returned with the actual observer attached.
            import os
            from tempfile import gettempdir
            pipe = os.environ["LATE_JOIN_READY_PIPE"]
            if sys.platform == "win32":
                reader = asyncio.StreamReader()
                protocol = asyncio.StreamReaderProtocol(reader)
                transport, _ = await loop.create_pipe_connection(
                    lambda: protocol, "\\\\.\\pipe\\" + pipe)
                writer = asyncio.StreamWriter(transport, protocol, reader, loop)
            else:
                reader, writer = await asyncio.open_unix_connection(
                    str(Path(gettempdir()) / ("CoreFxPipe_" + pipe)))
            try:
                if await reader.readexactly(1) != b"\x01":
                    raise RuntimeError("Invalid model readiness probe.")
                wire = Path("wire-" + os.environ["LATE_JOIN_MOCK_SCENARIO"]).read_text()
                if '"type":"session.start"' in wire:
                    raise RuntimeError("Model report preceded observer readiness.")
                writer.write(b"\x01")
                await writer.drain()
            finally:
                writer.close()
                await writer.wait_closed()
'''
        if source.count(target) != 1:
            raise AssertionError(f"expected exactly one actual {language} model wait")
        return source.replace(target, instrumentation.rstrip() + "\n" + target)

    def run_port(self, language, scenario, mutation=None, path=None):
        path = path or ROOT / "examples" / language / f"20-late-join.{language}"
        original = path.read_text()
        if language == "ts":
            source = original.replace("90000", "5000").replace("15000", "500")
        else:
            source = original.replace("loop.time() + 90", "loop.time() + 5").replace("loop.time() + 15", "loop.time() + 0.5")
        self.assertNotEqual(original, source, "shorten only the actual source's test budgets")
        if mutation:
            old, new = mutation
            self.assertIn(old, source, f"mutation must target real {language}20 source")
            source = source.replace(old, new, 1)
        source = self.instrument_readiness(source, language)
        with TemporaryDirectory(prefix="durable-port-case-", dir=self.directory.name) as directory:
            env = dict(os.environ, DURABLE_RUNTIME_PATH=str(self.runtime),
                       LATE_JOIN_MOCK_SCENARIO=scenario,
                       LATE_JOIN_READY_PIPE="lj-" + uuid.uuid4().hex)
            env.pop("DURABLE_EXAMPLE_FIXTURE", None)
            result = DeterministicParityTests.run_example(
                self, path, (original, source), env, Path(directory))
            artifacts = {p.name: p.read_text() for p in Path(directory).iterdir() if p.is_file()}
        return result, artifacts

    def test_physical_line_endings_preserve_generated_clients(self):
        run = subprocess.run

        def inspect_generated_client(command, **kwargs):
            if command[0] == "node":
                execute = "import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));"
                self.assertEqual(1, command[-1].count(execute))
                capture = [*command[:-1], command[-1].replace(execute, "process.stdout.write(code);")]
                generated = run(capture, **kwargs)
                self.assertEqual(0, generated.returncode, generated.stderr)
                code = generated.stdout
                self.assertIn("deadline.abort(), 5000)", code,
                              "generated client must contain the shortened overall deadline")
                self.assertIn("snapshotDeadline.abort(), 500)", code,
                              "generated client must contain the shortened history deadline")
                self.assertNotIn("90000", code)
                self.assertNotIn("15000", code)
                self.assertIn('createConnection(pipe)', code,
                              "generated client must install the observer readiness handshake")
                self.assertIn('socket.end(Buffer.from([1])', code)
                self.assertLess(code.index('createConnection(pipe)'),
                                code.index('root.waitForEffectiveModelAsync()'))
            return run(command, **kwargs)

        pythonpath = os.pathsep.join(filter(None, (str(ROOT / "lib/py"), os.environ.get("PYTHONPATH"))))
        with TemporaryDirectory(prefix="durable-physical-checkout-", dir=self.directory.name) as directory:
            for ending in ("\n", "\r\n"):
                for language in ("ts", "py"):
                    with self.subTest(line_ending=repr(ending), language=language):
                        original = ROOT / "examples" / language / f"20-late-join.{language}"
                        path = Path(directory) / original.name
                        physical = original.read_text().replace("\n", ending).encode()
                        path.write_bytes(physical)
                        self.assertEqual(physical, path.read_bytes())
                        self.assertEqual(ending == "\r\n", b"\r\n" in physical)
                        with mock.patch.dict(os.environ, PYTHONPATH=pythonpath), \
                                mock.patch.object(subprocess, "run", side_effect=inspect_generated_client):
                            result, artifacts = self.run_port(language, "delayed-user", path=path)
                        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                        self.assertIn("model-observer-ready", artifacts["trace-delayed-user"])
                        self.assertIn("getMessages:3", artifacts["trace-delayed-user"])
                        self.assertIn("tool-result-after-snapshot:True", artifacts["trace-delayed-user"])
                        self.assertIn("answer: One. Two. Three. Four. Five. DONE", result.stdout)
                        self.assertIn("deltas received after joining: 1", result.stdout)
                        self.assertLess(result.stdout.index("snapshot events:"),
                                        result.stdout.index("live subscription installed"))
                        self.assertLess(result.stdout.index("live subscription installed"),
                                        result.stdout.index("event text delta:"))

    def test_actual_sdk_barrier_delayed_history_and_configuration(self):
        for language in ("ts", "py"):
            with self.subTest(language=language):
                result, artifacts = self.run_port(language, "delayed-user")
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("effective model: mock-reported-model", result.stdout)
                self.assertIn("  user.message", result.stdout)
                self.assertIn("answer: One. Two. Three. Four. Five. DONE", result.stdout)
                self.assertIn("deltas received after joining: 1", result.stdout)
                self.assertLess(result.stdout.index("snapshot events:"),
                                result.stdout.index("live subscription installed"))
                self.assertLess(result.stdout.index("live subscription installed"),
                                result.stdout.index("event text delta:"))
                trace = artifacts["trace-delayed-user"]
                self.assertIn("model-observer-ready", trace)
                wire = [json.loads(line) for line in artifacts["wire-delayed-user"].splitlines()]
                model_indices = [i for i, message in enumerate(wire)
                                 if message.get("params", {}).get("event", {}).get("type") == "session.start"]
                self.assertEqual(1, len(model_indices), "the model report must remain a one-shot event")
                model_index = model_indices[0]
                restoration = [message for message in wire[:model_index]
                               if message.get("method") == "session.getMessages"]
                self.assertTrue(restoration,
                                "root restoration must finish its native history reads before readiness")
                for request in restoration:
                    self.assertTrue(any(message.get("id") == request["id"] and "result" in message
                                        for message in wire[:model_index]),
                                    "pre-submission history replies must precede the model report")
                self.assertFalse(any(message.get("method") == "session.send"
                                     for message in wire[:model_index]))
                self.assertIn("getMessages:3", trace)
                self.assertIn("tool-result-after-snapshot:True", trace)
                self.assertIn("The client has joined.", trace)
                config = json.loads(artifacts["config-delayed-user"])
                self.assertTrue(config["streaming"])
                self.assertEqual(["wait_for_join"], [tool["name"] for tool in config["tools"]])
                source = (ROOT / "examples" / language / f"20-late-join.{language}").read_text()
                for exact in ('extensions: ["late-join"]', 'tools: ["wait_for_join"]',
                              'permissions: ["custom-tool:wait_for_join"]'):
                    self.assertIn(exact if language == "ts" else exact.replace(": ", "="), source)
                self.assertNotIn("enable_file_hooks", source)
                self.assertNotIn("enableFileHooks", source)
                self.assertNotIn("model=", source)
                self.assertNotIn('model: "', source)
                self.assertNotIn("model: '", source)

    def test_premature_model_report_is_rejected_by_both_actual_clients(self):
        for language in ("ts", "py"):
            with self.subTest(language=language):
                result, _ = self.run_port(language, "old-model-timing")
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("Model report preceded observer readiness.", result.stdout + result.stderr)
                self.assertNotIn("live subscription installed", result.stdout)

    def test_actual_sdk_failure_races_and_original_assertions(self):
        failures = {
            "no-tool": "Late join timed out while waiting for wait_for_join",
            "early": "The run ended before joining while waiting for wait_for_join",
            "error": "mock original model failure",
            "missing-user": "actual UserMessageEvent",
            "stalled-read": "Late join timed out while reading the SDK snapshot",
            "mid-join-error": "mock original model failure",
            "no-delta": "the late joiner received live deltas",
            "empty-answer": "the answer is not empty",
            "no-answer": "the run finished",
            "no-idle": "Late join timed out while awaiting the completed counting answer",
        }
        for language in ("ts", "py"):
            for scenario, diagnostic in failures.items():
                with self.subTest(language=language, scenario=scenario):
                    result, _ = self.run_port(language, scenario)
                    self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn(diagnostic, result.stdout + result.stderr)
                    if scenario in ("error", "mid-join-error"):
                        self.assertNotIn("Error: The run ended before joining", result.stderr)
                    if scenario in ("missing-user", "mid-join-error"):
                        self.assertNotIn("live subscription installed", result.stdout)
                        self.assertNotIn("event text delta:", result.stdout)

    def test_actual_sdk_handler_cleanup_without_success_after_cancellation(self):
        mutations = {
            "ts": ("await until(release, signal);",
                   'try { await until(release, signal); }\n'
                   '    finally { console.error("actual SDK handler exited"); }'),
            "py": ("await asyncio.wait_for(release.wait(), timeout=max(0, expires - loop.time()))",
                   'try:\n'
                   '            await asyncio.wait_for(release.wait(), timeout=max(0, expires - loop.time()))\n'
                   '        finally:\n'
                   '            print("actual SDK handler exited", file=sys.stderr)'),
        }
        for language in ("ts", "py"):
            for scenario in ("missing-user", "mid-join-error"):
                with self.subTest(language=language, scenario=scenario):
                    result, artifacts = self.run_port(language, scenario, mutations[language])
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("actual SDK handler exited", result.stderr)
                    self.assertNotIn("The client has joined.", artifacts.get("trace-" + scenario, ""))

    def test_old_delay_and_early_release_source_mutations_are_red(self):
        for language in ("ts", "py"):
            if language == "ts":
                delay = ('await requirePendingRun(started, deadline.signal, "waiting for wait_for_join");',
                         "await new Promise(resolve => setTimeout(resolve, 1500));")
                gap = ("const read = root.contextAsync();", "releaseJoin();\n      const read = root.contextAsync();")
            else:
                delay = ('await require_pending_run(started.wait(), "waiting for wait_for_join")',
                         "await asyncio.sleep(1.5)")
                gap = ("read = asyncio.create_task(root.context_async())",
                       "release.set()\n                read = asyncio.create_task(root.context_async())")
            for scenario, mutation in (("early", delay), ("gap", gap)):
                with self.subTest(language=language, scenario=scenario):
                    result, _ = self.run_port(language, scenario, mutation)
                    self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertTrue("The run ended before joining" in result.stderr
                                    or "the late joiner received live deltas" in result.stderr,
                                    result.stdout + result.stderr)
                    self.assertNotIn("event text delta:", result.stdout)


if __name__ == "__main__":
    unittest.main()
