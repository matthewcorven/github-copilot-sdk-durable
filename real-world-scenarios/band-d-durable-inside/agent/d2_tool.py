"""The agent's two command-line entry points, both driven by the platform.

``d2-tool`` is registered as a Foundry *hosted tool*. The model calls it; the platform
runs it inside the same container, with the same ``$HOME``. It is what makes the working
state reachable from a conversation without the transcript carrying it:

    d2-tool state    --request PA-1        the working state, as JSON
    d2-tool assess   --request PA-1 --text "..."   commit the assessment, open the approval
    d2-tool approval --request PA-1        the approval document, as JSON
    d2-tool resume   --request PA-1        re-enter the parked approval task, report where it settled

``d2-probe`` is not a tool. §B/§C of ``evidence/`` need to observe the container from
inside it — which paths are writable, where the store is, what a session stop does to
each — so it is a separate script that prints measurements and changes nothing except a
marker file the survival evidence reads back.

Both resolve the store the same way (``Workspace.resolve``), so there is one answer to
"where is the working state" and it is printed by whichever runs first.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

from durable_state import (
    APPROVAL_EVENT_PREFIX,
    Approval,
    Workspace,
    WorkState,
)


def _print(payload: dict) -> None:
    print(json.dumps(payload, indent=1, sort_keys=True))


def state(args: argparse.Namespace) -> int:
    with _workspace() as workspace:
        record = _work_state(workspace, args.request)
        _print(
            {
                "requestId": args.request,
                "phase": record.phase,
                "assessment": record.assessment,
                "decision": record.decision,
                "decidedBy": record.decidedBy,
                "decidedAt": record.decidedAt,
                "resumedCount": record.resumedCount,
                "approvalTaskId": workspace.approval_task_id(args.request),
                "storePath": workspace.store_path,
                "task": _task_json(workspace, workspace.approval_task_id(args.request)),
            }
        )
    return 0


def assess(args: argparse.Namespace) -> int:
    with _workspace() as workspace:
        record = _work_state(workspace, args.request)
        if record.phase in ("awaiting-approval", "decided") and args.request:
            # One assessment per request. A second call is a duplicate delivery of the
            # same step, and re-opening an approval that already exists would discard the
            # deadline and the event the task is parked on.
            _print({"requestId": args.request, "phase": record.phase, "created": False})
            return 0

        workspace.note_assessment(args.request, args.text)
        task_id = workspace.start_approval(args.request)
        approval = workspace.approval(args.request) or Approval()

        _print(
            {
                "requestId": args.request,
                "phase": "awaiting-approval",
                "created": True,
                "approvalTaskId": task_id,
                "event": APPROVAL_EVENT_PREFIX + args.request,
                "deadline": approval.deadline,
                "deadlineIso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(approval.deadline)),
                "storePath": workspace.store_path,
            }
        )
    return 0


def resume(args: argparse.Namespace) -> int:
    """The restart path: the same store, the same task, re-entered at its checkpoint."""
    with _workspace() as workspace:
        resumed = workspace.note_resume(args.request)
        settled = workspace.resume_approval(args.request)
        record = workspace.work_state(args.request) or WorkState(requestId=args.request)
        _print(
            {
                "requestId": args.request,
                "resumedCount": resumed,
                "settled": settled,
                "phase": record.phase,
                "decision": record.decision,
                "decidedBy": record.decidedBy,
                "approval": _approval_json(workspace, args.request),
                "storePath": workspace.store_path,
            }
        )
    return 0


def approval(args: argparse.Namespace) -> int:
    with _workspace() as workspace:
        _print(_approval_json(workspace, args.request))
    return 0


def probe(args: argparse.Namespace) -> int:
    """Measures the container and optionally writes a survival marker. Changes no state."""
    facts = Workspace.facts()
    if args.mark:
        with _workspace() as workspace:
            marker = os.path.join(workspace.store_path.rsplit(os.sep, 1)[0], "survival-marker.json")
            payload = {
                "writtenAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "writtenByPid": os.getpid(),
                "storePath": workspace.store_path,
                "documents": [f"{document.kind}@{document.owner_id}" for document in workspace.store.documents],
                "tasks": [task.id for task in workspace.store.tasks],
            }
            with open(marker, "w", encoding="utf-8") as handle:
                json.dump(payload, handle, indent=1, sort_keys=True)
            facts["markerWritten"] = payload
            facts["markerPath"] = marker
    if args.read:
        with _workspace() as workspace:
            marker = os.path.join(workspace.store_path.rsplit(os.sep, 1)[0], "survival-marker.json")
            facts["markerPath"] = marker
            try:
                with open(marker, encoding="utf-8") as handle:
                    facts["markerRead"] = json.load(handle)
            except FileNotFoundError:
                facts["markerRead"] = None
    _print(facts)
    return 0


def _workspace() -> "WorkspaceContext":
    return WorkspaceContext()


class WorkspaceContext:
    def __enter__(self) -> Workspace:
        self._workspace = Workspace.open()
        return self._workspace

    def __exit__(self, *_: object) -> None:
        self._workspace.close()


def _approval_json(workspace: Workspace, request_id: str) -> dict:
    record = workspace.approval(request_id) or Approval()
    return {
        "requestId": request_id,
        "state": record.state,
        "deadline": record.deadline,
        "deadlineIso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(record.deadline))
        if record.deadline
        else "",
        "requestedAt": record.requestedAt,
        "decision": record.decision,
        "reviewer": record.reviewer,
        "reason": record.reason,
        "task": _task_json(workspace, workspace.approval_task_id(request_id)),
    }


def _work_state(workspace: Workspace, request_id: str) -> "WorkState":
    return workspace.work_state(request_id) or WorkState(requestId=request_id)


def _task_json(workspace: Workspace, task_id: str) -> dict | None:
    if not task_id:
        return None
    record = workspace.store.task(task_id)
    if record is None:
        return None
    return {
        "id": record.id,
        "name": record.name,
        "status": record.status.name if hasattr(record.status, "name") else str(record.status),
        "waitingOn": list(record.waiting_on),
        "restarts": record.restarts,
        "checkpoint": record.checkpoint,
        "outcome": None
        if record.outcome is None
        else {"status": record.outcome.status, "error": record.outcome.error, "result": record.outcome.result},
        "memos": record.memos,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="d2-tool")
    commands = parser.add_subparsers(dest="command", required=True)

    for name in ("state", "approval"):
        command = commands.add_parser(name)
        command.add_argument("--request", required=True)
        command.set_defaults(handler=state if name == "state" else approval)

    assess_command = commands.add_parser("assess")
    assess_command.add_argument("--request", required=True)
    assess_command.add_argument("--text", default="")
    assess_command.set_defaults(handler=assess)

    resume_command = commands.add_parser("resume")
    resume_command.add_argument("--request", required=True)
    resume_command.set_defaults(handler=resume)

    probe_command = commands.add_parser("probe")
    probe_command.add_argument("--mark", action="store_true")
    probe_command.add_argument("--read", action="store_true")
    probe_command.set_defaults(handler=probe)

    args = parser.parse_args(argv)
    return args.handler(args)


if __name__ == "__main__":
    sys.exit(main())