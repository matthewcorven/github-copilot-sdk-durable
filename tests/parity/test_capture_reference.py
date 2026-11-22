"""Exercise the actual capture CLI against isolated source/deployment trees."""

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import contextmanager
from pathlib import Path

from capture_reference import ROOT, REFERENCE_IDS, sha256

RUNTIME = Path("tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime")
FIXTURE = Path("tools/ReplayRuntime/fixtures/basic.json")


@contextmanager
def capture_tree():
    with tempfile.TemporaryDirectory(prefix="reference-capture-") as directory:
        root = Path(directory).resolve()
        (root / "tests/parity").mkdir(parents=True)
        shutil.copy2(ROOT / "tests/parity/capture_reference.py", root / "tests/parity")
        (root / "examples").mkdir()
        for name in ("run_all.py", "parity_manifest.py"):
            shutil.copy2(ROOT / "examples" / name, root / "examples")
        for identifier in REFERENCE_IDS:
            source, = (ROOT / "examples").glob(f"{identifier}-*.cs")
            shutil.copy2(source, root / "examples")
        shutil.copytree(ROOT / "examples/expected", root / "examples/expected")
        (root / "lib").symlink_to(ROOT / "lib", target_is_directory=True)
        (root / FIXTURE).parent.mkdir(parents=True)
        shutil.copy2(ROOT / FIXTURE, root / FIXTURE)
        shutil.copytree((ROOT / RUNTIME).parent, (root / RUNTIME).parent)
        yield root


def artifacts(root):
    return {path.name: path.read_bytes() for path in (root / "examples/expected").iterdir()
            if path.is_file()}


def capture(root, *args):
    # File-based apps key their build cache by source path, so a fresh temporary tree would
    # otherwise mint a full ~545 MB artifact directory in the per-user runfile cache per case
    # (mostly a copy of the Copilot CLI bundle). Build output belongs inside the throwaway tree.
    return subprocess.run(
        [sys.executable, "tests/parity/capture_reference.py", *args,
         "--artifacts-path", str(root / "artifacts")],
        cwd=root,
        env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1",
             "DURABLE_RUNTIME_PATH": "/must-not-select-this-runtime",
             "DURABLE_EXAMPLE_FIXTURE": "/not-a-supported-csharp-fixture",
             "DURABLE_EXAMPLE_REPLAY_FIXTURE": "/not-a-supported-csharp-fixture"},
        capture_output=True, text=True, timeout=600,
    )


class CaptureReferenceTests(unittest.TestCase):
    def test_stale_deployment_cannot_bless_new_source_fixture(self):
        for args in (("--write",), ()):
            with self.subTest(args=args), capture_tree() as root:
                fixture = root / FIXTURE
                fixture.write_text(fixture.read_text().replace("replay-model", "changed-source-model"))
                source_hash = sha256(fixture)
                deployed = (root / RUNTIME).parent / "fixtures/basic.json"
                deployed_hash = sha256(deployed)
                self.assertNotEqual(source_hash, deployed_hash)
                before = artifacts(root)
                result = capture(root, *args)
                self.assertEqual(artifacts(root), before, result.stdout + result.stderr)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("Deployed ReplayRuntime fixture differs from source", result.stderr)
                for detail in (str(fixture), str(deployed), source_hash, deployed_hash):
                    self.assertIn(detail, result.stderr)
                self.assertNotIn("Actual C# output verified:", result.stdout)

    def test_artifacts_path_keeps_build_output_inside_the_supplied_directory(self):
        # A file-based app's build output is keyed by source path, so a throwaway tree would
        # otherwise mint a full artifact directory in the per-user runfile cache for every
        # case. The option must actually redirect the build, not merely be accepted.
        with capture_tree() as root:
            result = capture(root, "--write")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("Actual C# output verified: 04-watches.cs", result.stdout)
            self.assertTrue((root / "artifacts" / "bin").is_dir(),
                            "the supplied artifacts path must receive the build output")

    def test_missing_or_invalid_inputs_fail_before_execution_or_writes(self):
        for kind in ("runtime", "source", "deployed", "non-executable", "directory"):
            for args in (("--write",), ()):
                with self.subTest(kind=kind, args=args), capture_tree() as root:
                    runtime = root / RUNTIME
                    source = root / FIXTURE
                    deployed = runtime.parent / "fixtures/basic.json"
                    if kind == "non-executable":
                        runtime.chmod(0o644)
                        bad_path = runtime
                    else:
                        bad_path = {"runtime": runtime, "source": source, "deployed": deployed,
                                    "directory": deployed}[kind]
                        bad_path.unlink()
                        if kind == "directory":
                            bad_path.mkdir()
                    before = artifacts(root)
                    result = capture(root, *args)
                    self.assertEqual(artifacts(root), before, result.stdout + result.stderr)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn(str(bad_path), result.stderr)
                    self.assertNotIn("Actual C# output verified:", result.stdout)

    def test_actual_csharp_references_match_and_capture_at_resolved_runtime(self):
        for linked in (False, True):
            with self.subTest(linked=linked), capture_tree() as root:
                runtime = root / RUNTIME
                if linked:
                    deployment = root / "relocated-runtime"
                    runtime.parent.rename(deployment)
                    runtime.parent.mkdir()
                    runtime.symlink_to(os.path.relpath(deployment / runtime.name, runtime.parent))
                    # This decoy is beside the link, not the executed apphost.
                    (runtime.parent / "fixtures").mkdir()
                    (runtime.parent / "fixtures/basic.json").write_text("{}")
                deployed = runtime.resolve().parent / "fixtures/basic.json"
                self.assertEqual(sha256(root / FIXTURE), sha256(deployed))
                before = artifacts(root)
                for args in (("--write",), ()):
                    result = capture(root, *args)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual(artifacts(root), before)
                    for identifier in REFERENCE_IDS:
                        self.assertRegex(result.stdout, f"Actual C# output verified: {identifier}-")
                    proof = json.loads((root / "examples/expected/provenance.json").read_text())
                    self.assertEqual(proof["fixtureSha256"], sha256(deployed))
                    self.assertEqual(sorted(proof["outputs"]), sorted(REFERENCE_IDS))


class CaptureGuardMutationTests(unittest.TestCase):
    def test_removed_or_source_only_guards_fail_actual_capture_regression(self):
        mutations = (
            ("if deployed_hash != source_hash:", "if False:"),
            ('deployed = runtime.parent / "fixtures/basic.json"',
             'deployed = source'),
            ("fixture_hash = reference_fixture_sha256(runtime)",
             'fixture_hash = sha256(ROOT / "tools/ReplayRuntime/fixtures/basic.json")'),
        )
        for original, replacement in mutations:
            with self.subTest(mutation=replacement), capture_tree() as root:
                script = root / "tests/parity/capture_reference.py"
                text = script.read_text()
                self.assertEqual(text.count(original), 1)
                script.write_text(text.replace(original, replacement))
                shutil.copy2(Path(__file__), root / "tests/parity")
                result = subprocess.run(
                    [sys.executable, "-m", "unittest", "discover", "-s", "tests/parity",
                     "-p", "test_capture_reference.py", "-k",
                     "test_stale_deployment_cannot_bless_new_source_fixture", "-v"],
                    cwd=root, capture_output=True, text=True, timeout=600,
                    env={**os.environ, "PYTHONDONTWRITEBYTECODE": "1"},
                )
                self.assertNotEqual(result.returncode, 0, "Capture accepted an unexecuted fixture hash")
                self.assertIn("Actual C# output verified: 04-watches.cs", result.stderr)
                self.assertIn("AssertionError", result.stderr)

    def test_example_stdout_carrying_a_warning_fails_the_clean_output_guard(self):
        # The clean-output guard has no other direct test: the mutations above only exercise the
        # deployment guards. A build warning is not example stdout, so capture must refuse the
        # run rather than record the warning as part of the reference.
        with capture_tree() as root:
            source = next((root / "examples").glob("00-*.cs"))
            source.write_text(source.read_text() + '\nConsole.WriteLine("probe: warning NU1903 from the example");\n')
            before = artifacts(root)
            result = capture(root)
            self.assertEqual(artifacts(root), before, result.stdout + result.stderr)
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn(f"Reference capture is not clean example stdout: {source.name}", result.stderr)
            self.assertNotIn("Actual C# output verified:", result.stdout)


if __name__ == "__main__":
    unittest.main()
