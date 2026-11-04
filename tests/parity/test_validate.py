"""The validator's own example lists and README instructions, checked against examples/.

`SANDBOX_EXAMPLES` is a hand-maintained list beside `LIVE_EXAMPLES`, and a README snippet that
names examples by a form the script no longer accepts breaks silently: an argument that does not
match a file is reported `MISSING`, so nothing points at the snippet. These tests read the script
and the examples rather than trusting either list.
"""

import os
import re
import subprocess
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
VALIDATE = ROOT / "scripts" / "validate.sh"
README = ROOT / "README.md"
EXAMPLES = ROOT / "examples"


def script_list(name):
    """The ids in a `NAME=(...)` array in validate.sh, assert the array is there."""
    match = re.search(rf"^{name}=\((.*)\)\s*$", VALIDATE.read_text(), re.MULTILINE)
    if match is None:
        raise AssertionError(f"scripts/validate.sh no longer declares {name}=(...)")
    return match.group(1).split()


def help_text():
    result = subprocess.run(["bash", str(VALIDATE), "--help"], cwd=ROOT,
                            capture_output=True, text=True, timeout=60)
    if result.returncode != 0:
        raise AssertionError(f"--help exited {result.returncode}:\n{result.stderr}")
    return result.stdout


class ScriptListTests(unittest.TestCase):
    def test_every_sandbox_example_is_also_a_live_example(self):
        # SKIP_LIVE=1 says it covers the sandbox examples, which holds only while they are a
        # subset of LIVE_EXAMPLES. A sandbox example missing from LIVE_EXAMPLES would also run
        # under SKIP_LIVE=1, which is the opposite of what both lists promise.
        live = set(script_list("LIVE_EXAMPLES"))
        sandbox = set(script_list("SANDBOX_EXAMPLES"))
        self.assertEqual(sorted(sandbox - live), [],
                         "SANDBOX_EXAMPLES must be a subset of LIVE_EXAMPLES")

    def test_every_sandbox_example_asserts_host_sandbox_enforcement(self):
        # The list is described as "examples that assert the host's sandbox enforced something
        # at the OS level", so each one must call the sandbox support surface. 28 is a
        # document-per-conversation example with a temp directory and no SandboxConfig; adding
        # its id here made SKIP_SANDBOX=1 skip an example that needs no sandbox backend.
        for number in script_list("SANDBOX_EXAMPLES"):
            with self.subTest(example=number):
                matches = sorted(EXAMPLES.glob(f"{number}-*.cs"))
                self.assertEqual(len(matches), 1,
                                 f"SANDBOX_EXAMPLES names {number}, but no single examples/{number}-*.cs exists")
                source = matches[0].read_text()
                self.assertGreater(source.count("SandboxSupport"), 0,
                                   f"{matches[0].name} is in SANDBOX_EXAMPLES but never calls SandboxSupport, "
                                   "so it does not assert host sandbox enforcement")

    def test_readme_validate_invocations_name_existing_examples(self):
        # The script resolves an argument to examples/<argument>.cs. A stale numbered form is
        # reported MISSING rather than rejected, so only reading the snippet finds it.
        readme = README.read_text()
        for command in re.findall(r"^\./scripts/validate\.sh(.*)$", readme, re.MULTILINE):
            arguments = [argument for argument in command.split() if not argument.startswith(("-", "$"))]
            for argument in arguments:
                with self.subTest(argument=argument):
                    self.assertTrue((EXAMPLES / f"{argument}.cs").is_file(),
                                    f"README runs `scripts/validate.sh {argument}`, but examples/{argument}.cs does not exist")

    def test_help_documents_both_skips_without_leaking_the_shebang(self):
        text = help_text()
        self.assertIn("SKIP_LIVE=1", text)
        self.assertIn("SKIP_SANDBOX=1", text)
        self.assertNotIn("set -uo pipefail", text,
                         "--help must print the header comment only, not the script body")


@unittest.skipIf(sys.platform == "win32", "validate.sh is only invoked on the POSIX CI hosts")
class SandboxSkipBehaviourTests(unittest.TestCase):
    def test_skip_sandbox_reports_a_skip_instead_of_running_the_example(self):
        # 33 needs a sandbox backend, so this is the one invocation that proves both halves
        # without a backend: it must skip rather than compile, run, or report MISSING.
        # SKIP_TESTS=1 is required, or the script runs dotnet test first.
        environment = dict(os.environ, SKIP_TESTS="1", SKIP_SANDBOX="1")
        result = subprocess.run(["bash", str(VALIDATE), "33-sandbox-sdk-filesystem"],
                                cwd=ROOT, env=environment, capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("SKIP     33-sandbox-sdk-filesystem.cs", result.stdout)
        self.assertIn("needs a sandbox backend", result.stdout)


if __name__ == "__main__":
    unittest.main()
