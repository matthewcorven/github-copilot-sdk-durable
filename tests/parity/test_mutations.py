"""Real-source mutations run only in isolated copies, never an owner's worktree."""

import subprocess
import sys
import unittest

from test_runners import isolated_contract


class FactMutationTests(unittest.TestCase):
    def test_four_facts_reject_realistic_library_regressions_in_both_languages(self):
        mutations = {
            "ts": [
                ("transaction", "this.undoLog?.undo();", "void this.undoLog;", "rollback leaked a partial entry"),
                ("fork", "return boundaries.has(entry) || !kinds || [...kinds].some(kind => !internalKinds.has(kind));",
                 "return true;", "inherited bookkeeping or post-boundary entry leaked"),
                ("recovery", "return task.memos[key] ?? null;", "return null;", "repeated the side effect"),
                ("abort", "await this.abortTaskAsync(child.id, cancellationToken);",
                 "void child;", "abort must visit each owned task once"),
            ],
            "py": [
                ("transaction", "self._undo.undo(self)", "pass", "rollback leaked a partial entry"),
                ("fork", "if entry not in bookkeeping][:limit]", "if True][:limit]", "inherited bookkeeping or later entry leaked"),
                ("recovery", "return task.memos.get(key)", "return None", "repeated the side effect"),
                ("abort", "await self.abort_task_async(child.id, cancellation)", "pass", "Lists differ"),
            ],
        }
        for language, cases in mutations.items():
            for fact, before, after, diagnostic in cases:
                with self.subTest(language=language, fact=fact), isolated_contract() as root:
                    path = root / f"lib/{language}/durable.{language}"
                    original = path.read_text()
                    self.assertEqual(original.count(before), 1, f"Mutation anchor drifted: {before}")
                    path.write_text(original.replace(before, after))
                    if language == "ts":
                        command = ["node", "--import", "tsx", "--test", f"--test-name-pattern={fact}", "parity.test.ts"]
                        cwd = root / "lib/ts"
                    else:
                        # Each selector names the actual test protecting the changed implementation.
                        names = {
                            "transaction": "test_transaction_rollback_leaves_no_partial_entry_or_document",
                            "fork": "test_fork_inherits_state_independent_edit_and_exact_filtered_transcript",
                            "recovery": "test_recovery_twice_executes_memoized_step_once_after_actual_process_crashes",
                            "abort": "test_abort_cascade_persists_exact_terminal_states_for_parent_and_two_children",
                        }
                        command = [sys.executable, "-m", "unittest", f"test_facts.ParityFacts.{names[fact]}"]
                        cwd = root / "tests/parity"
                    result = subprocess.run(command, cwd=cwd, capture_output=True, text=True, timeout=60)
                    self.assertNotEqual(result.returncode, 0, f"{language}/{fact}: mutation survived")
                    self.assertIn(diagnostic, result.stdout + result.stderr,
                                  f"Failure must name the protected artifact, not setup:\n{result.stdout}\n{result.stderr}")


if __name__ == "__main__":
    unittest.main()
