"""Targeted standalone and mutation checks: python3 examples/py/parity_test_11_21.py."""
import subprocess
import sys
import unittest
import json
import os
import ast
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT = Path(__file__).resolve().parents[2]


class DeterministicParityTests(unittest.TestCase):
    def run_example(self, path, mutation=None, env=None):
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
                f"let source = fs.readFileSync({json.dumps(str(path))}, 'utf8'); "
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
                       "from pathlib import Path; "
                       f"p = Path({str(path)!r}); "
                       "s = p.read_text(); "
                       f"s = s.replace({old!r}, {new!r}, 1); "
                       "exec(compile(s, str(p), 'exec'), "
                       "{'__name__': '__main__', '__file__': str(p)})"]
        return subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=15, env=env)

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


if __name__ == "__main__":
    unittest.main()
