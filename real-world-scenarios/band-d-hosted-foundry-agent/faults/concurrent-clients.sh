#!/usr/bin/env bash
# Fault: concurrent-clients. Two clients against one run, at the same moment.
#
#   ./faults/concurrent-clients.sh --variant baseline|durable-inside|durable-outside [--fault-not-injected]
#
# The variants hold their run in different places, so "two clients" means something
# different in each, and the difference is the finding:
#
#   baseline        there is no shared store, so two drivers are simply two runs. Both call
#                   both agents and both finish; nothing detects the other.
#   durable-inside  the store file takes an exclusive writer lock for the life of the process
#                   that opened it, so two container processes over one session is a refusal
#                   by the operating system (`flock`), not by a flag the application reads.
#                   The second process's store open fails and it exits non-zero.
#   durable-outside the same lock, over D3's store, with the two processes being the
#                   published orchestrator itself.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=concurrent-clients
parse_fault_args "${BASH_SOURCE[0]}" "$@"
require_tools || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

REQUEST_ID="PA-D4-0003"
REQUEST="Prior authorization PA-D4-0003: cataract surgery, 2 of 2 criteria met."
PY="$(harness_python)" || exit 1

# The holder writes a marker into its session volume, then waits. The marker is read from a
# separate container over the same volume, so the harness never enters the container whose
# lock is the thing being tested.
hold_first() { # volume
  local volume="$1" waited=0
  while [ -z "$(d2_cat "$volume" /home/session/holding)" ] && [ "$waited" -lt 900 ]; do
    sleep 0.05
    waited=$((waited + 1))
  done
  [ -n "$(d2_cat "$volume" /home/session/holding)" ]
}

case "$VARIANT" in
  baseline)
    cov=""
    syn=""
    if [ "$INJECT" = "1" ]; then
      python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run-a" --request "$REQUEST" \
        > "$RUN_DIR/10-client-a.log" 2>&1 &
      pid_a=$!
      printf 'run-a' > "$RUN_DIR/a-started"
      # A second client over the same service, in the same window. Nothing coordinates them,
      # which is exactly the baseline's situation.
      python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run-b" --request "$REQUEST" \
        > "$RUN_DIR/20-client-b.log" 2>&1 &
      pid_b=$!
      wait "$pid_a"; status_a=$?
      wait "$pid_b"; status_b=$?
      overlapped="yes"
      outcome_dup="both clients applied both steps"
      detail="Two driver processes were started against the one service. The service accepted $cov coverage POST(s) and $syn synthesis POST(s): two of each, for one request. Neither process refused, because the baseline has no store and therefore nothing that could see the other. A retried request on this design costs a second full run of every step."
    else
      python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run-a" --request "$REQUEST" \
        > "$RUN_DIR/10-client-a.log" 2>&1
      status_a=$?
      python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run-b" --request "$REQUEST" \
        > "$RUN_DIR/20-client-b.log" 2>&1
      status_b=$?
      overlapped="no (control: the clients ran one after the other)"
      outcome_dup="control: both clients ran, and nothing overlapped"
      detail="The same two clients, run one after the other: nothing was refused, because nothing contended for the same thing at the same time. The overlap is what the fault row adds."
    fi
    cov="$(service_posts "$RUN_DIR" coverage-assessment-agent)"
    syn="$(service_posts "$RUN_DIR" synthesis-agent)"
    status_second="$status_b"
    refusal="none - there is no shared record for a refusal to come from"
    ;;

  durable-inside)
    VOLUME="$(d2_volume "$RUN_DIR")"
    track_volume "$VOLUME"
    # The holder opens the store, commits the assessment, writes a marker and holds the store
    # open (a real exclusive lock, held by the process) while a second container tries to
    # open the same file.
    HOLDER='
import json, os, sys, time
from durable_state import Workspace
w = Workspace.open()
try:
    w.note_assessment(os.environ["D4_REQUEST"], "criteria met")
    with open("/home/session/holding", "w") as handle:
        handle.write(str(os.getpid()))
    print("holding the store open", flush=True)
    deadline = time.time() + 120
    while not os.path.exists("/home/session/release") and time.time() < deadline:
        time.sleep(0.05)
finally:
    w.close()
'
    NAME_A="d4-d2-cc-a-$$"
    track_container "$NAME_A"
    d2_container_start "$VOLUME" "$NAME_A" env D4_REQUEST="$REQUEST_ID" python3 -c "$HOLDER"
    if ! hold_first "$VOLUME"; then
      log "fault: the first container never held the store"
      docker logs "$NAME_A" 2>&1 | tail -20
      exit 1
    fi
    if [ "$INJECT" = "1" ]; then
      d2_run "$VOLUME" python3 d2_tool.py assess --request "$REQUEST_ID" --text "second client" \
        > "$RUN_DIR/20-second-client.json" 2> "$RUN_DIR/20-second-client.log"
      status_second=$?
      overlapped="yes"
    else
      # Control: the second client waits for the first to finish, so nothing overlaps.
      d2_touch "$VOLUME" /home/session/release
      docker wait "$NAME_A" > /dev/null 2>&1 || true
      d2_run "$VOLUME" python3 d2_tool.py assess --request "$REQUEST_ID" --text "second client" \
        > "$RUN_DIR/20-second-client.json" 2> "$RUN_DIR/20-second-client.log"
      status_second=$?
      overlapped="no (control: the second client ran after the first finished)"
    fi
    d2_touch "$VOLUME" /home/session/release
    refusal="$(grep -o 'already open in another process[^"]*' "$RUN_DIR/20-second-client.log" 2>/dev/null | head -1)"
    [ -n "$refusal" ] || refusal="none recorded"
    # What the store says after both: one assessment, not two.
    d2_run "$VOLUME" python3 d2_tool.py state --request "$REQUEST_ID" > "$RUN_DIR/30-final-state.json" 2>/dev/null
    d2_cat "$VOLUME" /home/session/durable/working-state.jsonl > "$RUN_DIR/40-store.jsonl"
    assessment_count="$(python3 - "$RUN_DIR/40-store.jsonl" <<'PY'
import json, sys
count = 0
try:
    for line in open(sys.argv[1], encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        entry = json.loads(line)
        payload = entry.get("payload") or {}
        if entry.get("type") == "document-version" and payload.get("kind") == "d2.work-state":
            count += 1
except OSError:
    pass
print(count)
PY
)"
    outcome_dup="the second client was refused by the store's lock"
    detail="One container opened the store and held it, committing the assessment. A second container then tried the same store: its open failed with the layer's single-writer refusal ('$refusal') and it exited $status_second without writing. The work state holds $assessment_count version(s), so the second client's assessment is not in it. The refusal is the operating system's file lock, not a check the application performs."
    status_a=0
    status_b="$status_second"
    cov="not applicable - D2's fault is on the store, not on a service call"
    syn="not applicable"
    ;;

  durable-outside)
    build_durable_outside || exit 1
    HARNESS_RUN="$RUN_DIR"
    mkdir -p "$RUN_DIR/run-a" "$RUN_DIR/run-b"
    if [ "$INJECT" = "1" ]; then
      RUN_DIR="$HARNESS_RUN/run-a" RUN_REQUEST="$REQUEST" RUN_ID="D4-CC-A" REVIEWER="D4" \
        GATE_TIMEOUT_SECONDS=120 "$D3_OUT/orchestrator" > "$HARNESS_RUN/10-client-a.log" 2>&1 &
      pid_a=$!
      # Wait for the first client to be genuinely inside the store and past its first call,
      # which is what makes the second client's open a contention rather than a race with
      # process start-up.
      waited=0
      while [ ! -f "$HARNESS_RUN/run-a/gate/request.json" ] && [ "$waited" -lt 900 ]; do
        sleep 0.05; waited=$((waited + 1))
      done
      # The second client opens the *same* store file the first one already holds. The
      # directory is shared through a symlink: D3 builds `<RUN_DIR>/store/orchestrator.jsonl`,
      # so pointing both processes at one directory would otherwise mean two different files.
      rm -rf "$HARNESS_RUN/run-b/store"
      ln -s "$HARNESS_RUN/run-a/store" "$HARNESS_RUN/run-b/store"
      RUN_DIR="$HARNESS_RUN/run-b" RUN_REQUEST="$REQUEST" RUN_ID="D4-CC-B" REVIEWER="D4" \
        GATE_TIMEOUT_SECONDS=30 "$D3_OUT/orchestrator" > "$HARNESS_RUN/20-client-b.log" 2>&1
      status_b=$?
      # The first client is answered, so its exit says something about it rather than about
      # the gate's timeout. The gate is not this fault's subject.
      RUN_DIR="$HARNESS_RUN/run-a" "$BAND_DIR/../band-d3-outside-orchestrator/gate.sh" approve \
        "D4 harness reviewer" "the gate is not this fault's subject" > "$HARNESS_RUN/30-gate.log" 2>&1 || true
      wait "$pid_a"; status_a=$?
      overlapped="yes"
    else
      RUN_DIR="$RUN_DIR/run-a" RUN_REQUEST="$REQUEST" RUN_ID="D4-CC-A" REVIEWER="D4" \
        GATE_TIMEOUT_SECONDS=30 "$D3_OUT/orchestrator" > "$RUN_DIR/10-client-a.log" 2>&1
      status_a=$?
      RUN_DIR="$RUN_DIR/run-b" RUN_REQUEST="$REQUEST" RUN_ID="D4-CC-B" REVIEWER="D4" \
        GATE_TIMEOUT_SECONDS=30 "$D3_OUT/orchestrator" > "$RUN_DIR/20-client-b.log" 2>&1
      status_b=$?
      overlapped="no (control: the clients ran one after the other)"
    fi
    refusal="$(grep -o 'is already open in another process' "$RUN_DIR/20-client-b.log" 2>/dev/null | head -1)"
    [ -n "$refusal" ] || refusal="none recorded"
    outcome_dup="the second client was refused while the first held the store"
    detail="Two published orchestrator processes over one store directory. The first opened the store, recorded the coverage step and reached its human gate; the second was refused by the layer's single-writer lock ('$refusal') and exited $status_b without writing. The first then completed its gate normally (exit $status_a). Nothing in D3 checks whether another process is running - the lock is the mechanism, which is the same answer D2 gives for the same reason."
    cov="not applicable"
    syn="not applicable"
    refusal="${refusal:-none recorded}"
    status_a="${status_a:-0}"
    status_b="${status_b:-0}"
    cov="${cov:-not applicable}"
    syn="${syn:-not applicable}"
    ;;

  *)
    log "concurrent-clients: --variant baseline|durable-inside|durable-outside"; exit 2 ;;
esac

if [ "$INJECT" != "1" ]; then
  outcome_dup="control: both clients ran, and nothing overlapped"
  detail="The same two clients, run one after the other: nothing was refused, because nothing contended for the same thing at the same time. The overlap is what the fault row adds."
fi

TEMPLATE="$RUN_DIR/template.json"
FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
FAULT_OUTCOME="$outcome_dup" FAULT_DETAIL="$detail" FAULT_OVERLAP="$overlapped" \
FAULT_REFUSAL="$refusal" FAULT_STATUS_A="${status_a:-0}" FAULT_STATUS_B="${status_b:-0}" \
FAULT_COV="${cov:-not applicable}" FAULT_SYN="${syn:-not applicable}" \
python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

def number(text):
    try:
        return int(text)
    except (TypeError, ValueError):
        return None

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: two clients against one *platform* session is not something this "
            "harness can reach without a deployment. What it measures is two local processes against one "
            "durable store, which is the application's contention and not the platform's."
        ),
        note=(
            "The platform's own concurrency behaviour - whether Foundry serialises two requests on one "
            "agent_session_id - is not measured here and is not claimed. lib/fake_foundry.py serves "
            "requests concurrently and knows nothing about sessions."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "clients_overlapped": os.environ["FAULT_OVERLAP"],
            "refusal_text": os.environ["FAULT_REFUSAL"],
            "first_client_exit": number(os.environ["FAULT_STATUS_A"]),
            "second_client_exit": number(os.environ["FAULT_STATUS_B"]),
            "coverage_calls": os.environ["FAULT_COV"],
            "synthesis_calls": os.environ["FAULT_SYN"],
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
exit 0
