"""Band D (D2) — a hosted prior-authorization agent whose working state is Durable.

The transcript is Foundry's. This agent does not mirror it: it reads the request it is
given, keeps its *own* working state in a Durable store on the session volume, and offers
the model tools that move that state forward. The one step that cannot be a tool call —
a person's approval — is a durable task parked on an external event with a durable
deadline, so a container that stops and starts again resumes at the approval rather than
before it.

Boot (``main``):

1. resolve the store (``Workspace.resolve``) and record the container's filesystem facts,
   so ``/home/session`` vs ``/tmp`` is measured rather than assumed;
2. install the two extensions: the tools, and the approval task definition;
3. build the Foundry agent with those tools;
4. serve it through ``ResponsesHostServer``.

No tool call is blocked on the store's lock while another commit is in flight: each tool
is synchronous, commits, and returns. The wait for a person is a ``WAITING`` checkpoint,
not an open call — that is the whole reason the approval survives a restart.
"""

from __future__ import annotations

import json
import os
import sys

from agent_framework import tool
from agent_framework import Agent
from agent_framework.foundry import FoundryChatClient
from agent_framework_foundry_hosting import ResponsesHostServer
from azure.identity import (
    AzureDeveloperCliCredential,
    ChainedTokenCredential,
    DefaultAzureCredential,
    ManagedIdentityCredential,
)
from dotenv import load_dotenv

from durable_state import Workspace

load_dotenv(override=True)  # override=True is required for Foundry-deployed env vars

_workspace: Workspace | None = None


def _store() -> Workspace:
    """The process's one store handle.

    One handle per process, opened lazily, because a JSONL store holds the file's exclusive
    lock for its lifetime: two handles in one container would mean the second open fails,
    which is the store's own guarantee and not something this agent should paper over.
    """
    global _workspace
    if _workspace is None:
        _workspace = Workspace.open()
        print(
            "d2: store opened at %s (%d documents, %d tasks in the file)"
            % (
                _workspace.store_path,
                len(_workspace.store.documents),
                len(_workspace.store.tasks),
            ),
            flush=True,
        )
    return _workspace


def _as_json(payload: object) -> str:
    return json.dumps(payload, indent=1, sort_keys=True)


@tool(name="working_state", description="Reads the durable working state for a prior-authorization request.")
def working_state(request_id: str) -> str:
    """The durable working state: which step the request is at, and the approval if any."""
    workspace = _store()
    state = workspace.work_state(request_id)
    approval = workspace.approval(request_id)
    task_id = workspace.approval_task_id(request_id)
    task = workspace.store.task(task_id) if task_id else None
    return _as_json(
        {
            "requestId": request_id,
            "phase": None if state is None else state.phase,
            "assessment": None if state is None else state.assessment,
            "decision": None if state is None else state.decision,
            "decidedBy": None if state is None else state.decidedBy,
            "decidedAt": None if state is None else state.decidedAt,
            "resumedCount": None if state is None else state.resumedCount,
            "approval": None
            if approval is None
            else {
                "state": approval.state,
                "deadline": approval.deadline,
                "decision": approval.decision,
                "reviewer": approval.reviewer,
            },
            "approvalTask": None
            if task is None
            else {
                "id": task.id,
                "status": task.status.name if hasattr(task.status, "name") else str(task.status),
                "waitingOn": list(task.waiting_on),
                "restarts": task.restarts,
                "checkpoint": task.checkpoint,
            },
            "storePath": workspace.store_path,
        }
    )


@tool(
    name="record_assessment",
    description=(
        "Commits the coverage assessment for a request and opens the human approval gate. "
        "Idempotent: a request that already has an approval keeps it, and its deadline."
    ),
)
def record_assessment(request_id: str, assessment: str) -> str:
    """Commits the assessment, then parks the approval task on its external event."""
    workspace = _store()
    state = workspace.work_state(request_id)
    if state is not None and state.phase in ("awaiting-approval", "decided"):
        return _as_json(
            {
                "requestId": request_id,
                "phase": state.phase,
                "created": False,
                "note": "an approval gate already exists for this request; its deadline is unchanged",
            }
        )

    workspace.note_assessment(request_id, assessment)
    task_id = workspace.start_approval(request_id)
    approval = workspace.approval(request_id)
    return _as_json(
        {
            "requestId": request_id,
            "phase": "awaiting-approval",
            "created": True,
            "approvalTaskId": task_id,
            "awaitingEvent": "external:approval/" + request_id,
            "deadline": None if approval is None else approval.deadline,
            "note": "the approval is a durable task parked on an external event; nothing is held open",
        }
    )


@tool(
    name="resume_work",
    description=(
        "Re-enters the parked approval task for a request and reports the step it resumed at. "
        "A restart that has no new answer re-parks the task; an answered one completes it."
    ),
)
def resume_work(request_id: str) -> str:
    """The restart path: same store, same task, at the checkpoint the park wrote."""
    workspace = _store()
    resumed = workspace.note_resume(request_id)
    settled = workspace.resume_approval(request_id)
    state = workspace.work_state(request_id)
    approval = workspace.approval(request_id)
    return _as_json(
        {
            "requestId": request_id,
            "resumedCount": resumed,
            "settled": settled,
            "phase": None if state is None else state.phase,
            "decision": None if state is None else state.decision,
            "approval": None
            if approval is None
            else {"state": approval.state, "decision": approval.decision, "reviewer": approval.reviewer},
        }
    )


@tool(
    name="record_approval",
    description=(
        "Records a human decision: delivers it as the external event the approval task parked on, "
        "then resumes the task so it completes at the approval step, not before it."
    ),
)
def record_approval(request_id: str, decision: str, reviewer: str, reason: str = "") -> str:
    """Delivers the event, then resumes — in that order, which is the crash-safety order."""
    workspace = _store()
    if decision not in ("approved", "denied", "pend_for_review"):
        return _as_json(
            {
                "requestId": request_id,
                "error": "decision must be approved, denied or pend_for_review",
                "recorded": False,
            }
        )

    woken = workspace.deliver(
        request_id, {"decision": decision, "reviewer": reviewer, "reason": reason}
    )
    settled = workspace.resume_approval(request_id)
    state = workspace.work_state(request_id)
    return _as_json(
        {
            "requestId": request_id,
            "wokenTasks": woken,
            "settled": settled,
            "finalPhase": None if state is None else state.phase,
            "note": "an event with no waiter wakes nothing: a repeated decision is a no-op"
            if not woken
            else "the task resumed at the step after the approval",
        }
    )


@tool(
    name="store_report",
    description=(
        "Reports the durable store verbatim: its path and digest, every document with its "
        "version count and latest value, and every task with its status, checkpoint and memos. "
        "Use it when asked what the working state contains rather than describing it."
    ),
)
def store_report() -> str:
    """The store, measured. Returned as JSON so a caller can diff two captures."""
    import hashlib

    workspace = _store()
    # A store with no commit yet has opened the path and written no file, which is a state
    # worth reporting rather than an error: it is what a fresh container looks like.
    raw = ""
    if os.path.exists(workspace.store_path):
        with open(workspace.store_path, "rb") as handle:
            data = handle.read()
        digest = hashlib.sha256(data).hexdigest()
        size = len(data)
        # The file verbatim. It is the cross-language store format - the same JSONL every
        # port writes - so quoting it is what makes the evidence checkable rather than
        # summarised.
        raw = data.decode("utf-8", "replace")
    else:
        digest = "no-file"
        size = 0

    return _as_json(
        {
            "storePath": workspace.store_path,
            "sha256": digest,
            "bytes": size,
            "raw": raw,
            "documents": [
                {
                    "kind": document.kind,
                    "ownerId": document.owner_id,
                    "versions": len(document.versions),
                    "latest": document.latest,
                }
                for document in workspace.store.documents
            ],
            "tasks": [
                {
                    "id": task.id,
                    "name": task.name,
                    "status": task.status.name if hasattr(task.status, "name") else str(task.status),
                    "waitingOn": list(task.waiting_on),
                    "restarts": task.restarts,
                    "checkpoint": task.checkpoint,
                    "memos": task.memos,
                    "outcome": None
                    if task.outcome is None
                    else {"status": task.outcome.status, "error": task.outcome.error},
                }
                for task in workspace.store.tasks
            ],
            "container": Workspace.facts(),
            "files": workspace.inventory(),
        }
    )


def main() -> None:
    workspace = _store()
    print("d2: container facts " + _as_json(Workspace.facts()), flush=True)

    client_id = os.environ.get("AZURE_CLIENT_ID")
    if client_id:
        credential = ChainedTokenCredential(
            ManagedIdentityCredential(client_id=client_id),
            AzureDeveloperCliCredential(
                tenant_id=os.environ.get("AZURE_TENANT_ID"), process_timeout=60
            ),
        )
    else:
        credential = DefaultAzureCredential()

    project_endpoint = os.environ.get("FOUNDRY_PROJECT_ENDPOINT") or os.environ[
        "AZURE_AI_PROJECT_ENDPOINT"
    ]
    model = os.environ.get("MODEL_DEPLOYMENT_NAME") or os.environ["AZURE_OPENAI_DEPLOYMENT_NAME"]

    agent = Agent(
        client=FoundryChatClient(
            project_endpoint=project_endpoint,
            model=model,
            credential=credential,
            allow_preview=True,
        ),
        name="durable-inside-agent",
        id="durable-inside-agent",  # must match the registered name for Foundry trace correlation
        instructions=(
            "You are a prior-authorization agent whose working state lives in a durable store. "
            "Use working_state to read it, record_assessment to commit an assessment and open the "
            "human approval gate, resume_work when a session restarts so the parked approval is "
            "re-entered, and record_approval when a person tells you their decision. Never claim a "
            "decision the store does not hold: read working_state and say what it returns. When "
            "asked what the working state contains, call store_report. The "
            "conversation transcript belongs to the platform; do not restate it as state."
        ),
        tools=[working_state, record_assessment, resume_work, record_approval, store_report],
        default_options={"store": False},
    )

    print("d2: serving, store at %s" % workspace.store_path, flush=True)
    ResponsesHostServer(agent).run()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:  # noqa: BLE001 - the host must see a loud, named start failure
        print("d2: start failed: %s: %s" % (type(error).__name__, error), file=sys.stderr, flush=True)
        raise