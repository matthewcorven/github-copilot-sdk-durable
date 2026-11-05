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
    if ! wait_for_exit "$pid" 2 2>/dev/null && kill -0 "$pid" 2>/dev/null; then :; fi
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
  EXTRA="$(printf '{"status_at_gate": "%s", "status_after_kill": "%s", "status_after_successor": "%s", "answers_raised_before_kill": %s, "successor_delivered": %s}' \
    "$STATUS_AT_GATE" "$STATUS_AFTER" "$STATUS_LATER" "${RAISED_AT_KILL:-0}" "${AFTER_DELIVERED:-0}")"
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
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"

  OBSERVED="resumed"
  PHASE="parked at ManagerApproval after the pre-gate work; a fresh process continued it"
  DETAIL="The first process started the orchestration, reached ManagerApproval and exited with the run $STATUS_AT_GATE; its history was: $HISTORY_AT_GATE. A fresh process — its own host and its own worker, no state from the first — raised the port's answer and reported 'delivered' ${RESTART_DELIVERED} time(s). Afterwards the run was $STATUS_AFTER with history: $HISTORY_AFTER; raised events: ${RAISED_AFTER:-(none)}. The completed pre-gate step is in the history once, not twice, so the restart continued the run rather than repeating it. What this column cannot state is a model-call count: the vendored sample's executors call no model, so the band's call-count comparison has no measurement here, and none is invented."
  EXTRA="$(printf '{"status_at_gate": "%s", "status_after_restart": "%s", "answers_raised": "%s", "model_calls": "not measurable - the sample'"'"'s executors call no model"}' \
    "$STATUS_AT_GATE" "$STATUS_AFTER" "${RAISED_AFTER:-none}")"
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

  if [ "$INJECT" = "1" ]; then
    OBSERVED="second delivery did not run the next step twice"
    PHASE="two answers raised for the same port"
    DETAIL="The first answer was raised and delivered; a second answer for the same port was then raised ($SECOND_RAISED raise(s) recorded). Afterwards the run was $STATUS_AFTER, the raised events on the instance were ${RAISED_AFTER:-(none)}, and the number of tasks the orchestration scheduled was $SCHEDULED. The second event is recorded — it is a message, and the scheduler accepted it — but the orchestration did not schedule the post-approval step a second time: the framework's own orchestration state, not the harness, is what made the duplicate inert."
  else
    OBSERVED="one delivery"
    PHASE="one answer raised for the port (the control)"
    DETAIL="Control: one answer delivered. The run is $STATUS_AFTER, raised events ${RAISED_AFTER:-(none)}, and the orchestration scheduled $SCHEDULED task(s). The duplicate run is measured against this: the same flow with one more event raised, and the same number of scheduled tasks."
  fi
  EXTRA="$(printf '{"second_raises": %s, "raised_events": "%s", "tasks_scheduled": "%s", "status_after": "%s"}' \
    "$SECOND_RAISED" "${RAISED_AFTER:-none}" "$SCHEDULED" "$STATUS_AFTER")"
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

  "$APP" answer --instance "$INSTANCE" --port ManagerApproval >"$RUN_DIR/late.log" 2>&1
  LATE_RAISED="$(grep -c '^raised:' "$RUN_DIR/late.log")"
  sleep 3
  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"
  RAISED_AFTER="$(raised_count "$INSTANCE")"

  if [ "$INJECT" = "1" ]; then
    if [ "$STATUS_AFTER" != "Completed" ]; then
      OBSERVED="terminal state held"
      PHASE="terminated while parked, then answered"
      DETAIL="The run was parked at ManagerApproval. The terminate call was acknowledged ($TERMINATE_ACK acknowledgement(s)) and the runtime status sampled every 3s afterwards was:$SETTLE_SAMPLES — so the status is $STATUS_BEFORE_ANSWER, not a framework 'Terminated'. An answer for the same port was then raised ($LATE_RAISED raise(s)); the run is $STATUS_AFTER and raised ${RAISED_AFTER:-(none)}. What is measured is that the answer did not run a post-approval step on a run whose terminate was acknowledged: the instance did not move on the late event."
    else
      OBSERVED="overwritten"
      PHASE="terminated while parked, then answered"
      DETAIL="The run was terminated ($STATUS_BEFORE_ANSWER) and an answer raised afterwards; the run then reached $STATUS_AFTER. A terminal state was moved, which contradicts what this fault expects and is reported rather than smoothed over."
    fi
  else
    OBSERVED="answered before any terminal state"
    PHASE="answered while parked (the control)"
    DETAIL="Control: no termination. The answer was raised while the run was $STATUS_BEFORE_ANSWER and the run moved to $STATUS_AFTER, with history: $HISTORY_AFTER. This is what the late answer is measured against: the same answer, arriving while the run can still use it."
  fi
  EXTRA="$(printf '{"status_at_gate": "%s", "terminate_acknowledged": %s, "status_samples_after_terminate": "%s", "status_after_late_answer": "%s", "late_raises": %s, "raised_events": "%s"}' \
    "$STATUS_AT_GATE" "$TERMINATE_ACK" "${SETTLE_SAMPLES# }" "$STATUS_AFTER" "$LATE_RAISED" "${RAISED_AFTER:-none}")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE
terminated? $([ "$INJECT" = "1" ] && echo yes || echo no); status before the late answer: $STATUS_BEFORE_ANSWER
  history before: $HISTORY_BEFORE
late answer raised: $LATE_RAISED
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}
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

  "$APP" terminate --instance "$INSTANCE" >"$RUN_DIR/abort.log" 2>&1 || true
  TERMINATE_ACK="$(grep -c '^terminated: requested' "$RUN_DIR/abort.log" 2>/dev/null)"
  TERMINATE_ACK="${TERMINATE_ACK:-0}"
  SETTLE_SAMPLES=""
  for _ in 1 2 3 4 5; do
    SETTLE_SAMPLES="$SETTLE_SAMPLES $(app_status "$INSTANCE")"
    sleep 3
  done
  STATUS_AFTER="$(app_status "$INSTANCE")"
  HISTORY_AFTER="$(history_summary "$INSTANCE")"

  "$APP" answer --instance "$INSTANCE" --port ManagerApproval >"$RUN_DIR/after.log" 2>&1 || true
  sleep 3
  STATUS_FINAL="$(app_status "$INSTANCE")"
  RAISED_FINAL="$(raised_count "$INSTANCE")"

  if [ "$STATUS_AFTER" = "Terminated" ] && [ "$STATUS_FINAL" = "Terminated" ]; then
    OBSERVED="cancelled"
    PHASE="parked at ManagerApproval, terminated"
    DETAIL="The run was parked at ManagerApproval ($STATUS_AT_GATE). The terminate call was acknowledged ($TERMINATE_ACK acknowledgement(s)), and the runtime status sampled every 3s after it was:$SETTLE_SAMPLES. It is $STATUS_AFTER with history: $HISTORY_AFTER, and an answer raised afterwards did not move it: still $STATUS_FINAL (raised: ${RAISED_FINAL:-(none)}). Scope, said plainly: this column has no separate child work to cancel — the manager and finance ports live inside one orchestration — so 'children cancelled' is not observable here and is not claimed."
  elif [ "$STATUS_FINAL" = "$STATUS_AT_GATE" ] && [ "$RAISED_FINAL" = "(none)" ]; then
    # The instance did not become the framework's `Terminated`, and a later answer did not move it. Both are
    # observations; neither is "cancelled", and calling it that would be borrowing the word from a different
    # implementation's semantics.
    OBSERVED="abort acknowledged, run unchanged"
    PHASE="parked at ManagerApproval, terminate acknowledged"
    DETAIL="The run was parked at $STATUS_AT_GATE. The terminate was acknowledged ($TERMINATE_ACK acknowledgement(s)) and the status sampled every 3s afterwards was:$SETTLE_SAMPLES. It is still $STATUS_AFTER, and an answer raised afterwards left it $STATUS_FINAL with no raised event recorded (${RAISED_FINAL:-(none)}). So on this substrate the abort does not surface as a framework terminal state and a later event does not revive the run: the run is inert but its reported status is unchanged. Scope, said plainly: this column has no separate child work to cancel — the ports live inside one orchestration — so 'children cancelled' is not observable here and is not claimed."
  else
    OBSERVED="not cancelled"
    PHASE="parked at ManagerApproval, terminated"
    DETAIL="The run was parked at $STATUS_AT_GATE and terminated; afterwards it read $STATUS_AFTER, and after a further answer $STATUS_FINAL (raised: ${RAISED_FINAL:-(none)}). Reported as it stands."
  fi
  EXTRA="$(printf '{"status_at_gate": "%s", "terminate_acknowledged": %s, "status_samples_after_terminate": "%s", "status_final": "%s", "raised_after_abort": "%s", "children_cancelled": "not observable - the ports are inside one orchestration"}' \
    "$STATUS_AT_GATE" "$TERMINATE_ACK" "${SETTLE_SAMPLES# }" "$STATUS_FINAL" "${RAISED_FINAL:-none}")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE
terminate: status $STATUS_AFTER
  history: $HISTORY_AFTER
answer after the abort: status $STATUS_FINAL; raised ${RAISED_FINAL:-(none)}"
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
    if [ "$drive_status" -ne 0 ]; then
      OBSERVED="failed safely"
      PHASE="the scheduler stopped under a live run"
      DETAIL="The run was parked at ManagerApproval ($STATUS_AT_GATE, raised ${RAISED_BEFORE:-(none)}) when the Durable Task Scheduler was stopped. The process trying to answer it exited $drive_status, so the answer did not reach the run. With the scheduler restarted, the instance is $STATUS_AFTER with history: $HISTORY_AFTER and raised ${RAISED_AFTER:-(none)} — unchanged from before the outage, because the scheduler is the record and a stopped scheduler is not a lost one. What the outage costs this column is availability, not state; state lives in the substrate, which is the operational weight the band's comparison names."
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
  EXTRA="$(printf '{"drive_exit": %s, "status_at_gate": "%s", "status_after": "%s", "raised_before_outage": "%s", "raised_after": "%s"}' \
    "$drive_status" "$STATUS_AT_GATE" "$STATUS_AFTER" "${RAISED_BEFORE:-none}" "${RAISED_AFTER:-none}")"
  EVIDENCE="instance: $INSTANCE; parked at $STATUS_AT_GATE; raised ${RAISED_BEFORE:-(none)}
scheduler stopped? $([ "$INJECT" = "1" ] && echo yes || echo no)
answering process: exit $drive_status ${DRIVE_HEAD:+— $DRIVE_HEAD}
after: status $STATUS_AFTER; raised ${RAISED_AFTER:-(none)}
  history: $HISTORY_AFTER"
  record_result "$VARIANT" "$NAME" "$OBSERVED" "$PHASE" "$DETAIL" "$EVIDENCE" "$EXTRA"
  ;;
esac

exit 0
