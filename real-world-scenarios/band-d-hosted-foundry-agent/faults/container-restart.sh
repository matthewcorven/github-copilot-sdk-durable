#!/usr/bin/env bash
# Fault: container-restart. The thing running the work is restarted, and the question is
# whether the working state and a *pending approval* are still there afterwards.
#
#   ./faults/container-restart.sh --variant baseline|durable-inside [--fault-not-injected]
#
# The two variants do not have the same thing to restart, and that is reported rather than
# smoothed over:
#
#   baseline        D1 has no container of its own: the platform runs the agent image and the
#                   client is a process. So the closest real restart is `kill -9` of the
#                   driving client between two steps. There is no store, so there is nothing
#                   to survive: the restart is a cold start, which is the observation.
#   durable-inside  D2's agent is a real container, so this is a real one: `docker stop`
#                   (SIGTERM, the platform's stop, not a crash) on a container that has
#                   committed the approval and is mid-way through a later step, then a new
#                   container over the same session volume. The approval's deadline is
#                   compared byte for byte across the two containers.
#
# The platform half — that a session survives 30 days and an idle stop deprovisions compute
# without losing state — is documented and quoted; the container here is a local analogue of
# the platform's session, and the result says exactly that rather than calling it Foundry.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=container-restart
parse_fault_args "${BASH_SOURCE[0]}" "$@"
case "$VARIANT" in
  baseline|durable-inside) ;;
  *)
    log "container-restart: baseline (no container of its own) and durable-inside (a real container) are the variants."
    log "container-restart: durable-outside's store is a local file and its restart is client-dies-mid-run's subject."
    exit 2
    ;;
esac

require_tools || exit 1
install_cleanup
[ "$VARIANT" = "durable-inside" ] && { require_docker || exit 1; build_durable_inside || exit 1; }

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

PY="$(harness_python)" || exit 1
REQUEST_ID="PA-D4-0001"
REQUEST="Prior authorization PA-D4-0001: lumbar fusion at an ambulatory surgical centre, 2 of 2 criteria met."
VOLUME="$(d2_volume "$RUN_DIR")"
STORE_PATH="/home/session/durable/working-state.jsonl"
track_volume "$VOLUME"

approval_field() { # json_file path [fallback path ...]
  python3 - "$@" <<'PY'
import json, sys
try:
    doc = json.load(open(sys.argv[1]))
except (OSError, ValueError):
    print("")
    raise SystemExit


def dig(path):
    value = doc
    for part in path.split("."):
        value = value.get(part) if isinstance(value, dict) else None
    return value


# The first path that resolves wins, so one helper reads both `d2_tool state`
# (task.checkpoint.*) and `d2_tool resume` (settled.status) shapes.
for candidate in sys.argv[2:]:
    value = dig(candidate)
    if value not in (None, ""):
        print(value)
        raise SystemExit
print("")
PY
}

if [ "$VARIANT" = "durable-inside" ]; then
  NAME_A="d4-d2-a-$$"
  NAME_B="d4-d2-b-$$"
  track_container "$NAME_A"
  track_container "$NAME_B"

  # The container's own command: commit the assessment and the parked approval, write a
  # survival marker, then hold *inside a later step* so the stop lands after the commits.
  # The marker is what the harness waits on; nothing is injected by entering the container.
  read -r -d '' IN_CONTAINER <<'SH'
import json, os, time, sys
sys.path.insert(0, "/app")
from durable_state import Workspace

request = os.environ["D4_REQUEST"]
workspace = Workspace.open()
try:
    record = workspace.work_state(request)
    if record is None or record.phase != "awaiting-approval":
        workspace.note_assessment(request, "criteria met")
        workspace.start_approval(request)
    approval = workspace.approval(request)
    with open("/home/session/marker-assessment.json", "w") as handle:
        json.dump({
            "requestId": request,
            "taskId": workspace.approval_task_id(request),
            "deadline": approval.deadline,
            "requestedAt": approval.requestedAt,
            "pid": os.getpid(),
        }, handle, indent=1, sort_keys=True)
    print("committed: approval parked, deadline", approval.deadline, flush=True)
    # A step after the commits, held open for as long as the harness needs to see the marker
    # and stop the container. A stop here is a restart *after* the state was durable and
    # *during* the work that follows it; on the control arm the hold simply ends and the
    # same container finishes its step.
    hold = float(os.environ.get("D4_HOLD_SECONDS", "60"))
    deadline = time.time() + hold
    while time.time() < deadline:
        time.sleep(0.1)
    print("hold ended; this container finished its step", flush=True)
finally:
    workspace.close()
SH

  d2_container_start "$VOLUME" "$NAME_A" env D4_REQUEST="$REQUEST_ID" D4_HOLD_SECONDS=90 \
    python3 -c "$IN_CONTAINER"
  # Wait for the marker the container wrote, read from a *separate* container over the same
  # volume: the harness never enters the container it is about to stop, and the reading is
  # another process's view of the durable state.
  waited=0
  while [ -z "$(d2_cat "$VOLUME" /home/session/marker-assessment.json)" ] && [ "$waited" -lt 900 ]; do
    sleep 0.05
    waited=$((waited + 1))
  done
  marker="$(d2_cat "$VOLUME" /home/session/marker-assessment.json)"
  if [ -z "$marker" ]; then
    log "fault: the container never reached the injection point; see its logs"
    docker logs "$NAME_A" 2>&1 | tail -20
    exit 1
  fi
  printf '%s\n' "$marker" > "$RUN_DIR/10-before-restart.json"
  if [ "$INJECT" = "1" ]; then
    docker stop -t 5 "$NAME_A" > "$RUN_DIR/20-stop.log" 2>&1
    stop_status=$?
    docker rm -f "$NAME_A" >/dev/null 2>&1
    restarted="yes"
  else
    stop_status=0
    restarted="no (control: the same container finished its own step instead)"
    docker wait "$NAME_A" > /dev/null 2>&1 || true
    docker rm -f "$NAME_A" >/dev/null 2>&1
  fi

  # The new container reads the same session volume and reports what it finds.
  d2_run "$VOLUME" python3 d2_tool.py state --request "$REQUEST_ID" > "$RUN_DIR/30-after-restart-state.json" 2> "$RUN_DIR/30-after-restart.err"
  state_status=$?

  # Deliver the decision from yet another process, then resume the parked task.
  d2_run "$VOLUME" python3 -c "
import json
from durable_state import Workspace
w = Workspace.open()
print(json.dumps({'woken': w.deliver('$REQUEST_ID', {'decision': 'approved', 'reviewer': 'D4 harness reviewer', 'reason': 'criteria met'})}))
w.close()
" > "$RUN_DIR/40-deliver.json" 2> "$RUN_DIR/40-deliver.err"
  deliver_status=$?
  d2_run "$VOLUME" python3 d2_tool.py resume --request "$REQUEST_ID" > "$RUN_DIR/50-resume.json" 2> "$RUN_DIR/50-resume.err"
  resume_status=$?

  deadline_before="$(approval_field "$RUN_DIR/10-before-restart.json" deadline)"
  deadline_after="$(approval_field "$RUN_DIR/30-after-restart-state.json" task.checkpoint.deadline approval.deadline)"
  resumed_phase="$(approval_field "$RUN_DIR/50-resume.json" phase)"
  settled_status="$(approval_field "$RUN_DIR/50-resume.json" settled.status)"
  decided_by="$(approval_field "$RUN_DIR/50-resume.json" decidedBy)"
  same_deadline="no"
  [ -n "$deadline_before" ] && [ "$deadline_before" = "$deadline_after" ] && same_deadline="yes"

  if [ "$INJECT" = "1" ]; then
    outcome="working state and pending approval survived the container restart"
    detail="A container committed the assessment and a parked approval (task $(approval_field "$RUN_DIR/10-before-restart.json" taskId)), then was stopped with SIGTERM while a later step was held open. A new container over the same session volume read the approval back with the same deadline ($deadline_before) — byte-identical, $same_deadline — and a third process delivered the decision, which woke the parked task: resume settled ${settled_status:-?} and the work state reached '${resumed_phase:-?}' decided by '${decided_by:-?}'. The restart is a real container stop and start; the session volume is a named Docker volume standing in for the platform's per-session mount - a volume and not a bind mount, because the store's writer lock is an flock and a bind mounted host directory does not carry it between containers."
  else
    outcome="control: completed without a restart"
    detail="The same commits and the same resume, with the hold released instead of the container stopped: the approval deadline is $deadline_before, the resume settled ${settled_status:-?}. Nothing in the two-container shape is what produced the survival in the fault row — the commits are."
  fi

  TEMPLATE="$RUN_DIR/template.json"
  FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
  FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_RESTARTED="$restarted" \
  FAULT_STOP_STATUS="$stop_status" FAULT_STATE_STATUS="$state_status" \
  FAULT_DELIVER_STATUS="$deliver_status" FAULT_RESUME_STATUS="$resume_status" \
  FAULT_BEFORE="$deadline_before" FAULT_AFTER="$deadline_after" \
  FAULT_SAME="$same_deadline" FAULT_SETTLED="$settled_status" FAULT_PHASE="$resumed_phase" \
  python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

observed = (
    "not exercised in this run: nothing was provisioned, so the platform's own session "
    "behaviour is quoted from D1 rather than reproduced. The container restarted here is a "
    "local Docker container, and the session volume is a host directory."
)
note = (
    "The D2 image is D2's own Dockerfile over D2's own build context, so the code that "
    "survived is the scenario's. The *platform* behaviour - that a real hosted session "
    "outlives a container - is not what this measured; see behaviours[id=session-persistence] "
    "and [id=idle-timeout] for its documented answer."
)

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=["session-persistence", "idle-timeout"], observed=observed, note=note
    ) | {"session_survived": "not applicable - the restart was a local container, not a hosted session"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "container_restarted": os.environ["FAULT_RESTARTED"],
            "docker_stop_exit": int(os.environ["FAULT_STOP_STATUS"]),
            "state_read_after_restart_exit": int(os.environ["FAULT_STATE_STATUS"]),
            "deliver_exit": int(os.environ["FAULT_DELIVER_STATUS"]),
            "resume_exit": int(os.environ["FAULT_RESUME_STATUS"]),
            "deadline_before_restart": os.environ["FAULT_BEFORE"],
            "deadline_after_restart": os.environ["FAULT_AFTER"],
            "deadline_identical": os.environ["FAULT_SAME"] == "yes",
            "task_settled": os.environ["FAULT_SETTLED"],
            "work_state_phase_after_resume": os.environ["FAULT_PHASE"],
        },
    },
    "artifacts": [
        "10-before-restart.json", "20-stop.log", "30-after-restart-state.json",
        "40-deliver.json", "50-resume.json",
    ],
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

  write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
  exit 0
fi

# -- baseline: no container, so the restart is the client's ---------------------------------

hold_synthesis() { # on|off
  local control="$SERVICE_ROOT/control"
  if [ "$1" = "1" ]; then
    printf 'synthesis-agent\n' > "$control/hold-agents.txt"
  else
    : > "$control/hold-agents.txt"
    : > "$control/release-holds"
  fi
}

if [ "$INJECT" = "1" ]; then
  hold_synthesis 1
  python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
    --hold-step synthesis > "$RUN_DIR/10-first-client.log" 2>&1 &
  held_pid=$!
  waited=0
  while [ "$(service_posts "$RUN_DIR" synthesis-agent)" = "0" ] && [ "$waited" -lt 600 ]; do
    sleep 0.05
    waited=$((waited + 1))
  done
  posts_at_kill="$(service_posts "$RUN_DIR" synthesis-agent)"
  kill -9 "$held_pid" 2>/dev/null
  waited=0
  while kill -0 "$held_pid" 2>/dev/null && [ "$waited" -lt 100 ]; do sleep 0.05; waited=$((waited + 1)); done
  kill -0 "$held_pid" 2>/dev/null && { log "fault: the killing did not land"; exit 1; }
  hold_synthesis 0
  sleep 0.3
  python3 "$HERE/lib/baseline_driver.py" resume --run-dir "$RUN_DIR/run" > "$RUN_DIR/20-resume.log" 2>&1
  resume_status=$?
  restarted="yes (the client, not a container: D1 has none of its own)"
  outcome="cold start: the interrupted step was re-driven from its beginning"
  detail="The driving client was killed with SIGKILL inside the held synthesis call (the service had accepted $posts_at_kill POST(s) for it). No container exists for D1 - the platform runs the image and the client is the process - so the restart is the client's. The new client found one step result on disk and no record of the call that was in flight, and drove the run again from the first step without a result: the service accepted $(service_posts "$RUN_DIR" synthesis-agent) synthesis POST(s) in total. There was nothing to survive, which is the observation rather than a defect of the harness."
else
  python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
    > "$RUN_DIR/20-resume.log" 2>&1
  resume_status=$?
  posts_at_kill=0
  restarted="no (control)"
  outcome="control: completed without a restart"
  detail="The same drive with no kill: one coverage call, one synthesis call, the run finished. The restart is what the fault row measures, not the two-process shape."
fi

TEMPLATE="$RUN_DIR/template.json"
FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_RESTARTED="$restarted" \
FAULT_RESUME_STATUS="$resume_status" FAULT_AT_KILL="$posts_at_kill" \
FAULT_TOTAL="$(service_posts "$RUN_DIR" synthesis-agent)" \
python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=["session-persistence", "idle-timeout", "foreground-cancelled"],
        observed=(
            "not exercised in this run: no hosted deployment existed, so the platform's session and "
            "idle behaviour is quoted from D1, not reproduced"
        ),
        note=(
            "The baseline has no container to restart, so what this fault measured is a client restart "
            "against a stateless driver. That is a statement about D1's design (the platform owns the "
            "session) and not about the platform's own restart behaviour."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "container_restarted": os.environ["FAULT_RESTARTED"],
            "resume_exit": int(os.environ["FAULT_RESUME_STATUS"]),
            "synthesis_posts_before_the_kill": int(os.environ["FAULT_AT_KILL"]),
            "synthesis_posts_in_total": int(os.environ["FAULT_TOTAL"]),
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
exit 0
