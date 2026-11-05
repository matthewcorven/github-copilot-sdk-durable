#!/usr/bin/env bash
# Fault: store-outage. Move the state store aside while the run is live.
#
#   ./faults/store-outage.sh --variant baseline|durable [--fault-not-injected]
#
# Both variants have a store this fault can reach — that is the difference from Band A, where the
# baseline kept its state in a process dictionary and the row was recorded `not exercised`. Here the
# baseline's state *is* the bare repository on disk (`repo.git`, the `repo-memory` branch), so moving it
# is a real outage of the thing that holds the run's state, not a substitute.
#
#   baseline — `repo.git` is moved aside after Phase 7 has written its outputs and before Phase 8 commits.
#              Phase 8 runs `git hash-object` against a path that no longer names a repository: the run
#              fails, loudly, with exit 1 and git's own message. The comments, labels and report are on
#              disk and the state is gone, which is the shape of the outage: the effects happened and
#              their record did not.
#   durable  — the store file is moved aside while the run is in flight. The process holds the moved
#              file's handle, so its remaining commits go to a path that no longer names its store: it
#              recreates the path (a fresh, empty store that grows its own history) while the moved file
#              keeps the committed prefix intact. That is a *split store*: nothing durable is lost, and
#              an operator reading the live path sees a different history from the one the process had.
#
# The move is a real `mv`, done by the operating system, to a real path, while the process is live. No
# in-process exception is used to simulate it.
#
#   --fault-not-injected moves nothing: the same run with the store in place must finish, so the
#   failure (or split) is attributable to the outage.
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
    *) log "store-outage: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "store-outage: --variant baseline|durable is required"; exit 2; }

FAULT="store-outage"
[ "$INJECT" = "1" ] || FAULT="store-outage-control"
FAULT_COMMAND="./faults/store-outage.sh --variant $VARIANT$( [ "$INJECT" = "1" ] || echo ' --fault-not-injected' )"
export FAULT_COMMAND

require_tools || exit 1

RUN_DIR="$(new_run_dir "$VARIANT" "$FAULT")"
track_dir "$RUN_DIR"
prepare_variant "$VARIANT" "$RUN_DIR/app" || exit 1

if [ "$VARIANT" = "baseline" ]; then
  if [ "$INJECT" = "1" ]; then
    PHASE="after Phase 7's outputs exist, before Phase 8 commits; repo.git moved aside"
  else
    PHASE="after Phase 7's outputs exist, before Phase 8 commits; repo.git left in place (the control)"
  fi
else
  if [ "$INJECT" = "1" ]; then
    PHASE="in flight (Phase 1 committed, Phase 2 running); the store file moved aside"
  else
    PHASE="in flight (Phase 1 committed, Phase 2 running); the store file left in place (the control)"
  fi
fi

# ── Start, and wait for the moment the outage is injected at ─────────────────────────────────────
HELD=0
if [ "$VARIANT" = "baseline" ]; then
  STATE_PATH="$RUN_DIR/repo.git"
  start_baseline "$RUN_DIR" "$STATE_PATH" run
  PID="$STARTED_PID"
  track_pid "$PID"
  # Held with SIGSTOP the moment Phase 5's labels.json exists, so the move happens while the process is
  # live and *before* Phase 8. Nothing between Phase 6 and Phase 7 touches git — only Phase 1 reads the
  # state and only Phase 8 writes it — so the run carries on, posts its comments, and meets the outage
  # where the outage matters. A stop on a log line would race the block-buffered stdout; an artefact is
  # a fact.
  wait_for_file "$RUN_DIR/outputs/run/labels.json" 120 || {
    log "store-outage: the baseline never reached Phase 5"
    exit 1
  }
  kill -0 "$PID" 2>/dev/null || { log "store-outage: the baseline exited before the outage"; exit 1; }
  kill -STOP "$PID" || { log "store-outage: could not hold the baseline"; exit 1; }
  HELD=1
else
  STATE_PATH="$RUN_DIR/store.jsonl"
  start_durable "$RUN_DIR" "$STATE_PATH" run
  PID="$STARTED_PID"
  track_pid "$PID"
  # The store file exists once Phase 1's commit has landed, which means the run is committed and live.
  wait_for_file "$STATE_PATH" 120 || {
    log "store-outage: the durable variant never created its store"
    exit 1
  }
  # And its handle is held from before the first line: the lock sidecar is the proof the process has
  # the file open, which is what makes moving it path an outage of a live run rather than of a file.
  wait_for_file "$STATE_PATH.lock" 60 || {
    log "store-outage: no lock sidecar; the store is not held by a live process"
    exit 1
  }
  # And Phase 1 has committed: the run record exists, so what the outage moves aside is a store with a
  # committed prefix rather than an empty file.
  deadline=$((SECONDS + 60))
  while [ -z "$(state_field durable "$STATE_PATH" phase)" ]; do
    [ "$SECONDS" -lt "$deadline" ] || { log "store-outage: the store never committed a run record"; exit 1; }
    kill -0 "$PID" 2>/dev/null || { log "store-outage: the run exited before the outage"; exit 1; }
    sleep 0.01
  done
fi

kill -0 "$PID" 2>/dev/null || { log "store-outage: the process exited before the outage"; exit 1; }

BEFORE_BYTES=0
BEFORE_STATE=""
if [ "$VARIANT" = "baseline" ]; then
  BEFORE_BYTES="$(du -sk "$STATE_PATH" 2>/dev/null | awk '{print $1 * 1024}')"
  BEFORE_STATE="$(state_field baseline "$STATE_PATH" phase)"
else
  BEFORE_BYTES="$(wc -c < "$STATE_PATH" | tr -d ' ')"
  BEFORE_STATE="$(state_field durable "$STATE_PATH" phase)"
fi
cp -R "$STATE_PATH" "$RUN_DIR/state.before-outage" 2>/dev/null || cp "$STATE_PATH" "$RUN_DIR/state.before-outage"

# ── Inject ───────────────────────────────────────────────────────────────────────────────────────
OUTAGE=0
MOVED="$RUN_DIR/state.removed"
if [ "$INJECT" = "1" ]; then
  mv "$STATE_PATH" "$MOVED" || { log "store-outage: could not move the store"; exit 1; }
  OUTAGE=1
  # A process that is still writing can recreate the path in the instant after the move. That is the
  # outage, not a failed injection: the bytes it continues to write are no longer the file it opened.
  # What the injection requires is that the *moved* file exists and the path was unlinked for at least
  # an instant, which `mv` guarantees; whether the process has already recreated it is an observation.
  RECREATED=0
  [ -e "$STATE_PATH" ] && RECREATED=1
  log "store-outage: moved $(basename "$STATE_PATH") aside while pid $PID was live"
fi
if [ "$HELD" = "1" ]; then
  log "store-outage: releasing pid $PID"
  kill -CONT "$PID" || { log "store-outage: could not release the held process"; exit 1; }
  # A real release-check: a stopped process reports state T; a running one does not.
  for _ in $(seq 1 200); do
    [ "$(ps -o state= -p "$PID" 2>/dev/null | tr -d ' ')" != "T" ] && break
    sleep 0.05
  done
  log "store-outage: pid $PID state now $(ps -o state= -p "$PID" 2>/dev/null | tr -d ' ')"
fi

wait_for_exit "$PID" 240 || { log "store-outage: the run never finished after the outage"; exit 1; }
STATUS="$WAIT_STATUS"
LOG="$RUN_DIR/run.log"

# ── Observe ──────────────────────────────────────────────────────────────────────────────────────
# What the run itself said: its last phase, its failure message if it had one, and what it posted.
LAST="$(last_phase "$LOG")"
POSTED="$(wrote_count "$LOG")"
FAILURE="$(grep -Ei 'failed|refused|could not|not a git repository|exception' "$LOG" 2>/dev/null | head -2 | tr '\n' ' ')"

# What the file system says now.
RECREATED=${RECREATED:-0}
LIVE_EXISTS=0
[ -e "$STATE_PATH" ] && LIVE_EXISTS=1
LIVE_STATE=""
LIVE_PHASE=""
[ "$LIVE_EXISTS" = "1" ] && LIVE_PHASE="$(state_field "$VARIANT" "$STATE_PATH" phase)"
MOVED_BYTES=0
MOVED_PHASE=""
MOVED_COMMIT=""
if [ -e "$MOVED" ]; then
  if [ "$VARIANT" = "baseline" ]; then
    MOVED_BYTES="$(du -sk "$MOVED" 2>/dev/null | awk '{print $1 * 1024}')"
    MOVED_PHASE="$(state_field baseline "$MOVED" phase)"
    MOVED_COMMIT="$("$PYTHON" "$HARNESS_DIR/lib/store.py" runs baseline "$MOVED" 2>/dev/null || echo '?')"
  else
    MOVED_BYTES="$(wc -c < "$MOVED" | tr -d ' ')"
    MOVED_PHASE="$(state_field durable "$MOVED" phase)"
    MOVED_COMMIT="$("$PYTHON" "$HARNESS_DIR/lib/store.py" runs durable "$MOVED" 2>/dev/null || echo '?')"
  fi
fi
# The moved bytes are read by a process that never saw the outage: a fresh reader over the file.
RECOVERED=""
if [ -e "$MOVED" ]; then
  RECOVERED="$(run_ids "$VARIANT" "$MOVED")"
fi

if [ "$OUTAGE" = "0" ]; then
  OUTCOME="completed"
  DETAIL="Control: nothing was moved. The run finished at '$LAST' (exit $STATUS) with the state at '$( [ "$VARIANT" = baseline ] && state_field baseline "$STATE_PATH" phase || state_field durable "$STATE_PATH" phase )' and $POSTED comment(s) posted. This is what the outage run is compared against: the same run, the same store, the same length."
elif [ "$VARIANT" = "baseline" ]; then
  OUTCOME="failed loudly, effects kept, state lost"
  DETAIL="repo.git was moved aside after Phase 7 had written its outputs and while Phase 8 was about to commit. Phase 8's git call ran against a path that no longer named a repository and the run failed with exit $STATUS ($FAILURE). The effects are on disk — $POSTED comment(s), labels.json, the report — and the state is gone: no memory/pr-triage branch in the moved repository ($MOVED_COMMIT commit(s) on it, state '$( [ -n "$MOVED_PHASE" ] && echo "$MOVED_PHASE" || echo none)'), and the path itself no longer exists. That is failing loudly and it is also the reason the baseline is in this band: the outage is discovered, but only after the comments were posted, and what it costs is exactly the cooldown-desync window."
else
  OUTCOME="failed safely, with a split store"
  DETAIL="The store file was moved aside while the run held its handle. The process did not fail: exit $STATUS, final phase '$LAST', $POSTED comment(s) posted, and it recreated the path, so the live store now holds '$LIVE_PHASE' with its own history while the moved file still holds the committed prefix it had at the outage ('$MOVED_PHASE', $MOVED_BYTES bytes, $MOVED_COMMIT generation(s) of state, triage list [$RECOVERED]). Nothing that was committed was lost — every record in the moved file is readable and the run record it holds is the one that was committed — but the state is now in two files, and an operator reading the live path is reading a history that never existed while the run was alive. The application fails safe in the sense that it reports no corruption and keeps running; it does not fail loudly, and it does not tell anyone the file it was writing had gone."
fi

EVIDENCE="outage=$([ "$OUTAGE" = "1" ] && echo yes || echo no); path recreated during the move=$RECREATED; before: state=${BEFORE_STATE:-none} $BEFORE_BYTES bytes; moved: exists=$([ -e "$MOVED" ] && echo yes || echo no) ${MOVED_BYTES:-0} bytes state=${MOVED_PHASE:-none} history=${MOVED_COMMIT:-n/a}; live path exists=$LIVE_EXISTS state=${LIVE_PHASE:-none}; run exit=$STATUS last phase=$LAST posted=$POSTED${FAILURE:+; failure: $FAILURE}; triage list found in the moved state=[${RECOVERED}]"

record_result "$VARIANT" "$FAULT" "$OUTCOME" "$PHASE" "$DETAIL" "$EVIDENCE"
exit 0
