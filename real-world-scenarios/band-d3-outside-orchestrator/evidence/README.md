# How to check this evidence

Every `.log` and `.txt` file here was written by redirecting a command's own output. The
`$` lines are the commands; the lines after them are what the tool printed, unedited.

Three checks a reviewer can run without leaving the branch:

```sh
# 1. The kill log's exit codes are the proof, not the absence of text.
grep -A2 'ps -p 21917' evidence/run/20-kill.log      # exit=1 after the kill

# 2. The gate resume and the finished rerun are different files, saying different things.
shasum -a 256 evidence/run/30-resume-at-gate.log evidence/run/70-rerun-finished.log
grep -h 'phase:\|restarts before' evidence/run/30-resume-at-gate.log evidence/run/70-rerun-finished.log

# 3. No line is a shape the tool cannot print: no Python dict repr from a --json call,
#    no hand-written ps annotation.
grep -rn "^{'" evidence/ ; grep -rn "ps exited" evidence/
```

**Why this file exists.** The first revision of this PR had two log files whose lines were
typed rather than captured: a `ps` annotation that contradicted the line above it, and a
`--json` call shown as a Python dict repr, which `count-calls.sh` never prints. All
evidence was re-run against a fresh deployment and recaptured. A reviewer is right not to
take that on trust, so the checks above are the ones that would catch a repeat.

Two capture rules the scripts follow, learned the hard way:

- **Never `| tail` before reading `$?`.** A pipe makes `$?` the exit of the last command
  in it, so `cmd | tail; echo $?` reports `tail`'s status. `teardown.md` records a failing
  teardown that reads `EXIT=0` when piped and `EXIT=2` when not.
- **Never `cmd | head` for a long-running process.** `head` closes the pipe and the
  process dies of SIGPIPE, which is how the real gate-resume transcript was lost the first
  time and replaced by a later run's log.

## Index

| File | What it is |
| --- | --- |
| `run/00-counts-baseline.*` | The platform's counts before anything ran: 0 and 0 |
| `run/10-run-to-gate.log` | Run to the human gate; prints the PID to kill |
| `run/20-kill.log` | `ps`, `kill -9`, then `ps`/`pgrep` proving the PID is gone |
| `run/21-counts-at-kill.json` | Counts at the moment of the kill: coverage 1, letter 0 |
| `run/30-resume-at-gate.log` | **The gate resume**: `restarts: 0`, `phase: gate`, one memo |
| `run/31-counts-after-resume.txt` | **+0** after the restart |
| `run/40-gate-answer.log` | The human decision, written by `gate.sh` |
| `run/51-delta-whole-run.txt` | **+2** across both processes |
| `run/60-store-report.json` | The store's task, memos and result |
| `run/70-rerun-finished.log` | The second control: `phase: letter`, `already finished` |
| `run/71-rerun-delta.txt` | **+0** for the rerun |
| `memo-key/00-transcript.txt` | Requirement 3, all five scenarios and their assertions |
| `memo-key/01-summary.txt` | The same, as a table |
| `memo-key/*.log` | Each scenario's own orchestrator output |
| `teardown.md` | Two teardowns, three verification layers, and the leftover check firing |
