#!/usr/bin/env python3
"""The checks `lib/result.py` makes, exercised as tests rather than trusted.

This band's whole subject is attribution, and the only thing standing between a local
analogue and a platform claim is `result.py`'s refusal. A guard nobody tests is a guard that
stops guarding the first time someone edits it, so each refusal below is a case that must
turn red.

Run: python3 faults/lib/test_result.py   (or via faults/run-all.sh, which runs it first)
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import platform as platform_module  # noqa: E402  - the local module, not the stdlib one
import result  # noqa: E402


def document(**overrides) -> dict:
    """A minimal honest result: a platform block, an application block, one behaviour."""
    doc = {
        "outcome": "something the application did",
        "platform": platform_module.block(
            touched=["no-replay"], observed="not exercised in this run", note="a stand-in, not Foundry"
        ),
        "application": {
            "outcome_detail": "the application did the thing",
            "observations": {"evidence": 1},
        },
    }
    doc.update(overrides)
    return doc


class Refusals(unittest.TestCase):
    def test_an_honest_document_is_accepted(self):
        self.assertEqual(result.validate(document()), [])

    def test_a_missing_platform_block_is_refused(self):
        doc = document()
        del doc["platform"]
        problems = result.validate(doc)
        self.assertTrue(any("'platform'" in problem for problem in problems), problems)

    def test_a_missing_application_block_is_refused(self):
        doc = document()
        del doc["application"]
        problems = result.validate(doc)
        self.assertTrue(any("'application'" in problem for problem in problems), problems)

    def test_an_empty_platform_answer_is_refused(self):
        doc = document()
        doc["platform"] = dict(doc["platform"], what_the_platform_did="")
        self.assertTrue(any("what_the_platform_did" in p for p in result.validate(doc)))

    def test_an_application_with_no_observation_is_refused(self):
        doc = document()
        doc["application"] = {"outcome_detail": "x", "observations": {}}
        self.assertTrue(any("observations" in p for p in result.validate(doc)))

    def test_reproduced_here_without_a_live_call_is_refused(self):
        """The failure mode this band exists to prevent, as a test."""
        doc = document()
        doc["platform"]["behaviours"][0]["status"] = "reproduced here"
        doc["platform"]["live_foundry_called"] = False
        problems = result.validate(doc)
        self.assertTrue(
            any("a local analogue is not the platform" in problem for problem in problems), problems
        )

    def test_reproduced_here_with_a_live_call_is_allowed(self):
        doc = document()
        doc["platform"]["behaviours"][0]["status"] = "reproduced here"
        doc["platform"]["live_foundry_called"] = True
        self.assertEqual(result.validate(doc), [])

    def test_a_third_status_is_refused(self):
        doc = document()
        doc["platform"]["behaviours"][0]["status"] = "observed"
        self.assertTrue(any("only two allowed" in p for p in result.validate(doc)))

    def test_missing_behaviours_are_refused(self):
        doc = document()
        doc["platform"]["behaviours"] = []
        self.assertTrue(any("four documented behaviours" in p for p in result.validate(doc)))

    def test_an_outcome_without_an_application_detail_is_refused(self):
        doc = document()
        doc["application"] = {"observations": {"a": 1}}
        self.assertTrue(any("outcome_detail" in p for p in result.validate(doc)))


class Paths(unittest.TestCase):
    """A checked-in result must not name one reader's home directory."""

    def test_the_repository_and_scratch_roots_become_tokens(self):
        text = (
            f"failed Access to the path '{result.SCRATCH_ROOT}/runs/durable-outside/x' is denied; "
            f"harness at {result.REPO_ROOT}"
        )
        normalised = result.normalise_paths(text)
        self.assertNotIn(result.REPO_ROOT, normalised)
        self.assertNotIn(result.SCRATCH_ROOT, normalised)
        self.assertIn("$FAULTS_SCRATCH/runs/durable-outside/x", normalised)
        self.assertIn("<repo>", normalised)

    def test_nested_text_is_normalised_and_other_values_untouched(self):
        document = {
            "application": {"observations": {"outcome_line": f"{result.REPO_ROOT}/a", "exit": 1}},
            "list": [f"{result.SCRATCH_ROOT}/b"],
        }
        normalised = result.normalise_paths(document)
        self.assertEqual(normalised["application"]["observations"]["outcome_line"], "<repo>/a")
        self.assertEqual(normalised["list"], ["$FAULTS_SCRATCH/b"])
        self.assertEqual(normalised["application"]["observations"]["exit"], 1)

    def test_the_normalisation_record_does_not_spell_the_roots(self):
        block = result._harness_block("store-outage", "durable-inside")
        written = json.dumps(block)
        self.assertNotIn(result.REPO_ROOT, written)
        self.assertNotIn(result.SCRATCH_ROOT, written)
        self.assertIn("<repo>", block["harness"]["paths_normalised"])

    def test_the_control_command_carries_the_flag(self):
        os.environ["FAULT_INJECT"] = "0"
        try:
            block = result._harness_block("store-outage", "durable-inside")
        finally:
            os.environ.pop("FAULT_INJECT", None)
        self.assertEqual(
            block["harness"]["command"],
            "./faults/store-outage.sh --variant durable-inside --fault-not-injected",
        )


class Behaviours(unittest.TestCase):
    def test_all_four_behaviours_are_present_and_documented_only(self):
        block = platform_module.block(touched=[], observed="nothing", note="nothing")
        ids = [entry["id"] for entry in block["behaviours"]]
        self.assertEqual(
            ids, ["session-persistence", "idle-timeout", "foreground-cancelled", "no-replay"]
        )
        statuses = {entry["status"] for entry in block["behaviours"]}
        self.assertEqual(statuses, {"documented, not exercised here"})
        self.assertFalse(block["live_foundry_called"])

    def test_only_the_touched_behaviour_is_marked(self):
        block = platform_module.block(touched=["idle-timeout"], observed="x", note="y")
        marked = [entry["id"] for entry in block["behaviours"] if entry["spoken_to_by_this_fault"]]
        self.assertEqual(marked, ["idle-timeout"])

    def test_an_unknown_behaviour_id_is_a_programming_error(self):
        with self.assertRaises(ValueError):
            platform_module.block(touched=["not-a-behaviour"], observed="x", note="y")


class CommandLine(unittest.TestCase):
    """The refusal has to be an exit status, not just a returned list."""

    def run_result(self, payload: dict) -> subprocess.CompletedProcess:
        with tempfile.TemporaryDirectory() as directory:
            template = os.path.join(directory, "template.json")
            out = os.path.join(directory, "out.json")
            with open(template, "w", encoding="utf-8") as handle:
                json.dump(payload, handle)
            process = subprocess.run(
                [
                    sys.executable, os.path.join(HERE, "result.py"),
                    "--out", out, "--fault", "probe", "--variant", "baseline",
                    "--template", template,
                ],
                capture_output=True,
                text=True,
            )
            self.wrote = os.path.exists(out)
            return process

    def test_a_blurred_result_exits_non_zero_and_writes_nothing(self):
        doc = document()
        doc["platform"]["behaviours"][0]["status"] = "reproduced here"
        process = self.run_result(doc)
        self.assertEqual(process.returncode, 2, process.stderr)
        self.assertFalse(self.wrote, "a refused result must not be written")

    def test_an_honest_result_exits_zero_and_writes(self):
        process = self.run_result(document())
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertTrue(self.wrote)


class DockerIdentity(unittest.TestCase):
    """A cached identity must name the image this run used, not an earlier commit's."""

    def _record(self, scratch: str, image: str, digest: str) -> subprocess.CompletedProcess:
        script = os.path.join(HERE, "..", "lib", "common.sh")
        # `record_docker_identity` reads the daemon; `docker` is replaced by a stub so the
        # test is about the cache key, not about a machine having a daemon.
        stub_dir = os.path.join(scratch, "bin")
        os.makedirs(stub_dir, exist_ok=True)
        stub = os.path.join(stub_dir, "docker")
        with open(stub, "w", encoding="utf-8") as handle:
            handle.write(
                "#!/usr/bin/env bash\n"
                "case \"$1 $2\" in\n"
                "  'version --format') echo 'client 1.0 / server 1.0' ;;\n"
                f"  'image inspect') printf '%s\\n' '{digest}' ;;\n"
                "  *) exit 1 ;;\n"
                "esac\n"
            )
        os.chmod(stub, 0o755)
        # `common.sh` derives SCRATCH_ROOT from FAULTS_SCRATCH (it ignores a bare
        # SCRATCH_ROOT), so the cache layout under test is the one a run really uses.
        environment = dict(
            os.environ, PATH=f"{stub_dir}:{os.environ['PATH']}", FAULTS_SCRATCH=scratch
        )
        return subprocess.run(
            [
                "bash", "-c",
                '. "$0"; D2_IMAGE="$1"; record_docker_identity',
                script, image,
            ],
            capture_output=True,
            text=True,
            env=environment,
        )

    def test_a_cached_identity_for_another_image_is_not_reused(self):
        with tempfile.TemporaryDirectory() as scratch:
            first = self._record(scratch, "band-d4-d2:aaaa", "1111")
            self.assertIn("band-d4-d2:aaaa", first.stdout)
            second = self._record(scratch, "band-d4-d2:bbbb", "2222")
            self.assertIn("band-d4-d2:bbbb", second.stdout, second.stderr)
            self.assertIn("2222", second.stdout)
            self.assertNotIn("band-d4-d2:aaaa", second.stdout)

    def test_a_cached_identity_is_reused_for_the_same_image(self):
        with tempfile.TemporaryDirectory() as scratch:
            self._record(scratch, "band-d4-d2:aaaa", "1111")
            again = self._record(scratch, "band-d4-d2:aaaa", "1111")
            self.assertIn("band-d4-d2:aaaa", again.stdout, again.stderr)
            self.assertTrue(
                any(name.startswith("docker-identity-band-d4-d2_aaaa") for name in os.listdir(scratch)),
                os.listdir(scratch),
            )

    def test_a_stale_unkeyed_identity_is_ignored(self):
        """The old cache path wrote a file that survived a rebuild; it must not be read."""
        with tempfile.TemporaryDirectory() as scratch:
            with open(os.path.join(scratch, "docker-identity.txt"), "w", encoding="utf-8") as handle:
                handle.write("client and server: client 1 / server 1; image band-d4-d2:c9a23873ca15 (sha256:aaaa)\n")
            current = self._record(scratch, "band-d4-d2:bbbb", "2222")
            self.assertNotIn("c9a23873ca15", current.stdout)
            self.assertIn("band-d4-d2:bbbb", current.stdout)


class NotExercisedControls(unittest.TestCase):
    """A control cell's command must not repeat the fault's invocation."""

    def run_not_exercised(self, control: bool) -> dict:
        with tempfile.TemporaryDirectory() as directory:
            out = os.path.join(directory, "x", "container-restart-control.json")
            args = [
                "bash", os.path.join(HERE, "not_exercised.sh"),
                "--variant", "durable-outside", "--fault", "container-restart",
                "--out", out, "--reason", "no container", "--detail", "recorded",
            ]
            if control:
                args.append("--control")
            process = subprocess.run(args, capture_output=True, text=True)
            self.assertEqual(process.returncode, 0, process.stderr)
            with open(out, encoding="utf-8") as handle:
                return json.load(handle)

    def test_the_control_cell_carries_the_flag(self):
        document = self.run_not_exercised(control=True)
        self.assertEqual(
            document["harness"]["command"],
            "./faults/container-restart.sh --variant durable-outside --fault-not-injected",
        )

    def test_the_fault_cell_does_not_carry_the_flag(self):
        document = self.run_not_exercised(control=False)
        self.assertEqual(
            document["harness"]["command"],
            "./faults/container-restart.sh --variant durable-outside",
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
