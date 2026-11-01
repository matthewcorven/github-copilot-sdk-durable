import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "examples"))
from run_all import load_manifest, normalise_output


@contextmanager
def isolated_contract():
    with tempfile.TemporaryDirectory(prefix="parity-mutation-") as directory:
        root = Path(directory)
        ignore = shutil.ignore_patterns("node_modules", ".venv", "bin", "obj", "__pycache__")
        for path in ("examples", "tests/parity", "lib/ts", "lib/py"):
            shutil.copytree(ROOT / path, root / path, ignore=ignore)
        (root / "scripts").mkdir()
        shutil.copy2(ROOT / "scripts/validate.sh", root / "scripts/validate.sh")
        (root / "tools").mkdir()
        (root / "tools/ReplayRuntime").symlink_to(ROOT / "tools/ReplayRuntime", target_is_directory=True)
        for path in ("lib/ts/node_modules", "examples/ts/node_modules"):
            (root / path).symlink_to(ROOT / path, target_is_directory=True)
        yield root


def execute(root, language, *args):
    command = (["node", str(root / "examples/run_all.mjs")] if language == "ts"
               else [sys.executable, str(root / "examples/run_all.py")])
    env = os.environ.copy()
    for name in ("DURABLE_RUNTIME_PATH", "DURABLE_EXAMPLE_FIXTURE", "DURABLE_EXAMPLE_REPLAY_FIXTURE"):
        env.pop(name, None)
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    return subprocess.run(command + list(args), cwd=root, env=env, capture_output=True, text=True, timeout=60)


class RunnerContractTests(unittest.TestCase):
    def test_normalization_preserves_meaning_and_matches_node(self):
        text = ("c17 e32 t4 s0123456789abcdef0123456789abcdef\r\n"
                "01234567-89ab-cdef-0123-456789abcdef 2026-11-01T14:30:00.123-05:00\n"
                "/private/var/folders/ab/123/T/work/file.txt\n"
                "C:\\Users\\tester\\AppData\\Local\\Temp\\work\\file.txt\n"
                "Pending True count: 2 receipt: visa3\n")
        expected = "<id> <id> <id> <session>\n<guid> <timestamp>\n<temp>\n<temp>\nPending True count: 2 receipt: visa3\n"
        self.assertEqual(normalise_output(text), expected)
        result = subprocess.run(["node", "--input-type=module", "-e",
            "import {normaliseOutput} from './examples/run_all.mjs';"
            "process.stdout.write(normaliseOutput(process.argv[1]));", text],
            cwd=ROOT, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, expected)
        self.assertNotEqual(normalise_output("count: 3\n"), normalise_output("count: 2\n"))

    def test_exact_inventory_selection_and_four_csharp_outputs(self):
        manifest = load_manifest()
        self.assertEqual(len(manifest["examples"]), 36)
        selected = [entry["id"] for entry in manifest["examples"] if entry["deterministic"]]
        self.assertEqual(selected, ["00", "01", "03", "04", "05", "06", "09", "11", "12", "14", "17", "18", "23", "32", "35"])
        for language in ("ts", "py"):
            result = execute(ROOT, language, "--deterministic", "--list")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual([line.split()[1] for line in result.stdout.splitlines() if line.startswith("SELECT ")], selected)
            for entry in manifest["examples"]:
                if not entry["deterministic"]:
                    self.assertIn(f"EXCLUDE {entry['id']} {entry['source']}: {entry['liveReason']}", result.stdout)

    def test_missing_actual_inventory_fails_both_runners(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                (root / f"examples/{language}/01-documents.{language}").unlink()
                result = execute(root, language, "--list")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Missing example", result.stderr)

    def test_removed_global_entry_fails_both_runners(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                path = root / "examples/parity.json"
                manifest = json.loads(path.read_text())
                manifest["examples"].pop()
                path.write_text(json.dumps(manifest))
                result = execute(root, language, "--list")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("FAIL parity contract", result.stderr)

    def test_invalid_boolean_or_timeout_shape_fails_inventory_validation(self):
        for language in ("ts", "py"):
            for field in ("live", "timeoutSeconds"):
                with self.subTest(language=language, field=field), isolated_contract() as root:
                    global_path = root / "examples/parity.json"
                    manifest = json.loads(global_path.read_text())
                    if field == "live":
                        manifest["examples"][0]["live"] = 0
                    else:
                        row = manifest["examples"][0]["languages"][language]
                        row["evidence"]["timeoutSeconds"] = 0
                        range_path = root / row["rangeManifest"]
                        declaration = json.loads(range_path.read_text())
                        declaration["examples"][0] = row["evidence"]
                        range_path.write_text(json.dumps(declaration))
                    global_path.write_text(json.dumps(manifest))
                    result = execute(root, language, "--list")
                    self.assertNotEqual(result.returncode, 0, f"Invalid {field} passed inventory validation")
                    self.assertIn("FAIL parity contract", result.stderr)

    def test_real_example_assertion_failure_exits_nonzero(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                file = root / f"examples/{language}/00-conversation.{language}"
                original = file.read_text()
                self.assertIn("a conversation has an id", original)
                if language == "ts":
                    source = original.replace("standalone.id.length > 0", "standalone.id.length < 0")
                else:
                    source = original.replace("len(standalone.id) > 0", "len(standalone.id) < 0")
                self.assertNotEqual(original, source, "mutation must change the real example assertion")
                file.write_text(source)
                result = execute(root, language, "--deterministic", "00")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("a conversation has an id", result.stderr)
                self.assertIn("FAIL 00", result.stderr)

    def test_real_example_output_delta_fails_comparison(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                file = root / f"examples/{language}/00-conversation.{language}"
                with file.open("a") as handle:
                    handle.write('\nconsole.log("incorrect inventory count: 99");\n' if language == "ts"
                                 else '\nprint("incorrect inventory count: 99")\n')
                result = execute(root, language, "--deterministic", "00")
                self.assertNotEqual(result.returncode, 0, "Runner accepted mismatched actual example output")
                self.assertIn("actual C# output mismatch", result.stderr)
                self.assertIn("incorrect inventory count: 99", result.stderr)

    def test_wrong_checked_in_output_fails_even_with_updated_checksum(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                output = root / "examples/expected/00-conversation.txt"
                output.write_text(output.read_text().replace("conversation count: 2", "conversation count: 99"))
                path = root / "examples/expected/provenance.json"
                proof = json.loads(path.read_text())
                proof["outputs"]["00"]["expectedSha256"] = hashlib.sha256(output.read_bytes()).hexdigest()
                path.write_text(json.dumps(proof))
                result = execute(root, language, "--deterministic", "00")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("actual C# output mismatch", result.stderr)

    def test_pending_api_is_a_failure_not_an_exclusion_or_pass(self):
        for language in ("ts", "py"):
            with self.subTest(language=language), isolated_contract() as root:
                global_path = root / "examples/parity.json"
                manifest = json.loads(global_path.read_text())
                row = manifest["examples"][0]["languages"][language]
                row["status"] = row["evidence"]["status"] = "pending-native-api"
                row["evidence"]["pendingReason"] = "missing native API regression"
                range_path = root / row["rangeManifest"]
                declaration = json.loads(range_path.read_text())
                declaration["examples"][0] = row["evidence"]
                range_path.write_text(json.dumps(declaration))
                global_path.write_text(json.dumps(manifest))
                result = execute(root, language, "--deterministic", "00")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("FAIL 00", result.stderr)
                self.assertIn("pending-native-api", result.stderr)
                self.assertNotIn("PASS 00", result.stdout)

    def test_all_available_does_not_silently_replay_live_requirements(self):
        for language in ("ts", "py"):
            with self.subTest(language=language):
                command = (["node", "examples/run_all.mjs"] if language == "ts"
                           else [sys.executable, "examples/run_all.py"])
                result = subprocess.run(command + ["--all-available", "13"], cwd=ROOT,
                    env={**os.environ, "DURABLE_RUNTIME_PATH": "/not-native"},
                    capture_output=True, text=True, timeout=30)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("All-available requires the native runtime", result.stderr)

    def test_deterministic_mode_cannot_return_success_for_excluded_requested_ids(self):
        for language in ("ts", "py"):
            with self.subTest(language=language):
                result = execute(ROOT, language, "--deterministic", "02")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("excluded from deterministic mode", result.stderr)


if __name__ == "__main__":
    unittest.main()
