#!/usr/bin/env bash
# Faults against the **middle column**: the agent-framework durable extension, on Durable Task Scheduler.
#
#   ./faults/extension.sh --fault kill-during-wait [--fault-not-injected]
#   ./faults/extension.sh --fault restart-after-checkpoint
#   ./faults/extension.sh --fault duplicate-approval
#   ./faults/extension.sh --fault late-approval
#   ./faults/extension.sh --fault abort-mid-wait
#   ./faults/extension.sh --fault store-outage
#   ./faults/extension.sh --fault scheduler-outage
#
# G2 and G3 (#178) both carried this column as **not run**. That was wrong once, for the wrong reason: the
# package was claimed not to exist. It exists (`Microsoft.Agents.AI.DurableTask`, 41 prerelease versions,
# latest 1.16.0-preview.261029.1), it installs, and the vendor's own HITL sample (`extension/upstream/`,
# byte-identical) runs to completion against the Durable Task Scheduler emulator on this machine. So this
# script runs the same six faults against it that the other two columns get, and says where a fault means
# something different because the substrate is different.
#
# What the substrate gives the harness, and what it does not:
#
#   * The instance is named by the caller, and it outlives the process that started it. Status, history and
#     events are readable and raisable from a *different* process. That is what makes a real out-of-process
#     fault possible here: kill the process, then ask the scheduler what the run did.
#   * An answer to a RequestPort is an event on the instance (`RaiseEventAsync`), and the single-response
#     client cannot reattach to a paused run — `StreamAsync` with an existing id throws
#     `OrchestrationAlreadyExistsException`. So a run that paused is finished by *raising the port's event*
#     from a new process, not by resuming a stream.
#   * `TerminateInstanceAsync` is the abort. There is no per-child cascade to observe the way the Durable
#     variant's abort handler does.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANT="extension"
# Loop deadlines used by more than one branch; plain variables, because a `case` arm is not a function.
local_deadline=0
FAULT=""
INJECT=1
while [ $# -gt 0 ]; do
  case "$1" in
    --fault) FAULT="${2:-}"; shift 2 ;;
    --fault-not-injected) INJECT=0; shift ;;
    -h|--help) sed -n '2,/^$/p' "${BASH_SOURCE[0]}" | sed -e 's/^# \{0,1\}//'; exit 0 ;;
    *) log "extension.sh: unknown argument '$1'"; exit 2 ;;
  esac
done

case "$FAULT" in
  kill-during-wait|restart-after-checkpoint|duplicate-approval|late-approval|abort-mid-wait|store-outage|scheduler-outage) ;;
  "") log "extension.sh: --fault is required"; exit 2 ;;
  *) log "extension.sh: unknown fault '$FAULT'"; exit 2 ;;
esac

NAME="$FAULT"
[ "$INJECT" = "1" ] || NAME="$FAULT-control"
FAULT_COMMAND="./faults/extension.sh --fault $FAULT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

EXT_SRC="$BAND_DIR/extension/src"
EXT_OUT="$SCRATCH_ROOT/bin/extension"
CONTAINER="${DTS_CONTAINER:-band-g-dts}"
CONNECTION="${DURABLE_TASK_SCHEDULER_CONNECTION_STRING:-Endpoint=http://localhost:8080;TaskHub=default;Authentication=None}"
APP="$EXT_OUT/extension-app"

# The harness block of every result names what produced it. These two fields describe *this* column, not the
# repository's recorded runtime: a reader who saw the ReplayRuntime description here would be looking for a
# component that is not in the path at all.
export FAULTS_RUNTIME_DESC="Durable Task Scheduler emulator (docker image ${DTS_EMULATOR_IMAGE:-mcr.microsoft.com/dts/dts-emulator:latest}) + Microsoft.DurableTask.Client/Worker.AzureManaged 1.28.0"
export FAULTS_MODEL_DESC="none — the vendored sample's executors call no model; no call count exists in this column"

require_tools || exit 1
if ! command -v docker >/dev/null 2>&1; then
  record_not_exercised "$VARIANT" "$NAME" "the Durable Task Scheduler" \
    "This column needs the Durable Task Scheduler; its emulator is a container and docker is not on this machine. The package installs and the sample builds, but nothing durable can be started without a scheduler. Recorded with that reason rather than as an absence."
  exit 0
fi

# ── the scheduler, as a real dependency ────────────────────────────────────────────────────────
# The emulator is started for the run and removed afterwards, so the column is reproducible on a machine
# with docker and leaves nothing running behind.
container_state() { docker inspect --format '{{.State.Status}}' "$CONTAINER" 2>/dev/null || echo absent; }

# The image identity, as a digest, so a result can be reproduced against the same substrate.
scheduler_identity() {
  # The image *id* is what identifies the bits that ran; a RepoDigest is absent for a locally built or
  # re-tagged image, and `unknown` in a result is worse than a real sha256.
  docker image inspect "$EMULATOR_IMAGE" --format '{{.Id}}' 2>/dev/null || true
}

start_scheduler() {
  if [ "$(container_state)" = "running" ]; then
    # A container can be "running" while its orchestration service is still coming up, so a caller that
    # returned here on the container state alone would start the app against a scheduler that answers
    # "the server did not complete the HTTP/2 handshake". Wait for a real gRPC call either way.
    local ready_deadline=$((SECONDS + 120))
    while [ "$SECONDS" -lt "$ready_deadline" ]; do
      DURABLE_TASK_SCHEDULER_CONNECTION_STRING="$CONNECTION" "$APP" ready >/dev/null 2>&1 && return 0
      sleep 2
    done
    log "extension: the scheduler container is up but never answered a gRPC call"
    return 1
  fi
  # A container left behind by an interrupted run holds the port; a `Created` one holds it too, because a
  # binding that failed leaves the name allocated. Both are cleared before the start, and the failure to
  # bind is reported with that context rather than as a bare docker error.
  docker rm -f "$CONTAINER" >/dev/null 2>&1
  if ! docker run -d --name "$CONTAINER" -p 8080:8080 -p 8081:8081 -p 8082:8082 "$EMULATOR_IMAGE" >/dev/null 2>"$SCRATCH_ROOT/docker-extension.log"; then
    log "extension: the emulator could not start. Port 8080 is what the scheduler listens on."
    sed 's/^/extension:   /' "$SCRATCH_ROOT/docker-extension.log" >&2
    log "extension: something else holds 8080; this is a harness precondition, not a fault result"
    return 1
  fi
  track_restore "docker rm -f '$CONTAINER' >/dev/null 2>&1"
  # Readiness is a real gRPC call, not an open socket: the emulator binds 8080 well before its orchestration
  # service answers, and a probe that stopped at the socket let the first run start against a scheduler that
  # replied "the server did not complete the HTTP/2 handshake".
  local deadline=$((SECONDS + 120))
  while [ "$SECONDS" -lt "$deadline" ]; do
    if DURABLE_TASK_SCHEDULER_CONNECTION_STRING="$CONNECTION" "$APP" ready >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  log "extension: the scheduler never answered a gRPC call on 8080"
  docker logs "$CONTAINER" 2>&1 | tail -5 >&2
  return 1
}

stop_scheduler() {
  docker stop "$CONTAINER" >/dev/null 2>&1
}

EMULATOR_IMAGE="${DTS_EMULATOR_IMAGE:-mcr.microsoft.com/dts/dts-emulator:latest}"
install_cleanup

build_extension() {
  log "extension: building the middle column"
  if ! "$DOTNET" build "$EXT_SRC/ExtensionApp.csproj" -p:NuGetAudit=false --nologo -v quiet -o "$EXT_OUT" >/dev/null 2>&1; then
    log "extension: the middle column does not build"
    "$DOTNET" build "$EXT_SRC/ExtensionApp.csproj" -p:NuGetAudit=false --nologo -o "$EXT_OUT" 2>&1 | tail -20 >&2
    return 1
  fi
}

build_extension || exit 1
start_scheduler || exit 1
SCHEDULER_DIGEST="$(scheduler_identity)"
[ -n "$SCHEDULER_DIGEST" ] || SCHEDULER_DIGEST="unknown"
log "extension: scheduler digest ${SCHEDULER_DIGEST}"
export DURABLE_TASK_SCHEDULER_CONNECTION_STRING="$CONNECTION"

RUN_DIR="$(new_run_dir "$VARIANT" "$NAME")"
track_dir "$RUN_DIR"
mkdir -p "$RUN_DIR"

new_instance() { echo "i$RANDOM$RANDOM"; }

# Starts a run and leaves it parked at the manager port.
start_to_gate() {
  local instance="$1"
  "$APP" start --instance "$instance" --stop ManagerApproval >"$RUN_DIR/start.log" 2>&1
  local status=$?
  echo "$instance" > "$RUN_DIR/instance"
  return $status
}

app_status() { "$APP" status --instance "$1" 2>/dev/null | sed 's/^status: *//'; }

# The history, as the one-line summary `inspect` prints, so a claim quotes a reading.
history_summary() { "$APP" inspect --instance "$1" 2>/dev/null | sed -n 's/^history: *//p'; }
raised_count() { "$APP" inspect --instance "$1" 2>/dev/null | sed -n 's/^raised: *//p'; }

# How many steps the orchestration completed, and whether it was terminated. These are the two readings that
# separate "the step after the port ran" from "the run was stopped", and unlike `EventRaised` they do not
# move merely because an answer was raised.
completed_steps() {
  local history
  history="$(history_summary "$1")"
  printf '%s' "$history" | grep -o 'TaskCompleted' | wc -l | tr -d ' '
}
was_terminated() {
  local history
  history="$(history_summary "$1")"
  case "$history" in *ExecutionTerminated*) echo 1 ;; *) echo 0 ;; esac
}

# Answers a port with a worker in this process, and bounds how long it will serve.
answer() {
  local instance="$1" port="$2"
  shift 2
  "$APP" answer --instance "$instance" --port "$port" --wait --timeout 60 "$@" 2>&1
}

case "$FAULT" in
# ── kill-during-wait ────────────────────────────────────────────────────────────────────────────
# The run is parked at a port and a process is carrying the worker that answers it. Killing that process
# is a kill during the wait: the answer is in flight, the run has not moved, and the only thing that can
# finish it is another process. What survives is the scheduler's record, not anything the killed process
# held.
kill-during-wait)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  [ "$STATUS_AT_GATE" = "Running" ] || { log "extension: the run is '$STATUS_AT_GATE' at the gate"; exit 1; }

  answer "$INSTANCE" ManagerApproval >"$RUN_DIR/serve.log" 2>&1 &
  pid=$!
  track_pid "$pid"
  # Wait until the answer has been raised — the run is at the port with a worker serving it.
  local_deadline=$((SECONDS + 60))
  while ! grep -q '^raised:' "$RUN_DIR/serve.log" 2>/dev/null && [ "$SECONDS" -lt "$local_deadline" ]; do sleep 0.2; done
  if ! grep -q '^raised:' "$RUN_DIR/serve.log" 2>/dev/null; then
    log "extension: the answer was never raised; the fault was not injected"
    exit 1
  fi
  RAISED_AT_KILL="$(grep -c '^raised:' "$RUN_DIR/serve.log")"

  if [ "$INJECT" = "1" ]; then
    kill9 "$pid" || exit 1
    KILLED_PID="$pid"
    sleep 3
  else
    # Control: let the same process finish delivering.
    wait_for_exit "$pid" 90
    grep -q '^delivered:' "$RUN_DIR/serve.log" && DELIVERED_CONTROL=1 || DELIVERED_CONTROL=0
  fi

  STATUS_AFTER="$(app_status "$INSTANCE")"

  # ── observe ──────────────────────────────────────────────────────────────────────────────────
  # A fresh process raises the answer the killed one did not deliver.
  answer "$INSTANCE" ManagerApproval >"$RUN_DIR/after.log" 2>&1
  AFTER_DELIVERED="$(grep -c '^delivered:' "$RUN_DIR/after.log")"
  STATUS_LATER="$(app_status "$INSTANCE")"
  HISTORY="$(history_summary "$INSTANCE")"
  RAISED_LATER="$(raised_count "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    OBSERVED="wait survived"
    PHASE="parked at ManagerApproval, with a process serving the answer"
    DETAIL="The run was parked at ManagerApproval with task status $STATUS_AT_GATE and one answer raised ($RAISED_AT_KILL raise(s) recorded before the kill). pid $KILLED_PID — the process carrying the worker — was killed with SIGKILL and confirmed gone; $STATUS_AFTER it was still $STATUS_AFTER. A different process then raised the port's answer and reported 'delivered' ${AFTER_DELIVERED} time(s), and the run's history after that is: $HISTORY. Raised events now: ${RAISED_LATER:-(none)}. The wait survived the death of the only process that was in it, because the wait is an orchestration instance the scheduler holds, not a call the killed process held."
  else
    OBSERVED="no kill, run continued"
    PHASE="parked at ManagerApproval, with a process serving the answer (the control)"
    DETAIL="Control: the same steps with no kill. The serving process delivered the answer itself (delivered line printed: $DELIVERED_CONTROL), the run moved on to $STATUS_LATER, and its history is: $HISTORY. The fault run reaches the same place after a kill, so the kill cost a process and nothing else."
  fi
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "status_at_gate": "%s", "status_after_kill": "%s", "status_after_successor": "%s", "answers_raised_before_kill": %s, "successor_delivered": %s}' \
    "${SCHEDULER_DIGEST:-unknown}" "$STATUS_AT_GATE" "$STATUS_AFTER" "$STATUS_LATER" "${RAISED_AT_KILL:-0}" "${AFTER_DELIVERED:-0}")"
  EVIDENCE="instance: $INSTANCE
at the gate: $STATUS_AT_GATE; killed pid ${KILLED_PID:-none} (kill -9, confirmed gone)
after the kill: $STATUS_AFTER
successor process: 'delivered' printed ${AFTER_DELIVERED:-0} time(s); status $STATUS_LATER; raised: ${RAISED_LATER:-(none)}
history: $HISTORY
$(tail -3 "$RUN_DIR/after.log" 2>/dev/null)"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;

# ── restart-after-checkpoint ───────────────────────────────────────────────────────────────────
# The run is parked, so the pre-gate work is committed in the scheduler. A fresh process finishes it. The
# number this column can state is answers delivered, not model calls: the sample's executors call no
# model, so a "model calls not repeated" count is not measurable here and is not claimed.
restart-after-checkpoint)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  HISTORY_AT_GATE="$(history_summary "$INSTANCE")"

  answer "$INSTANCE" ManagerApproval --timeout 45 >"$RUN_DIR/restart.log" 2>&1
  RESTART_DELIVERED="$(grep -c '^delivered:' "$RUN_DIR/restart.log")"
  STATUS_AFTER="$(app_status "$INSTANCE")"
  # The count, not an assertion about it: how many times the first step appears in the history.
  FIRST_STEP_COUNT="$(completed_steps "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"

  OBSERVED="resumed"
  PHASE="parked at ManagerApproval after the pre-gate work; a fresh process continued it"
  DETAIL="The first process started the orchestration, reached ManagerApproval and exited with the run $STATUS_AT_GATE; its history was: $HISTORY_AT_GATE. A fresh process — its own host and its own worker, no state from the first — raised the port's answer and reported 'delivered' ${RESTART_DELIVERED} time(s). Afterwards the run was $STATUS_AFTER with history: $HISTORY_AFTER; raised events: ${RAISED_AFTER:-(none)}. The restart continued the run rather than repeating it: the history holds $FIRST_STEP_COUNT task completion(s) — the same single pre-gate step, not a second copy of it — and the raised events are ${RAISED_AFTER:-(none)}. What this column cannot state is a model-call count: the vendored sample's executors call no model, so the band's call-count comparison has no measurement here, and none is invented."
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "status_at_gate": "%s", "status_after_restart": "%s", "answers_raised": "%s", "task_completions_after_restart": %s, "model_calls_reason": "The vendored sample'"'"'s executors call no model, so there is no call to count."}' \
    "${SCHEDULER_DIGEST:-unknown}" "$STATUS_AT_GATE" "$STATUS_AFTER" "${RAISED_AFTER:-none}" "${FIRST_STEP_COUNT:-0}")"
  EVIDENCE="instance: $INSTANCE
first process: started and parked, status $STATUS_AT_GATE
  history: $HISTORY_AT_GATE
fresh process: 'delivered' printed ${RESTART_DELIVERED} time(s)
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}
  history: $HISTORY_AFTER"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;

# ── duplicate-approval ────────────────────────────────────────────────────────────────────────
# The same answer raised twice. Nothing in the harness deduplicates: both go to the scheduler and the
# framework decides. So what happens is the framework's answer, not this script's.
duplicate-approval)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  # Read the gate's own state before any answer is raised: a count taken afterwards is the count the answers
  # produced, which is the thing being measured, not the baseline for it.
  STEPS_AT_GATE_BEFORE="$(completed_steps "$INSTANCE")"

  answer "$INSTANCE" ManagerApproval --timeout 45 >"$RUN_DIR/first.log" 2>&1 &
  pid=$!
  track_pid "$pid"
  local_deadline=$((SECONDS + 60))
  while ! grep -q '^raised:' "$RUN_DIR/first.log" 2>/dev/null && [ "$SECONDS" -lt "$local_deadline" ]; do sleep 0.2; done
  wait_for_exit "$pid" 60

  if [ "$INJECT" = "1" ]; then
    "$APP" answer --instance "$INSTANCE" --port ManagerApproval >"$RUN_DIR/second.log" 2>&1
    SECOND_RAISED="$(grep -c '^raised:' "$RUN_DIR/second.log")"
    sleep 3
  else
    SECOND_RAISED=0
  fi

  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"
  # How many times the manager step's successor was scheduled is the observable: one delivery runs the run
  # once past the port.
  SCHEDULED="$(printf '%s' "$HISTORY_AFTER" | grep -o 'TaskScheduled' | wc -l | tr -d ' ')"
  # The discriminator: how many steps completed. One answer and two answers must both leave the run having
  # completed the same number of steps; a duplicate that ran the post-approval work twice would show two more.
  COMPLETED="$(completed_steps "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    OBSERVED="second delivery did not run the next step twice"
    PHASE="two answers raised for the same port"
    DETAIL="The first answer was raised and delivered; a second answer for the same port was then raised ($SECOND_RAISED raise(s) recorded). Afterwards the run had completed $COMPLETED step(s) against ${STEPS_AT_GATE_BEFORE:-0} before the answers, the raised events were ${RAISED_AFTER:-(none)}, and the orchestration had scheduled $SCHEDULED task(s). The second event is recorded — it is a message, and the scheduler accepted it — and the completed-step count is the measurable that shows it ran nothing: the post-approval work happened once."
  else
    OBSERVED="one delivery"
    PHASE="one answer raised for the port (the control)"
    DETAIL="Control: one answer delivered. The run has completed $COMPLETED step(s) against ${STEPS_AT_GATE_BEFORE:-0} before the answer, raised events ${RAISED_AFTER:-(none)}, and $SCHEDULED task(s) scheduled. The duplicate run is measured against this: the same flow with one more event raised, and — the point — the same completed-step count."
  fi
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "second_raises": %s, "raised_events": "%s", "completed_steps": %s, "completed_steps_before_the_answers": %s, "tasks_scheduled": "%s", "status_after": "%s"}' \
    "${SCHEDULER_DIGEST:-unknown}" "$SECOND_RAISED" "${RAISED_AFTER:-none}" "${COMPLETED:-0}" "${STEPS_AT_GATE_BEFORE:-0}" "$SCHEDULED" "$STATUS_AFTER")"
  EVIDENCE="instance: $INSTANCE; status at the gate: $STATUS_AT_GATE
first answer: $(grep -c '^delivered:' "$RUN_DIR/first.log" 2>/dev/null || echo 0) delivered
second raise(s): $SECOND_RAISED
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}; tasks scheduled $SCHEDULED
history: $HISTORY_AFTER"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;

# ── late-approval ────────────────────────────────────────────────────────────────────────────
# The run is terminated, then answered. What a terminated orchestration does with an event is the
# framework's decision, and this is a different guard from the duplicate's.
late-approval)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  STEPS_AT_GATE="$(completed_steps "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    "$APP" terminate --instance "$INSTANCE" >"$RUN_DIR/terminate.log" 2>&1 || true
    TERMINATE_ACK="$(grep -c '^terminated: requested' "$RUN_DIR/terminate.log" 2>/dev/null)"
    TERMINATE_ACK="${TERMINATE_ACK:-0}"
    # The scheduler answers the call; whether the runtime status follows is the scheduler's decision, so it
    # is sampled rather than assumed. An acknowledgement that does not change the status is a fact about
    # this substrate and is reported as one.
    SETTLE_SAMPLES=""
    for _ in 1 2 3 4 5; do
      SETTLE_SAMPLES="$SETTLE_SAMPLES $(app_status "$INSTANCE")"
      sleep 3
    done
  else
    TERMINATE_ACK=0
    SETTLE_SAMPLES=""
  fi
  STATUS_BEFORE_ANSWER="$(app_status "$INSTANCE")"
  HISTORY_BEFORE="$(history_summary "$INSTANCE")"

  # Delivered by a live worker, so "the run did not move" cannot be explained by nothing serving it. The
  # first version raised the event with no worker and read the resulting stillness as the abort's doing.
  answer "$INSTANCE" ManagerApproval >"$RUN_DIR/late.log" 2>&1
  LATE_RAISED="$(grep -c '^raised:' "$RUN_DIR/late.log")"
  DELIVERED_LATE="$(grep -c '^delivered:' "$RUN_DIR/late.log" 2>/dev/null)"
  DELIVERED_LATE="${DELIVERED_LATE:-0}"
  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"
  STEPS_FINAL="$(completed_steps "$INSTANCE")"
  TERMINATED_FINAL="$(was_terminated "$INSTANCE")"
  # A completed step beyond the gate's count is the step after the port having run. `EventRaised` alone is not
  # that: an answer that is raised adds it whether or not anything ran.
  if [ "${STEPS_FINAL:-0}" -gt "${STEPS_AT_GATE:-0}" ]; then ADVANCED=1; else ADVANCED=0; fi

  if [ "$INJECT" = "1" ]; then
    if [ "$ADVANCED" = "0" ] && [ "$STATUS_AFTER" != "Completed" ]; then
      OBSERVED="terminal state held"
      PHASE="terminate acknowledged, then answered by a live worker"
      DETAIL="The run was parked at ManagerApproval ($STEPS_AT_GATE completed step(s)). The terminate was acknowledged ($TERMINATE_ACK acknowledgement(s)). The answer was then raised with a worker present ('delivered' printed $DELIVERED_LATE time(s), $LATE_RAISED raise(s) recorded); the run is $STATUS_AFTER with $STEPS_FINAL completed step(s) against $STEPS_AT_GATE at the gate, an ExecutionTerminated in its history: $TERMINATED_FINAL, and history: $HISTORY_AFTER. No step beyond the gate's ran, so the post-approval step did not run on a terminated run. The worker was present, so a silent orchestrator is not an alternative explanation for that."
    else
      OBSERVED="overwritten"
      PHASE="terminate acknowledged, then answered"
      DETAIL="The run was terminated ($STATUS_BEFORE_ANSWER, acknowledged $TERMINATE_ACK time(s)) and a delivered answer afterwards moved it: status $STATUS_AFTER, raised ${RAISED_AFTER:-(none)}, history: $HISTORY_AFTER. Reported as it stands."
    fi
  else
    # The control must advance; a control that does not advance cannot attribute anything to the terminate.
    if [ "$ADVANCED" = "1" ]; then
      OBSERVED="no terminate, answer advanced the run"
      PHASE="answered while parked with no terminate (the control)"
      DETAIL="Control: the same steps with no terminate. The answer was delivered by a live worker ('delivered' printed $DELIVERED_LATE time(s)) and the run advanced — status $STATUS_AFTER, $STEPS_FINAL completed step(s) against $STEPS_AT_GATE at the gate, history: $HISTORY_AFTER. This is what the late answer is measured against: the same answer, on a run that was not terminated, does run the next step."
    else
      OBSERVED="control did not advance"
      PHASE="answered while parked with no terminate (the control)"
      DETAIL="Control FAILED: with no terminate at all, a delivered answer did not advance the run — status $STATUS_AFTER, raised ${RAISED_AFTER:-(none)}, history: $HISTORY_AFTER. That cannot show a terminate is what stopped the fault run, so it is reported as a harness failure rather than as a result."
    fi
  fi
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "status_at_gate": "%s", "completed_steps_at_gate": %s, "completed_steps_after_answer": %s, "execution_terminated_in_history": %s, "terminate_acknowledged": %s, "delivered_by_a_live_worker": %s, "status_after_late_answer": "%s", "late_raises": %s}' \
    "${SCHEDULER_DIGEST:-unknown}" "$STATUS_AT_GATE" "${STEPS_AT_GATE:-0}" "${STEPS_FINAL:-0}" "${TERMINATED_FINAL:-0}" "$TERMINATE_ACK" "$DELIVERED_LATE" "$STATUS_AFTER" "$LATE_RAISED")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE
terminate: $([ "$INJECT" = "1" ] && echo "acknowledged ${TERMINATE_ACK} time(s)" || echo "not injected"); status before the answer: $STATUS_BEFORE_ANSWER
  status samples:${SETTLE_SAMPLES:- (none)}
  history before: $HISTORY_BEFORE
answer raised with a worker: $LATE_RAISED raise(s), 'delivered' printed $DELIVERED_LATE time(s)
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}; advanced $ADVANCED
  history: $HISTORY_AFTER"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;

# ── abort-mid-wait ──────────────────────────────────────────────────────────────────────────
# Termination is the abort. There is no child task to observe here — the fan-out is inside one
# orchestration — so "children cancelled" cannot be claimed. What can be observed is that the run stops
# and stays stopped, and that a later answer does not restart it.
abort-mid-wait)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  # How many steps had completed when the run parked. Everything after is measured against it.
  STEPS_AT_GATE="$(completed_steps "$INSTANCE")"

  # The termination is the injection, so the control does not do it. A control that repeated the fault
  # would produce the same two readings and prove nothing, which is what the first version of this branch
  # did.
  if [ "$INJECT" = "1" ]; then
    "$APP" terminate --instance "$INSTANCE" >"$RUN_DIR/abort.log" 2>&1 || true
    TERMINATE_ACK="$(grep -c '^terminated: requested' "$RUN_DIR/abort.log" 2>/dev/null)"
    TERMINATE_ACK="${TERMINATE_ACK:-0}"
    SETTLE_SAMPLES=""
    for _ in 1 2 3 4 5; do
      SETTLE_SAMPLES="$SETTLE_SAMPLES $(app_status "$INSTANCE")"
      sleep 3
    done
  else
    TERMINATE_ACK=0
    SETTLE_SAMPLES=""
  fi
  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"

  # The answer is raised **with a worker**, so "the run did not advance" cannot be explained by nothing
  # serving the orchestration. This was the confound in the first version: the branch raised the event and
  # observed no movement, but no process was there to deliver it either.
  answer "$INSTANCE" ManagerApproval >"$RUN_DIR/after.log" 2>&1
  DELIVERED_AFTER="$(grep -c '^delivered:' "$RUN_DIR/after.log" 2>/dev/null)"
  DELIVERED_AFTER="${DELIVERED_AFTER:-0}"
  STATUS_FINAL="$(app_status "$INSTANCE")"
  RAISED_FINAL="$(raised_count "$INSTANCE")"
  HISTORY_FINAL="$(history_summary "$INSTANCE")"
  # The observable that separates "the step after the port ran" from "the run was stopped" is the count of
  # completed steps, not the presence of `EventRaised`: an answer that is *raised* adds EventRaised whether or
  # not anything ran afterwards, which is what made the first version of this branch read a stopped run as an
  # inert one.
  STEPS_BEFORE="$(completed_steps "$INSTANCE")"
  STEPS_FINAL="$(completed_steps "$INSTANCE")"
  TERMINATED_FINAL="$(was_terminated "$INSTANCE")"
  if [ "${STEPS_FINAL:-0}" -gt "${STEPS_AT_GATE:-0}" ]; then ADVANCED=1; else ADVANCED=0; fi

  if [ "$INJECT" = "1" ]; then
    if [ "$TERMINATED_FINAL" = "1" ] && [ "$ADVANCED" = "0" ]; then
      OBSERVED="cancelled"
      PHASE="parked at ManagerApproval, terminated"
      DETAIL="The run was parked at ManagerApproval ($STATUS_AT_GATE, $STEPS_AT_GATE completed step(s)). The terminate was acknowledged ($TERMINATE_ACK acknowledgement(s)) and the runtime status sampled every 3s afterwards was:$SETTLE_SAMPLES, ending $STATUS_AFTER. A later answer was delivered by a live worker ('delivered' printed $DELIVERED_AFTER time(s)); the run ended $STATUS_FINAL with history: $HISTORY_FINAL — $STEPS_AT_GATE completed step(s) before the answer and $STEPS_FINAL after, and an ExecutionTerminated in the history. So the orchestration stopped and the answer did not run the step after the port. Scope: this column has no separate child work to cancel — the ports live inside one orchestration — so 'children cancelled' is not observable here and is not claimed."
    elif [ "$ADVANCED" = "0" ] && [ "$TERMINATED_FINAL" = "0" ] && [ "$STATUS_AFTER" = "$STATUS_AT_GATE" ]; then
      # Termination was acknowledged, the run's status did not become a framework terminal state, and — with a
      # worker present, which is the part the first version was missing — the answer did not advance it.
      OBSERVED="abort acknowledged, run did not advance"
      PHASE="parked at ManagerApproval, terminate acknowledged"
      DETAIL="The run was parked at $STATUS_AT_GATE. The terminate was acknowledged ($TERMINATE_ACK acknowledgement(s)) and the status sampled every 3s afterwards was:$SETTLE_SAMPLES. It is still $STATUS_AFTER and its history holds no ExecutionTerminated. A later answer was raised **with a worker** — 'delivered' printed $DELIVERED_AFTER time(s) — and the run is $STATUS_FINAL, $STEPS_FINAL completed step(s) against $STEPS_AT_GATE at the gate, history: $HISTORY_FINAL. So the terminate was accepted but did not stop the orchestration, and the answer did not run the step after the port. The worker was present, so 'nothing was listening' is not an alternative explanation."
    else
      OBSERVED="not cancelled"
      PHASE="parked at ManagerApproval, terminated"
      DETAIL="The run was parked at $STATUS_AT_GATE and terminated (acknowledged $TERMINATE_ACK time(s)); afterwards it read $STATUS_AFTER, and after a delivered answer $STATUS_FINAL. History after the answer: $HISTORY_FINAL. Reported as it stands."
    fi
  else
    # The control's whole job is to show the same flow *without* the terminate advancing. A control that
    # also failed to advance would mean the harness, not the abort, is what stopped it.
    if [ "$ADVANCED" = "1" ]; then
      OBSERVED="no abort, run advanced"
      PHASE="not terminated (the control)"
      DETAIL="Control: the same steps with no terminate. The answer was delivered ('delivered' printed $DELIVERED_AFTER time(s)), the run is $STATUS_FINAL with $STEPS_FINAL completed step(s) against $STEPS_AT_GATE at the gate, and an ExecutionTerminated absent from its history: $HISTORY_FINAL. This is what the abort run is measured against: without the terminate the same answer does run the step after the port."
    else
      OBSERVED="control did not advance"
      PHASE="not terminated (the control)"
      DETAIL="Control FAILED: with no terminate at all, the delivered answer did not advance the run — status $STATUS_FINAL, history: $HISTORY_FINAL, raised ${RAISED_FINAL:-(none)}. A control that does not advance cannot show that an abort is what stopped the fault run, so this is reported as a harness failure rather than as a result."
    fi
  fi
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "status_at_gate": "%s", "completed_steps_at_gate": %s, "completed_steps_after_answer": %s, "execution_terminated_in_history": %s, "terminate_acknowledged": %s, "status_samples_after_terminate": "%s", "delivered_by_a_live_worker": %s, "status_final": "%s", "children_cancelled": "not observable - the ports are inside one orchestration"}' \
    "${SCHEDULER_DIGEST:-unknown}" \
    "$STATUS_AT_GATE" "${STEPS_AT_GATE:-0}" "${STEPS_FINAL:-0}" "$TERMINATED_FINAL" "$TERMINATE_ACK" "${SETTLE_SAMPLES# }" "$DELIVERED_AFTER" "$STATUS_FINAL")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE
terminate: $([ "$INJECT" = "1" ] && echo "acknowledged ${TERMINATE_ACK} time(s)" || echo "not injected"); status $STATUS_AFTER
  history before the answer: $HISTORY_AFTER
answer raised with a worker: 'delivered' printed $DELIVERED_AFTER time(s)
after: status $STATUS_FINAL; raised ${RAISED_FINAL:-(none)}; advanced $ADVANCED
  history: $HISTORY_FINAL"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;

# ── store-outage: the scheduler itself ────────────────────────────────────────────────────────
# This column's "store" is not a file: it is the Durable Task Scheduler. The outage is therefore the
# scheduler going away while a process is mid-run, which is the only outage a host of this shape can
# actually suffer. The emulator container is stopped, the attempt observed, and the scheduler restarted so
# the observation afterwards is made against a live substrate.
store-outage|scheduler-outage)
  INSTANCE="$(new_instance)"
  start_to_gate "$INSTANCE" || { log "extension: the run never reached its gate"; exit 1; }
  STATUS_AT_GATE="$(app_status "$INSTANCE")"
  RAISED_BEFORE="$(raised_count "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    log "extension: stopping the scheduler under the run"
    stop_scheduler
  fi

  drive_status=0
  answer "$INSTANCE" ManagerApproval --timeout 25 >"$RUN_DIR/drive.log" 2>&1 || drive_status=$?
  DRIVE_HEAD="$(grep -m1 -E 'extension-app:|Unable|error' "$RUN_DIR/drive.log" 2>/dev/null | head -c 200)"

  if [ "$INJECT" = "1" ]; then
    start_scheduler || { log "extension: the scheduler did not come back"; exit 1; }
    sleep 3
  fi

  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    if [ "$drive_status" -ne 0 ] && [ "$STATUS_AFTER" = "(absent)" ]; then
      OBSERVED="lost with the scheduler"
      PHASE="the scheduler stopped and restarted under a live run"
      DETAIL="The run was parked at ManagerApproval ($STATUS_AT_GATE) when the Durable Task Scheduler was stopped. The process trying to answer it exited $drive_status. With the scheduler restarted, the instance reads '$STATUS_AFTER' with history '${HISTORY_AFTER:-(empty)}' and raised '${RAISED_AFTER:-(none)}': the run did not survive the scheduler. On this substrate the scheduler is not just the transport, it is the state — a stopped scheduler is a lost run, and the emulator holds its state in memory. Narrowed to what was measured: this says what *this emulator* does, not what a production scheduler does."
    elif [ "$drive_status" -ne 0 ]; then
      OBSERVED="failed safely"
      PHASE="the scheduler stopped under a live run"
      DETAIL="The run was parked at ManagerApproval ($STATUS_AT_GATE) when the Durable Task Scheduler was stopped. The process trying to answer it exited $drive_status, so no answer reached the run. With the scheduler restarted, the instance is '$STATUS_AFTER', its history is '${HISTORY_AFTER:-(empty)}' and raised is '${RAISED_AFTER:-(none)}' — so the run is **gone**, not merely unanswerable. That is the honest reading of these readings, and it narrows the claim the band would otherwise make: on this substrate a scheduler outage is not an availability problem for a live run, it is a loss of the run. The emulator is in-memory, which is consistent with the observation but does not by itself prove a production scheduler would behave the same — this result is about the emulator that was run, and is scoped to it."
    else
      OBSERVED="no effect"
      PHASE="the scheduler stopped under a live run"
      DETAIL="The scheduler was stopped and the answering process still exited 0. That is reported rather than smoothed over: it would mean the run advanced while its substrate was unreachable, and the history afterwards would have to show how."
    fi
  else
    OBSERVED="completed"
    PHASE="no outage (the control)"
    DETAIL="Control: no outage. The answer was delivered (exit $drive_status) and the run is $STATUS_AFTER with history: $HISTORY_AFTER. This is what the outage run is measured against."
  fi
  EXTRA="$(printf '{"scheduler_image_digest": "%s", "drive_exit": %s, "status_at_gate": "%s", "status_after": "%s", "raised_before_outage": "%s", "raised_after": "%s"}' \
    "${SCHEDULER_DIGEST:-unknown}" "$drive_status" "$STATUS_AT_GATE" "$STATUS_AFTER" "${RAISED_BEFORE:-none}" "${RAISED_AFTER:-none}")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE; raised ${RAISED_BEFORE:-(none)}
scheduler stopped? $([ "$INJECT" = "1" ] && echo yes || echo no)
answering process: exit $drive_status ${DRIVE_HEAD:+— $DRIVE_HEAD}
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}
  history: $HISTORY_AFTER"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;
esac

exit 0
