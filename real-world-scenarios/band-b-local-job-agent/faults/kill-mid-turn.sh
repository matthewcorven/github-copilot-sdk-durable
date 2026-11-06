#!/usr/bin/env bash
# Fault: kill-mid-turn. A real SIGKILL of a real process while a turn is in flight.
#
#   ./faults/kill-mid-turn.sh --variant baseline|durable [--fault-not-injected]
#
# The injection point is the gate holding the runtime's `tool.execution_start` notification: the model
# has already answered with a tool call, the CLI is about to run it, and the application is blocked in
# `SendAndWaitAsync`/`SubmitAsync`. At that instant the PID this script started is killed with `kill -9`
# and confirmed gone. Nothing about the kill is simulated, and the PID is the application's own apphost —
# see the note at the top of lib/common.sh about `dotnet run` putting a host in front of it.
#
# The one thing this fault must not do is confuse "the target still has files" with "the work survived".
# A turn is a set of tool calls; killing it in the middle can leave some of them applied. So the
# observation is not "did the directory change" but "what does a second process do with it":
#
#   baseline  a new process re-reads `PLAN.md` and starts again at its own iteration 1. Whether the
#             half-turn's edits are on disk is reported, file by file, against the digest taken before
#             the kill — but nothing resumes them.
#   durable   a new process finds the checkpoint at `turn-started` and refuses, naming the iteration.
#             That is the variant's stated limit and it is reported as such, with the store's own record.
#
#   --fault-not-injected is the control: the same run with the held notification released instead of
#   the process killed. It must finish, or the "lost"/"refused" result would belong to the harness.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANT=""
INJECT=1
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) VARIANT="${2:-}"; shift 2 ;;
    --fault-not-injected) INJECT=0; shift ;;
    *) log "kill-mid-turn: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "kill-mid-turn: --variant baseline|durable is required"; exit 2; }
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "kill-mid-turn: unknown variant"; exit 2; }

FAULT="kill-mid-turn"
[ "$INJECT" = "1" ] || FAULT="kill-mid-turn-control"
FAULT_COMMAND="./faults/kill-mid-turn.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")" || exit 1
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

# The second tool call of the run. Holding the first would be a kill on the turn's opening read, before
# anything had been written; the second is reached with the target already touched, which is what makes
# "what did the half-turn leave" a question worth asking.
FAULTS_HOLD="out:tool.execution_start"
FAULTS_COUNT=2
export FAULTS_HOLD FAULTS_COUNT

start_variant "$VARIANT" "$RUN_DIR" "$TARGET_CAP"
PID="$STARTED_PID"

if ! wait_for_inflight "$RUN_DIR" 300; then
  log "kill-mid-turn: the gate never held a tool call, so no turn was in flight"
  tail -15 "$RUN_DIR/app.log" >&2
  exit 1
fi
FRAME="$(inflight_frame "$RUN_DIR")"
log "kill-mid-turn: $VARIANT is blocked at the gate on '$FRAME'"

BEFORE_PLAN="$(plan_counts "$RUN_DIR/target")"
product_digest "$RUN_DIR/target" > "$RUN_DIR/before.digest"
BEFORE_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"

if [ "$INJECT" = "1" ]; then
  kill_and_confirm "$PID" application || exit 1
  log "kill-mid-turn: kill -9 $PID; the process is gone"
  KILLED=1
else
  log "kill-mid-turn-control: not injecting; releasing the held tool call so the turn can finish"
  release_gate "$RUN_DIR"
  KILLED=0
fi

if [ "$KILLED" = "0" ]; then
  if ! wait_app "$RUN_DIR" 1200; then
    log "kill-mid-turn-control: the uninterrupted run did not finish within the budget"
    exit 1
  fi
  CONTROL_STATUS="$(app_status)"
  CONTROL_PLAN="$(plan_counts "$RUN_DIR/target")"
  CONTROL_VERIFY="$(verify_target "$RUN_DIR/target")"
  CONTROL_ITERATIONS="$(iterations_run "$RUN_DIR/app.log")"
  CONTROL_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
  if [ "$CONTROL_VERIFY" = "pass" ]; then
    record_result "$VARIANT" "$FAULT" "completed" \
      "a tool call in flight (the control)" \
      "Control: the same steps, with the held tool call released instead of the process killed. $CONTROL_ITERATIONS iteration(s), exit $CONTROL_STATUS, plan '$CONTROL_PLAN' (status, ticked, open), verifier: pass, store '$CONTROL_STORE'. This is what the killed run is measured against: the kill is the only difference." \
      "gate held '$FRAME' and released it; the run then reached its own end"
    exit 0
  fi
  record_result "$VARIANT" "$FAULT" "control did not finish" \
    "a tool call in flight (the control)" \
    "Control FAILED: without the kill the same run did not produce a target that matches the plan ($CONTROL_VERIFY), exit $CONTROL_STATUS, plan '$CONTROL_PLAN'. A fault cannot be blamed for a run the harness could not finish." \
    ""
  exit 0
fi

# What the half-turn left behind. Reported, not interpreted: a file that differs is a tool call that had
# already run when the kill landed.
product_digest "$RUN_DIR/target" > "$RUN_DIR/after.digest"
CHANGED_FILES="$(comm -13 <(cut -c67- "$RUN_DIR/before.digest" | LC_ALL=C sort) <(cut -c67- "$RUN_DIR/after.digest" | LC_ALL=C sort) | tr '\n' ' ')"
MODIFIED_FILES="$(join -j 2 <(sort -k2 "$RUN_DIR/before.digest") <(sort -k2 "$RUN_DIR/after.digest") | awk '$2 != $3 {print $1}' | tr '\n' ' ')"
AFTER_KILL_PLAN="$(plan_counts "$RUN_DIR/target")"
AFTER_KILL_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"

reap_servers "$RUN_DIR"
rm -f "$RUN_DIR/pid" "$RUN_DIR/inflight" "$RUN_DIR/release"

# ── the second process ─────────────────────────────────────────────────────────────────────────────
FAULTS_HOLD=""
FAULTS_COUNT=0
export FAULTS_HOLD FAULTS_COUNT
start_variant "$VARIANT" "$RUN_DIR" "$TARGET_CAP"
SECOND_PID="$STARTED_PID"
if ! wait_app "$RUN_DIR" 1200; then
  log "kill-mid-turn: the second process did not finish within the budget"
  exit 1
fi
SECOND_STATUS="$(app_status)"
AFTER_PLAN="$(plan_counts "$RUN_DIR/target")"
AFTER_VERIFY="$(verify_target "$RUN_DIR/target")"
AFTER_STORE="$(store_state "$RUN_DIR/target/.durable/store.jsonl")"
END="$(end_line "$RUN_DIR/app.log")"
LEFT="plan before '$BEFORE_PLAN', after the kill '$AFTER_KILL_PLAN'; files the half-turn left new: [${CHANGED_FILES:-none}], files it left modified: [${MODIFIED_FILES:-none}]"

if [ "$VARIANT" = "baseline" ]; then
  record_result "$VARIANT" "$FAULT" "lost its position" \
    "a tool call in flight in iteration 1" \
    "Killed pid $PID with SIGKILL while the runtime's tool call was at the gate ($FRAME). $LEFT. A new process ran the same command: exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier '$AFTER_VERIFY', last line \"$END\". The baseline's position is a local variable, so nothing continued the killed turn: the second process re-read the plan and began at its own iteration 1. Whatever the half-turn had already written stays on disk and is not recorded anywhere — that is what 'lost its position' means here, not 'lost its files'." \
    "gate frame: '$FRAME'; before.digest/after.digest hold the product digest on both sides; exit $SECOND_STATUS; plan after the second process: $AFTER_PLAN; nothing about the baseline records an iteration"
  exit 0
fi

if grep -q 'was in flight' "$RUN_DIR/app.log" 2>/dev/null; then
  record_result "$VARIANT" "$FAULT" "refused to resume the dead turn" \
    "a tool call in flight in iteration 1" \
    "Killed pid $PID with SIGKILL while the runtime's tool call was at the gate ($FRAME). Store before the kill '$BEFORE_STORE', after it '$AFTER_KILL_STORE'; $LEFT. A new process read that record and refused: the turn was issued and never finished, so re-running it could duplicate the writes it had already made. Its last line: \"$END\". Exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier '$AFTER_VERIFY', store after '$AFTER_STORE'. This is the variant's stated limit — nothing durable records what a turn that does not return did — reported as the outcome rather than smoothed into 'resumed'." \
    "gate frame: '$FRAME'; checkpoint before kill: ${BEFORE_STORE##* }; after kill: ${AFTER_KILL_STORE##* }; after the second process: ${AFTER_STORE##* }"
  exit 0
fi

if grep -q 'resuming at iteration' "$RUN_DIR/app.log" 2>/dev/null; then
  record_result "$VARIANT" "$FAULT" "resumed after the kill" \
    "a tool call in flight in iteration 1" \
    "Killed pid $PID with SIGKILL while a tool call was at the gate ($FRAME). $LEFT. The second process found a checkpoint and resumed: exit $SECOND_STATUS, plan '$AFTER_PLAN', verifier '$AFTER_VERIFY', last line \"$END\". Store before '$BEFORE_STORE', after the kill '$AFTER_KILL_STORE', after the resume '$AFTER_STORE'." \
    "gate frame: '$FRAME'"
  exit 0
fi

record_result "$VARIANT" "$FAULT" "lost its position" \
  "a tool call in flight in iteration 1" \
  "Killed pid $PID with SIGKILL while a tool call was at the gate ($FRAME). $LEFT. The second process neither resumed nor refused: exit $SECOND_STATUS, plan '$AFTER_PLAN', store '$AFTER_STORE', last line \"$END\"." \
  "gate frame: '$FRAME'"
exit 0
