#!/usr/bin/env bash
# The shared machinery the Band D4 fault scripts are callers of: a scratch run directory, a
# variant, a service whose call log is the measurement, a deterministic injection point, and
# one JSON result per fault per variant.
#
# The rules this file keeps, borrowed from the earlier bands and extended for this one:
#
#   1. A fault is injected by the operating system or by the container runtime against a real
#      process. A kill is `kill -9` on a PID this shell started, confirmed gone. A container
#      restart is `docker stop` on a container that is running. A store outage is a real
#      permission change on a real directory, not a flag the application reads.
#   2. Every fault has a `--fault-not-injected` control, so a difference between the two is
#      attributable to the fault and not to the run.
#   3. Every result separates `platform` from `application`, and `lib/result.py` refuses one
#      that does not. This band's subject is attribution; the format enforces it.
#   4. A run leaves nothing behind: one directory per fault per variant, a trap that reaps
#      every process and container it started, and no store, marker or permission change
#      left unless KEEP=1.
#
# Nothing is provisioned and nothing leaves this machine. `platform.live_foundry_called` is
# false in every result, and `lib/fake_foundry.py` is a stand-in that is never described as
# Foundry.

set -uo pipefail

HARNESS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BAND_DIR="$(cd "$HARNESS_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BAND_DIR/../.." && pwd)"
CHECKED_IN_RESULTS="$HARNESS_DIR/results"
SCRATCH_ROOT="${FAULTS_SCRATCH:-$REPO_ROOT/.scratch/band-d4-faults}"
COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
export FAULTS_COMMIT="$COMMIT"

# What a result records in `harness.docker`. A run that used a container records the daemon
# and the image, read from the daemon rather than typed in; a run that used none says so,
# and says which kind of process it used instead.
DEFAULT_DOCKER_NOTE="not used by this fault"

# A partial run is a diagnostic, not evidence, so it never lands in the checked-in tree.
# Band G's rule, kept: a summary that describes a matrix nobody ran is worse than none.
RESULTS_DIR="${FAULTS_RESULTS_DIR:-$CHECKED_IN_RESULTS}"

DOTNET="${DOTNET:-$(command -v dotnet || echo /usr/local/share/dotnet/dotnet)}"
D3_OUT="$SCRATCH_ROOT/bin/d3"
D2_IMAGE="band-d4-d2:${COMMIT:0:12}"
D2_SOURCE="$BAND_DIR/../band-d-durable-inside"

log() { printf '%s\n' "$*" >&2; }

# The interpreter that can import the durable layer, so the fault harness measures the
# repository's own code rather than an installed copy. Created once, on demand.
harness_python() {
  if [ -n "${PYTHON:-}" ] && "$PYTHON" -c "import durable" >/dev/null 2>&1; then
    printf '%s\n' "$PYTHON"
    return 0
  fi
  for candidate in "$REPO_ROOT/.scratch/band-d4/venv/bin/python" "$REPO_ROOT/.scratch/agentvenv/bin/python"; do
    if [ -x "$candidate" ] && "$candidate" -c "import durable" >/dev/null 2>&1; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

require_tools() {
  local missing=0
  if ! harness_python >/dev/null; then
    log "harness: no interpreter imports \`durable\`; create one with:"
    log "harness:   python3 -m venv .scratch/band-d4/venv && .scratch/band-d4/venv/bin/python -m pip install -e lib/py"
    missing=1
  fi
  command -v python3 >/dev/null 2>&1 || { log "harness: python3 is required"; missing=1; }
  command -v curl >/dev/null 2>&1 || { log "harness: curl is required (the service readiness probe)"; missing=1; }
  return $missing
}

# A container restart has to be a real container, so the durable-inside variant needs docker.
require_docker() {
  command -v docker >/dev/null 2>&1 || {
    log "harness: docker is required for durable-inside: a container restart has to be a real container"
    return 1
  }
  docker info >/dev/null 2>&1 || { log "harness: the docker daemon is not answering"; return 1; }
}

# D3 is a file-based C# app, published so the PID the shell starts is the application.
require_dotnet() {
  [ -x "$DOTNET" ] || { log "harness: dotnet is required to publish real-world-scenarios/band-d3-outside-orchestrator"; return 1; }
}

# One port per fault per variant, so two runs never fight over a socket and a stale listener
# is found by asking for the port rather than by reading a log.
port_for() {
  case "$1:$2" in
    baseline:client-dies-mid-run) echo 5411 ;;
    baseline:client-dies-mid-run-control) echo 5412 ;;
    baseline:container-restart) echo 5413 ;;
    baseline:container-restart-control) echo 5414 ;;
    baseline:duplicate-delivery) echo 5415 ;;
    baseline:duplicate-delivery-control) echo 5416 ;;
    baseline:concurrent-clients) echo 5417 ;;
    baseline:concurrent-clients-control) echo 5418 ;;
    baseline:store-outage) echo 5419 ;;
    baseline:store-outage-control) echo 5420 ;;
    baseline:agent-call-failure) echo 5421 ;;
    baseline:agent-call-failure-control) echo 5422 ;;
    durable-inside:client-dies-mid-run) echo 5431 ;;
    durable-inside:client-dies-mid-run-control) echo 5432 ;;
    durable-inside:container-restart) echo 5433 ;;
    durable-inside:container-restart-control) echo 5434 ;;
    durable-inside:duplicate-delivery) echo 5435 ;;
    durable-inside:duplicate-delivery-control) echo 5436 ;;
    durable-inside:concurrent-clients) echo 5437 ;;
    durable-inside:concurrent-clients-control) echo 5438 ;;
    durable-inside:store-outage) echo 5439 ;;
    durable-inside:store-outage-control) echo 5440 ;;
    durable-outside:client-dies-mid-run) echo 5451 ;;
    durable-outside:agent-call-failure) echo 5453 ;;
    durable-outside:agent-call-failure-control) echo 5454 ;;
    durable-outside:duplicate-delivery) echo 5455 ;;
    durable-outside:duplicate-delivery-control) echo 5456 ;;
    durable-outside:concurrent-clients) echo 5457 ;;
    durable-outside:concurrent-clients-control) echo 5458 ;;
    durable-outside:store-outage) echo 5459 ;;
    durable-outside:store-outage-control) echo 5460 ;;
    durable-outside:container-restart) echo 5461 ;;
    durable-outside:container-restart-control) echo 5462 ;;
    *) echo 5498 ;;
  esac
}

control_name() {
  case "$1" in
    concurrent-clients) echo "$1-control" ;;
    *) echo "$1-control" ;;
  esac
}

# Per-fault argument parsing. Every fault script is this two-liner plus its own body.
parse_fault_args() { # script_path "$@"
  local script="$1"
  shift
  VARIANT=""
  INJECT=1
  while [ $# -gt 0 ]; do
    case "$1" in
      --variant) VARIANT="${2:-}"; shift 2 ;;
      --fault-not-injected) INJECT=0; shift ;;
      -h|--help) sed -n '2,/^$/p' "$script" | sed -e 's/^# \{0,1\}//'; exit 0 ;;
      *) log "fault: unknown argument '$1'"; exit 2 ;;
    esac
  done
  case "$VARIANT" in
    baseline|durable-inside|durable-outside) ;;
    *) log "fault: --variant baseline|durable-inside|durable-outside is required"; exit 2 ;;
  esac
  # Exported because `lib/result.py` spells the invocation that produced the file, and a
  # control's command line is not the fault's.
  export FAULT_INJECT="$INJECT"
}

new_run_dir() { # variant fault
  local dir="$SCRATCH_ROOT/runs/$1/$2"
  rm -rf "$dir"
  mkdir -p "$dir"
  printf '%s\n' "$dir"
}

# -- the service whose log is the call count -------------------------------------------

SERVICE_PID=""
SERVICE_ROOT=""

start_service() { # run_dir port
  SERVICE_ROOT="$1/service"
  mkdir -p "$SERVICE_ROOT/control"
  python3 "$HARNESS_DIR/lib/fake_foundry.py" --root "$SERVICE_ROOT" --port "$2" \
    > "$1/service.log" 2>&1 &
  SERVICE_PID=$!
  local waited=0
  while [ "$waited" -lt 200 ]; do
    if [ -f "$SERVICE_ROOT/ready" ]; then
      export FOUNDRY_BASE_URL="http://127.0.0.1:$2"
      export FOUNDRY_TOKEN="offline-no-credential"
      return 0
    fi
    sleep 0.05
    waited=$((waited + 1))
  done
  log "harness: the stand-in service never wrote $SERVICE_ROOT/ready; see $1/service.log"
  return 1
}

stop_service() {
  if [ -n "$SERVICE_PID" ] && kill -0 "$SERVICE_PID" 2>/dev/null; then
    kill -9 "$SERVICE_PID" 2>/dev/null
    wait "$SERVICE_PID" 2>/dev/null
  fi
  SERVICE_PID=""
}

# Posts the service accepted, per agent, read from the service's own log.
service_posts() { # run_dir [agent]
  python3 - "$1/service/calls.jsonl" "${2:-}" <<'PY'
import json, sys
path, agent = sys.argv[1], sys.argv[2]
posts = []
try:
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            entry = json.loads(line)
            if entry.get("method") == "POST" and (not agent or entry["agent"] == agent):
                posts.append(entry)
except OSError:
    pass
print(len(posts))
PY
}

# -- the durable-outside variant (D3), published as its own binary ------------------------
#
# `dotnet run` puts a host in front of the application, so a PID the shell starts would be the
# host and `kill -9` would land on the wrapper. Publishing and starting the binary is the
# difference between injecting a fault and illustrating one (Band G's rule).

build_durable_outside() {
  if [ -x "$D3_OUT/orchestrator" ]; then
    return 0
  fi
  log "harness: publishing the D3 orchestrator (once)"
  mkdir -p "$D3_OUT"
  "$DOTNET" publish "$BAND_DIR/../band-d3-outside-orchestrator/orchestrator.cs" \
    -c Release -o "$D3_OUT" > "$SCRATCH_ROOT/d3-publish.log" 2>&1 || {
      log "harness: the D3 publish failed; see ${SCRATCH_ROOT#"$REPO_ROOT"/}/d3-publish.log"
      return 1
    }
}

# -- the durable-inside variant (D2), built locally as its own image ----------------------
#
# The image is D2's own Dockerfile with D2's own build context, assembled exactly as its
# infra/deploy.sh assembles it - `agent/` plus `lib/py` as `durable-package/`. Building it
# from the same files is what makes "the container restarted" a statement about D2's code.

build_durable_inside() {
  if docker image inspect "$D2_IMAGE" >/dev/null 2>&1; then
    record_docker_identity >/dev/null
    return 0
  fi
  log "harness: building the D2 image $D2_IMAGE (once, from agent/ + lib/py)"
  local ctx="$SCRATCH_ROOT/d2-context"
  rm -rf "$ctx"
  mkdir -p "$ctx/durable-package"
  cp "$D2_SOURCE/agent/main.py" "$D2_SOURCE/agent/durable_state.py" \
     "$D2_SOURCE/agent/d2_tool.py" "$D2_SOURCE/agent/requirements.txt" \
     "$D2_SOURCE/agent/Dockerfile" "$ctx/"
  cp "$REPO_ROOT/lib/py/durable.py" "$REPO_ROOT/lib/py/pyproject.toml" "$ctx/durable-package/"
  docker build --platform linux/amd64 -t "$D2_IMAGE" "$ctx" > "$SCRATCH_ROOT/d2-build.log" 2>&1 || {
    log "harness: the D2 image build failed; see ${SCRATCH_ROOT#"$REPO_ROOT"/}/d2-build.log"
    return 1
  }
  record_docker_identity >/dev/null
}

# The container a fault will use, required and identified before it writes any result.
# Calling it twice is cheap and idempotent, which is why the durable-inside branches can
# call it without tracking whether the image was already there.
# shellcheck source=/dev/null
require_d2() {
  require_docker || return 1
  build_durable_inside || return 1
  record_docker_identity >/dev/null
}

# The identity a result records when it used a container: the daemon's own versions, the
# image the fault ran in, and that image's digest. Written once per harness process, beside
# the build log, so every fault on a run records the same container it actually used.
DOCKER_IDENTITY_FILE=""
record_docker_identity() {
  DOCKER_IDENTITY_FILE="$SCRATCH_ROOT/docker-identity.txt"
  if [ -s "$DOCKER_IDENTITY_FILE" ]; then
    cat "$DOCKER_IDENTITY_FILE"
    return 0
  fi
  mkdir -p "$SCRATCH_ROOT"
  {
    local versions digest
    versions="$(docker version --format 'client {{.Client.Version}} / server {{.Server.Version}}' 2>/dev/null)"
    digest="$(docker image inspect "$D2_IMAGE" --format '{{.Id}}' 2>/dev/null)"
    printf 'client and server: %s; image %s (%s)\n' \
      "${versions:-versions unavailable}" "$D2_IMAGE" "sha256:${digest#sha256:}"
  } > "$DOCKER_IDENTITY_FILE"
  cat "$DOCKER_IDENTITY_FILE"
}

# Run one command in a fresh D2 container over this fault's session volume.
#
# `--rm` plus no volume means the container's filesystem is gone when it exits; the platform's
# per-session volume is modelled by a **named Docker volume** mounted at the one path D2
# resolves its store under ($HOME=/home/session).
#
# It has to be a Docker volume and not a host directory, and the reason is the fault:
# `flock` over a bind mount is local to each container, so two containers over one host
# directory both "hold" the lock and D2's single-writer refusal never fires. A named volume
# shares one filesystem between containers, so the lock — and therefore the refusal — is the
# layer's own. The volume is created with the run and removed by the cleanup trap.
d2_volume() { # run_dir
  printf '%s\n' "d4-$(printf '%s' "$1" | shasum -a 256 | cut -c1-16)"
}

d2_run() { # session_volume [command...]
  local volume="$1"; shift
  docker volume create "$volume" >/dev/null
  docker run --rm -v "$volume:/home/session" -e HOME=/home/session -w /app \
    "$D2_IMAGE" "$@"
}

# Run one command over this fault's volume with the volume mounted read-only.
#
# This is the store-outage injection: the store is left exactly where it is and the
# *filesystem* refuses the write, which is an unavailability the same store survives. It is
# deliberately not `rm -rf` of the store — deleting the state and then reporting that the
# state was protected is the mistake this shape exists to make impossible.
d2_run_readonly() { # session_volume [command...]
  local volume="$1"; shift
  docker volume create "$volume" >/dev/null
  docker run --rm -v "$volume:/home/session:ro" -e HOME=/home/session -w /app \
    "$D2_IMAGE" "$@"
}

d2_run_bg() { # session_volume logfile name command...
  local volume="$1" logfile="$2" name="$3"; shift 3
  docker volume create "$volume" >/dev/null
  docker run --rm --name "$name" -v "$volume:/home/session" -e HOME=/home/session -w /app \
    "$D2_IMAGE" "$@" > "$logfile" 2>&1 &
  printf '%s\n' $!
}

# A container started detached, so it can be stopped (the container-restart fault).
d2_container_start() { # session_volume name command...
  local volume="$1" name="$2"; shift 2
  docker volume create "$volume" >/dev/null
  docker run -d --name "$name" -v "$volume:/home/session" -e HOME=/home/session -w /app \
    "$D2_IMAGE" "$@" >/dev/null 2>&1
}

# Files the container wrote, read from the host through a throwaway container. Nothing here
# enters a running container: the reading is a separate process over the same volume.
d2_cat() { # session_volume path
  local volume="$1" path="$2"
  docker run --rm -v "$volume:/home/session" "$D2_IMAGE" cat "$path" 2>/dev/null
}

d2_ls() { # session_volume dir
  local volume="$1" dir="$2"
  docker run --rm -v "$volume:/home/session" "$D2_IMAGE" sh -c "ls -1 '$dir' 2>/dev/null" 2>/dev/null
}

d2_touch() { # session_volume path
  local volume="$1" path="$2"
  docker run --rm -v "$volume:/home/session" "$D2_IMAGE" sh -c ": > '$path'" 2>/dev/null
}

d2_container_exec() { # name command...
  local name="$1"; shift
  docker exec -e HOME=/home/session -w /app "$name" "$@"
}

# -- results ----------------------------------------------------------------------------

write_result() { # template_file out_file fault variant
  local template="$1" out="$2" fault="$3" variant="$4"
  if ! harness_python >/dev/null; then
    python3 "$HARNESS_DIR/lib/result.py" --out "$out" --fault "$fault" --variant "$variant" --template "$template"
    return $?
  fi
  python3 "$HARNESS_DIR/lib/result.py" --out "$out" --fault "$fault" --variant "$variant" --template "$template"
}

# Where this invocation's results go, and a note when that is not the checked-in tree.
result_path() { # variant fault
  local name="${2}"
  [ "${INJECT:-1}" = "1" ] || name="$(control_name "$2")"
  printf '%s/%s/%s.json\n' "$RESULTS_DIR" "$1" "$name"
}

# -- lifecycle ----------------------------------------------------------------------------

CLEANUP_PATHS=()
CLEANUP_CONTAINERS=()
CLEANUP_VOLUMES=()

track_path() { CLEANUP_PATHS+=("$1"); }
track_container() { CLEANUP_CONTAINERS+=("$1"); }
track_volume() { CLEANUP_VOLUMES+=("$1"); }

install_cleanup() {
  # shellcheck disable=SC2317
  cleanup() {
    local status=$?
    stop_service
    local container
    for container in "${CLEANUP_CONTAINERS[@]:-}"; do
      [ -n "$container" ] || continue
      docker rm -f "$container" >/dev/null 2>&1
    done
    local volume
    for volume in "${CLEANUP_VOLUMES[@]:-}"; do
      [ -n "$volume" ] || continue
      docker volume rm -f "$volume" >/dev/null 2>&1
    done
    if [ "${KEEP:-0}" != "1" ]; then
      local path
      for path in "${CLEANUP_PATHS[@]:-}"; do
        [ -n "$path" ] || continue
        chmod -R u+w "$path" 2>/dev/null
        rm -rf "$path"
      done
    else
      local path
      for path in "${CLEANUP_PATHS[@]:-}"; do
        [ -n "$path" ] || continue
        log "kept: $path"
      done
      for volume in "${CLEANUP_VOLUMES[@]:-}"; do
        [ -n "$volume" ] && log "kept volume: $volume"
      done
    fi
    exit "$status"
  }
  trap cleanup EXIT INT TERM
}
