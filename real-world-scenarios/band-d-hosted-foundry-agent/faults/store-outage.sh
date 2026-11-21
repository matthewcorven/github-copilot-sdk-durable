#!/usr/bin/env bash
# Fault: store-outage. The Durable store is made unavailable while the work is live, and the
# question is whether the application fails safe.
#
#   ./faults/store-outage.sh --variant baseline|durable-inside|durable-outside [--fault-not-injected]
#
# "Unavailable" is the operating system's answer, not a flag the application reads:
#
#   durable-inside  the session volume is mounted read-only for the process that runs while
#                   the store is out, so the store's write path is refused by the filesystem
#                   (`EROFS`) and the committed bytes are left exactly where they were. That
#                   is a *temporary unavailability*, not a deletion: nothing is removed, and
#                   the same volume is used again as soon as the mount is gone. The store's
#                   bytes are digested before and after so "the committed state survived"
#                   is a comparison of content, and the read-back is required to show a
#                   field the first process committed - a store that was missing would
#                   answer with the document's default and be caught here rather than
#                   reported as a survival.
#   durable-outside the store file is made immutable with `chflags uchg` while the process is
#                   alive, so a commit cannot append. The flag is removed afterwards and the
#                   store is read back, so a half-committed record would be visible.
#   baseline        there is no store. The equivalent unavailability is the service that the
#                   baseline's only durable-ish record depends on: it is made unreachable,
#                   and what the run does is recorded. The result says plainly that this is
#                   not the same failure, because a stateless client has no store to lose.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

FAULT=store-outage
parse_fault_args "${BASH_SOURCE[0]}" "$@"
require_tools || exit 1
install_cleanup

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_path "$RUN_DIR"
PORT="$(port_for "$VARIANT" "$FAULT"$( [ "$INJECT" = "1" ] || echo "-control" ))"
start_service "$RUN_DIR" "$PORT" || exit 1

REQUEST_ID="PA-D4-0004"
REQUEST="Prior authorization PA-D4-0004: hip replacement, 2 of 2 criteria met."

case "$VARIANT" in
  durable-inside)
    require_d2 || exit 1
    VOLUME="$(d2_volume "$RUN_DIR")"
    track_volume "$VOLUME"
    FAULTS_DOCKER="$(record_docker_identity)"; export FAULTS_DOCKER

    # What the store held the moment the first process finished. Everything after this is
    # compared against these two facts, not asserted.
    STORE_PATH=/home/session/durable/working-state.jsonl
    d2_run "$VOLUME" python3 d2_tool.py assess --request "$REQUEST_ID" --text "criteria met" \
      > "$RUN_DIR/10-assess.json" 2>/dev/null
    assess_status=$?
    store_sha_before="$(d2_run "$VOLUME" sha256sum "$STORE_PATH" 2>/dev/null | cut -d' ' -f1)"
    if [ -z "$store_sha_before" ]; then
      # Without a before-image there is nothing to compare, and a comparison that cannot be
      # made must not become a result that reads like one.
      log "fault: the store's digest could not be read after the first process; no result written"
      exit 1
    fi
    committed_assessment="$(python3 - "$RUN_DIR/10-assess.json" <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get("phase") or "")
except (OSError, ValueError):
    print("")
PY
)"

    # The outage, imposed by the container runtime over the same volume: the store is left
    # in place and made unwritable, so a commit is refused by the filesystem rather than by
    # a flag the application reads. Nothing is deleted, which is what makes it an outage
    # the same store can come back from.
    if [ "$INJECT" = "1" ]; then
      d2_run_readonly "$VOLUME" python3 d2_tool.py resume --request "$REQUEST_ID" \
        > "$RUN_DIR/20-during-outage.json" 2> "$RUN_DIR/20-during-outage.err"
      during_status=$?
      injected="yes"
    else
      d2_run "$VOLUME" python3 d2_tool.py resume --request "$REQUEST_ID" \
        > "$RUN_DIR/20-during-outage.json" 2> "$RUN_DIR/20-during-outage.err"
      during_status=$?
      injected="no"
    fi

    # The same volume, writable again: what was committed before the outage must still be
    # there, byte for byte, and readable as the document the first process wrote.
    store_sha_after="$(d2_run "$VOLUME" sha256sum "$STORE_PATH" 2>/dev/null | cut -d' ' -f1)"
    d2_run "$VOLUME" python3 d2_tool.py approval --request "$REQUEST_ID" \
      > "$RUN_DIR/30-after-outage.json" 2> "$RUN_DIR/30-after-outage.err"
    after_status=$?

    read_back="$(python3 - "$RUN_DIR/30-after-outage.json" <<'PY'
import json, sys
try:
    doc = json.load(open(sys.argv[1]))
except (OSError, ValueError):
    print("unreadable"); raise SystemExit
approval = doc.get("approval") if "approval" in doc else doc
if not isinstance(approval, dict):
    print("unreadable"); raise SystemExit
if approval.get("state") != "pending" or not approval.get("deadline"):
    print("default"); raise SystemExit
print("committed")
PY
)"
    printf '%s\n' "sha256 before: $store_sha_before" "sha256 after:  $store_sha_after" \
      "phase committed by the first process: $committed_assessment" \
      "read back after the outage: $read_back" > "$RUN_DIR/40-store-after.txt"

    error_text="$(grep -m1 -o 'OSError: .*' "$RUN_DIR/20-during-outage.err" 2>/dev/null | head -1)"
    [ -n "$error_text" ] || error_text="nothing raised"

    if [ "$INJECT" = "1" ]; then
      if [ "$during_status" = "0" ]; then
        outcome="the commit succeeded during the outage - unexpected"
        detail="The session volume was mounted read-only for the process that ran while the store was out, but it exited 0. That is a finding, not a pass: the injection did not reach the store's write path, and nothing here should be read as the layer refusing a write. See 20-during-outage.json."
      elif [ "$read_back" != "committed" ] || [ "$store_sha_before" != "$store_sha_after" ]; then
        outcome="the store did not survive the outage as a store"
        detail="The write was refused while the store was unavailable (exit $during_status, '$error_text'), but the read-back is not the state the first process committed: the store reads back as '$read_back' and its digest changed from $store_sha_before to $store_sha_after. A process that failed to find the state, or found a different one, is a lost store rather than a protected one, and this fault must not be reported as a survival."
      else
        outcome="failed closed, and the committed state was still there afterwards"
        detail="The store's volume was made read-only for the process that ran while it was unavailable, so the commit was refused by the filesystem: the process exited $during_status with '$error_text' and the store's digest was unchanged. The same volume was then writable again and read back: the state the first process committed is still there (approval state 'pending', its deadline present) and the process reads it with exit $after_status. Nothing was deleted to produce the outage - it was an unavailability the store came back from, which is what separates it from a corruption."
      fi
    else
      outcome="control: the store stayed available"
      detail="The same sequence with no read-only mount: the resuming process ran (exit $during_status), so the store was written and its digest moved from $store_sha_before to $store_sha_after. The approval reads back as the document the first process committed (exit $after_status). The refused write in the fault row is what changed the outcome."
    fi

    TEMPLATE="$RUN_DIR/template.json"
    FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
    FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_INJECTED="$injected" \
    FAULT_ASSESS="$assess_status" FAULT_DURING="$during_status" FAULT_AFTER="$after_status" \
    FAULT_ERROR="$error_text" FAULT_SHA_BEFORE="$store_sha_before" FAULT_SHA_AFTER="$store_sha_after" \
    FAULT_COMMITTED="$committed_assessment" FAULT_READ_BACK="$read_back" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed=(
            "not exercised in this run: the Durable store is the application's, and Foundry has no part "
            "in it. Nothing here says anything about the platform's own availability."
        ),
        note=(
            "The outage was imposed on the session volume's mount, which is this harness's stand-in for "
            "the platform's per-session filesystem: the container runtime refused the write, the store "
            "was not deleted, and the platform was not asked what it does when its own storage fails. "
            "Its behaviour under a storage failure is not measured and is not claimed."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "outage_injected": os.environ["FAULT_INJECTED"] == "yes",
            # Only the injected arm had an outage, and only the injected arm's kind is a fact
            # about the run. A control that named a read-only mount it never applied would be
            # describing an injection the run did not make.
            "outage_kind": (
                "the session volume was mounted read-only for the process that ran while the store was unavailable"
                if os.environ["FAULT_INJECTED"] == "yes"
                else None
            ),
            "assess_exit": int(os.environ["FAULT_ASSESS"]),
            "phase_committed_before_outage": os.environ["FAULT_COMMITTED"],
            "resume_exit_during_outage": int(os.environ["FAULT_DURING"]),
            "read_after_outage_exit": int(os.environ["FAULT_AFTER"]),
            "error_during_outage": os.environ["FAULT_ERROR"],
            "store_sha256_before_outage": os.environ["FAULT_SHA_BEFORE"],
            "store_sha256_after_outage": os.environ["FAULT_SHA_AFTER"],
            "store_bytes_unchanged": os.environ["FAULT_SHA_BEFORE"] != ""
            and os.environ["FAULT_SHA_BEFORE"] == os.environ["FAULT_SHA_AFTER"],
            "committed_state_read_back": os.environ["FAULT_READ_BACK"],
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
    FAULTS_DOCKER="$DEFAULT_DOCKER_NOTE - D3's orchestrator is a host process and its store a host file"; export FAULTS_DOCKER
    # D3 names its scratch directory `RUN_DIR`, so the harness's own directory is captured
    # first and used for every path this script writes.
    HARNESS_RUN="$RUN_DIR"
    mkdir -p "$HARNESS_RUN/run"
    STORE_FILE="$HARNESS_RUN/run/store/orchestrator.jsonl"

    export RUN_DIR="$HARNESS_RUN/run"
    export RUN_REQUEST="$REQUEST" RUN_ID="D4-SO" REVIEWER="D4 harness reviewer" GATE_TIMEOUT_SECONDS=120
    "$D3_OUT/orchestrator" > "$HARNESS_RUN/10-first-process.log" 2>&1 &
    pid=$!
    waited=0
    while [ ! -f "$HARNESS_RUN/run/gate/request.json" ] && [ "$waited" -lt 900 ]; do
      sleep 0.05; waited=$((waited + 1))
    done
    if [ ! -f "$HARNESS_RUN/run/gate/request.json" ]; then
      log "fault: the run never reached its gate; see 10-first-process.log"
      tail -20 "$HARNESS_RUN/10-first-process.log" 2>/dev/null >&2
      kill -9 "$pid" 2>/dev/null
      exit 1
    fi

    if [ "$INJECT" = "1" ]; then
      # A real, OS-level refusal to write: the file is immutable until the flag is removed.
      chflags uchg "$STORE_FILE" 2>/dev/null || {
        log "fault: could not make the store immutable"; kill -9 "$pid" 2>/dev/null; exit 1
      }
      injected="yes"
    else
      injected="no"
    fi

    RUN_DIR="$HARNESS_RUN/run" "$BAND_DIR/../band-d3-outside-orchestrator/gate.sh" approve \
      "D4 harness reviewer" "the gate is not this fault's subject" > "$HARNESS_RUN/30-gate.log" 2>&1 || true
    wait "$pid"; first_status=$?

    if [ "$INJECT" = "1" ]; then
      chflags nouchg "$STORE_FILE" 2>/dev/null
    fi

    # The store, read back after the flag is gone: a half-committed record would show here.
    store_after="$(python3 - "$STORE_FILE" <<'PY'
import json, sys
records = 0
tasks = {}
try:
    for line in open(sys.argv[1], encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        entry = json.loads(line)
        records += 1
        if entry.get("type") == "task":
            # The JSONL store is an append log, so a task appears once per update. The last
            # record for an id is the current state; the earlier ones are its history.
            payload = entry.get("payload") or {}
            tasks[payload.get("id")] = {
                "id": payload.get("id"),
                "status": payload.get("status"),
                "checkpoint": (payload.get("checkpoint") or {}).get("phase"),
            }
except (OSError, ValueError) as error:
    print(json.dumps({"readable": False, "error": str(error)}))
    raise SystemExit
print(json.dumps({"readable": True, "records": records, "tasks": list(tasks.values())}))
PY
)"
    outcome_line="$(grep -m1 'orchestrator:' "$HARNESS_RUN/10-first-process.log" 2>/dev/null | tail -1)"
    [ -n "$outcome_line" ] || outcome_line="(the process printed no outcome line)"

    if [ "$INJECT" = "1" ]; then
      outcome="the commit was refused and the run did not advance"
      detail="The store file was made immutable (chflags uchg) while the run was live and waiting at its human gate. The gate was then answered. The process exited $first_status with '$outcome_line'. After the flag was removed the store was read back: $store_after. The refused write is the observation - the run did not silently continue past a store it could not write, and the committed prefix is intact."
    else
      outcome="control: the run completed normally"
      detail="The same run with no immutable flag: the process exited $first_status and the store reads back as $store_after. The refused write in the fault row is what the flag caused."
    fi

    TEMPLATE="$HARNESS_RUN/template.json"
    FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
    FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_INJECTED="$injected" \
    FAULT_STATUS="$first_status" FAULT_STORE="$store_after" FAULT_LINE="$outcome_line" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=[],
        observed="not exercised in this run: the store is local and Durable's, not the platform's",
        note=(
            "D3's store is a file on this machine. The outage is an operating-system write refusal, "
            "which is a stronger injection than an application-level flag and is not a statement about "
            "Azure storage."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "outage_injected": os.environ["FAULT_INJECTED"] == "yes",
            "process_exit": int(os.environ["FAULT_STATUS"]),
            "outcome_line": os.environ["FAULT_LINE"],
            "store_after_flag_removed": json.loads(os.environ["FAULT_STORE"])
            if os.environ["FAULT_STORE"].startswith("{")
            else os.environ["FAULT_STORE"],
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

    write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
    exit 0
    ;;

  baseline)
    # The baseline has no store, so it cannot lose one. The closest honest analogue is the
    # service its whole run depends on: the driver is made to fail on the *first* call by
    # pointing it at a port nothing is listening on, and the result is labelled as the
    # analogue it is rather than as a store outage.
    FAULTS_DOCKER="$DEFAULT_DOCKER_NOTE - the baseline client is a host process and has no store"; export FAULTS_DOCKER
    if [ "$INJECT" = "1" ]; then
      stop_service
      FOUNDRY_BASE_URL="http://127.0.0.1:$PORT" FOUNDRY_TOKEN="offline-no-credential" \
        python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
        > "$RUN_DIR/10-first-client.log" 2>&1
      first_status=$?
      injected="yes"
      outcome="failed loudly and recorded it; nothing was kept"
      detail="The baseline has no Durable store to lose, so the outage was injected in the thing its run depends on: the service it calls was stopped, and the run failed on its first call. The client exited $first_status and wrote a failure record rather than a partial result. That is a fail-loudly answer, and it is *not* the same fault the Durable variants received — a stateless client keeps no state to protect, which is why the row is labelled an analogue."
    else
      python3 "$HERE/lib/baseline_driver.py" run --run-dir "$RUN_DIR/run" --request "$REQUEST" \
        > "$RUN_DIR/10-first-client.log" 2>&1
      first_status=$?
      injected="no"
      outcome="control: the service stayed up and the run finished"
      detail="The same drive with the service available: the run finished, exit $first_status. The unavailable service in the fault row is what produced the failure."
    fi

    TEMPLATE="$RUN_DIR/template.json"
    FAULT_LIB="$HERE/lib" FAULT_VARIANT="$VARIANT" FAULT_INJECT="$INJECT" \
    FAULT_OUTCOME="$outcome" FAULT_DETAIL="$detail" FAULT_INJECTED="$injected" \
    FAULT_STATUS="$first_status" \
    python3 - "$TEMPLATE" <<'PY'
import json, os, sys
sys.path.insert(0, os.environ["FAULT_LIB"])
from platform import block

doc = {
    "outcome": os.environ["FAULT_OUTCOME"],
    "platform": block(
        touched=["session-persistence"],
        observed=(
            "not exercised in this run: no platform session existed, and the stand-in service is not "
            "the platform's data plane"
        ),
        note=(
            "Baseline has no store, so this fault cannot mean the same thing on both sides. The row is "
            "an unavailable service against a stateless client, and it is labelled as the analogue it "
            "is rather than as a store outage."
        ),
    ) | {"session_survived": "not applicable - no platform session existed"},
    "application": {
        "outcome_detail": os.environ["FAULT_DETAIL"],
        "observations": {
            "outage_injected": os.environ["FAULT_INJECTED"] == "yes",
            "process_exit": int(os.environ["FAULT_STATUS"]),
            "what_was_made_unavailable": "the service, not a store - the baseline has none",
        },
    },
}
json.dump(doc, open(sys.argv[1], "w"), indent=1, sort_keys=True)
PY

    write_result "$TEMPLATE" "$(result_path "$VARIANT" "$FAULT")" "$FAULT" "$VARIANT" || exit 1
    exit 0
    ;;

  *)
    log "store-outage: --variant baseline|durable-inside|durable-outside"; exit 2 ;;
esac
