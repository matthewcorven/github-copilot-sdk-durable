#!/usr/bin/env bash
# The shared machinery every Band B fault script uses: build once, stage a target, start a variant,
# drive it, fault it, observe it, record it.
#
# A fault script is a caller of this file, not a copy of it. What lives here:
#
#   - a *run directory*: a staged target, a gate, a log, recreated per run so nothing is inherited;
#   - a *variant*: how to build it into a real apphost and how to start it;
#   - a *gate*: the JSON-RPC relay in front of the Copilot CLI, which is the injection point;
#   - a *result*: one JSON file per fault per variant, written even when the fault could not be
#     exercised, so "not exercised" is a recorded outcome rather than a missing file.
#
# The rule this file exists to keep: a fault is injected against a real process, by the operating
# system, at a moment the harness can point at. Nothing here fakes a crash in-process.
#
# Two things about this band decide the shape of everything below.
#
# 1. **The variants make live model calls.** There is no recorded runtime for this job: both variants
#    build a session with a working directory and send a prompt, and the CLI performs the edits. Band A
#    could replay a fixture; Band B cannot, because the work *is* the model's tool calls inside the
#    target. So the gate in front of the CLI is what both the injection and the observation hang from.
#
# 2. **`dotnet run file.cs` is not the application.** It starts a `dotnet` host which starts the
#    application, so a PID from `$!` names the host: `kill -9` would land on the wrapper and the job
#    would keep running. Both variants are therefore built into an apphost once per run
#    (`dotnet build file.cs -c Release -o <dir>`) and started as `<dir>/ralph-loop*`. The PID this file
#    records is the application's own, and the death it confirms is that PID's.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
RESULTS_DIR="${FAULTS_RESULTS_DIR:-$HARNESS_DIR/results}"
CHECKED_IN_RESULTS="$HARNESS_DIR/results"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-b-faults}"
COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
export FAULTS_COMMIT="$COMMIT"

DOTNET="${DOTNET:-/opt/homebrew/bin/dotnet}"
PYTHON="${PYTHON:-python3}"
GATE="$HARNESS_DIR/lib/cli_gate.py"
# How many docs sections the staged target has. The template's ten plus this many extra, so a run has
# several iterations to be faulted between; see lib/stage.py.
TARGET_SECTIONS="${FAULTS_SECTIONS:-14}"
# The cap given to a variant when a fault needs the run to have a budget. It is generous on purpose:
# the fault, not the cap, is what stops the run.
TARGET_CAP="${FAULTS_CAP:-12}"

log() { printf '%s\n' "$*" >&2; }

require_tools() {
  local missing=0
  if [ ! -x "$DOTNET" ]; then log "harness: $DOTNET is not executable; .NET 10 is required"; missing=1; fi
  command -v "$PYTHON" >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  command -v bash >/dev/null 2>&1 || { log "harness: bash is required"; missing=1; }
  return $missing
}

variant_source() {
  case "$1" in
    baseline) echo "$BAND_DIR/baseline/ralph-loop.cs" ;;
    durable) echo "$BAND_DIR/durable/ralph-loop-durable.cs" ;;
  esac
}

# The apphost the build produces is named after the source file, without `.cs`.
variant_binary() {
  case "$1" in
    baseline) echo ralph-loop ;;
    durable) echo ralph-loop-durable ;;
  esac
}

# Builds a variant once per run into `<run>/app`, and prints nothing to stdout: `prepare_variant`
# failures are the caller's, and a build's log is the run's.
prepare_variant() {
  local variant="$1" out="$2"
  local source binary
  source="$(variant_source "$variant")"
  binary="$(variant_binary "$variant")"
  [ -x "$out/$binary" ] && return 0
  log "harness: building $variant (this compiles the file-based app; about a minute cold)"
  {
    "$DOTNET" build "$source" -c Release -o "$out" --nologo -v quiet
  } >"$out.build.log" 2>&1
  local status=$?
  if [ ! -x "$out/$binary" ]; then
    log "harness: build of $variant produced no apphost at $out/$binary (status $status)"
    tail -20 "$out.build.log" >&2
    return 1
  fi
  return 0
}

# The CLI the SDK shipped with the variant's build. Using the bundled CLI rather than whatever is first
# on PATH keeps the harness independent of the machine's install — and it is not decoration: with
# `COPILOT_CLI_PATH` pointed at the 1.0.105 install, `gpt-5.1-codex-mini` is refused as unavailable,
# while the bundled 1.0.103 runs it.
variant_cli() {
  local out="$1" candidate
  for candidate in "$out"/runtimes/*/native/copilot; do
    [ -x "$candidate" ] && { echo "$candidate"; return 0; }
  done
  return 1
}

# Writes the gate script both variants are pointed at. The gate is a shell script so that the variant
# env var can name one path, and the python file stays a file the harness edits.
write_gate() {
  local path="$1" rundir="$2" cli="$3"
  cat > "$path" <<GATE
#!/usr/bin/env bash
exec "$PYTHON" "$GATE" "$rundir/gate.log" "$rundir" -- "$cli" "\$@"
GATE
  chmod +x "$path"
}

# A fresh run directory with a staged target, the gate written, and nothing else. The stage is the
# band's own template plus `lib/stage.py`, which is what gives a run more than one iteration.
new_run_dir() {
  local variant="$1" fault="$2"
  local dir="$SCRATCH_ROOT/$variant/$fault"
  rm -rf "$dir"
  mkdir -p "$dir/target" "$dir/app"
  cp -R "$BAND_DIR/baseline/target/." "$dir/target/"
  "$PYTHON" "$HARNESS_DIR/lib/stage.py" "$dir/target" "$TARGET_SECTIONS" >/dev/null || {
    log "harness: could not stage the target in $dir"
    return 1
  }
  echo "$dir"
}

# Starts a variant as its own process, in the target directory, with the gate in front of its CLI.
#
# Call it as a statement. `PID=$(start_variant ...)` would run the function in a subshell, the background
# job would belong to that subshell, and `wait` in the caller would report 127 instead of the
# application's status. STARTED_PID exists for exactly that reason.
start_variant() {
  local variant="$1" rundir="$2"
  shift 2
  local out="$rundir/app" binary cli
  binary="$(variant_binary "$variant")"
  cli="$(variant_cli "$out")" || { log "harness: no bundled CLI under $out/runtimes"; return 1; }
  write_gate "$rundir/copilot" "$rundir" "$cli"

  # A run that starts a second loop needs each loop's output kept apart, so the log name is the
  # caller's to choose. The default is one log per run.
  local applog="${FAULTS_APP_LOG:-$rundir/app.log}"
  pushd "$rundir/target" >/dev/null || return 1
  if [ "$variant" = "baseline" ]; then
    # Started by this shell, not in a subshell: `wait` must be able to report this process's status, and
    # `kill -9` must reach the application rather than a shell that has already exited.
    COPILOT_CLI_PATH="$rundir/copilot" \
    RALPH_MODEL="${FAULTS_MODEL:-gpt-5.1-codex-mini}" \
      "$out/$binary" "$@" >"$applog" 2>&1 &
  else
    DURABLE_RUNTIME_PATH="$rundir/copilot" \
    RALPH_MODEL="${FAULTS_MODEL:-gpt-5.1-codex-mini}" \
      "$out/$binary" "$@" >"$applog" 2>&1 &
  fi
  echo $! > "$rundir/pid"
  popd >/dev/null || true
  STARTED_PID="$(cat "$rundir/pid")"
  # On stderr, always: a caller that captures a helper's stdout (`STATUS="$(run_one ...)"`) must not get
  # this line mixed into the value it captured.
  log "harness: started $variant as pid $STARTED_PID"
}

# Waits until the application exits, then reports the application's *own* exit status.
#   0  it exited; the status is in APP_STATUS
#   1  it was still running when the timeout expired (it was stopped, and the status is meaningless)
APP_STATUS=""
wait_app() {
  local rundir="$1" seconds="${2:-900}"
  local pid deadline
  APP_STATUS=""
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] || { APP_STATUS="no-process"; return 0; }
  deadline=$((SECONDS + seconds))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.2; done
  if kill -0 "$pid" 2>/dev/null; then return 1; fi
  wait "$pid" 2>/dev/null
  APP_STATUS=$?
  return 0
}

app_status() { echo "${APP_STATUS:-not-observed}"; }

# Waits for the gate to say it has the application blocked inside a frame, and reports which frame.
wait_for_inflight() {
  local rundir="$1" seconds="${2:-240}"
  local deadline=$((SECONDS + seconds))
  while [ ! -s "$rundir/inflight" ]; do
    [ "$SECONDS" -lt "$deadline" ] || return 1
    sleep 0.1
  done
  return 0
}

# Which frame the gate is holding, as the gate wrote it: `<direction> <selector> <occurrence> <app-pid>`.
inflight_frame() { head -1 "$1/inflight" 2>/dev/null | awk '{print $1 " " $2 " " $3}'; }

release_gate() {
  local rundir="$1"
  : > "$rundir/release"
}

# Kills a PID and waits for that same PID to be gone. Returns non-zero when the process survived, which
# is a harness error and never a finding.
kill_and_confirm() {
  local pid="$1" what="${2:-process}"
  kill -9 "$pid" 2>/dev/null || true
  local deadline=$((SECONDS + 20))
  while kill -0 "$pid" 2>/dev/null && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
  if kill -0 "$pid" 2>/dev/null; then
    log "harness: $what pid $pid survived SIGKILL, which cannot happen"
    return 1
  fi
  return 0
}

# Every process the gate wrote down for this run: the application, the gate and the CLI. A `kill -9` on
# the application reparents the other two, so the tree walk cannot find them; the gate's `servers` file
# is what makes the reap by PID possible without matching on a path.
reap_servers() {
  local rundir="$1" app gate runtime
  [ -f "$rundir/servers" ] || return 0
  while read -r app gate runtime _; do
    [ -n "${runtime:-}" ] && kill -9 "$runtime" 2>/dev/null || true
    [ -n "${gate:-}" ] && kill -9 "$gate" 2>/dev/null || true
    [ -n "${app:-}" ] && kill -9 "$app" 2>/dev/null || true
  done < "$rundir/servers"
  rm -f "$rundir/servers"
}

stop_variant_keep() {
  local rundir="$1" pid
  pid="$(cat "$rundir/pid" 2>/dev/null || true)"
  [ -n "$pid" ] && kill_and_confirm "$pid" application
  reap_servers "$rundir"
  rm -f "$rundir/pid" "$rundir/inflight" "$rundir/release"
}

# ── the target, as the fault and the observation see it ──────────────────────────────────────────────

# The files the job produces: the plan, the notes, and the sections. `.durable/` and `app/` are not part
# of the product — one is the record, the other is the binary — so neither is ever in a comparison.
product_files() {
  local target="$1"
  ( cd "$target" && find . -type f \
      -not -path './.durable/*' \
      -not -path './app/*' \
      -not -name '.band-b-*' | LC_ALL=C sort )
}

# The digest of the product: one line per file, `<sha256>  <path>`. Two targets are the same product
# when this text is the same, which is what the run-equivalence test compares.
product_digest() {
  local target="$1"
  ( cd "$target" && product_files "$target" | while IFS= read -r file; do
      printf '%s  %s\n' "$(shasum -a 256 "$file" | cut -d' ' -f1)" "$file"
    done )
}

# The same digest with the *presentation* differences removed: blank lines dropped entirely, and trailing
# whitespace stripped from every line that remains. Blank lines are dropped rather than collapsed because
# the difference this exists to explain is not "one blank line or two" but "a blank line between the H1 and
# the body, or none" — the Durable variant normalises that away, the baseline does not. This is a weaker comparison than `product_digest` and is
# reported separately, never in its place: the Durable variant normalises "H1, one blank line, body" so its
# runs are byte-identical, and the baseline does not, so two correct baseline runs differ by a blank line.
# A caller that wants to say "the same work, differently spaced" needs this; a caller that wants to say
# "identical" needs the other one.
product_digest_normalised() {
  local target="$1"
  ( cd "$target" && product_files "$target" | while IFS= read -r file; do
      "$PYTHON" - "$file" <<'PY'
import hashlib, re, sys
text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
out = []
for line in text.replace("\r\n", "\n").split("\n"):
    line = line.rstrip()
    if line == "":
        continue
    out.append(line)
print(hashlib.sha256("\n".join(out).encode("utf-8")).hexdigest())
PY
    done )
}

# How many plan items are ticked, how many are open, and whether the status line says complete. Read from
# the file, not from what a process printed.
plan_counts() {
  local target="$1"
  "$PYTHON" - "$target/PLAN.md" <<'PY'
import sys
try:
    text = open(sys.argv[1], encoding="utf-8").read()
except OSError:
    print("missing 0 0")
    raise SystemExit(0)
open_items = sum(1 for line in text.split("\n") if line.startswith("- [ ]"))
done_items = sum(1 for line in text.split("\n") if line.startswith("- [x]"))
status = "complete" if "Status: complete" in text else ("in-progress" if "Status: in-progress" in text else "unknown")
print("%s %d %d" % (status, done_items, open_items))
PY
}

# What the verifier says about the target: `pass` or `fail`, with its own first complaint.
verify_target() {
  local target="$1" output
  if output="$(bash "$BAND_DIR/baseline/verify.sh" "$target" 2>&1)"; then
    echo "pass"
  else
    echo "fail: $(printf '%s' "$output" | grep -m1 '^FAIL' || printf 'no FAIL line')"
  fi
}

# The iteration the run reached, from the variant's own output. Both variants print `=== Iteration n/N`
# and one of the two terminal lines below, so one reader covers both.
iterations_run() {
  local log_file="$1"
  grep -c '^=== Iteration ' "$log_file" 2>/dev/null || true
}

# The shape of a run's ending, in the variant's own words.
end_line() {
  local log_file="$1"
  grep -m1 -E 'plan reports complete|iteration cap reached|stopped at a committed|refused:|run did not complete|Unhandled exception' "$log_file" 2>/dev/null |
    cut -c1-160
}

# The store a Durable run left, and whether every line of it parses and the last task record is
# readable. A torn tail is a fact about the store, so it is reported rather than assumed.
store_state() {
  local store="$1"
  "$PYTHON" - "$store" <<'PY'
import json, sys
path = sys.argv[1]
lines = 0
torn = 0
last_task = None
try:
    handle = open(path, encoding="utf-8")
except OSError:
    print("missing 0 0 -")
    raise SystemExit(0)
for raw in handle:
    raw = raw.strip()
    if not raw:
        continue
    lines += 1
    try:
        record = json.loads(raw)
    except ValueError:
        torn += 1
        continue
    if record.get("type") == "task":
        last_task = record.get("payload") or {}
checkpoint = "-"
if last_task:
    value = last_task.get("checkpoint")
    if isinstance(value, str):
        try:
            value = json.loads(value)
        except ValueError:
            value = None
    if isinstance(value, dict):
        checkpoint = "%s/%s" % (value.get("stage", "?"), value.get("iteration", "?"))
print("%s %d %d %s" % ("present" if lines else "empty", lines, torn, checkpoint))
PY
}

# Frames the gate saw, by direction. Two of these, one per connection, is how a concurrent run proves
# that two clients overlapped instead of assuming it.
gate_frames() {
  local rundir="$1" direction="$2"
  grep -c "\[gate\] $direction " "$rundir/gate.log" 2>/dev/null || true
}

# ── results ─────────────────────────────────────────────────────────────────────────────────────────

write_result() {
  local variant="$1" fault="$2" json="$3"
  local path="$RESULTS_DIR/$variant/$fault.json"
  mkdir -p "$RESULTS_DIR/$variant"
  if [ -f "$path" ] && [ "${FAULTS_ALLOW_DOWNGRADE:-0}" != "1" ]; then
    local before after
    before="$("$PYTHON" -c 'import json,sys; print((json.load(open(sys.argv[1])) or {}).get("outcome",""))' "$path" 2>/dev/null || true)"
    after="$("$PYTHON" -c 'import json,sys; print((json.loads(sys.argv[1]) or {}).get("outcome",""))' "$json" 2>/dev/null || true)"
    if [ "$after" = "not exercised" ] && [ -n "$before" ] && [ "$before" != "not exercised" ]; then
      log "harness: refusing to replace a measured '$before' result at $path with 'not exercised'"
      log "harness: set FAULTS_ALLOW_DOWNGRADE=1 to record the loss of capability deliberately"
      return 1
    fi
  fi
  printf '%s\n' "$json" > "$path"
  log "harness: wrote $path"
}

# The one way a result is recorded. `detail` says what was observed; `evidence` carries the raw
# observations the claim comes from, so a reader can check the sentence against them.
record_result() {
  local variant="$1" fault="$2" outcome="$3" phase="$4" detail="$5" evidence="${6:-}"
  local json
  json="$("$PYTHON" - "$variant" "$fault" "$outcome" "$phase" "$detail" "$evidence" <<'PY'
import datetime, json, os, sys
variant, fault, outcome, phase, detail, evidence = sys.argv[1:7]
print(json.dumps({
    "variant": variant,
    "fault": fault,
    "outcome": outcome,
    "phase": phase,
    "detail": detail,
    "evidence": evidence,
    "harness": {
        "dotnet": os.environ.get("DOTNET", "dotnet"),
        "model": os.environ.get("FAULTS_MODEL", "gpt-5.1-codex-mini"),
        "command": os.environ.get("FAULT_COMMAND", ""),
        "script": os.environ.get("FAULT_SCRIPT", ""),
        "commit": os.environ.get("FAULTS_COMMIT", "unknown"),
    },
    "recordedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
}, indent=2, ensure_ascii=False))
PY
)"
  write_result "$variant" "$fault" "$json"
}

record_not_exercised() {
  local variant="$1" fault="$2" phase="$3" reason="$4"
  record_result "$variant" "$fault" "not exercised" "$phase" "$reason" ""
}

# ── cleanup ────────────────────────────────────────────────────────────────────────────────────────

CLEANUP_DIRS=()
cleanup_all() {
  local status=$?
  local dir
  for dir in ${CLEANUP_DIRS[@]+"${CLEANUP_DIRS[@]}"}; do
    if [ "${KEEP:-0}" = "1" ]; then
      # KEEP is the promise that a failure can be inspected afterwards: the processes are stopped and
      # the directory is left alone.
      stop_variant_keep "$dir" 2>/dev/null || true
      log "harness: kept $dir"
    else
      stop_variant_keep "$dir" 2>/dev/null || true
      rm -rf "$dir"
    fi
  done
  return $status
}
trap cleanup_all EXIT INT TERM

track_dir() { CLEANUP_DIRS+=("$1"); }
