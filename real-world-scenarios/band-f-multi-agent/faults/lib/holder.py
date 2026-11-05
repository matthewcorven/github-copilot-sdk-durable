"""Reads the post-recovery holder out of each variant's own state, never off a log line.

The band's assertion is "which agent held control after recovery?". That answer has to come from
the place the variant says control lives:

  durable   the `checkpoint.holder` of the `band-f.delegation` task record, read from the JSONL
            store's last task record. The checkpoint is the only record of where the run is, so
            this is the variant's own authoritative answer.
  baseline  the framework's own checkpoint. `FileSystemJsonCheckpointStore` writes one JSON
            document per superstep; the agent whose handoff state is queued at the newest
            checkpoint is the agent that held control when the process stopped. The port's
            `control-transfer.jsonl` is *not* consulted: the port's own README calls it derived.

Both readers return the same shape, so a fault script compares like with like:

    {"holder": str|null, "source": "...", "detail": {...}}
"""

import glob
import json
import os
import sys

AGENTS = ("triage_agent", "refund_agent", "order_agent", "return_agent")


# ── durable ────────────────────────────────────────────────────────────────


def durable_task(store_path):
    """The last task record in the JSONL store, or None."""
    last = None
    try:
        handle = open(store_path, encoding="utf-8")
    except OSError:
        return None
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") == "task":
                last = record.get("payload") or {}
    return last


def durable(store_path):
    task = durable_task(store_path)
    if task is None:
        return {"holder": None, "source": "durable task record", "detail": {"store": store_path}}
    checkpoint = task.get("checkpoint") or {}
    outcome = task.get("outcome") or {}
    result = (outcome.get("result") or {}) if isinstance(outcome, dict) else {}
    return {
        "holder": checkpoint.get("holder"),
        "source": "durable task checkpoint (checkpoint.holder)",
        "detail": {
            "phase": checkpoint.get("phase"),
            "turn": checkpoint.get("turn"),
            "attempts": checkpoint.get("attempts"),
            "delegationEntry": checkpoint.get("delegationEntry"),
            "status": task.get("status"),
            "restarts": task.get("restarts"),
            "reportedHolders": result.get("holders"),
            "reason": result.get("reason"),
        },
    }


def durable_wire(store_path):
    """Every control record the store holds, oldest first, with the entry it was committed under."""
    records = []
    try:
        handle = open(store_path, encoding="utf-8")
    except OSError:
        return records
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") != "document-version":
                continue
            payload = record.get("payload") or {}
            if payload.get("kind") != "band-f.wire":
                continue
            value = payload.get("value") or {}
            value["entry"] = payload.get("entryId")
            value["seq"] = payload.get("sequence")
            records.append(value)
    records.sort(key=lambda value: value.get("seq") or 0)
    return records


# ── baseline ───────────────────────────────────────────────────────────────


def _baseline_checkpoints(directory):
    """The framework's checkpoints, oldest first, read from index.jsonl and the documents it names."""
    index = os.path.join(directory, "index.jsonl")
    order = []
    try:
        handle = open(index, encoding="utf-8")
    except OSError:
        handle = None
    if handle is not None:
        with handle:
            for line in handle:
                line = line.strip()
                if not line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                info = entry.get("checkpointInfo") or {}
                if info.get("checkpointId") and entry.get("fileName"):
                    order.append((info["checkpointId"], os.path.join(directory, entry["fileName"])))

    if not order:
        # No index (the store writes it lazily): fall back to the documents themselves, ordered by
        # the step number they carry. The index is preferred because it records write order.
        for path in glob.glob(os.path.join(directory, "*%2Ejson")):
            order.append((os.path.basename(path), path))

    checkpoints = []
    for checkpoint_id, path in order:
        try:
            document = json.load(open(path, encoding="utf-8"))
        except (OSError, ValueError):
            continue
        checkpoints.append((checkpoint_id, document))
    checkpoints.sort(key=lambda pair: pair[1].get("stepNumber", 0))
    return checkpoints


def _executor_agent(executor_id):
    """The stable agent id behind a framework executor id (`<agent>_<agent>`), or None."""
    for agent in AGENTS:
        if executor_id == agent + "_" + agent:
            return agent
    return None


def _queued(document):
    """The queued handoff states: who the workflow is about to run, and who last held control.

    A superstep checkpoints its `queuedMessages` as `HandoffState` values. A message queued at an
    agent's executor means that agent is about to run — it holds control. A message queued at
    `HandoffEnd` means the last agent finished and its `previousAgentId` is the one that just held
    control. The two readings are both returned so a caller can see when they disagree.
    """
    queued = (document.get("runnerData") or {}).get("queuedMessages") or {}
    next_agents = []
    finished_agents = []
    for executor_id, messages in queued.items():
        for message in messages:
            value = ((message or {}).get("message") or {}).get("value")
            if not isinstance(value, dict):
                continue
            agent = _executor_agent(executor_id)
            if agent is not None:
                next_agents.append(agent)
            previous = value.get("previousAgentId")
            if previous in AGENTS and previous not in finished_agents:
                finished_agents.append(previous)
    return next_agents, finished_agents


def _last_spoke(document):
    """The author of the last assistant message in the checkpointed conversation, or None."""
    for _key, value in (document.get("stateData") or {}).items():
        if not str(_key).endswith("SharedState"):
            continue
        history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
        for message in reversed(history):
            if message.get("role") == "assistant" and message.get("authorName") in AGENTS:
                return message["authorName"]
    return None


def baseline(directory):
    """Who holds control in the newest framework checkpoint, read from the checkpoint itself."""
    checkpoints = _baseline_checkpoints(directory)
    if not checkpoints:
        return {
            "holder": None,
            "source": "framework checkpoint (none found)",
            "detail": {"directory": directory},
        }

    checkpoint_id, document = checkpoints[-1]
    next_agents, finished_agents = _queued(document)
    last_spoke = _last_spoke(document)

    if next_agents:
        holder = next_agents[-1]
        basis = "a handoff is queued at " + holder + "'s executor, so " + holder + " is about to run"
    elif finished_agents:
        holder = finished_agents[-1]
        basis = "the last handoff is queued at HandoffEnd, so " + holder + " is the agent that held control"
    elif last_spoke is not None:
        holder = last_spoke
        basis = "nothing is queued and " + last_spoke + " was the last agent to speak"
    else:
        holder = None
        basis = "nothing is queued and no agent has spoken"

    return {
        "holder": holder,
        "source": "framework checkpoint (queued handoff target, else the last agent to speak)",
        "detail": {
            "checkpoint": checkpoint_id,
            "stepNumber": document.get("stepNumber"),
            "queuedAt": next_agents,
            "previousAgent": finished_agents,
            "lastSpoke": last_spoke,
            "basis": basis,
            "checkpoints": len(checkpoints),
            "disagreement": bool(
                (next_agents and last_spoke and next_agents[-1] != last_spoke)
                or (not next_agents and finished_agents and last_spoke and finished_agents[-1] != last_spoke)
            ),
        },
    }


def baseline_log(directory):
    """The port's own control-transfer log, read only to cross-check, never to attribute control."""
    path = os.path.join(directory, "control-transfer.jsonl")
    records = []
    try:
        handle = open(path, encoding="utf-8")
    except OSError:
        return records
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except ValueError:
                continue
    return records


def snapshot(variant, path):
    """Everything an observer needs to say "the agent that took control after the fault".

    This is the one call every fault makes immediately before injecting and immediately after
    recovery, so the two readings are comparable without each script re-deriving them:

        holder         the controlling agent the state itself names
        fingerprint    a value that changes on any commit, so "nothing moved" is checkable
        counts         the specialist tool results, as a multiset
        authors        baseline only: the agents that spoke in the checkpointed conversation, in order
        checkpointIndex baseline only: how many checkpoints the store held
        maxSeq         durable only: the highest committed sequence number
    """
    document = read(variant, path)
    state = {
        "holder": document.get("holder"),
        "source": document.get("source"),
        "detail": document.get("detail"),
        "fingerprint": None,
        "counts": {},
        "authors": [],
        "checkpointIndex": 0,
        "maxSeq": 0,
    }
    if variant == "durable":
        records = durable_wire(path)
        counts = {}
        for record in records:
            if record.get("event") == "produced" and record.get("detail"):
                counts[record["detail"]] = counts.get(record["detail"], 0) + 1
        tool_counts = {}
        try:
            for line in open(path, encoding="utf-8"):
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except ValueError:
                    continue
                if record.get("type") != "document-version":
                    continue
                payload = record.get("payload") or {}
                if payload.get("kind") != "band-f.specialist-output":
                    continue
                result = (payload.get("value") or {}).get("toolResult")
                if result:
                    tool_counts[str(result)] = tool_counts.get(str(result), 0) + 1
        except OSError:
            pass
        state["counts"] = tool_counts
        state["maxSeq"] = durable_max_seq(path)
        state["fingerprint"] = "seq:%d/wire:%d/tasks:%s" % (
            state["maxSeq"],
            len(records),
            (document.get("detail") or {}).get("status"),
        )
    else:
        checkpoints = _baseline_checkpoints(path)
        authors = _authors(checkpoints[-1][1]) if checkpoints else []
        # The framework's tool results, as a multiset, from the conversation it checkpoints.
        counts = {}
        if checkpoints:
            for key, value in (checkpoints[-1][1].get("stateData") or {}).items():
                if not str(key).endswith("SharedState"):
                    continue
                history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
                for message in history:
                    for content in message.get("contents") or []:
                        if content.get("$type") == "functionResult" and content.get("result") is not None:
                            text = str(content["result"])
                            if text == "Transferred.":
                                continue
                            counts[text] = counts.get(text, 0) + 1
                break
        state["counts"] = counts
        state["authors"] = authors
        state["checkpointIndex"] = len(checkpoints)
        state["fingerprint"] = "checkpoints:%d/authors:%d/last:%s" % (
            len(checkpoints),
            len(authors),
            (document.get("detail") or {}).get("checkpoint"),
        )
    return state


def wire(store_path):
    """Every control record the durable store holds, oldest first, with the entry it was committed
    under. Read by `--wire`, so a caller never parses the store's envelope itself."""
    return durable_wire(store_path)


# ── who acted first after recovery ─────────────────────────────────────────
#
# "Who holds control after recovery" is a state claim; "which agent acted first in the recovered
# process" is the evidence for it. Both are read from committed state: the durable variant's control
# records carry the entry each was committed under, and the baseline's framework checkpoints carry
# the conversation the workflow was reconstructed from.


def durable_max_seq(store_path):
    """The highest sequence number any committed record in the store carries."""
    highest = 0
    try:
        handle = open(store_path, encoding="utf-8")
    except OSError:
        return 0
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            highest = max(highest, record.get("seq") or 0)
    return highest


COORDINATOR_DOCUMENTS = {
    # A routing decision and the user turn it is for are commits the coordinator makes. Counting
    # them is what makes "the coordinator re-entered routing" visible as an action, instead of
    # only the delegation that follows it.
    "band-f.routing": ("routing decision", AGENTS[0]),
    "band-f.user-turn": ("user turn", AGENTS[0]),
}


def durable_first_after(store_path, after_seq):
    """The first thing the recovered process committed, and the agent that committed it.

    Read from document versions in sequence order, not from a log: a routing decision is as much an
    action as a specialist's output, and leaving it out would report the coordinator's re-entry as
    though the specialist had acted first.
    """
    candidates = []
    try:
        handle = open(store_path, encoding="utf-8")
    except OSError:
        return None
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if record.get("type") != "document-version":
                continue
            payload = record.get("payload") or {}
            sequence = payload.get("sequence") or record.get("seq") or 0
            if sequence <= after_seq:
                continue
            kind = payload.get("kind") or ""
            value = payload.get("value") or {}
            if kind in COORDINATOR_DOCUMENTS:
                event, agent = COORDINATOR_DOCUMENTS[kind]
                candidates.append((sequence, {
                    "agent": agent,
                    "event": event,
                    "turn": value.get("turn"),
                    "entry": payload.get("entryId"),
                    "seq": sequence,
                }))
            elif kind == "band-f.wire" and value.get("event") in (
                "produced", "reused-output", "delegated", "delegated-from-memo", "settled"
            ):
                candidates.append((sequence, {
                    "agent": value.get("agent"),
                    "event": value.get("event"),
                    "turn": value.get("turn"),
                    "entry": payload.get("entryId"),
                    "seq": sequence,
                }))
    if not candidates:
        return None
    candidates.sort(key=lambda pair: pair[0])
    return candidates[0][1]


def _authors(document):
    """Every agent that spoke in the checkpointed conversation, in order."""
    for key, value in (document.get("stateData") or {}).items():
        if not str(key).endswith("SharedState"):
            continue
        history = (((value or {}).get("value") or {}).get("conversation") or {}).get("history") or []
        return [m.get("authorName") for m in history if m.get("role") == "assistant" and m.get("authorName") in AGENTS]
    return []


def baseline_first_after(directory, after_index, authors_before):
    """The first agent to speak in a checkpoint the recovered process wrote.

    `authors_before` is the author list at the stop, so the first agent *beyond* it is the one the
    recovered process put in control — the coordinator's own re-entry included, which is what makes
    a resume at the coordinator distinguishable from a resume at a specialist.
    """
    for index, (checkpoint_id, document) in enumerate(_baseline_checkpoints(directory)):
        if index < after_index:
            continue
        authors = _authors(document)
        if len(authors) > len(authors_before):
            return {
                "agent": authors[len(authors_before)],
                "checkpoint": checkpoint_id,
                "stepNumber": document.get("stepNumber"),
                "index": index,
            }
    return None


def baseline_from_index(index_path, directory):
    """The holder in the checkpoint the *named* index file lists last.

    `baseline(directory)` reads the index at its usual path. This reads a moved or copied index, which
    is what survives a store outage, and applies the same rule to the document it names.
    """
    last = None
    try:
        with open(index_path, encoding="utf-8") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    last = line
    except OSError:
        return {"holder": None, "source": "framework checkpoint (no index)", "detail": {"index": index_path}}
    if not last:
        return {"holder": None, "source": "framework checkpoint (empty index)", "detail": {"index": index_path}}
    try:
        entry = json.loads(last)
    except ValueError:
        return {"holder": None, "source": "framework checkpoint (unreadable index entry)",
                "detail": {"index": index_path}}
    stored = entry.get("fileName") or ""
    # The framework percent-encodes the dot in the file name; on disk it is percent-encoded too, so
    # the stored name is tried first and the decoded one second.
    candidates = [stored, stored.replace("%2E", ".")]
    checkpoint_id = (entry.get("checkpointInfo") or {}).get("checkpointId")
    document, name = None, stored
    for candidate in candidates:
        try:
            document = json.load(open(os.path.join(directory, candidate), encoding="utf-8"))
            name = candidate
            break
        except (OSError, ValueError):
            continue
    if document is None:
        return {"holder": None, "source": "framework checkpoint (document missing)",
                "detail": {"index": index_path, "checkpoint": checkpoint_id, "fileName": name}}
    next_agents, finished = _queued(document)
    last_spoke = _last_spoke(document)
    holder = (next_agents[-1] if next_agents else (finished[-1] if finished else last_spoke))
    return {
        "holder": holder,
        "source": "framework checkpoint named by the index's last entry",
        "detail": {
            "checkpoint": checkpoint_id,
            "stepNumber": document.get("stepNumber"),
            "queuedAt": next_agents,
            "previousAgent": finished,
            "lastSpoke": last_spoke,
        },
    }


def newest_document(directory):
    """The newest checkpoint document, by the last entry of the index."""
    checkpoints = _baseline_checkpoints(directory)
    return checkpoints[-1][1] if checkpoints else {}


def read(variant, path):
    """The holder for a variant: a store file for the durable variant, a directory for the baseline."""
    return durable(path) if variant == "durable" else baseline(path)


def main(argv):
    if len(argv) >= 4 and argv[1] == "snapshot" and argv[2] in ("durable", "baseline"):
        print(json.dumps(snapshot(argv[2], argv[3]), indent=2))
        return 0
    if len(argv) >= 3 and argv[1] == "--wire":
        for record in durable_wire(argv[2]):
            print(json.dumps(record, sort_keys=True))
        return 0
    if len(argv) >= 3 and argv[1] == "--max-seq":
        print(durable_max_seq(argv[2]))
        return 0
    if len(argv) >= 4 and argv[1] == "--first-after-durable":
        print(json.dumps(durable_first_after(argv[2], int(argv[3] or 0)), indent=2))
        return 0
    if len(argv) >= 4 and argv[1] == "--index-holder":
        print(json.dumps(baseline_from_index(argv[2], argv[3]), indent=2))
        return 0
    if len(argv) >= 3 and argv[1] == "--authors":
        print(json.dumps(_authors(newest_document(argv[2]))))
        return 0
    if len(argv) >= 3 and argv[1] == "--checkpoint-count":
        print(len(_baseline_checkpoints(argv[2])))
        return 0
    if len(argv) >= 5 and argv[1] == "--first-after-baseline":
        after_index = int(argv[3] or 0)
        authors_before = json.loads(argv[4]) if argv[4] else []
        print(json.dumps(baseline_first_after(argv[2], after_index, authors_before), indent=2))
        return 0
    if len(argv) < 3 or argv[1] not in ("durable", "baseline"):
        print("usage: holder.py <durable|baseline> <store.jsonl|checkpoint-dir>"
              " | holder.py --wire <store.jsonl>", file=sys.stderr)
        return 2
    print(json.dumps(read(argv[1], argv[2]), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
