"""Range-local inventory and assertion-coverage gates, not runtime parity evidence."""
import ast
import asyncio
import json
import os
import re
import subprocess
import sys
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
TODO_FIXTURE = ROOT / "examples" / "ts" / "fixtures" / "10-extension-state.json"
GENERIC_ASSERTIONS = (
    ("00-conversation", "a standalone conversation has no parent"),
    ("00-conversation", "a standalone conversation has no owner task"),
    ("03-owned-conversations", "the child has no owning conversation"),
)


def run_native_todo(language, fixture):
    command = (
        ["node", "--experimental-transform-types", "examples/ts/10-extension-state.ts"]
        if language == "ts" else [sys.executable, "examples/py/10-extension-state.py"]
    )
    env = dict(os.environ, DURABLE_EXAMPLE_REPLAY_FIXTURE=str(fixture))
    try:
        return subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True, timeout=30)
    except subprocess.TimeoutExpired as error:
        raise AssertionError(
            f"{language} native todo replay timed out: stdout={error.stdout!r}; stderr={error.stderr!r}"
        ) from error


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
    return re.findall(r'Check\.\w+(?:<[^>\n]+>)?\([^\n]*,\s*"([^"]+)"\)', text)


def verify_assertion_coverage(test, language, suffix):
    for name in NAMES:
        original = assertion_messages(ROOT / "examples" / f"{name}.cs")
        port = assertion_messages(ROOT / "examples" / language / f"{name}.{suffix}")
        test.assertTrue(original, f"{name}: original assertion inventory must not be empty")
        for message in original:
            test.assertIn(message, port, f"{language}/{name}: missing original assertion: {message}")
            test.assertGreaterEqual(port.count(message), original.count(message), f"{name}: lost repeated assertion")


class RangeParityTests(unittest.TestCase):
    def test_generic_original_assertions_are_in_inventory(self):
        for name, message in GENERIC_ASSERTIONS:
            with self.subTest(example=name, message=message):
                self.assertIn(message, assertion_messages(ROOT / "examples" / f"{name}.cs"))

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
                self.assertEqual(expected, entry["liveReason"], entry["file"])
                self.assertEqual(entry["file"], entry["path"])
                self.assertEqual(Path(entry["original"]).name, entry["source"])
                self.assertIn(entry["status"], ("ported", "pending-native-api"))
                self.assertEqual("not-applicable", entry["sandboxMode"])
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

    def test_inventory_gate_rejects_removed_generic_reference_assertions(self):
        for name, message in GENERIC_ASSERTIONS:
            for language, suffix in (("ts", "ts"), ("py", "py")):
                with self.subTest(example=name, language=language, message=message):
                    path = ROOT / "examples" / language / f"{name}.{suffix}"
                    source = path.read_text()
                    self.assertEqual(1, source.count(message))
                    mutated = "".join(
                        line for line in source.splitlines(keepends=True) if message not in line
                    )
                    self.assertNotEqual(source, mutated)
                    try:
                        path.write_text(mutated)
                        with self.assertRaisesRegex(AssertionError, re.escape(message)):
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

    def test_python_native_tool_schema_and_handler(self):
        from copilot.tools import ToolInvocation
        from tools import file_tool

        for name, argument in (("read", "path"), ("write", "path"), ("grep", "pattern")):
            tool = file_tool(name, f"{name} description")
            self.assertEqual(name, tool.name)
            self.assertEqual(f"{name} description", tool.description)
            self.assertTrue(tool.overrides_built_in_tool)
            self.assertIn(argument, tool.parameters["required"])
            self.assertEqual("string", tool.parameters["properties"][argument]["type"])
            result = asyncio.run(tool.handler(ToolInvocation(
                tool_name=name, arguments={argument: "actual-handler-argument"},
            )))
            self.assertEqual("success", result.result_type)
            self.assertEqual(f"{name} actual-handler-argument", result.text_result_for_llm)

    def test_typescript_native_tool_schema_and_handler(self):
        result = subprocess.run(
            ["node", "--experimental-transform-types", "--input-type=module", "-e",
             """import {fileTool} from './tools.ts';
const results = [];
for (const [name, argument] of [['read', 'path'], ['write', 'path'], ['grep', 'pattern']]) {
  const tool = fileTool(name, `${name} description`);
  const args = {[argument]: 'actual-handler-argument'};
  results.push({name: tool.name, description: tool.description,
    overrides: tool.overridesBuiltInTool, schema: tool.parameters,
    output: await tool.handler(args, {toolName: name, arguments: args})});
}
console.log(JSON.stringify(results));"""],
            cwd=ROOT / "examples" / "ts", capture_output=True, text=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        for tool, (name, argument) in zip(json.loads(result.stdout), (("read", "path"), ("write", "path"), ("grep", "pattern")), strict=True):
            self.assertEqual(name, tool["name"])
            self.assertEqual(f"{name} description", tool["description"])
            self.assertTrue(tool["overrides"])
            self.assertIn(argument, tool["schema"]["required"])
            self.assertEqual("string", tool["schema"]["properties"][argument]["type"])
            self.assertEqual(f"{name} actual-handler-argument", tool["output"])

    @unittest.skipUnless(os.environ.get("DURABLE_RUNTIME_PATH"), "requires the real ReplayRuntime executable")
    def test_todo_replay_requires_real_tool_state(self):
        expected_model = json.loads(TODO_FIXTURE.read_text())["sessions"][0]["model"]
        for language in ("ts", "py"):
            with self.subTest(language=language):
                positive = run_native_todo(language, TODO_FIXTURE)
                self.assertEqual(0, positive.returncode, positive.stdout + positive.stderr)
                self.assertIn(f"effective model: {expected_model}", positive.stdout)
                self.assertIn("Scripted confirmation", positive.stdout)
                # A plausible answer with no tool call must not pass the document gate.
                negative = run_native_todo(language, ROOT / "tools" / "ReplayRuntime" / "fixtures" / "basic.json")
                self.assertNotEqual(0, negative.returncode)
                self.assertIn("the tool wrote the todo into the document", negative.stderr)

    @unittest.skipUnless(os.environ.get("DURABLE_RUNTIME_PATH"), "requires the real ReplayRuntime executable")
    def test_todo_gate_rejects_actual_handler_mutation(self):
        for language, old, new in (
            ("ts", "draft.value.push(item);", "void item;"),
            ("py", "draft.value.append(params.item)", "pass"),
        ):
            suffix = "ts" if language == "ts" else "py"
            path = ROOT / "examples" / language / f"10-extension-state.{suffix}"
            source = path.read_text()
            self.assertEqual(1, source.count(old))
            try:
                path.write_text(source.replace(old, new, 1))
                negative = run_native_todo(language, TODO_FIXTURE)
                self.assertNotEqual(0, negative.returncode)
                self.assertIn("the tool wrote the todo into the document", negative.stderr)
            finally:
                path.write_text(source)


if __name__ == "__main__":
    unittest.main()
