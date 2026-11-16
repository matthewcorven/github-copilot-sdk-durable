#!/usr/bin/env bash
# Fault: agent-call-failure. One agent call fails, and the question is what the resume does
# about the calls that had already succeeded.
#
#   ./faults/agent-call-failure.sh --variant baseline|durable-outside [--fault-not-injected]
#
# This is D3's memo, measured the only way that means anything: the *last* call fails, the
# failure is lifted, and the run resumes. Every earlier step is then a candidate for a
# re-call, so the count answers "does the memo prevent it" rather than "was there anything
# left to do".
#
#   baseline        the last call fails; the resume skips the step whose result file exists
#                   and calls the one that failed. There is no identity for a step, so a
#                   *fresh* client would repeat everything — measured in duplicate-delivery,
#                   and stated here so this row is not read as a memo.
#   durable-outside the last call fails; the resume finds a memo for coverage and for the
#                   human decision, so both are hits, and only the failed step is called.
#                   The failed step has no memo *because it never returned*, which is the
#                   boundary this fault exists to name.
#
# `durable-inside` has no agent call of its own to fail: its application code writes the
# working state, and the model call belongs to the platform. Its result records that as
# `not exercised` rather than borrowing one of the other columns.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=agent-call-failure
parse_fault_args "${BASH_SOURCE[0]}" "$@"
require_tools || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

REQUEST_ID="PA-D4-0005"
REQUEST="Prior authorization PA-D4-0005: spinal fusion, 2 of 2 criteria met."

fail_agent() { # agent|""
  local control="$SERVICE_ROOT/control"
  if [ -n "$1" ]; then
    printf '%s\n' "$1" > "$control/fail-agents.txt"
  else
    : > "$control/fail-agents.txt"
  fi
}

case "$VARIANT" in
  durable-inside)
    TEMPLATE="$RUN_DIR/template.json"
    FAULT_LIB="$HERE/lib" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": "not exercised: this variant's application code makes no agent call",
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: the only model call in D2 is the platform's own, inside the "
            "hosted turn, and this harness has no deployment to fail it in"
        ),
        note=(
            "D2's durable task parks on a human event; it issues no agent call and holds no memo of "
            "one. Failing a call it does not make would have produced a fabrication, so the row is "
            "recorded as not exercised instead."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": "D2's application code (agent/durable_state.py, agent/d2_tool.py) writes the working state and parks an approval task. The agent call that produces the assessment is the platform's, made by the hosted turn before the tool runs. The D3 memo this fault is about has no counterpart here, so there is nothing to fail and nothing to memoise.",
        "observations": {
            "agent_call_made_by_application": False,
            "fault_exercised": False,
            "reason": "no application-issued agent call exists in this variant",
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY
    write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
    exit 0
    ;;

  durable-outside)
    build_durable_outside || exit 1
    HARNESS_RUN="$RUN_DIR"
    mkdir -p "$HARNESS_RUN/run"
    STORE_FILE="$HARNESS_RUN/run/store/orchestrator.jsonl"

    export RUN_DIR="$HARNESS_RUN/run"
    export RUN_REQUEST="$REQUEST" RUN_ID="D4-ACF" REVIEWER="D4 harness reviewer" GATE_TIMEOUT_SECONDS=120

    if [ "$INJECT" = "1" ]; then
      # The *last* call fails, so every earlier step is a candidate for a re-call on resume.
      fail_agent d3-decision-letter
      injected="yes"
    else
      injected="no"
    fi

    "$D3_OUT/orchestrator" > "$HARNESS_RUN/10-first-process.log" 2>&1 &
    pid=$!
    waited=0
    while [ ! -f "$HARNESS_RUN/run/gate/request.json" ] && [ "$waited" -lt 900 ]; do
      sleep 0.05; waited=$((waited + 1))
    done
    if [ -f "$HARNESS_RUN/run/gate/request.json" ]; then
      RUN_DIR="$HARNESS_RUN/run" "$BAND_DIR/../band-d3-outside-orchestrator/gate.sh" approve \
        "D4 harness reviewer" "the gate is not this fault's subject" > "$HARNESS_RUN/20-gate.log" 2>&1 || true
    fi
    wait "$pid"; first_status=$?

    coverage_after_first="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
    letter_after_first="$(service_posts "$HARNESS_RUN" d3-decision-letter)"
    first_phase="$(grep -m1 '^phase:' "$HARNESS_RUN/10-first-process.log" 2>/dev/null | awk '{print $2}')"
    [ -n "$first_phase" ] || first_phase="new run (no checkpoint before this process)"
    first_memos="$(grep -m1 'memos:' "$HARNESS_RUN/10-first-process.log" 2>/dev/null)"

    # The failure is lifted, and the same store is driven again. This is the resume the fault
    # is about: which of the earlier steps is called a second time?
    fail_agent ""
    "$D3_OUT/orchestrator" > "$HARNESS_RUN/30-resume.log" 2>&1
    resume_status=$?

    coverage_total="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
    letter_total="$(service_posts "$HARNESS_RUN" d3-decision-letter)"
    coverage_repeat=$((coverage_total - coverage_after_first))
    letter_repeat=$((letter_total - letter_after_first))
    resume_phase="$(grep -m1 '^phase:' "$HARNESS_RUN/30-resume.log" 2>/dev/null | awk '{print $2}')"
    resume_memos="$(grep -m1 'memos:' "$HARNESS_RUN/30-resume.log" 2>/dev/null)"

    completed="no"
    grep -q 'run complete' "$HARNESS_RUN/30-resume.log" 2>/dev/null && completed="yes"
    terminal_before="no"
    grep -q 'already finished' "$HARNESS_RUN/30-resume.log" 2>/dev/null && terminal_before="yes"

    if [ "$INJECT" = "1" ]; then
      if [ "$terminal_before" = "yes" ]; then
        outcome="the failure ended the run, so the resume had nothing to re-enter"
        detail="The decision-letter agent answered 503 for every attempt (the orchestrator retries 4x), and the failure settled the task as failed rather than leaving it running: the first process exited $first_status, and its store is at phase '${first_phase:-?}' with $letter_after_first letter call attempt(s). 'already finished' in the resume log is the reason nothing was called again: '${resume_memos:-no memo line}'. Coverage was not repeated and the letter was not retried, but not because of a memo — the run was terminal. This is a real limitation of the fault as aimed: it shows what a terminal failure does, not what a memo does. The memo's own evidence is D3's kill-at-gate run (+0 calls) and the coverage memo that this store still holds (2 memos on record)."
      else
        outcome="the failed call was retried; the calls that had succeeded were not repeated"
        detail="The decision-letter agent answered 503, and the run did not settle: the store is at phase '${resume_phase:-?}' with '${resume_memos:-no memo line}'. The failure was then lifted and the same store was driven again: coverage was called $coverage_repeat more time(s) and the decision letter $letter_repeat more time(s), and the run finished: $completed. That is a memo hit on the steps that completed and a re-call on the step that did not."
      fi
    else
      outcome="control: the run completed with one call per step"
      detail="The same run with no injected failure: coverage $coverage_total call(s), the gate answered once, the letter $letter_total call(s), the run finished: $completed."
    fi

    # ── The memo arm ──────────────────────────────────────────────────────────
    #
    # A 5xx is terminal in D3 (the orchestrator retries the transport 4x, then the phase
    # throws and the task settles `failed`), so the failed-call arm above cannot show a
    # memo doing anything: there is no resume to re-enter. The memo's own claim is about a
    # call that *succeeded*. So the same fault runs a second injection against a fresh
    # store: D3's documented `STOP_AFTER_MEMO` seam, which stops the process after the
    # coverage memo is written and before the checkpoint advances. The next process
    # re-enters `coverage` — the only state in which "+0 calls" means "the memo was used"
    # rather than "there was nothing left to do".
    #
    # The seam is `STOP_AFTER_COVERAGE=stuck`, not `STOP_AFTER_MEMO=1`. The latter only
    # throws when the process has already been cancelled, so it is a no-op in a process
    # nobody is signalling; D3's own evidence uses the `stuck` shape for exactly this
    # reason, and the first run of this arm proved the difference by walking on to the gate.
    MEMO_RUN="$HARNESS_RUN/memo-arm"
    mkdir -p "$MEMO_RUN/run"
    (
      export RUN_DIR="$MEMO_RUN/run" RUN_REQUEST="$REQUEST" RUN_ID="D4-ACF-MEMO" \
        REVIEWER="D4 harness reviewer" GATE_TIMEOUT_SECONDS=120 STOP_AFTER_COVERAGE=stuck
      "$D3_OUT/orchestrator"
    ) > "$MEMO_RUN/10-first-process.log" 2>&1
    memo_first_status=$?
    memo_calls_before="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
    (
      export RUN_DIR="$MEMO_RUN/run" RUN_REQUEST="$REQUEST" RUN_ID="D4-ACF-MEMO" \
        REVIEWER="D4 harness reviewer" GATE_TIMEOUT_SECONDS=20
      "$D3_OUT/orchestrator"
    ) > "$MEMO_RUN/20-resume.log" 2>&1 &
    memo_pid=$!
    waited=0
    while [ ! -f "$MEMO_RUN/run/gate/request.json" ] && [ "$waited" -lt 900 ]; do
      sleep 0.05; waited=$((waited + 1))
    done
    if [ -f "$MEMO_RUN/run/gate/request.json" ]; then
      RUN_DIR="$MEMO_RUN/run" "$BAND_DIR/../band-d3-outside-orchestrator/gate.sh" approve \
        "D4 harness reviewer" "the gate is not this fault's subject" > "$MEMO_RUN/30-gate.log" 2>&1 || true
      wait "$memo_pid" 2>/dev/null
    else
      kill -9 "$memo_pid" 2>/dev/null
    fi
    memo_calls_after="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
    memo_repeat=$((memo_calls_after - memo_calls_before))
    memo_hit="no"
    grep -q '\[coverage\] memo hit' "$MEMO_RUN/20-resume.log" 2>/dev/null && memo_hit="yes"
    memo_phase_before="$(grep -m1 '^phase:' "$MEMO_RUN/20-resume.log" 2>/dev/null | awk '{print $2}')"
    [ -n "$memo_phase_before" ] || memo_phase_before="none - the resume read no checkpoint"

    TEMPLATE="$HARNESS_RUN/template.json"
    FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
    FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_INJECTED="$injected" \
    FAULT_FIRST="$first_status" FAULT_RESUME="$resume_status" \
    FAULT_COV1="$coverage_after_first" FAULT_COV2="$coverage_total" FAULT_COVREP="$coverage_repeat" \
    FAULT_LET1="$letter_after_first" FAULT_LET2="$letter_total" FAULT_LETREP="$letter_repeat" \
    FAULT_COMPLETED="$completed" FAULT_TERMINAL="$terminal_before" \
    FAULT_PHASE1="$first_phase" FAULT_PHASE2="$resume_phase" \
    FAULT_MEMOS1="$first_memos" FAULT_MEMOS2="$resume_memos" \
    FAULT_MEMO_COV1="$memo_calls_before" FAULT_MEMO_COV2="$memo_calls_after" \
    FAULT_MEMO_REP="$memo_repeat" FAULT_MEMO_HIT="$memo_hit" \
    FAULT_MEMO_PHASE="$memo_phase_before" FAULT_MEMO_STATUS="$memo_first_status" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: the 503 came from lib/fake_foundry.py, this harness's stand-in "
            "for the data plane, not from Foundry. What a hosted agent does when its model call fails "
            "is a different question and is not answered here."
        ),
        note=(
            "The call failure is an application-visible 5xx from a local server, which is the shape "
            "Azure returns but not Azure. Nothing in this result says anything about the platform's own "
            "agent reliability."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "failure_injected": os.environ["FAULT_INJECT"] == "1",
            "failed_agent": "d3-decision-letter",
            "first_process_exit": int(os.environ["FAULT_FIRST"]),
            "resume_exit": int(os.environ["FAULT_RESUME"]),
            "coverage_calls_before_resume": int(os.environ["FAULT_COV1"]),
            "coverage_calls_total": int(os.environ["FAULT_COV2"]),
            "coverage_calls_repeated_by_resume": int(os.environ["FAULT_COVREP"]),
            "letter_calls_before_resume": int(os.environ["FAULT_LET1"]),
            "letter_calls_total": int(os.environ["FAULT_LET2"]),
            "letter_calls_repeated_by_resume": int(os.environ["FAULT_LETREP"]),
            "task_was_terminal_before_resume": os.environ["FAULT_TERMINAL"] == "yes",
            "phase_before_resume": os.environ["FAULT_PHASE1"],
            "phase_seen_by_resume": os.environ["FAULT_PHASE2"],
            "memos_before_resume": os.environ["FAULT_MEMOS1"],
            "memos_seen_by_resume": os.environ["FAULT_MEMOS2"],
            "run_completed_after_resume": os.environ["FAULT_COMPLETED"] == "yes",
            "memo_arm": {
                "injection": "STOP_AFTER_COVERAGE=stuck on a fresh store - the coverage memo is written, the checkpoint is not advanced, the process stops with the task still at `coverage`",
                "coverage_calls_before_resume": int(os.environ["FAULT_MEMO_COV1"]),
                "coverage_calls_after_resume": int(os.environ["FAULT_MEMO_COV2"]),
                "coverage_calls_repeated_by_resume": int(os.environ["FAULT_MEMO_REP"]),
                "resume_reported_a_memo_hit": os.environ["FAULT_MEMO_HIT"] == "yes",
                "phase_the_resume_re_entered": os.environ["FAULT_MEMO_PHASE"],
                "first_process_exit": int(os.environ["FAULT_MEMO_STATUS"]),
            },
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

    write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
    exit 0
    ;;

  baseline)
    if [ "$INJECT" = "1" ]; then
      fail_agent synthesis-agent
      injected="yes"
    else
      injected="no"
    fi
    python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
      > "$RUN_DIR/10-first-client.log" 2>&1
    first_status=$?
    cov_after_first="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
    syn_after_first="$(service_posts "$RUN_DIR" synthesis-agent)"

    fail_agent ""
    python3 "$HERE/lib/baseline_driver.py" resume --run-dir "$RUN_DIR/run" \
      > "$RUN_DIR/20-resume.log" 2>&1
    resume_status=$?

    cov_total="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
    syn_total="$(service_posts "$RUN_DIR" synthesis-agent)"
    completed="no"
    grep -q 'run finished' "$RUN_DIR/20-resume.log" 2>/dev/null && completed="yes"

    if [ "$INJECT" = "1" ]; then
      outcome="the failed call was retried; the earlier step was skipped by its result file"
      detail="The synthesis agent answered 503 for every attempt, so the run failed (exit $first_status) after coverage had succeeded. The failure was lifted and the client resumed: coverage was called $((cov_total - cov_after_first)) more time(s) and synthesis $((syn_total - syn_after_first)) more time(s), and the run finished: $completed. The skip is real and it is a *file check*, not a memo: there is no key over the step or the agent's identity, so a fresh client delivering the same request repeats both steps (measured in duplicate-delivery)."
    else
      outcome="control: the run completed with one call per step"
      detail="The same drive with no injected failure: coverage $cov_total call(s), synthesis $syn_total call(s), the run finished: $completed."
    fi

    TEMPLATE="$RUN_DIR/template.json"
    FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
    FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_INJECTED="$injected" \
    FAULT_FIRST="$first_status" FAULT_RESUME="$resume_status" \
    FAULT_COV1="$cov_after_first" FAULT_COV2="$cov_total" \
    FAULT_SYN1="$syn_after_first" FAULT_SYN2="$syn_total" FAULT_COMPLETED="$completed" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: the failure came from this harness's stand-in service, and no "
            "hosted deployment existed to fail a real call in"
        ),
        note=(
            "The 5xx is local. The baseline has no memo, and that is a statement about the "
            "accelerator's design rather than about the platform."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "failure_injected": os.environ["FAULT_INJECT"] == "1",
            "failed_agent": "synthesis-agent",
            "first_process_exit": int(os.environ["FAULT_FIRST"]),
            "resume_exit": int(os.environ["FAULT_RESUME"]),
            "coverage_calls_before_resume": int(os.environ["FAULT_COV1"]),
            "coverage_calls_total": int(os.environ["FAULT_COV2"]),
            "synthesis_calls_before_resume": int(os.environ["FAULT_SYN1"]),
            "synthesis_calls_total": int(os.environ["FAULT_SYN2"]),
            "run_completed_after_resume": os.environ["FAULT_COMPLETED"] == "yes",
            "skip_mechanism": "a result file on disk - there is no memo key",
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

    write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
    exit 0
    ;;

  *)
    log "agent-call-failure: --variant baseline|durable-inside|durable-outside"; exit 2 ;;
esac
