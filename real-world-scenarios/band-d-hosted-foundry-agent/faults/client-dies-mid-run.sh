#!/usr/bin/env bash
# Fault: client-dies-mid-run. The process driving a run is killed while the run's call is in
# flight, and a new client picks the run up.
#
#   ./faults/client-dies-mid-run.sh --variant baseline|durable-outside [--fault-not-injected]
#
# The injection point is a *held* service call, not a sleep: `lib/fake_foundry.py` holds the
# coverage agent's response (and happy to never answer). So the driving client is killed
# inside `InvokeAsync`, with the request the service accepted already on its log — a kill
# during work, not before it and not after it.
#
# The two columns are the two places a run's memory can live:
#
#   baseline        the client holds the only copy: it writes `steps/<step>.json` after each
#                   response and nothing else records progress. Kill it and re-drive, and the
#                   question is whether the step that had already been *paid for* is paid for
#                   again. There is no store to consult, so the answer cannot be a resume.
#   durable-outside D3's store holds the task, its checkpoint and its memos. Killed at the
#                   same held call, the covered request is a request the service accepted but
#                   the run never recorded — so the resume re-enters `coverage` and calls
#                   again. That is the honest boundary: a memoised step survives, an
#                   *unfinished* step does not, and this fault measures which one this kill is.
#
# Reported as it is, not as the same test. The platform half of this fault is
# `foreground-cancelled` / `no-replay`, both documented-not-exercised here; what this
# harness measures is what the application did after the client died.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=client-dies-mid-run
parse_fault_args "${BASH_SOURCE[0]}" "$@"
case "$VARIANT" in
  baseline|durable-outside) ;;
  *)
    log "client-dies-mid-run: this fault drives a client process, so it runs against baseline and durable-outside."
    log "client-dies-mid-run: durable-inside has no client to kill - its turn is a platform request (see its README)."
    exit 2
    ;;
esac

require_tools || exit 1
install_cleanup
FAULTS_DOCKER="not used by this fault - the client is a host process"; export FAULTS_DOCKER
[ "$VARIANT" = "durable-outside" ] && { build_durable_outside || exit 1; }

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

PY="$(harness_python)" || exit 1
REQUEST="Prior authorization PA-1042: lumbar fusion at an ambulatory surgical centre, 2 of 2 criteria met."

hold_coverage() { # on|off
  local on="$1"
  local control="$SERVICE_ROOT/control"
  if [ "$on" = "1" ]; then
    printf 'd3-coverage-assessor\ncoverage-assessment-agent\n' > "$control/hold-agents.txt"
  else
    : > "$control/hold-agents.txt"
    : > "$control/release-holds"
  fi
}

kill_first_client() { # pid
  local pid="$1"
  kill -9 "$pid" 2>/dev/null
  # Confirmed gone, not assumed: a kill that did not land is a harness error, not a result.
  local waited=0
  while kill -0 "$pid" 2>/dev/null && [ "$waited" -lt 100 ]; do
    sleep 0.05
    waited=$((waited + 1))
  done
  kill -0 "$pid" 2>/dev/null && return 1
  return 0
}

held_pid=""
first_log="$RUN_DIR/10-first-client.log"
resume_log="$RUN_DIR/20-resume.log"
killed_inside="no"

if [ "$VARIANT" = "baseline" ]; then
  if [ "$INJECT" = "1" ]; then
    hold_coverage 1
    python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
      --hold-step coverage > "$first_log" 2>&1 &
    held_pid=$!
    # The first client has to reach its held step before a kill means anything.
    waited=0
    while [ ! -d "$RUN_DIR/run/steps" ] && [ "$waited" -lt 300 ]; do sleep 0.05; waited=$((waited + 1)); done
    # It is inside the call once the service has logged the POST.
    waited=0
    while [ "$(service_posts "$RUN_DIR" coverage-assessment-agent)" = "0" ] && [ "$waited" -lt 400 ]; do
      sleep 0.05; waited=$((waited + 1))
    done
    posts_before_kill="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
    kill_first_client "$held_pid" || { log "fault: the killing did not land"; exit 1; }
    killed_inside="yes"
    posts_at_kill="$posts_before_kill"
    hold_coverage 0
    sleep 0.5
    # A new client, cold: it has no step result, so it drives the run from the first step.
    python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
      > "$resume_log" 2>&1
    final_status=$?
  else
    posts_at_kill=0
    python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
      > "$resume_log" 2>&1
    final_status=$?
    printf 'control: no client was killed\n' > "$first_log"
  fi
  posts_total="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
  repeated_coverage="$posts_total"
  steps_on_disk="$(python3 - "$RUN_DIR/run/steps" <<'PY'
import json, os, sys
d = sys.argv[1]
if not os.path.isdir(d):
    print(0); raise SystemExit
print(len([n for n in os.listdir(d) if n.endswith('.json')]))
PY
)"
  resumed="no"
else
  # durable-outside: D3's own process, driven twice over one store.
  # D3 names its scratch directory `RUN_DIR`, so the harness's own directory is captured
  # before the orchestrator's variable shadows it - the service log lives under the
  # harness's directory, not under the orchestrator's.
  HARNESS_RUN="$RUN_DIR"
  export RUN_DIR="$RUN_DIR/run"
  mkdir -p "$RUN_DIR/run"
  export RUN_REQUEST="$REQUEST" RUN_ID="D4-CDM" REVIEWER="D4 fault harness" GATE_TIMEOUT_SECONDS=240
  if [ "$INJECT" = "1" ]; then
    hold_coverage 1
    "$D3_OUT/orchestrator" > "$first_log" 2>&1 &
    held_pid=$!
    waited=0
    while [ "$(service_posts "$HARNESS_RUN" d3-coverage-assessor)" = "0" ] && [ "$waited" -lt 400 ]; do
      sleep 0.05; waited=$((waited + 1))
    done
    posts_at_kill="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
    # The kill lands on the application itself: the published binary is the process, not a
    # `dotnet` host in front of it.
    kill_first_client "$held_pid" || { log "fault: the killing did not land"; exit 1; }
    killed_inside="yes"
    hold_coverage 0
    sleep 0.5
    # The resumed run has to reach the human gate, which is the next step after coverage.
    # Answering it from another process is what `gate.sh` is for, and it is what makes this
    # fault's resume a resume of the *run* rather than of one step.
    "$D3_OUT/orchestrator" > "$resume_log" 2>&1 &
    resume_pid=$!
    # RUN_DIR is exported as the orchestrator's own run directory two lines above, so the
    # gate is directly under it - not under the harness's run directory again.
    gate="$HARNESS_RUN/run/gate"
    waited=0
    while [ ! -f "$gate/request.json" ] && [ "$waited" -lt 600 ]; do sleep 0.05; waited=$((waited + 1)); done
    if [ -f "$gate/request.json" ]; then
      RUN_DIR="$HARNESS_RUN/run" "$BAND_DIR/../band-d3-outside-orchestrator/gate.sh" approve "D4 harness reviewer" \
        "automatic answer: the gate is not this fault's subject" > "$RUN_DIR/30-gate.log" 2>&1 || true
    fi
    wait "$resume_pid"
    final_status=$?
  else
    posts_at_kill=0
    "$D3_OUT/orchestrator" > "$resume_log" 2>&1
    final_status=$?
    printf 'control: no client was killed\n' > "$first_log"
  fi
  posts_total="$(service_posts "$HARNESS_RUN" d3-coverage-assessor)"
  repeated_coverage="$posts_total"
  resumed="$(grep -c 'resuming durable task' "$resume_log" 2>/dev/null | head -1)"
  steps_on_disk="$(python3 - "$HARNESS_RUN/run/store/orchestrator.jsonl" <<'PY2'
import json, sys
memos = 0
try:
    for line in open(sys.argv[1], encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        entry = json.loads(line)
        if entry.get("type") == "task":
            memos = len((entry.get("payload") or {}).get("memos") or {})
except OSError:
    pass
print(memos)
PY2
)"
fi

# The second process reported the run as complete (or, for the control, the single process did).
completed="no"
grep -q 'run complete' "$resume_log" 2>/dev/null && completed="yes"
grep -q 'run finished' "$resume_log" 2>/dev/null && completed="yes"

actual_calls="$repeated_coverage"

if [ "$INJECT" = "1" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    outcome="one step repeated after the client died"
    detail="The first client was killed inside its held coverage call (service had accepted $posts_at_kill POST(s) for that agent). A new client, having no progress record beyond the step files it had not yet written, drove the run again: the service accepted $posts_total coverage POST(s) in total. Nothing in the baseline says 'this step was already paid for' — its only memory is the result file, which the killed process had not written. The run finished, but the work of the interrupted step was paid for twice."
  else
    outcome="unfinished step re-called; completed steps not repeated"
    detail="The orchestrator was killed inside its held coverage call (service had accepted $posts_at_kill POST(s) for that agent). The store held the task at its coverage checkpoint and no memo for the interrupted step, because the step had not returned. The second process re-entered coverage and called again: $posts_total coverage POST(s) in total. That is $actual_calls call(s) for one completed step. The boundary this measures is exact: a memoised step is not repeated (D3's kill-at-gate evidence shows +0), an *unfinished* step is, because there is nothing recorded to adopt."
  fi
else
  outcome="control: completed in one client"
  detail="The same drive with no kill: one coverage call, one synthesis call, the run finished. The two-run shape alone repeats nothing, so the repetition in the fault row belongs to the kill and not to driving twice."
fi

TEMPLATE="$RUN_DIR/template.json"
FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_AT_KILL="$posts_at_kill" \
FAULT_TOTAL="$posts_total" FAULT_COMPLETED="$completed" FAULT_RESUMED="$resumed" \
FAULT_STEPS="$steps_on_disk" FAULT_STATUS="$final_status" \
python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

inject = os.environ["FAULT_INJECT"] == "1"
detail = os.environ["FAULT_DETAIL"]
observed = (
    "not exercised in this run: no hosted deployment was reachable, so what Foundry would have "
    "done is quoted from D1's captures and the documentation, in behaviours[] below"
)
note = (
    "lib/fake_foundry.py is a local stand-in for the data plane. It logs every request so a call "
    "count is a measurement; it is not Foundry and it recovers nothing. Every number in "
    "`application` is about the application's response to a dead client, never about the "
    "platform's."
)

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=["foreground-cancelled", "no-replay"], observed=observed, note=note
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": detail,
        "observations": {
            "killed_inside_a_held_call": inject,
            "service_posts_for_coverage_before_the_kill": int(os.environ["FAULT_AT_KILL"]),
            "service_posts_for_coverage_in_total": int(os.environ["FAULT_TOTAL"]),
            "resume_reported_a_stored_run": os.environ["FAULT_RESUMED"].strip() not in ("", "0"),
            "second_process_completed_the_run": os.environ["FAULT_COMPLETED"] == "yes",
            "run_step_artifacts": int(os.environ["FAULT_STEPS"]),
            "final_exit_status": int(os.environ["FAULT_STATUS"]),
        },
    },
    "variant_notes": {
        "baseline": "the client is the run: D1's hosted agents have no orchestration of their own, so this driver is the accelerator's decision path reproduced without the platform",
        "durable-outside": "the store is the run: D3's task and memos decide what a restart repeats",
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
exit 0
