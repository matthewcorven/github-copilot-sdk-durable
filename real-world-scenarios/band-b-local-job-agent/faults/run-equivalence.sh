#!/usr/bin/env bash
# The extra evidence Band B must produce: a run that was interrupted and continued must leave the same
# product as a run that was not.
#
#   ./faults/run-equivalence.sh --variant baseline|durable
#
# Three runs of the same variant over the same staged target, in three directories:
#
#   1. reference   uninterrupted, one process, to its own end
#   2. repeat      a second uninterrupted run, which is what says two clean runs are even the same thing.
#                  Without it, a difference in (3) could be the model's variance rather than the
#                  interruption, and the comparison would be meaningless.
#   3. interrupted the run is stopped at a boundary and a second process is started against the same
#                  target. Which boundary depends on the variant, and both are named in the result:
#                  the Durable variant's own committed-iteration stop (`STOP_AFTER_ITERATION`), because
#                  that is the only stop this band can put between iterations — from outside the process
#                  the only reachable frame is inside the next turn; the baseline is killed with SIGKILL
#                  inside its pass, because it has no other stop and no second iteration to be stopped
#                  between.
#
# **What is compared, exactly.** The product, not the directory: every regular file under the target
# except the store (`.durable/`, which is the record and differs by construction — one process opened it
# versus two) and the harness's own build output. For each file, its SHA-256 and its path relative to the
# target; the comparison is of that text. So "identical" means *the same file set with the same bytes in
# each file* — not merely the same names, and not merely the same set of contents in a different
# arrangement. Two directories with the same bytes under one renamed file are NOT identical by this test.
#
# The comparison is made able to fail two ways, and both are exercised on every run:
#
#   * a *self-check*: a copy of the reference with one byte appended to one file must report a
#     difference, and a copy with an extra file must report a difference. A comparator that only ever
#     says "identical" would pass the main comparison vacuously, so the run proves it is not one before
#     it uses it.
#   * the *repeat*: if the two clean runs disagree the result says so, and no claim about the interrupted
#     run is made. That is the honest outcome, not a failed harness.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib/common.sh
. "$HERE/lib/common.sh"

VARIANT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --variant) VARIANT="${2:-}"; shift 2 ;;
    *) log "run-equivalence: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$VARIANT" ] || { log "run-equivalence: --variant baseline|durable is required"; exit 2; }
[ "$VARIANT" = "baseline" ] || [ "$VARIANT" = "durable" ] || { log "run-equivalence: unknown variant"; exit 2; }

FAULT="run-equivalence"
FAULT_COMMAND="./faults/run-equivalence.sh --variant $VARIANT"
export FAULT_COMMAND
FAULT_SCRIPT="$(basename "${BASH_SOURCE[0]}")"
export FAULT_SCRIPT

require_tools || exit 1

WORK="$SCRATCH_ROOT/$VARIANT/run-equivalence"
rm -rf "$WORK"
mkdir -p "$WORK"
track_dir "$WORK"
prepare_variant "$VARIANT" "$WORK/app" || exit 1

# One staged target, copied three times, so all three runs start from the same bytes.
mkdir -p "$WORK/template"
cp -R "$BAND_DIR/baseline/target/." "$WORK/template/"
"$PYTHON" "$HARNESS_DIR/lib/stage.py" "$WORK/template" "$TARGET_SECTIONS" >/dev/null || exit 1

# ── the comparator, and the proof that it can say no ────────────────────────────────────────────────
# `compare_product <a> <b>`: prints `identical` or `differs: ...`, and exits 0 either way (a difference is
# an observation). The self-check below is what makes its verdict worth reading.
# `compare_normalised <a> <b>`: the same file set, compared with runs of blank lines collapsed and trailing
# whitespace stripped. Reported beside the byte comparison and never instead of it: it answers "the same
# work, differently spaced", which is what a second pass over a target the first pass already finished
# produces when the writer does not normalise its own output.
compare_normalised() {
  local a="$1" b="$2"
  local da="$WORK/.normal-a" db="$WORK/.normal-b"
  product_digest_normalised "$a" > "$da"
  product_digest_normalised "$b" > "$db"
  local files_a
  files_a="$(wc -l < "$da" | tr -d ' ')"
  if diff -q "$da" "$db" >/dev/null 2>&1; then
    echo "identical ($files_a file(s), sha256 per file after dropping blank lines)"
    return 0
  fi
  local differing
  differing="$(paste -d: "$da" "$db" | awk -F: '$1 != $2 {print NR}' | wc -l | tr -d ' ')"
  echo "differs: $differing of $files_a file(s) differ even after dropping blank lines"
  return 0
}

compare_product() {
  local a="$1" b="$2"
  local da="$WORK/.digest-a" db="$WORK/.digest-b"
  product_digest "$a" > "$da"
  product_digest "$b" > "$db"
  local files_a files_b
  files_a="$(wc -l < "$da" | tr -d ' ')"
  files_b="$(wc -l < "$db" | tr -d ' ')"
  if diff -q "$da" "$db" >/dev/null 2>&1; then
    echo "identical ($files_a file(s), sha256 per file and path)"
    return 0
  fi
  local only_a only_b differing
  only_a="$(comm -23 <(cut -c67- "$da" | LC_ALL=C sort) <(cut -c67- "$db" | LC_ALL=C sort) | tr '\n' ' ')"
  only_b="$(comm -13 <(cut -c67- "$da" | LC_ALL=C sort) <(cut -c67- "$db" | LC_ALL=C sort) | tr '\n' ' ')"
  differing="$(join -j 2 <(sort -k2 "$da") <(sort -k2 "$db") | awk '$2 != $3 {print $1}' | tr '\n' ' ')"
  echo "differs: only in $a [${only_a:-none}], only in $b [${only_b:-none}], different bytes [${differing:-none}]; $files_a vs $files_b file(s)"
  return 0
}

SELF_CHECK="not run"
if [ -d "$WORK/template" ]; then
  mkdir -p "$WORK/selfcheck"
  cp -R "$WORK/template/." "$WORK/selfcheck/"
  printf 'one extra byte' >> "$WORK/selfcheck/PLAN.md"
  SELF_SAME="$(compare_product "$WORK/template" "$WORK/template")"
  SELF_SAME_NORMALISED="$(compare_normalised "$WORK/template" "$WORK/template")"
  SELF_BYTES="$(compare_product "$WORK/template" "$WORK/selfcheck")"
  # The normalised comparator must also be able to say no, and a *line* of text is the smallest change it
  # cannot explain away as spacing. It is checked before the extra file is added, so the file sets still
  # line up: a comparison that also differed by file count would not be testing the normaliser.
  printf '\nA line that is not in the template.\n' >> "$WORK/selfcheck/PLAN.md"
  SELF_NORMALISED="$(compare_normalised "$WORK/template" "$WORK/selfcheck")"
  printf 'a file that is not in the template\n' > "$WORK/selfcheck/extra.txt"
  SELF_EXTRA="$(compare_product "$WORK/template" "$WORK/selfcheck")"
  rm -rf "$WORK/selfcheck"
  if [[ "$SELF_SAME" == identical* && "$SELF_SAME_NORMALISED" == identical* && "$SELF_BYTES" == differs:* && "$SELF_EXTRA" == differs:* && "$SELF_NORMALISED" == differs:* ]]; then
    SELF_CHECK="passes: the same tree is 'identical' to both comparators; one appended byte reports '$SELF_BYTES'; one extra file reports '$SELF_EXTRA'; an appended line reports, after normalising, '$SELF_NORMALISED'"
  else
    SELF_CHECK="FAILED: same='$SELF_SAME' bytes='$SELF_BYTES' extra='$SELF_EXTRA'"
    log "run-equivalence: the comparator cannot be trusted, so no comparison is recorded"
    record_result "$VARIANT" "$FAULT" "comparator failed its self-check" \
      "the comparison primitive, before it was used" \
      "The self-check did not behave: $SELF_CHECK. A comparator that cannot say 'differs' makes 'identical' meaningless, so the run stopped rather than record a comparison it could not make." \
      ""
    exit 0
  fi
fi

# ── 1. reference ───────────────────────────────────────────────────────────────────────────────────
run_to_end() {
  local dir="$1" cap="$2" log_name="${4:-app.log}"
  local old_log="${FAULTS_APP_LOG:-}" old_env=()
  if [ ${#EXTRA_ENV[@]} -gt 0 ]; then old_env=("${EXTRA_ENV[@]}"); fi
  # A third argument replaces the extra environment for this run; none leaves the caller's in place.
  if [ "${3:-}" != "" ]; then EXTRA_ENV=("$3"); fi
  FAULTS_APP_LOG="$dir/$log_name"
  export FAULTS_APP_LOG
  start_variant "$VARIANT" "$dir" "$cap"
  local pid="$STARTED_PID"
  local status="timeout"
  if wait_app "$dir" 1800; then status="$(app_status)"; fi
  reap_servers "$dir"
  rm -f "$dir/pid"
  FAULTS_APP_LOG="$old_log"
  EXTRA_ENV=(${old_env[@]+"${old_env[@]}"})
  export FAULTS_APP_LOG
  echo "$status"
}

# Each run has its own apphost directory as well as its own target: `start_variant` starts `<run>/app/<binary>`
# in `<run>/target`, and three runs sharing one apphost directory would share one gate script.
for name in reference repeat interrupted; do
  mkdir -p "$WORK/$name/target"
  cp -R "$WORK/app" "$WORK/$name/app"
done
cp -R "$WORK/template/." "$WORK/reference/target/"
cp -R "$WORK/template/." "$WORK/repeat/target/"
cp -R "$WORK/template/." "$WORK/interrupted/target/"

FAULTS_HOLD=""
FAULTS_COUNT=0
export FAULTS_HOLD FAULTS_COUNT

log "run-equivalence: reference run"
REFERENCE_STATUS="$(run_to_end "$WORK/reference" "$TARGET_CAP")"
REFERENCE_VERIFY="$(verify_target "$WORK/reference/target")"

log "run-equivalence: repeat run"
REPEAT_STATUS="$(run_to_end "$WORK/repeat" "$TARGET_CAP")"
REPEAT_VERIFY="$(verify_target "$WORK/repeat/target")"

REPRODUCIBLE="$(compare_product "$WORK/reference/target" "$WORK/repeat/target")"
REPRODUCIBLE_NORMALISED="$(compare_normalised "$WORK/reference/target" "$WORK/repeat/target")"

# ── 2. interrupted, then continued ─────────────────────────────────────────────────────────────────
log "run-equivalence: interrupted run (stopped after a committed iteration)"
if [ "$VARIANT" = "durable" ]; then
  # The variant's own seam: it commits an iteration and then stops, by a cancellation it raises itself.
  # That is the one stop this band can put *between* iterations — from outside the process the only
  # reachable frame is inside the next turn (see restart-after-checkpoint) — and it unwinds rather than
  # dying, so the store is closed and the next process opens it cleanly.
  INTERRUPT_FRAME="STOP_AFTER_ITERATION=2, the variant's own committed-iteration stop"
  STOP_WORD="STOP_AFTER_ITERATION=2"
  EXTRA_ENV=("$STOP_WORD")
  INTERRUPTED_STATUS="$(run_to_end "$WORK/interrupted" "$TARGET_CAP" "" app1.log)"
  EXTRA_ENV=()
else
  # A SIGKILL inside the pass, which is the only stop this harness can put on the baseline: the kill lands
  # on the application's own PID while a tool call is in flight.
  FAULTS_HOLD="out:tool.execution_start"
  FAULTS_COUNT=2
  export FAULTS_HOLD FAULTS_COUNT
  FAULTS_APP_LOG="$WORK/interrupted/app1.log"
  export FAULTS_APP_LOG
  start_variant "$VARIANT" "$WORK/interrupted" "$TARGET_CAP"
  KILLED_PID="$STARTED_PID"
  if ! wait_for_inflight "$WORK/interrupted" 300; then
    log "run-equivalence: the gate never reached the interruption point"
    exit 1
  fi
  INTERRUPT_FRAME="$(inflight_frame "$WORK/interrupted")"
  kill_and_confirm "$KILLED_PID" application || exit 1
  reap_servers "$WORK/interrupted"
  rm -f "$WORK/interrupted/pid" "$WORK/interrupted/inflight" "$WORK/interrupted/release"
  FAULTS_HOLD=""
  FAULTS_COUNT=0
  export FAULTS_HOLD FAULTS_COUNT
fi

FAULTS_APP_LOG="$WORK/interrupted/app.log"
export FAULTS_APP_LOG

INTERRUPTED_STATUS="$(run_to_end "$WORK/interrupted" "$TARGET_CAP")"
INTERRUPTED_VERIFY="$(verify_target "$WORK/interrupted/target")"
INTERRUPTED_END="$(end_line "$WORK/interrupted/app.log")"

EQUIVALENT="$(compare_product "$WORK/reference/target" "$WORK/interrupted/target")"
EQUIVALENT_NORMALISED="$(compare_normalised "$WORK/reference/target" "$WORK/interrupted/target")"

# An interrupted run whose second process refused (the Durable variant's in-flight limit) is not a
# difference in the product: it is a run that stopped. The two are separate sentences.
HONEST_NOTE=""
REFUSED_IN_FLIGHT=0
# Every log this run wrote: the first process's and the continuation's, whichever names the helper gave
# them. A check that named one file and missed the other would report a refusal as a difference.
if grep -qs 'was in flight' "$WORK/interrupted/"*.log; then
  REFUSED_IN_FLIGHT=1
  HONEST_NOTE=" The second process refused to continue: \"$INTERRUPTED_END\". The interruption landed in a turn the variant does not resume, so this run has no continued product to compare — the comparison below is between a finished product and a stopped one and is reported for completeness, not as evidence that a resume changed anything."
fi

if [ "$VARIANT" = "baseline" ]; then
  HEADLINE="An uninterrupted baseline run compared with one killed mid-turn and then re-run. The baseline cannot continue a run — its position is a local variable — so its 'continued' product was written by two processes where the reference's was written by one. The comparison is between those two products, and every file that differs is named."
else
  HEADLINE="An uninterrupted Durable run compared with one killed between iterations and then continued by a second process. The checkpoint is the loop's position, so the second process reads it instead of the plan alone."
fi

DETAIL="$HEADLINE Compared: the same file set, with the same SHA-256 per file, at the same relative path — every regular file under the target except .durable/ (the store, which differs by construction) and the harness's build directory. Reference: $REFERENCE_STATUS (verifier $REFERENCE_VERIFY). Repeat, uninterrupted: $REPEAT_STATUS (verifier $REPEAT_VERIFY), which compared '$REPRODUCIBLE' against the reference byte for byte, and '$REPRODUCIBLE_NORMALISED' after dropping blank lines — that is what says whether two clean runs are the same thing on this machine. Interrupted at '$INTERRUPT_FRAME' with SIGKILL, then continued: exit $INTERRUPTED_STATUS, verifier $INTERRUPTED_VERIFY, last line \"$INTERRUPTED_END\".$HONEST_NOTE Interrupted vs reference: $EQUIVALENT (normalised: $EQUIVALENT_NORMALISED). The verdict is taken from the interrupted product against the reference, after dropping blank lines, because the repeat run above shows two clean runs of this variant agree byte for byte. Where they do not, the result says the runs are not comparable and claims nothing about the interruption. Comparator self-check: $SELF_CHECK."

if [ "$REFUSED_IN_FLIGHT" = "1" ]; then
  # Nothing continued the run, so there is no continued product to compare. Said first, because no
  # comparison below would mean anything about a resume.
  OUTCOME="not resumed: the interruption landed in the in-flight turn"
elif [[ "$REPRODUCIBLE_NORMALISED" != identical* ]]; then
  OUTCOME="not comparable: two clean runs differed"
elif [[ "$EQUIVALENT_NORMALISED" == identical* ]]; then
  OUTCOME="identical"
else
  OUTCOME="differs"
fi

EVIDENCE="reference: $REFERENCE_VERIFY; repeat vs reference: $REPRODUCIBLE (normalised: $REPRODUCIBLE_NORMALISED); interrupted vs reference: $EQUIVALENT (normalised: $EQUIVALENT_NORMALISED); verdict taken from: interrupted vs reference, normalised; interruption point: '$INTERRUPT_FRAME'; comparator self-check: $SELF_CHECK"
record_result "$VARIANT" "$FAULT" "$OUTCOME" \
  "reference, repeat, and kill-then-continue of the same staged target" \
  "$DETAIL" \
  "$EVIDENCE"
exit 0
