"""Reads either Band G variant's durable state from disk, so an observation never depends on a live process.

Every fault script observes with this, from a process the fault did not interrupt. Three outcomes:

* a value — the field was found;
* exit 3 and no output — the field does not exist (no store, no task, no gate, no checkpoint);
* exit 2 — the invocation names a field this reader does not know, which is a harness bug.

The distinction between the first two is deliberate. A fault's claim is checked against a value, so a
claim whose subject is missing has to be able to say "absent" and let the caller fail; a reader that
prints an empty string for a missing file would turn every one of those into a silent pass. The fault
scripts treat exit 3 as a failed claim, and print the reason.

The baseline keeps the framework's own file store: one JSON file per checkpoint, an `index.jsonl`, and
the `gate.json` the port writes. The Durable variant keeps one JSONL store; its records are replayed here
the same way the library replays them, so the reader is a witness rather than a copy of the format's
documentation.
"""

import json
import os
import sys

# The store writes a task status as its enum ordinal. The names are the ones the variant prints, so a
# result can be read without a decoder ring.
STATUSES = {0: "pending", 1: "running", 2: "waiting", 3: "sleeping", 4: "terminal"}


def status_name(value):
    return STATUSES.get(value, str(value)) if isinstance(value, int) else str(value)


# ── the baseline: the framework's file checkpoint store ─────────────────────────────────────────
def baseline(dir_path, field):
    checkpoint_dir = os.path.join(dir_path, "checkpoints")
    gate_path = os.path.join(checkpoint_dir, "gate.json")

    def checkpoints():
        # One JSON file per checkpoint. `index.jsonl` and `gate.json` are not checkpoints, and a scratch
        # marker is not either; counting them would make an empty store look populated.
        # A checkpoint file is named `<session>_<checkpoint>%2Ejson`: the framework percent-encodes
        # the dot. Matching the encoded suffix is what keeps this a count of checkpoints rather than a
        # count of every file in the directory, since `gate.json`, `index.jsonl` and the scratch marker
        # all live beside them.
        return sorted(
            os.path.join(checkpoint_dir, name)
            for name in os.listdir(checkpoint_dir)
            if "%2Ejson" in name
        )

    def gate():
        return json.load(open(gate_path, encoding="utf-8"))

    if field == "checkpoint-count":
        if not os.path.isdir(checkpoint_dir):
            return None
        return str(len(checkpoints()))

    if field == "gate-checkpoint":
        if not os.path.isfile(gate_path):
            return None
        return gate()["CheckpointId"]

    if field == "gate-session":
        if not os.path.isfile(gate_path):
            return None
        return gate()["SessionId"]

    if field == "gate-request":
        if not os.path.isfile(gate_path):
            return None
        return gate()["RequestId"]

    if field == "latest-checkpoint":
        if not os.path.isfile(os.path.join(checkpoint_dir, "index.jsonl")):
            return None
        latest = None
        for line in open(os.path.join(checkpoint_dir, "index.jsonl"), encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            info = record.get("checkpointInfo") or {}
            latest = info.get("checkpointId") or record.get("checkpointId") or latest
        return latest

    if field == "store-files":
        if not os.path.isdir(checkpoint_dir):
            return None
        return str(len([p for p in os.listdir(checkpoint_dir) if os.path.isfile(os.path.join(checkpoint_dir, p))]))

    if field == "pending-requests":
        # Read out of the checkpoint file itself, the way the port's own `run` mode does: the pending
        # request is the thing a resume re-emits, so its presence is the gate.
        if not os.path.isfile(gate_path) or not os.path.isdir(checkpoint_dir):
            return None
        ident = gate()["CheckpointId"]
        matches = [p for p in checkpoints() if ident in os.path.basename(p)]
        if not matches:
            return None
        snapshot = json.load(open(matches[0], encoding="utf-8"))
        requests = snapshot.get("runnerData", {}).get("outstandingRequests", [])
        return ",".join(sorted(r.get("requestId", "") for r in requests))

    if field == "approved-in-store":
        # A post-approval checkpoint is one that is not the gate checkpoint and holds no outstanding
        # request. Its existence is the record that the workflow ran past the gate.
        gate_id = None
        if not os.path.isdir(checkpoint_dir):
            return None
        if os.path.isfile(gate_path):
            gate_id = gate()["CheckpointId"]
        for path in checkpoints():
            if gate_id and gate_id in os.path.basename(path):
                continue
            snapshot = json.load(open(path, encoding="utf-8"))
            if not snapshot.get("runnerData", {}).get("outstandingRequests"):
                return "yes"
        return "no"

    raise SystemExit(2)


# ── the Durable variant: one JSONL store, replayed ─────────────────────────────────────────────
def durable(dir_path, field):
    store = os.path.join(dir_path, "store.jsonl")
    if not os.path.isfile(store):
        return None

    tasks = {}
    documents = {}
    for line in open(store, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        record = json.loads(line)
        payload = record.get("payload") or {}
        if record.get("type") == "task":
            tasks[payload.get("id")] = payload
        elif record.get("type") == "document-version":
            documents[payload.get("kind")] = payload.get("value") or {}

    workflow = next((t for t in tasks.values() if t.get("name") == "band-g.brief-workflow"), None)
    gates = [t for t in tasks.values() if t.get("name") == "band-g.approval-gate"]
    ledger = documents.get("band-g.model-ledger") or {}
    decision = documents.get("band-g.approval") or {}

    if field == "task-status":
        return None if workflow is None else status_name(workflow.get("status"))

    if field == "task-id":
        return None if workflow is None else str(workflow.get("id"))

    if field == "phase":
        if workflow is None:
            return None
        return (workflow.get("checkpoint") or {}).get("phase")

    if field == "restarts":
        return None if workflow is None else str(workflow.get("restarts"))

    if field == "gate-deadline":
        if not gates:
            return None
        return (gates[-1].get("checkpoint") or {}).get("deadline")

    if field == "gate-phase":
        if not gates:
            return None
        return (gates[-1].get("checkpoint") or {}).get("phase")

    if field in ("gate-status", "gate-verdict"):
        if not gates:
            return None
        gate = gates[-1]
        if field == "gate-status":
            return status_name(gate.get("status"))
        outcome = gate.get("outcome") or {}
        result = outcome.get("result") or {}
        return result.get("verdict")

    if field == "verdict":
        if workflow is None:
            return None
        outcome = workflow.get("outcome") or {}
        return (outcome.get("result") or {}).get("verdict")

    if field == "outcome-status":
        if workflow is None:
            return None
        return (workflow.get("outcome") or {}).get("status")

    if field == "outcome-error":
        if workflow is None:
            return None
        return (workflow.get("outcome") or {}).get("error") or ""

    if field == "calls":
        return str(len(ledger.get("calls") or []))

    if field == "call-steps":
        return ",".join(call.get("step", "") for call in ledger.get("calls") or [])

    if field == "memo-hits":
        return str(ledger.get("memoHits", 0))

    if field == "decision":
        if not decision or not decision.get("recorder"):
            return None
        return "%s %s" % (decision.get("recorder"), "approved" if decision.get("approved") else "rejected")

    if field == "decision-approved":
        if not decision or not decision.get("recorder"):
            return None
        return "yes" if decision.get("approved") else "no"

    if field == "observed-children":
        # The abort handler records what it saw of its own children, at the moment it ran.
        if workflow is None:
            return None
        outcome = workflow.get("outcome") or {}
        observed = (outcome.get("result") or {}).get("observed")
        if not observed:
            return None
        return ",".join("%s=%s%s" % (o.get("taskId"), o.get("status"), "" if o.get("verdict") is None else ":" + o.get("verdict")) for o in observed)

    raise SystemExit(2)


def main(argv):
    if len(argv) != 3:
        sys.stderr.write("usage: store_read.py baseline|durable <dir> <field>\n")
        return 2
    variant, dir_path, field = argv
    value = (baseline if variant == "baseline" else durable)(dir_path, field)
    if value is None:
        return 3
    sys.stdout.write(str(value))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
