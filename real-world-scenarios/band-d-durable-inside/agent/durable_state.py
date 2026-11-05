"""The Durable working-state layer, as this agent uses it.

Three things live here, and each one is a decision rather than a helper:

1. ``WorkState`` / ``Approval`` — the two documents. A ``WorkState`` is one prior-auth
   request's working state; an ``Approval`` is one human decision, with its durable
   deadline. Both are Durable documents, so they are commits with history, not a file
   the agent rewrites.

2. ``ApprovalTask`` — the human-in-the-loop step as a *task*, not a loop. It commits a
   ``WAITING`` checkpoint carrying the deadline and ends the run. Nothing keeps a
   coroutine alive while a person thinks; ``DurableStore.raise_event_async`` is what
   wakes it, and it wakes at the checkpoint the park wrote.

3. ``Workspace`` — where the store lives in the container, and what the platform's
   filesystem does to it. The path is resolved from the platform's own mount, never
   assumed, because a store on an ephemeral path is a working state that vanishes with
   the container: exactly the failure this band exists to rule out.

The Durable layer is consumed as the published package, not copied: a private copy
would be the failure mode ``.github/copilot-instructions.md`` names.
"""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass, field
from typing import Any

from durable import (
    DocumentDefinition,
    DurableHarness,
    Extension,
    ExtensionRegistry,
    DocumentHistory,
    DurableJson,
    DurableStore,
    DurableStoreFormat,
    DurableStoreOptions,
    DurableTask,
    DurableTaskDefinition,
    ForkPolicy,
    TaskCommit,
    TaskRuntime,
)

# -- document kinds ---------------------------------------------------------
#
# The kind is the document's identity in the store, so it is a name, not a path.
# Every kind this agent writes is listed here; nothing else writes documents.

WORK_STATE_KIND = "d2.work-state"
APPROVAL_KIND = "d2.approval"

# -- task names -------------------------------------------------------------

APPROVAL_TASK_NAME = "d2.approval-task"

# -- external events --------------------------------------------------------

#: The event the approval task parks on. Delivered by ``Workspace.deliver``.
APPROVAL_EVENT_PREFIX = "external:approval/"

#: How long a human has to answer before the approval fails, in seconds. A durable
#: deadline: it is written into the checkpoint, so it is the same instant for every
#: process that later reads the store.
APPROVAL_DEADLINE_SECONDS = 24 * 60 * 60


@dataclass(slots=True)
class WorkState:
    """One prior-auth request's working state.

    Deliberately small, and deliberately not the transcript: Foundry owns the
    conversation, and copying it here would be a second copy of the platform's record.
    What is here is what the *agent* needs to continue its own work — where in the
    workflow it is, and what it has produced so far.
    """

    #: The request the state belongs to.
    requestId: str = ""
    #: ``intake`` -> ``assessing`` -> ``awaiting-approval`` -> ``decided``.
    phase: str = "intake"
    #: What the assessment concluded, and why. Free text on purpose: the model writes it.
    assessment: str = ""
    #: The decision, once one has been made.
    decision: str = ""
    #: Who made it, and when. Empty until the approval task's event arrives.
    decidedBy: str = ""
    decidedAt: str = ""
    #: How many times this request has been picked up by a process. This is how a
    #: restart is *visible* in the working state rather than asserted in prose.
    resumedCount: int = 0
    #: The durable task that owns the approval step, once it has been created.
    approvalTaskId: str = ""


@dataclass(slots=True)
class Approval:
    """One human decision, waiting for an external event.

    The deadline is stored, not computed: a restarted process must read the same instant
    it wrote, or a "durable deadline" would quietly become "24 hours from now, again".
    """

    requestId: str = ""
    #: ``pending`` while parked, ``answered`` once the event arrived, ``expired`` past the deadline.
    state: str = "pending"
    #: Unix seconds at which the approval stops being answerable.
    deadline: float = 0.0
    #: Unix seconds at which the park happened.
    requestedAt: float = 0.0
    #: The answer, once delivered.
    decision: str = ""
    reviewer: str = ""
    reason: str = ""


#: The request's working state. ``latest`` history: the state is a position, and the
#: position that matters is the current one. The entries still record each transition.
WORK_STATE = DocumentDefinition(
    WORK_STATE_KIND, DocumentHistory.LATEST, ForkPolicy.CURRENT, initial=WorkState, type=WorkState
)

#: The approval. ``rewindable``: what a person decided, when, and on what deadline is the
#: record an auditor reads, so every version is kept — including a park that was entered
#: twice because the container restarted mid-wait.
APPROVAL = DocumentDefinition(
    APPROVAL_KIND, DocumentHistory.REWINDABLE, ForkPolicy.CURRENT, initial=Approval, type=Approval
)


def approval_task() -> DurableTaskDefinition:
    """The approval step, as a durable task.

    Two phases, and the split is the whole mechanism:

    ``park``  commits ``WAITING`` with the deadline in the checkpoint and the event name in
              ``waiting_on``. The run then ends — no timer, no coroutine, no open request.
              The deadline is what makes the wait *durable*: it is written once, and every
              later process reads the same instant.
    ``await`` runs only after an event has woken the task. If the answer is there it
              completes; if the deadline has passed it fails; if an unrelated resume
              re-entered it, it re-parks on the same event, which costs one commit and
              leaves the task observable as waiting.

    The phase writes nothing but the task's own checkpoint. Documents are written by the
    caller around the task (see ``Workspace``), so one writer owns them and the task's
    state machine stays a state machine.
    """

    async def park(_input: Any, _checkpoint: Any, runtime: TaskRuntime) -> TaskCommit:
        request_id = _request_id(runtime.task.input)
        now = time.time()
        return await runtime.commit_async(
            lambda: TaskCommit.waiting_for_event(
                {
                    "phase": "await",
                    "requestId": request_id,
                    "deadline": now + APPROVAL_DEADLINE_SECONDS,
                    "requestedAt": now,
                },
                APPROVAL_EVENT_PREFIX + request_id,
            )
        )

    async def await_phase(_input: Any, checkpoint: Any, runtime: TaskRuntime) -> TaskCommit:
        request_id = _request_id(runtime.task.input) or str(checkpoint.get("requestId") or "")
        answer = await runtime.memo_async(DurableStore.EXTERNAL_EVENT_MEMO_KEY)
        if answer is not None:
            # The decision and the end of the task are one commit: a store cannot hold a
            # finished task with a pending approval, or a decided approval with a task
            # still parked. The documents go in the transaction overload, which is the
            # one that can write them alongside the terminal outcome.
            async def settle(tx: Any) -> TaskCommit:
                _apply_answer(tx, request_id, answer)
                return TaskCommit.completed(answer)

            return await runtime.commit_async(settle)

        deadline = float(checkpoint.get("deadline") or 0.0)
        if time.time() >= deadline:
            return await runtime.commit_async(
                lambda: TaskCommit.failed("approval deadline expired")
            )

        # Not answered and not expired: re-park. The run ends here again, so a task
        # resumed by a restart that has nothing to act on costs one commit and stays
        # visible, instead of spinning or completing early.
        return await runtime.commit_async(
            lambda: TaskCommit.waiting_for_event(checkpoint, APPROVAL_EVENT_PREFIX + request_id)
        )

    return DurableTask.define_phases(
        APPROVAL_TASK_NAME,
        lambda: {"phase": "park"},
        {"park": park, "await": await_phase},
        lambda _input, _checkpoint, runtime: runtime.commit_async(lambda: TaskCommit.aborted()),
    )


def _request_id(payload: Any) -> str:
    if isinstance(payload, dict):
        return str(payload.get("requestId") or "")
    return ""


def _apply_answer(tx: Any, request_id: str, answer: Any) -> None:
    """Writes the human's answer into both documents, inside the caller's transaction."""
    decision = str(answer.get("decision") if isinstance(answer, dict) else answer)
    reviewer = str(answer.get("reviewer") if isinstance(answer, dict) else "")
    reason = str(answer.get("reason") if isinstance(answer, dict) else "")

    approval = tx.doc(APPROVAL, request_id)
    approval.value.requestId = request_id
    approval.value.state = "answered"
    approval.value.decision = decision
    approval.value.reviewer = reviewer
    approval.value.reason = reason
    approval.save(tx.append_entry(request_id, "durable.approval-answered"))

    state = tx.doc(WORK_STATE, request_id)
    state.value.requestId = request_id
    state.value.phase = "decided"
    state.value.decision = decision
    state.value.decidedBy = reviewer
    state.value.decidedAt = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    state.save(tx.append_entry(request_id, "durable.approval-applied"))


@dataclass(slots=True)
class Workspace:
    """The store, and the filesystem facts that decide whether it survives.

    ``resolve()`` is not a convenience: a hosted agent's ``/tmp`` is ephemeral and parts
    of the image are read-only, so the store's location is the difference between a
    working state that survives a session stop and one that does not. ``facts()`` returns
    what was actually true in this container, so ``evidence/`` can quote it instead of
    describing the documentation.
    """

    #: Where the container's persistent, platform-owned session volume is mounted. The
    #: platform injects ``HOME=/home/session``; the store goes under it unless
    #: ``DURABLE_STORE_PATH`` names another path on purpose.
    SESSION_HOME = "/home/session"

    store: DurableStore = field(init=False)
    store_path: str = field(init=False)

    @staticmethod
    def resolve() -> str:
        explicit = os.environ.get("DURABLE_STORE_PATH", "").strip()
        if explicit:
            return explicit

        home = os.environ.get("HOME", "").strip() or Workspace.SESSION_HOME
        return os.path.join(home, "durable", "working-state.jsonl")

    @classmethod
    def open(cls) -> "Workspace":
        workspace = cls()
        workspace.store_path = cls.resolve()
        os.makedirs(os.path.dirname(workspace.store_path), exist_ok=True)
        workspace.store = DurableStore.create(
            DurableStoreOptions(path=workspace.store_path, format=DurableStoreFormat.JSONL)
        )
        return workspace

    def close(self) -> None:
        self.store.close()

    # -- read helpers -------------------------------------------------------

    def work_state(self, request_id: str) -> WorkState | None:
        value = _latest(self.store, WORK_STATE_KIND, request_id)
        return None if value is None else _to(value, WorkState)

    def approval(self, request_id: str) -> Approval | None:
        value = _latest(self.store, APPROVAL_KIND, request_id)
        return None if value is None else _to(value, Approval)

    def approval_task_id(self, request_id: str) -> str:
        """The durable task that owns this request's approval, or "" before one is created."""
        for task in self.store.tasks:
            if task.name == APPROVAL_TASK_NAME and _request_id(task.input) == request_id:
                return task.id
        return ""

    # -- writes -------------------------------------------------------------

    def note_assessment(self, request_id: str, assessment: str) -> None:
        """Commits the assessment step. One document version, one entry, one transaction."""

        async def body(tx: Any) -> None:
            draft = tx.doc(WORK_STATE, request_id)
            draft.value.requestId = request_id
            draft.value.phase = "awaiting-approval"
            draft.value.assessment = assessment
            draft.save(tx.append_entry(request_id, "durable.assessment"))

        _run(self.store.commit_async(body))

    def note_resume(self, request_id: str) -> int:
        """Counts a process picking the request up, so a restart is visible in the state."""
        count = 0

        async def body(tx: Any) -> None:
            nonlocal count
            draft = tx.doc(WORK_STATE, request_id)
            count = draft.value.resumedCount + 1
            draft.value.resumedCount = count
            draft.value.requestId = request_id
            draft.save(tx.append_entry(request_id, "durable.resume"))

        _run(self.store.commit_async(body))
        return count

    def start_approval(self, request_id: str) -> str:
        """Creates the approval task, or returns the one this request already has.

        Idempotent by the store, not by a flag: a request has one approval task because
        the store is asked for it first, so a duplicate delivery re-attaches instead of
        creating a second task that would wait for the same event.

        Two commits, in this order, and the order is the crash-safety design:

        1. The task, already at its ``await`` checkpoint, which carries the deadline. A
           process that dies after this leaves a task the next process finds parked, with
           the deadline the *first* process set.
        2. The readable record — the approval document and the work state pointing at the
           task. A process that dies before this leaves a task with no document; the next
           ``start_approval`` finds the task by name and writes the document from the
           task's checkpoint. The reverse order would leave a document claiming a deadline
           no task holds.

        Nothing durable is written inside a blocking wait, which is the trap the layer's
        own notes record: a memo or a commit issued while a task run is holding the store
        per-request can deadlock against it. The wait is the ``wait_event`` call the caller
        makes *after* this method returns.
        """
        existing = self.approval_task_id(request_id)
        if existing:
            self._ensure_parked_document(request_id, existing)
            return existing

        # One instant for the document and the task's checkpoint: the park and its deadline
        # are one event, so a reader diffing the two must not see a millisecond drift that
        # looks like a second park.
        requested_at = time.time()
        deadline = requested_at + APPROVAL_DEADLINE_SECONDS

        async def create(tx: Any) -> str:
            record = tx.create_task(
                approval_task(),
                DurableJson.to_wire({"requestId": request_id}),
                owner_conversation_id=request_id,
            )
            # The initial checkpoint a task is created with names no phase, so the first
            # run enters ``park``. Writing the park checkpoint here instead would skip the
            # phase that owns it.
            return record.id

        task_id = _run(self.store.commit_async(create))

        async def park(tx: Any) -> None:
            draft = tx.doc(APPROVAL, request_id)
            draft.value.requestId = request_id
            draft.value.state = "pending"
            draft.value.deadline = deadline
            draft.value.requestedAt = requested_at
            draft.save(tx.append_entry(request_id, "durable.approval-parked"))

        async def mark_waiting(tx: Any) -> None:
            state = tx.doc(WORK_STATE, request_id)
            state.value.requestId = request_id
            state.value.phase = "awaiting-approval"
            state.value.approvalTaskId = task_id
            state.save(tx.append_entry(request_id, "durable.approval-created"))

        _run(self.store.commit_async(park))
        _run(self.store.commit_async(mark_waiting))

        # Park the task at its own checkpoint. This process owns no task runner, so this is
        # the run: one `waiting` commit, then nothing.
        _run(
            self.store.commit_task_async(
                task_id,
                lambda: TaskCommit.waiting_for_event(
                    {
                        "phase": "await",
                        "requestId": request_id,
                        "deadline": deadline,
                        "requestedAt": requested_at,
                    },
                    APPROVAL_EVENT_PREFIX + request_id,
                ),
            )
        )
        return task_id

    def _ensure_parked_document(self, request_id: str, task_id: str) -> None:
        """Restores the readable record from a task that exists, if the document is missing."""
        if self.approval(request_id) is not None:
            return

        checkpoint = (self.store.task(task_id) or object()).checkpoint or {}
        if not isinstance(checkpoint, dict) or not checkpoint.get("deadline"):
            return

        async def body(tx: Any) -> None:
            draft = tx.doc(APPROVAL, request_id)
            draft.value.requestId = request_id
            draft.value.state = "pending"
            draft.value.deadline = float(checkpoint["deadline"])
            draft.value.requestedAt = float(checkpoint.get("requestedAt") or 0.0)
            draft.save(tx.append_entry(request_id, "durable.approval-recovered"))

        _run(self.store.commit_async(body))

    def deliver(self, request_id: str, answer: dict[str, Any]) -> list[str]:
        """Delivers a human's answer as the external event the task parked on."""
        return _run(
            self.store.raise_event_async(
                APPROVAL_EVENT_PREFIX + request_id, DurableJson.to_wire(answer)
            )
        )

    def inventory(self) -> dict[str, Any]:
        """Every file under the store's directory, with what it is.

        Three sidecars exist and each has a different meaning: the store is the working
        state, ``.lock`` is the running container's exclusive-writer lock, and
        ``.lock.owner`` is the record of *which* container holds it. A restart is visible
        in this list — the owner's process id and start time change while the store's
        digest and size do not.
        """
        directory = os.path.dirname(self.store_path)
        files: list[dict[str, Any]] = []
        if os.path.isdir(directory):
            for name in sorted(os.listdir(directory)):
                path = os.path.join(directory, name)
                if not os.path.isfile(path):
                    continue
                entry: dict[str, Any] = {
                    "name": name,
                    "bytes": os.path.getsize(path),
                    "mtime": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(os.path.getmtime(path))),
                }
                if name.endswith(".owner"):
                    try:
                        entry["owner"] = json.loads(open(path, encoding="utf-8").read())
                    except (OSError, ValueError):
                        entry["owner"] = None
                files.append(entry)
        return {"directory": directory, "files": files, "pid": os.getpid()}

    def resume_approval(self, request_id: str) -> dict[str, Any]:
        """Re-enters the parked approval task and returns what its run settled at.

        This is the restart, not a re-run: the task is read from the store, re-entered at
        the ``await`` checkpoint the park wrote, and driven to an outcome. The run's first
        phase — ``park`` — is never entered again, which is why the deadline stays the one
        the first process set.

        A harness with no SDK client is enough, and is deliberate: a task run needs the
        store and the registry, not a conversation. The step has nothing to ask a model.
        """
        task_id = self.approval_task_id(request_id)
        if not task_id:
            return {"requestId": request_id, "settled": None, "reason": "no approval task"}

        async def settle() -> dict[str, Any]:
            registry = ExtensionRegistry()
            registry.install(Extension("d2", tasks=[approval_task()]))
            harness = DurableHarness(None, self.store, registry)
            try:
                outcome = await harness.wait_for_task_async(task_id)
            finally:
                harness.close()
            return {
                "taskId": task_id,
                "status": outcome.status,
                "result": outcome.result,
                "error": outcome.error,
            }

        return _run(settle())



    @staticmethod
    def facts() -> dict[str, Any]:
        """What this container's filesystem actually does, measured rather than asserted.

        Six probes, each in its own directory, because the answer that matters is not
        "is ``/`` writable" but "which of these paths survives the container". The
        survival half is answered by writing a marker and reading it in the *next*
        session; see ``agent/probe_paths.py`` and ``evidence/container-filesystem.md``.
        """
        probes: dict[str, Any] = {}
        for path in ("/", "/app", "/tmp", "/files", "/home/session", Workspace.SESSION_HOME):
            probes[path] = _writable(path)

        marker = os.path.join(Workspace.SESSION_HOME, "d2-marker")
        probes["markerPath"] = marker
        probes["home"] = os.environ.get("HOME", "")
        probes["storePath"] = Workspace.resolve()
        probes["userId"] = os.getuid() if hasattr(os, "getuid") else None
        return probes


def _writable(path: str) -> dict[str, Any]:
    result: dict[str, Any] = {"exists": os.path.isdir(path)}
    if not result["exists"]:
        return result

    probe = os.path.join(path, f".d2-write-probe-{os.getpid()}")
    try:
        with open(probe, "w", encoding="utf-8") as handle:
            handle.write("probe")
        os.remove(probe)
        result["writable"] = True
    except OSError as error:
        result["writable"] = False
        result["error"] = f"{type(error).__name__}: {error}"
    return result


def _latest(store: DurableStore, kind: str, owner_id: str) -> Any:
    state = store.document(kind, owner_id)
    return None if state is None else state.latest


def _to(value: Any, kind: type) -> Any:
    if isinstance(value, dict):
        return kind(**{key: value[key] for key in kind.__dataclass_fields__ if key in value})
    return value


def _run(awaitable: Any) -> Any:
    """Runs a durable call to completion.

    Every caller in this scenario is a standalone process — the tool script the platform
    invokes — never a coroutine inside the hosting server's loop, so one ``asyncio.run``
    per call is the whole story. The store's commits are synchronous under its own lock,
    so nothing is left pending when the loop closes.
    """
    import asyncio

    return asyncio.run(awaitable)