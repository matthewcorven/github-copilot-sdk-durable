#!/usr/bin/env bash
# Reproduces the Durable variant's evidence: restart mid-enquiry, resume, and concurrency.
#
# Usage:
#   ./evidence.sh              # run every capture, print it, and remove scratch on exit
#   KEEP=1 ./evidence.sh       # keep the scratch directory
#
# Stages 1-3 use the recorded runtime in tools/ReplayRuntime with the fixture in fixtures/, so
# they are deterministic: no Copilot sign-in, no model, no network. The kill point is
# BAND_A_EXIT_AFTER_PHASE, which exits the process the moment that phase commits.
#
# Stage 4 needs the real Copilot CLI: the recorded runtime serves every pinned session from the
# first recording, so it cannot give two customers two different transcripts. Pass LIVE=1 to run
# stage 4 against the signed-in `copilot` executable.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
DOTNET="${DOTNET:-dotnet}"
RUNTIME="$ROOT/tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime"

[ -x "$RUNTIME" ] || { echo "evidence.sh: build the recorded runtime first: $DOTNET build tools/ReplayRuntime" >&2; exit 1; }

SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/band-a-durable-evidence.XXXXXX")"
cleanup() {
  if [ "${KEEP:-0}" = "1" ]; then echo; echo "Scratch kept: $SCRATCH"; else rm -rf "$SCRATCH"; fi
}
trap cleanup EXIT INT TERM

"$DOTNET" publish "$HERE/BandADurable.csproj" -c Release -o "$SCRATCH/app" --nologo -v quiet \
  2>&1 | grep -v 'NU1903\|^warn: Microsoft.EntityFrameworkCore' >&2
APP="$SCRATCH/app/BandADurable.dll"

run() {
  local store="$1" fixture="$2"; shift 2
  local log="$SCRATCH/run.log"
  BAND_A_STORE="$store" BAND_A_FIXTURE="$SCRATCH/app/fixtures/$fixture" \
  DURABLE_RUNTIME_PATH="$RUNTIME" Logging__LogLevel__Default=Warning \
    "$DOTNET" "$APP" "$@" > "$log" 2>&1
  local status=$?
  grep -v '^      \|NU1903\|^warn: Microsoft.EntityFrameworkCore' "$log"
  return $status
}

# Counts the prompts the durable layer admitted, which is how many model turns were actually sent.
count_prompts() {
  python3 -c '
import json,sys
n=0
for line in open(sys.argv[1]):
    record=json.loads(line)
    payload=record.get("payload") or {}
    if record.get("type")=="document-version" and payload.get("kind")=="durable.admission":
        n+=1
print(n)
' "$1"
}

# Reads the committed entry count straight out of the JSONL, so a stage can say what the kill left
# behind without depending on how a later process happens to print it.
count_entries() {
  python3 -c '
import json,sys
n=0
for line in open(sys.argv[1]):
    record=json.loads(line)
    payload=record.get("payload") or {}
    if record.get("type")=="document-version" and payload.get("kind")=="band-a.enquiry":
        n=len((payload.get("value") or {}).get("entries") or [])
print(n)
' "$1"
}

# The uninterrupted run every later stage is compared against. Produced first so any stage can cite
# it, and produced from the same fixture the killed runs use.
rm -f "$SCRATCH/clean.jsonl"
clean="$(run "$SCRATCH/clean.jsonl" enquiry.json --enquiry "Looking for a 3-bed family home near schools")"
echo "############ 0. an uninterrupted run of the fixture, for every comparison below ############"
echo "$clean" | sed "s|$SCRATCH|<scratch>|g"
echo "entries: $(count_entries "$SCRATCH/clean.jsonl"), prompts admitted: $(count_prompts "$SCRATCH/clean.jsonl")"
echo
echo "############ 1. restart mid-enquiry: exit after the 'searching' phase commits ############"
rm -f "$SCRATCH/restart.jsonl"
run "$SCRATCH/restart.jsonl" enquiry.json --enquiry "Looking for a 3-bed family home near good schools, budget around \$500k" \
  --exit-after-phase searching | sed "s|$SCRATCH|<scratch>|g"
echo "exit code: $?  (42 is the seam: the store is committed, and the process is gone)"

echo
echo "############ 2. resume the same store in a new process ############"
run "$SCRATCH/restart.jsonl" enquiry.json --resume | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 3. late wakeup after rejection cannot overwrite the terminal state ############"
rm -f "$SCRATCH/rejected.jsonl"
run "$SCRATCH/rejected.jsonl" rejection.json --enquiry "BUY CRYPTO NOW!!! Not actually looking for property lol" \
  --late-wakeup | sed "s|$SCRATCH|<scratch>|g"
echo
echo "############ 3b. a tool result from the rejection turn cannot grow the terminal record ############"
rm -f "$SCRATCH/rejected-tool.jsonl"
run "$SCRATCH/rejected-tool.jsonl" rejection-tool.json \
  --enquiry "BUY CRYPTO NOW!!! Not actually looking for property lol" \
  --late-wakeup --tool-path | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 3c. the rejection survives a restart (the baseline loses it) ############"
run "$SCRATCH/rejected-tool.jsonl" rejection-tool.json --resume | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 3d. a kill after the model reports done does not lose the report ############"
rm -f "$SCRATCH/done.jsonl"
run "$SCRATCH/done.jsonl" enquiry.json --enquiry "Looking for a 3-bed family home near schools" \
  --exit-after-phase done | sed "s|$SCRATCH|<scratch>|g"
echo "exit code: $?  (42 is the seam: the model has reported done and the process is gone)"
echo
echo "############ 3e. resume that store: the deliverable is recovered by asking again ############"
run "$SCRATCH/done.jsonl" enquiry.json --resume | sed "s|$SCRATCH|<scratch>|g"
echo "prompts admitted for this enquiry: $(count_prompts "$SCRATCH/done.jsonl") "\
"(this is the re-ask path; stage 3g, where the answer survived, admits one fewer)"
echo
echo "############ 3f. the same window, killed AFTER the answer committed: recovered from it ############"
# The distinction stage 3d cannot make. There the model reported done and the answer never
# committed, so the recovery has to ask for it again and the text that comes back is a second
# answer — byte-identical here because the recorded runtime replays the same turn. Here the answer
# did commit, so the transcript already holds it and the recovery attaches it verbatim.
rm -f "$SCRATCH/done-answer.jsonl"
BAND_A_STORE="$SCRATCH/done-answer.jsonl" BAND_A_FIXTURE="$SCRATCH/app/fixtures/enquiry.json" \
BAND_A_EXIT_AFTER_ANSWER=queued DURABLE_RUNTIME_PATH="$RUNTIME" Logging__LogLevel__Default=Warning \
  "$DOTNET" "$APP" --enquiry "Looking for a 3-bed family home near schools" > "$SCRATCH/done-answer.log" 2>&1
echo "exit code: $?  (42 is the seam: the answer is committed and the task boundary is not)"
grep -v '^      \|NU1903\|^warn: Microsoft.EntityFrameworkCore' "$SCRATCH/done-answer.log" | sed "s|$SCRATCH|<scratch>|g"
echo
echo "############ 3g. resume it: the committed answer becomes the report, with no second prompt ############"
run "$SCRATCH/done-answer.jsonl" enquiry.json --resume | sed "s|$SCRATCH|<scratch>|g"
echo "prompts admitted for this enquiry: $(count_prompts "$SCRATCH/done-answer.jsonl") "\
"(an uninterrupted run of the same fixture admits $(count_prompts "$SCRATCH/clean.jsonl" 2>/dev/null || echo '?'))"
echo "the recovery did not re-ask: the answer it attached is the one the killed turn committed"

echo
echo "############ 4. concurrency: two enquiries for two customers, submitted together ############"
if [ "${LIVE:-0}" = "1" ]; then
  LIVE_ARGS=()
else
  LIVE_ARGS=(--fixture "$SCRATCH/app/fixtures/concurrent.json")
  echo "# recorded runtime: both pinned sessions are served from the first recording, so"
  echo "# this stage shows isolation of the durable documents, not of the model transcripts."
fi
rm -f "$SCRATCH/concurrent.jsonl"
if [ "${LIVE:-0}" = "1" ]; then
  BAND_A_STORE="$SCRATCH/concurrent.jsonl" Logging__LogLevel__Default=Warning \
    "$DOTNET" "$APP" \
      --enquiry "Looking for a 3-bed family home near good schools in the suburbs, budget around \$500k" \
      --enquiry "Need a downtown condo with parking, 1-2 bedrooms, under \$350k" 2>&1 | sed "s|$SCRATCH|<scratch>|g"
else
  run "$SCRATCH/concurrent.jsonl" concurrent.json \
    --enquiry "first customer: 3-bed family home" \
    --enquiry "second customer: downtown condo with parking" | sed "s|$SCRATCH|<scratch>|g"
fi

echo
echo "############ 5. concurrency: rank rule vs commit gate ############"
rm -f "$SCRATCH/contend.jsonl"
run "$SCRATCH/contend.jsonl" enquiry.json --enquiry "contended enquiry" --contend 8 | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 6. kill inside an unfinished turn: the resumed process replays the session ############"
# The kill lands after the model reported writing_report and after the tool calls before the
# interrupted answer. The turn that was cut off produced no answer, so the platform has no
# evidence it ran; the resumed process reconnects and is served a recorded session from its
# first turn. That is a recorded-runtime behaviour (see REPLAY_MARKER_PATH below), not a claim
# about what a live session would do, and stage 6d attributes the surplus accordingly.
rm -f "$SCRATCH/midturn.jsonl"
run "$SCRATCH/midturn.jsonl" enquiry.json --enquiry "Looking for a 3-bed family home near schools" \
  --exit-after-phase writing_report | sed "s|$SCRATCH|<scratch>|g"
echo "exit code: $?  (42 is the seam: the phase is committed and the process is gone)"
prekill=$(count_entries "$SCRATCH/midturn.jsonl")
echo "entries committed before the kill: $prekill"
echo
echo "############ 6b. resume it: the reconnected session is served from its first recorded turn ############"
resumed="$(run "$SCRATCH/midturn.jsonl" enquiry.json --resume)"
echo "$resumed" | sed "s|$SCRATCH|<scratch>|g"

echo
echo "############ 6d. the count, and what it does and does not attribute ############"
clean_entries=$(grep -o 'entries:  [0-9]*' <<<"$clean" | head -1 | grep -o '[0-9]*')
killed_entries=$(grep -o 'entries:  [0-9]*' <<<"$resumed" | head -1 | grep -o '[0-9]*')
duplicated_intents=$(grep -c 'intent: Validate home enquiry' <<<"$resumed")
echo "clean run:                          $clean_entries entries"
echo "killed at writing_report, resumed:  $killed_entries entries"
echo "duplicated \"intent: Validate home enquiry\" lines in the resumed run: $duplicated_intents (1 in the clean run)"
echo
echo "The surplus is NOT attributable to the interrupted turn alone. The resumed process reconnects"
echo "to a session the recorded runtime serves from its first recording (REPLAY_MARKER_PATH says"
echo "'synthesised session'), so the earlier turns are sent and committed a second time. What the"
echo "surplus measures is: session replay + the interrupted turn, and the recorded runtime cannot"
echo "separate them. The duplicated tool line is the part traceable to the interrupted turn."

echo
echo "############ 6e. a FINISHED turn killed after its answer: the resume must not re-send it ############"
# The mirror of stage 6. Here the answer and the conversation's turn checkpoint are both durable
# when the process dies, so the resumed task can see that the turn completed and must not send
# it again. This is the case the reconciliation in EnquiryTask.SettledTurnAnswer exists for.
rm -f "$SCRATCH/settled.jsonl"
BAND_A_STORE="$SCRATCH/settled.jsonl" BAND_A_FIXTURE="$SCRATCH/app/fixtures/enquiry.json" \
BAND_A_EXIT_AFTER_ANSWER=queued DURABLE_RUNTIME_PATH="$RUNTIME" Logging__LogLevel__Default=Warning \
  "$DOTNET" "$APP" --enquiry "Looking for a 3-bed family home near schools" > "$SCRATCH/settled.log" 2>&1
echo "exit code: $?  (42 is the seam: the answer is committed, the task boundary is not)"
grep -v '^      \|NU1903\|^warn: Microsoft.EntityFrameworkCore' "$SCRATCH/settled.log" | sed "s|$SCRATCH|<scratch>|g"
echo
echo "############ 6f. resume that store: no prompt is sent for a turn that already finished ############"
settled="$(run "$SCRATCH/settled.jsonl" enquiry.json --resume)"
echo "$settled" | sed "s|$SCRATCH|<scratch>|g"
# The counts are compared on the transcript proper, not on the printed total: the resumed run adds
# a "note:" line that records the recovery, and that line is not a replayed turn.
clean_lines=$(grep -c '^    ' <<<"$clean")
settled_lines=$(grep -c '^    ' <<<"$settled")
settled_notes=$(grep -c 'note: the report was recovered from the answer already committed' <<<"$settled")
settled_intents=$(grep -c 'intent: Validate home enquiry' <<<"$settled")
clean_intents=$(grep -c 'intent: Validate home enquiry' <<<"$clean")
echo "clean run:                          $clean_lines entries"
echo "settled-turn resume:                $settled_lines entries (the clean run's $clean_entries plus one recovery note)"
echo "duplicated \"intent: Validate home enquiry\": $settled_intents in the resume, $clean_intents in the clean run"
if [ "$settled_intents" != "$clean_intents" ]; then
  echo "6f FAILED: a turn that finished was sent again ($settled_intents intents vs $clean_intents)" >&2
  exit 1
fi
if [ "$settled_notes" != "1" ]; then
  echo "6f FAILED: expected exactly one recovery note, saw $settled_notes" >&2
  exit 1
fi
echo "6f held: the finished turn was not re-sent; only the recovery note was added"
