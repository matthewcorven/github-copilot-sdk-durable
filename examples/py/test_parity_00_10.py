"""Range-local inventory and assertion-coverage gates, not runtime parity evidence."""
import ast
import json
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
NAMES = (
    "00-conversation", "01-documents", "02-forks", "03-owned-conversations",
    "04-watches", "05-harness", "06-configuration", "07-harness-conversations",
    "08-context", "09-registry-reload", "10-extension-state",
)
EXCLUSIONS = {
    "02": "needs sessions.fork, which the runtime does not answer",
    "07": "sets a reasoning effort, so the harness queries models.list for a model the fixture does not record",
    "08": "needs sessions.fork, which the runtime does not answer",
    "10": "asserts a tool wrote into the document; the recording has no tool call",
}


def assertion_messages(path):
    text = path.read_text()
    if path.suffix == ".py":
        return [
            node.args[-1].value
            for node in ast.walk(ast.parse(text))
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
            and isinstance(node.func.value, ast.Name) and node.func.value.id == "Check"
            and node.args and isinstance(node.args[-1], ast.Constant)
            and isinstance(node.args[-1].value, str)
        ]
    return re.findall(r'Check\.\w+\([^\n]*,\s*"([^"]+)"\)', text)


def verify_assertion_coverage(test, language, suffix):
    for name in NAMES:
        original = assertion_messages(ROOT / "examples" / f"{name}.cs")
        port = assertion_messages(ROOT / "examples" / language / f"{name}.{suffix}")
        test.assertTrue(original, f"{name}: original assertion inventory must not be empty")
        for message in original:
            test.assertIn(message, port, f"{language}/{name}: missing original assertion: {message}")
            test.assertGreaterEqual(port.count(message), original.count(message), f"{name}: lost repeated assertion")


class RangeParityTests(unittest.TestCase):
    def test_all_original_assertions_are_present(self):
        for language, suffix in (("ts", "ts"), ("py", "py")):
            with self.subTest(language=language):
                verify_assertion_coverage(self, language, suffix)

    def test_manifest_inventory_and_exact_exclusions(self):
        script = (ROOT / "scripts" / "validate.sh").read_text()
        live = re.search(r"LIVE_EXAMPLES=\(([^)]+)\)", script).group(1).split()
        for language, suffix in (("ts", "ts"), ("py", "py")):
            manifest = json.loads((ROOT / "examples" / language / "parity-00-10.json").read_text())
            self.assertEqual([f"{name}.{suffix}" for name in NAMES], [e["file"] for e in manifest["examples"]])
            for entry in manifest["examples"]:
                self.assertEqual(entry["id"] in live, entry["live"], entry["file"])
                expected = EXCLUSIONS.get(entry["id"])
                self.assertEqual(expected, entry["deterministicExclusionReason"], entry["file"])
                self.assertEqual(expected is None, entry["deterministic"], entry["file"])
                self.assertEqual(expected if entry["live"] else None, entry["liveReason"], entry["file"])
                if expected:
                    self.assertIn(expected, script)
                self.assertTrue((ROOT / "examples" / language / entry["file"]).is_file())
                self.assertTrue((ROOT / "examples" / language / entry["original"]).is_file())

    def test_python_assertion_failures_exit_nonzero(self):
        result = subprocess.run(
            ["python3", "-O", "-c", "from check import Check; Check.equal('expected', 'regressed', 'offending document value')"],
            cwd=ROOT / "examples" / "py", capture_output=True, text=True,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("offending document value", result.stderr)
        self.assertIn("regressed", result.stderr)

    def test_inventory_gate_rejects_removed_real_assertions(self):
        for language, suffix in (("ts", "ts"), ("py", "py")):
            path = ROOT / "examples" / language / f"00-conversation.{suffix}"
            source = path.read_text()
            mutated = "".join(
                line for line in source.splitlines(keepends=True)
                if "a failed transaction writes nothing" not in line
            )
            self.assertNotEqual(source, mutated)
            try:
                path.write_text(mutated)
                with self.assertRaisesRegex(AssertionError, "a failed transaction writes nothing"):
                    verify_assertion_coverage(self, language, suffix)
            finally:
                path.write_text(source)

    def test_typescript_assertion_failures_exit_nonzero(self):
        result = subprocess.run(
            ["node", "--experimental-transform-types", "--input-type=module", "-e",
             "import {Check} from './check.ts'; Check.equal('expected', 'regressed', 'offending document value');"],
            cwd=ROOT / "examples" / "ts", capture_output=True, text=True,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertIn("offending document value", result.stderr)
        self.assertIn("regressed", result.stderr)


if __name__ == "__main__":
    unittest.main()
