"""Targeted standalone and mutation checks: python3 examples/py/parity_test_11_21.py."""
import subprocess
import sys
import unittest
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class DeterministicParityTests(unittest.TestCase):
    def run_example(self, path, mutation=None):
        if mutation is None:
            command = (["node", "--experimental-transform-types"] if path.suffix == ".ts"
                       else [sys.executable]) + [str(path)]
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
        return subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=15)

    def test_tasks_standalone(self):
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                result = self.run_example(ROOT / "examples" / language / f"11-tasks.{suffix}")
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn("receipt: 500; payments charged: 1", result.stdout)

    def test_recovery_standalone(self):
        result = self.run_example(ROOT / "examples/py/12-recovery.py")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("attempts: [1, 2, 2, 3, 4, 5]; printed: [1, 2, 3, 4, 5]", result.stdout)

    def test_failed_payment_assertion_exits_nonzero(self):
        result = self.run_example(ROOT / "examples/py/11-tasks.py",
                                  ('Check.equal(500, paid.result["receipt"]',
                                   'Check.equal(501, paid.result["receipt"]'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: the receipt holds the amount", result.stderr)

    def test_missing_memo_repeats_effect_and_fails(self):
        result = self.run_example(ROOT / "examples/py/12-recovery.py",
                                  ('if await runtime.memo_async(f"printed-{n}") is None:',
                                   'if True:'))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: no tick was printed twice, and none was skipped", result.stderr)
        self.assertIn("found [1, 2, 2, 3, 4, 5]", result.stderr)

    def test_failed_typescript_payment_assertion_exits_nonzero(self):
        path = ROOT / "examples/ts/11-tasks.ts"
        script = (
            "const fs = require('node:fs'); "
            f"const ts = require({json.dumps(str(ROOT / 'lib/ts/node_modules/typescript'))}); "
            f"let source = fs.readFileSync({json.dumps(str(path))}, 'utf8'); "
            "if (!source.includes('Check.equal(500, result')) throw new Error('missing mutation target'); "
            "source = source.replace('Check.equal(500, result', 'Check.equal(501, result'); "
            f"source = source.replace('../../lib/ts/durable.ts', {json.dumps((ROOT / 'lib/ts/durable.ts').as_uri())}); "
            "const code = ts.transpileModule(source, {compilerOptions: "
            "{target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext}}).outputText; "
            "import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'));"
        )
        result = subprocess.run(["node", "--experimental-transform-types", "-e", script],
                                cwd=ROOT, text=True, capture_output=True, timeout=15)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Assertion failed: the receipt holds the amount", result.stderr)

    def test_range_metadata_preserves_inventory_and_live_classification(self):
        stems = ["11-tasks", "12-recovery", "13-chat", "14-system-prompt", "15-real-model",
                 "16-coding-tools", "17-print", "18-json", "19-inbox", "20-late-join",
                 "21-subagent-foreground"]
        live = {13, 15, 16, 17, 18, 19, 20, 21}
        for language, suffix in [("ts", "ts"), ("py", "py")]:
            with self.subTest(language=language):
                metadata = json.loads((ROOT / "examples" / language / "parity-11-21.json").read_text())
                self.assertEqual([f"{stem}.{suffix}" for stem in stems],
                                 [item["file"] for item in metadata["examples"]])
                for item in metadata["examples"]:
                    number = int(item["file"][:2])
                    self.assertEqual(number in live, item["live"], item["file"])
                    if item["live"]:
                        self.assertTrue(item["liveReason"], item["file"])
                    if item["status"] == "implemented-core":
                        self.assertTrue((ROOT / "examples" / language / item["file"]).is_file())


if __name__ == "__main__":
    unittest.main()
