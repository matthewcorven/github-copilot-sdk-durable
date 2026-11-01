"""The durable layer, ported from ``lib/Durable.cs``.

This module is a language port, not a redesign: every public type keeps the name of
the C# type it mirrors, so a reader can move between the two files by name. The
language-neutral contract both ports implement is ``docs/DESIGN.md`` section 3.

Where Python has no equivalent for a C# construct, the approximation is recorded in
``docs/COMPARATIVE_ANALYSIS.md`` rather than being left implicit here.

The runtime the harness connects to is selected by ``DURABLE_RUNTIME_PATH`` (see
:attr:`DurableHarnessOptions.RUNTIME_PATH_ENVIRONMENT_VARIABLE`), exactly as the C#
harness does. Nothing in this module stands in for that runtime.
"""

from __future__ import annotations

import asyncio
import contextlib
import dataclasses
import functools
import inspect
import json
import os
import sys
import threading
import time
from collections.abc import AsyncIterator, Awaitable, Callable, Mapping, Sequence
from datetime import datetime, timezone
from dataclasses import dataclass, field
from enum import Enum, IntEnum
from pathlib import Path
from typing import Any, ClassVar, Final, Generic, TypeVar, get_type_hints
from uuid import uuid4

from copilot import CopilotClient, RuntimeConnection
from copilot.generated.rpc import (
    PermissionDecisionApproveOnce, PermissionDecisionReject, SandboxConfig, SessionsForkRequest,
)

__all__ = [
    "AgentChoices",
    "Check",
    "ConversationCheckpoint",
    "ConversationRecord",
    "DocumentDefinition",
    "DocumentDraft",
    "DocumentHistory",
    "DocumentState",
    "DocumentVersion",
    "DurableClientBuilder",
    "DurableClientOptions",
    "DurableConversation",
    "DurableCopilotClient",
    "DurableExtensions",
    "DurableFrame",
    "DurableHarness",
    "DurableHarnessOptions",
    "DurableJson",
    "DurableModelReport",
    "DurableRecoveryRequiredException",
    "DurableSandboxAuth",
    "DurableSandboxCredentials",
    "DurableSandboxFilesystem",
    "DurableSandboxNetwork",
    "DurableSandboxPolicy",
    "DurableSandboxPolicyException",
    "DurableSandboxProxy",
    "DurableStore",
    "DurableStoreFormat",
    "DurableStoreOptions",
    "DurableTask",
    "DurableTaskDefinition",
    "DurableTaskRecord",
    "DurableWatch",
    "EntryRecord",
    "Extension",
    "ExtensionRegistry",
    "ForkIntent",
    "ForkIntentState",
    "ForkPolicy",
    "InMemoryStoreProvider",
    "InputAdmission",
    "JsonlStoreProvider",
    "LegacyJsonStoreProvider",
    "PermissionDecision",
    "PermissionGrant",
    "PermissionPolicy",
    "PromptSection",
    "ResolvedAgent",
    "SectionInput",
    "SessionBinding",
    "StoreFileLock",
    "StoreSnapshot",
    "TaskCommit",
    "TaskOutcome",
    "TaskRuntime",
    "TaskStateSnapshot",
    "TaskStatus",
    "ToolWrapper",
    "Transaction",
    "UndoLog",
    "bookkeeping_entries",
    "expand_paths",
    "resolve_runtime_path",
    "validate_sandbox_policy",
]

T = TypeVar("T")

#: The sentinel the C# provider uses for an entry it has never seen. ``sequence_of``
#: returns it so an unknown entry sorts after every real write.
UNKNOWN_SEQUENCE: Final[int] = sys.maxsize


# ---------------------------------------------------------------------------
# Documents
# ---------------------------------------------------------------------------


class ForkPolicy(IntEnum):
    """How a document's value travels into a fork.

    The wire format is the C# enum's numeric value: the store serializes enums as
    numbers, so these values are part of the on-disk contract.
    """

    AS_OF = 0
    CURRENT = 1
    INITIAL = 2


class DocumentHistory(IntEnum):
    """Whether a document keeps every committed version or only the latest."""

    LATEST = 0
    REWINDABLE = 1


@dataclass(frozen=True, slots=True)
class DocumentDefinition(Generic[T]):
    """A named, typed document the durable layer stores per owner.

    ``initial`` is a factory rather than a value so each owner gets its own copy of a
    mutable default, which is what the C# ``Func<T>`` parameter does.
    """

    kind: str
    history: DocumentHistory
    fork: ForkPolicy
    initial: Callable[[], T] | None = None
    #: The stored value's type. C# recovers it from the generic argument of
    #: ``DocumentDefinition<T>``; Python has to carry it, because a document read back
    #: from the store is plain JSON until something names the type to rebuild.
    type: type[T] | None = None

    def initial_value(self) -> T | None:
        return None if self.initial is None else self.initial()

    def deserialize(self, value: Any) -> T | None:
        """Reads a stored value back into this definition's type."""
        if value is None or self.type is None:
            return value
        if not dataclasses.is_dataclass(self.type):
            return value
        return _from_wire(self.type, value)


@dataclass(frozen=True, slots=True)
class DocumentVersion:
    """One committed value of a document, anchored to the entry that committed it."""

    entry_id: str
    value: Any
    sequence: int


@dataclass(frozen=True, slots=True)
class EntryRecord:
    """The store's record of one entry: which conversation it belongs to and when."""

    id: str
    conversation_id: str
    kind: str
    data: Any
    sequence: int


@dataclass(slots=True)
class DocumentState:
    """A document's committed versions, oldest first."""

    kind: str
    owner_id: str
    fork: ForkPolicy
    initial: Any = None
    versions: list[DocumentVersion] = field(default_factory=list)

    @property
    def latest(self) -> Any:
        """The most recent committed value, or ``None`` when nothing was committed."""
        return self.versions[-1].value if self.versions else None

    def as_of(self, entry_id: str) -> Any:
        """The value as of exactly ``entry_id``, or ``None`` when it never committed there."""
        for version in reversed(self.versions):
            if version.entry_id == entry_id:
                return version.value
        return None

    def as_of_or_before(
        self,
        entry_id: str,
        sequence_of: Callable[[str], int] | None = None,
    ) -> Any:
        """The value as of ``entry_id``, falling back to the last version before it.

        With a ``sequence_of`` resolver the boundary is the entry's write sequence, so a
        version committed by an earlier entry still counts. Without one the only
        fallback is the document's initial value, which is what a fork taken at an entry
        the document never committed at inherits.
        """
        if sequence_of is None:
            exact = self.as_of(entry_id)
            if exact is not None:
                return exact
            return self.versions[0].value if self.versions else None

        boundary = sequence_of(entry_id)
        for version in reversed(self.versions):
            if version.entry_id == entry_id:
                return version.value
            if version.sequence <= boundary:
                return version.value
        return None


# ---------------------------------------------------------------------------
# Tasks
# ---------------------------------------------------------------------------


class TaskStatus(IntEnum):
    """The durable task state machine. ``TERMINAL`` is final and never overwritten."""

    PENDING = 0
    RUNNING = 1
    WAITING = 2
    SLEEPING = 3
    TERMINAL = 4


@dataclass(slots=True)
class TaskOutcome:
    """How a task ended, or ``pending`` while it has not."""

    status: str
    result: Any = None
    error: str | None = None

    @property
    def is_completed(self) -> bool:
        return self.status == "completed"

    @property
    def is_aborted(self) -> bool:
        return self.status == "aborted"

    @property
    def is_failed(self) -> bool:
        return self.status == "failed"

    @staticmethod
    def pending() -> TaskOutcome:
        return TaskOutcome("pending")

    @staticmethod
    def completed(result: Any = None) -> TaskOutcome:
        return TaskOutcome("completed", result=result)

    @staticmethod
    def failed(error: str) -> TaskOutcome:
        return TaskOutcome("failed", error=error)

    @staticmethod
    def aborted() -> TaskOutcome:
        return TaskOutcome("aborted")


@dataclass(slots=True)
class TaskCommit:
    """What a task phase asks the store to record when it returns."""

    status: TaskStatus
    checkpoint: Any = None
    outcome: TaskOutcome | None = None
    waiting_on: list[str] = field(default_factory=list)

    @staticmethod
    def running(checkpoint: Any) -> TaskCommit:
        """Continues the task with a new checkpoint."""
        return TaskCommit(TaskStatus.RUNNING, checkpoint=checkpoint)

    @staticmethod
    def waiting(checkpoint: Any, on: Sequence[str]) -> TaskCommit:
        """Continues the task, waiting on child tasks."""
        return TaskCommit(TaskStatus.WAITING, checkpoint=checkpoint, waiting_on=list(on))

    @staticmethod
    def completed(result: Any = None) -> TaskCommit:
        """Finishes the task successfully."""
        return TaskCommit(TaskStatus.TERMINAL, outcome=TaskOutcome.completed(result))

    @staticmethod
    def failed(error: str) -> TaskCommit:
        """Finishes the task with a failure."""
        return TaskCommit(TaskStatus.TERMINAL, outcome=TaskOutcome.failed(error))

    @staticmethod
    def aborted() -> TaskCommit:
        """Finishes the task as aborted."""
        return TaskCommit(TaskStatus.TERMINAL, outcome=TaskOutcome.aborted())


@dataclass(slots=True)
class DurableTaskRecord:
    """The store's record of one task."""

    id: str
    name: str
    input: Any = None
    checkpoint: Any = None
    status: TaskStatus = TaskStatus.PENDING
    outcome: TaskOutcome | None = None
    restarts: int = 0
    owner_conversation_id: str | None = None
    owner_task_id: str | None = None
    background: bool = False
    waiting_on: list[str] = field(default_factory=list)
    memos: dict[str, Any] = field(default_factory=dict)


@dataclass(slots=True)
class TaskStateSnapshot:
    """A task's mutable state, captured so a failed transaction can put it back."""

    status: TaskStatus
    checkpoint: Any
    outcome: TaskOutcome | None
    waiting_on: list[str]
    memos: dict[str, Any]

    @staticmethod
    def capture(task: DurableTaskRecord) -> TaskStateSnapshot:
        return TaskStateSnapshot(
            status=task.status,
            checkpoint=task.checkpoint,
            outcome=task.outcome,
            waiting_on=list(task.waiting_on),
            memos=dict(task.memos),
        )

    def apply_to(self, task: DurableTaskRecord) -> None:
        task.status = self.status
        task.checkpoint = self.checkpoint
        task.outcome = self.outcome
        task.waiting_on = list(self.waiting_on)
        task.memos = dict(self.memos)


class TaskRuntime:
    """The context a task phase runs with."""

    def __init__(
        self,
        store: DurableStore,
        task: DurableTaskRecord,
        definition: DurableTaskDefinition,
        cancellation: asyncio.Event | None = None,
    ) -> None:
        self._store = store
        self.task = task
        self.definition = definition
        self.cancellation = cancellation

    @property
    def now(self) -> float:
        """The current wall-clock time, as a ``time.time()`` value."""
        return time.time()

    async def commit_async(
        self,
        commit: Callable[[Transaction], Awaitable[TaskCommit]] | Callable[[], TaskCommit],
    ) -> TaskCommit:
        """Saves a checkpoint, or finishes the task with an outcome.

        The callback may create child tasks: they are written in the same transaction as
        the checkpoint that records them.
        """
        return await self._store.commit_task_async(self.task.id, commit)

    def create_child_task(
        self,
        tx: Transaction,
        definition: DurableTaskDefinition,
        input: Any = None,
    ) -> DurableTaskRecord:
        """Creates a child task owned by this task, in the caller's transaction."""
        return tx.create_task(definition, input, owner_task_id=self.task.id)

    def schedule_task(self, task_id: str, cancellation: asyncio.Event | None = None) -> None:
        """Schedules a task to run."""
        self._store.schedule_task(task_id, cancellation)

    async def memo_async(self, key: str, value: Any = None) -> Any:
        """Reads an idempotency memo, or writes one when ``value`` is supplied."""
        return await self._store.memo_async(self.task.id, key, value)

    async def sleep_until_async(
        self,
        deadline: float,
        next_checkpoint: Callable[[], TaskCommit],
    ) -> TaskCommit:
        """Sleeps until a durable deadline, saving the wake time in the checkpoint."""
        return await self._store.sleep_until_async(
            self.task.id, deadline, next_checkpoint, self.cancellation
        )

    async def outcomes_async(self, task_ids: Sequence[str]) -> list[TaskOutcome]:
        """Reads the outcomes of child tasks."""
        return await self._store.outcomes_async(task_ids)


class DurableTaskDefinition:
    """The definition of a durable task: a checkpointed state machine."""

    def __init__(
        self,
        name: str,
        initial: Callable[[], Any],
        phases: Mapping[str, Callable[[TaskRuntime], Awaitable[TaskCommit]]],
        abort: Callable[[TaskRuntime], Awaitable[TaskCommit]] | None = None,
    ) -> None:
        self.name = name
        self.initial = initial
        self.phases = dict(phases)
        self.abort = abort

    async def run_abort(self, runtime: TaskRuntime) -> TaskCommit:
        """Runs instead of the phases after an abort."""
        if self.abort is None:
            return TaskCommit.aborted()
        return await self.abort(runtime)


class DurableTask:
    """Builds durable task definitions with typed input, checkpoint, and outcome."""

    @staticmethod
    def define(
        name: str,
        initial: Callable[[], Any],
        phase: Callable[[Any, Any, TaskRuntime], Awaitable[TaskCommit]],
        abort: Callable[[Any, Any, TaskRuntime], Awaitable[TaskCommit]] | None = None,
    ) -> DurableTaskDefinition:
        """Defines a single-phase task whose checkpoint and outcome are typed."""

        async def run(runtime: TaskRuntime) -> TaskCommit:
            return await phase(runtime.task.input, runtime.task.checkpoint, runtime)

        async def run_abort(runtime: TaskRuntime) -> TaskCommit:
            if abort is None:
                return TaskCommit.aborted()
            return await abort(runtime.task.input, runtime.task.checkpoint, runtime)

        return DurableTaskDefinition(
            name,
            lambda: DurableJson.clone(initial()),
            {"run": run},
            run_abort,
        )

    @staticmethod
    def define_phases(
        name: str,
        initial: Callable[[], Any],
        phases: Mapping[str, Callable[[Any, Any, TaskRuntime], Awaitable[TaskCommit]]],
        abort: Callable[[Any, Any, TaskRuntime], Awaitable[TaskCommit]] | None = None,
    ) -> DurableTaskDefinition:
        """Defines a multi-phase task from a phase-name to handler map."""
        handlers: dict[str, Callable[[TaskRuntime], Awaitable[TaskCommit]]] = {}
        for phase_name, handler in phases.items():

            async def run(
                runtime: TaskRuntime,
                handler: Callable[[Any, Any, TaskRuntime], Awaitable[TaskCommit]] = handler,
            ) -> TaskCommit:
                return await handler(runtime.task.input, runtime.task.checkpoint, runtime)

            handlers[phase_name] = run

        async def run_abort(runtime: TaskRuntime) -> TaskCommit:
            if abort is None:
                return TaskCommit.aborted()
            return await abort(runtime.task.input, runtime.task.checkpoint, runtime)

        return DurableTaskDefinition(
            name,
            lambda: DurableJson.clone(initial()),
            handlers,
            run_abort,
        )


class DurableJson:
    """The store's JSON conventions.

    The C# store serializes with ``JsonSerializerDefaults.Web``: camelCase property
    names, nulls omitted, and enums as numbers. This class is the single place the
    Python port reproduces that, so the two ports read each other's files.
    """

    @staticmethod
    def dumps(value: Any) -> str:
        return json.dumps(
            DurableJson.to_wire(value),
            separators=(",", ":"),
            ensure_ascii=False,
        )

    @staticmethod
    def loads(text: str) -> Any:
        return json.loads(text)

    @staticmethod
    def to_wire(value: Any) -> Any:
        """Converts a value into the JSON shape the C# store writes."""
        if isinstance(value, Enum):
            return int(value)  # type: ignore[call-overload]
        if dataclasses.is_dataclass(value) and not isinstance(value, type):
            return {
                _camel(f.name): DurableJson.to_wire(getattr(value, f.name))
                for f in dataclasses.fields(value)
                if getattr(value, f.name) is not None and not f.metadata.get("json_ignore")
            }
        if isinstance(value, Mapping):
            return {str(key): DurableJson.to_wire(item) for key, item in value.items()}
        if isinstance(value, (list, tuple)):
            return [DurableJson.to_wire(item) for item in value]
        return value

    @staticmethod
    def clone(value: Any) -> Any:
        """A deep copy, so a stored value cannot be mutated through the caller's handle."""
        if value is None or isinstance(value, (str, int, float, bool)):
            return value
        return json.loads(json.dumps(DurableJson.to_wire(value)))


def _camel(name: str) -> str:
    head, *tail = name.split("_")
    return head + "".join(part[:1].upper() + part[1:] for part in tail)


async def _sleep(delay: float, cancellation: asyncio.Event | None) -> None:
    """Sleeps for ``delay`` seconds, waking early when ``cancellation`` is set."""
    if cancellation is None:
        await asyncio.sleep(delay)
        return
    with contextlib.suppress(asyncio.TimeoutError):
        await asyncio.wait_for(cancellation.wait(), timeout=delay)


def bookkeeping_entries(
    store: DurableStore,
    conversation_id: str,
    entries: Sequence[str],
) -> set[str]:
    """The entries that exist only to anchor the durable layer's own bookkeeping.

    The durable layer writes an entry whenever it commits one of its internal documents:
    the fork intent, the session binding, the agent choices a conversation was created or
    reconfigured with. Those entries are durable state, not conversation, so they are kept
    out of :meth:`DurableConversation.entries`.

    The set is derived from the store's document state rather than from an entry's kind,
    because the kind is caller-supplied: an app may write a ``"fork"`` entry of its own and
    must still see it. An entry is bookkeeping when it is anchored by at least one document
    version and every version anchored to it belongs to an internal document kind. The entry
    a fork was taken at is never bookkeeping: it is inherited transcript, and a fork's copy
    of the parent's agent document is anchored to it.

    A fork inherits the parent's entries verbatim, including the ones that were bookkeeping
    in the parent, so the answer is the union over the conversation and its ancestors.
    """
    bookkeeping: set[str] = set()
    boundaries: set[str] = set()
    user_anchored: set[str] = set()
    visited: set[str] = set()

    current: str | None = conversation_id
    while current is not None and current not in visited:
        visited.add(current)
        record = store.conversation(current)
        if record is not None and record.fork_entry_id is not None:
            boundaries.add(record.fork_entry_id)

        anchored: set[str] = set()
        for document in store.documents:
            if document.owner_id != current:
                continue
            is_internal = document.kind in INTERNAL_DOCUMENT_KINDS
            for version in document.versions:
                if not version.entry_id:
                    continue
                anchored.add(version.entry_id)
                if not is_internal:
                    user_anchored.add(version.entry_id)

        for entry in entries:
            if entry in anchored:
                bookkeeping.add(entry)

        current = None if record is None else record.parent_conversation_id

    bookkeeping -= user_anchored
    bookkeeping -= boundaries
    return bookkeeping


# ---------------------------------------------------------------------------
# Conversations
# ---------------------------------------------------------------------------

def _event_kind(event: Any) -> str | None:
    kind = event.get("type") if isinstance(event, Mapping) else getattr(event, "type", None)
    return kind.value if isinstance(kind, Enum) else kind


def _event_data(event: Any) -> Any:
    return event.get("data", {}) if isinstance(event, Mapping) else getattr(event, "data", None)


def _event_field(data: Any, name: str) -> Any:
    if isinstance(data, Mapping):
        return data.get(name, data.get(_camel(name)))
    return getattr(data, name, None)


@dataclass(slots=True)
class ConversationRecord:
    """A conversation's durable identity and its place in the fork tree."""

    id: str
    parent_conversation_id: str | None = None
    fork_entry_id: str | None = None
    owner_task_id: str | None = None
    owner_conversation_id: str | None = None


class ForkIntentState(IntEnum):
    """How far a fork has got."""

    PENDING = 0
    COMPLETED = 1
    ABANDONED = 2


@dataclass(slots=True)
class ForkIntent:
    """The durable record of a fork in progress, owned by the fork itself.

    It is written in the same transaction that creates the fork's conversation record
    and before the native fork call, so a crash between the two is detectable: a fork
    whose intent is still ``PENDING`` has no session, and reopening the store either
    completes it or abandons it.
    """

    parent_conversation_id: str = ""
    fork_entry_id: str = ""
    session_id: str | None = None
    state: ForkIntentState = ForkIntentState.PENDING


@dataclass(slots=True)
class SessionBinding:
    """The durable record binding a conversation to the native session serving it."""

    session_id: str | None = None
    model: str | None = None
    bound_at: str | None = None
    pinned: bool = False


@dataclass(slots=True)
class InputAdmission:
    """The durable record of one prompt's admission, written before the prompt is sent."""

    admission_id: str | None = None
    prompt: str | None = None
    state: str | None = None
    admitted_at: str | None = None
    settled_at: str | None = None


@dataclass(slots=True)
class ConversationCheckpoint:
    """The durable record of one checkpoint: a turn that finished, or a tool call."""

    checkpoint_id: str | None = None
    kind: str | None = None
    event_id: str | None = None
    message_id: str | None = None
    tool_name: str | None = None
    tool_args: str | None = None
    success: bool | None = None
    error: str | None = None
    content: str | None = None
    recorded_at: str | None = None


class DurableRecoveryRequiredException(RuntimeError):
    """Raised when a conversation's native transcript is gone.

    The durable conversation is intact; what is missing is the runtime's own
    transcript. Continuing with a fresh, empty session would silently discard the
    model's context, so the resume fails instead and the host decides what to do.
    """

    def __init__(self, conversation_id: str, session_id: str | None) -> None:
        super().__init__(
            f"Conversation '{conversation_id}' cannot be resumed: the recorded SDK session "
            f"'{session_id if session_id is not None else '(none)'}' has no recoverable native "
            "transcript. The conversation itself is intact in the store; re-create its session "
            "deliberately rather than continuing with an empty replacement."
        )
        self.conversation_id = conversation_id
        self.session_id = session_id


class DurableSandboxPolicyException(RuntimeError):
    """Raised when a stored sandbox policy cannot be applied to a session as written.

    The alternative to failing is worse than failing: a policy that is silently
    narrowed produces a session that looks protected and is not.
    """

    def __init__(self, reason: str, policy: DurableSandboxPolicy) -> None:
        super().__init__(
            f"The stored sandbox policy cannot be applied: {reason} The session was not created "
            "or resumed, because running it without the protection the policy asks for would be "
            "a silent downgrade. Correct the stored policy, or clear it deliberately to run the "
            "conversation unsandboxed."
        )
        self.reason = reason
        self.policy = policy


# ---------------------------------------------------------------------------
# Store plumbing
# ---------------------------------------------------------------------------


class DurableStoreFormat(IntEnum):
    """The on-disk format of a durable store."""

    AUTO = 0
    IN_MEMORY = 1
    JSONL = 2
    LEGACY_JSON = 3


@dataclass(frozen=True, slots=True)
class DurableStoreOptions:
    """How a durable store is opened: where it lives and in what format."""

    path: str | None = None
    format: DurableStoreFormat = DurableStoreFormat.AUTO
    file_name: str = "durable-store.jsonl"
    provider: Callable[[DurableStoreOptions], DurableStoreProvider] | None = None
    provider_name: str | None = None


class UndoLog:
    """The undo log of a transaction in flight.

    A transaction writes straight into the provider's collections, so a throw has to
    undo what it wrote. The log records the inverse of every mutation and replays it in
    reverse when the transaction fails.
    """

    def __init__(self) -> None:
        self._actions: list[Callable[[], None]] = []

    def remove(self, mapping: dict[Any, Any], key: Any) -> None:
        """Records that a key was added to a dict, so it can be removed again."""
        self._actions.append(lambda: mapping.pop(key, None))

    def append(self, items: list[Any]) -> None:
        """Records that an item was appended to a list, so it can be removed again."""
        self._actions.append(lambda: items.pop())

    def sequence(self, restore: Callable[[], None]) -> None:
        """Records a restore action, such as resetting the write sequence."""
        self._actions.append(restore)

    def restore(self, snapshot: TaskStateSnapshot, task: DurableTaskRecord) -> None:
        """Records the task's own state, so a failed phase leaves the checkpoint where it was."""
        self._actions.append(lambda: snapshot.apply_to(task))

    def undo(self, provider: DurableStoreProvider) -> None:
        """Undoes every recorded mutation, most recent first."""
        for action in reversed(self._actions):
            action()
        self._actions.clear()


@dataclass(slots=True)
class DocumentVersionSnapshot:
    """The persisted shape of one document version."""

    entry_id: str = ""
    sequence: int = 0
    value: Any = None


@dataclass(slots=True)
class DocumentSnapshot:
    """The persisted shape of one document."""

    kind: str = ""
    owner_id: str = ""
    fork: ForkPolicy = ForkPolicy.AS_OF
    initial: Any = None
    versions: list[DocumentVersionSnapshot] = field(default_factory=list)


@dataclass(slots=True)
class StoreSnapshot:
    """A whole store's state, for providers that persist a single file."""

    sequence: int = 0
    conversations: list[ConversationRecord] = field(default_factory=list)
    tasks: list[DurableTaskRecord] = field(default_factory=list)
    documents: list[DocumentSnapshot] = field(default_factory=list)
    entries: dict[str, list[str]] = field(default_factory=dict)
    entry_records: list[EntryRecord] = field(default_factory=list)


class DurableStoreProvider:
    """The persistence seam of the durable store.

    The store owns the commit gate; the provider owns the state and the undo log that
    protects it. A provider is disposable because a persistent backend holds a resource
    for the store's lifetime: the exclusive lock on the store file.
    """

    @property
    def is_persistent(self) -> bool:
        return False

    def open(self, options: DurableStoreOptions) -> None:
        """Loads the committed state, or starts empty when there is none."""

    def close(self) -> None:
        """Releases whatever the provider holds for the store's lifetime."""

    def begin_transaction(self) -> None:
        raise NotImplementedError

    def rollback(self) -> None:
        raise NotImplementedError

    def commit(self) -> None:
        raise NotImplementedError

    @property
    def sequence(self) -> int:
        raise NotImplementedError

    @property
    def conversations(self) -> Sequence[ConversationRecord]:
        raise NotImplementedError

    @property
    def tasks(self) -> Sequence[DurableTaskRecord]:
        raise NotImplementedError

    @property
    def documents(self) -> Sequence[DocumentState]:
        raise NotImplementedError

    def conversation(self, conversation_id: str) -> ConversationRecord | None:
        raise NotImplementedError

    def task(self, task_id: str) -> DurableTaskRecord | None:
        raise NotImplementedError

    def document(self, kind: str, owner_id: str) -> DocumentState | None:
        raise NotImplementedError

    def entries(self, conversation_id: str) -> Sequence[str]:
        raise NotImplementedError

    def sequence_of(self, entry_id: str) -> int:
        raise NotImplementedError

    def next_sequence(self) -> int:
        raise NotImplementedError

    def add_conversation(self, record: ConversationRecord) -> None:
        raise NotImplementedError

    def add_entry(self, conversation_id: str, entry_id: str) -> None:
        raise NotImplementedError

    def copy_entries(
        self,
        from_conversation_id: str,
        to_conversation_id: str,
        fork_entry_id: str | None,
    ) -> None:
        raise NotImplementedError

    def ensure_document(
        self,
        kind: str,
        owner_id: str,
        fork: ForkPolicy,
        initial: Any = None,
    ) -> DocumentState:
        raise NotImplementedError

    def record_document_version(self, state: DocumentState, entry_id: str, value: Any) -> None:
        raise NotImplementedError

    def copy_document(self, source: DocumentState, to_owner: str, fork_entry_id: str | None) -> None:
        raise NotImplementedError

    def add_task(self, record: DurableTaskRecord) -> DurableTaskRecord:
        raise NotImplementedError

    def update_task(self, record: DurableTaskRecord) -> None:
        raise NotImplementedError


class InMemoryStoreProvider(DurableStoreProvider):
    """The ephemeral tier: state lives in the process and the filesystem is never touched."""

    def __init__(self) -> None:
        self._documents: dict[str, DocumentState] = {}
        self._tasks: dict[str, DurableTaskRecord] = {}
        self._conversations: dict[str, ConversationRecord] = {}
        # The entry state is shared with the JSONL provider, which diffs the committed
        # entry lists and records against the file at commit time.
        self._entries: dict[str, list[str]] = {}
        self._entry_records: dict[str, EntryRecord] = {}
        self._task_states: dict[str, TaskStateSnapshot] = {}
        self._sequence = 0
        self._undo: UndoLog | None = None

    @property
    def is_persistent(self) -> bool:
        return False

    def open(self, options: DurableStoreOptions) -> None:
        """Starts empty. The in-memory provider never touches the filesystem."""

    def close(self) -> None:
        """The in-memory provider holds nothing, so this is a no-op."""

    def begin_transaction(self) -> None:
        self._undo = UndoLog()

    def rollback(self) -> None:
        if self._undo is not None:
            self._undo.undo(self)
        self._undo = None

    def commit(self) -> None:
        self._undo = None
        self._persist()

    def _end_transaction(self) -> None:
        """Discards the undo log once the backend has accepted the write."""
        self._undo = None

    @property
    def sequence(self) -> int:
        return self._sequence

    @property
    def conversations(self) -> Sequence[ConversationRecord]:
        return list(self._conversations.values())

    @property
    def tasks(self) -> Sequence[DurableTaskRecord]:
        return list(self._tasks.values())

    @property
    def documents(self) -> Sequence[DocumentState]:
        return list(self._documents.values())

    def conversation(self, conversation_id: str) -> ConversationRecord | None:
        return self._conversations.get(conversation_id)

    def task(self, task_id: str) -> DurableTaskRecord | None:
        return self._tasks.get(task_id)

    def document(self, kind: str, owner_id: str) -> DocumentState | None:
        return self._documents.get(self.document_key(kind, owner_id))

    def entries(self, conversation_id: str) -> Sequence[str]:
        return self._entries.get(conversation_id, [])

    def sequence_of(self, entry_id: str) -> int:
        record = self._entry_records.get(entry_id)
        return record.sequence if record is not None else UNKNOWN_SEQUENCE

    def next_sequence(self) -> int:
        self._sequence += 1
        sequence = self._sequence
        if self._undo is not None:
            self._undo.sequence(lambda: setattr(self, "_sequence", sequence - 1))
        return sequence

    def add_conversation(self, record: ConversationRecord) -> None:
        if self._undo is not None:
            self._undo.remove(self._conversations, record.id)
        self._conversations[record.id] = record

    def add_entry(self, conversation_id: str, entry_id: str) -> None:
        entries = self._entries.get(conversation_id)
        if entries is None:
            entries = []
            self._entries[conversation_id] = entries
            if self._undo is not None:
                self._undo.remove(self._entries, conversation_id)

        if self._undo is not None:
            self._undo.append(entries)
        entries.append(entry_id)
        if self._undo is not None:
            self._undo.remove(self._entry_records, entry_id)
        self._entry_records[entry_id] = EntryRecord(
            id=entry_id,
            conversation_id=conversation_id,
            kind="entry",
            data=None,
            sequence=self.next_sequence(),
        )

    def copy_entries(
        self,
        from_conversation_id: str,
        to_conversation_id: str,
        fork_entry_id: str | None,
    ) -> None:
        """Copies the parent's entries up to and including the fork entry into the fork."""
        source = list(self.entries(from_conversation_id))
        if fork_entry_id is None:
            inherited = source
        else:
            inherited = []
            for entry in source:
                if entry == fork_entry_id:
                    break
                inherited.append(entry)
            inherited.append(fork_entry_id)

        if self._undo is not None:
            self._undo.remove(self._entries, to_conversation_id)
        self._entries[to_conversation_id] = inherited

    def ensure_document(
        self,
        kind: str,
        owner_id: str,
        fork: ForkPolicy,
        initial: Any = None,
    ) -> DocumentState:
        key = self.document_key(kind, owner_id)
        state = self._documents.get(key)
        if state is None:
            state = DocumentState(
                kind=kind,
                owner_id=owner_id,
                fork=fork,
                initial=DurableJson.clone(initial),
            )
            self._documents[key] = state
            if self._undo is not None:
                self._undo.remove(self._documents, key)
        return state

    def record_document_version(self, state: DocumentState, entry_id: str, value: Any) -> None:
        if self._undo is not None:
            self._undo.append(state.versions)
        state.versions.append(
            DocumentVersion(
                entry_id=entry_id,
                value=DurableJson.clone(value),
                sequence=self.next_sequence(),
            )
        )

    def copy_document(self, source: DocumentState, to_owner: str, fork_entry_id: str | None) -> None:
        """Copies a document into another owner, honouring its fork policy."""
        target = self.ensure_document(source.kind, to_owner, source.fork, source.initial)
        if source.fork is ForkPolicy.AS_OF:
            value = (
                source.latest
                if fork_entry_id is None
                else source.as_of_or_before(fork_entry_id, self.sequence_of)
            )
        elif source.fork is ForkPolicy.CURRENT:
            value = source.latest
        elif source.fork is ForkPolicy.INITIAL:
            value = source.initial
        else:
            value = None

        if value is not None:
            if self._undo is not None:
                self._undo.append(target.versions)
            target.versions.append(
                DocumentVersion(
                    entry_id=fork_entry_id if fork_entry_id is not None else "",
                    value=DurableJson.clone(value),
                    sequence=self.next_sequence(),
                )
            )

    def add_task(self, record: DurableTaskRecord) -> DurableTaskRecord:
        if self._undo is not None:
            self._undo.remove(self._tasks, record.id)
        self._tasks[record.id] = record
        self._task_states[record.id] = TaskStateSnapshot.capture(record)
        return record

    def update_task(self, record: DurableTaskRecord) -> None:
        """Records the current state of a task that was mutated in place."""
        current = TaskStateSnapshot.capture(record)
        previous = self._task_states.get(record.id)
        if self._undo is not None and previous is not None:
            self._undo.restore(previous, record)
        self._task_states[record.id] = current

    @staticmethod
    def document_key(kind: str, owner_id: str) -> str:
        """The key a document is stored under."""
        return f"{kind}@{owner_id}"

    def _persist(self) -> None:
        """Flushes the state to the backend. The in-memory provider has nothing to flush."""

    def _create_snapshot(self) -> StoreSnapshot:
        return StoreSnapshot(
            sequence=self._sequence,
            conversations=list(self._conversations.values()),
            tasks=list(self._tasks.values()),
            documents=[
                DocumentSnapshot(
                    kind=document.kind,
                    owner_id=document.owner_id,
                    fork=document.fork,
                    initial=DurableJson.clone(document.initial),
                    versions=[
                        DocumentVersionSnapshot(
                            entry_id=version.entry_id,
                            sequence=version.sequence,
                            value=DurableJson.clone(version.value),
                        )
                        for version in document.versions
                    ],
                )
                for document in self._documents.values()
            ],
            entries={key: list(value) for key, value in self._entries.items()},
            entry_records=list(self._entry_records.values()),
        )

    def _restore_snapshot(self, snapshot: StoreSnapshot) -> None:
        self._sequence = snapshot.sequence
        for conversation in snapshot.conversations:
            self._conversations[conversation.id] = conversation

        for task in snapshot.tasks:
            self._tasks[task.id] = task
            self._task_states[task.id] = TaskStateSnapshot.capture(task)

        for document in snapshot.documents:
            state = DocumentState(
                kind=document.kind,
                owner_id=document.owner_id,
                fork=document.fork,
                initial=DurableJson.clone(document.initial),
            )
            for version in document.versions:
                state.versions.append(
                    DocumentVersion(
                        entry_id=version.entry_id,
                        value=DurableJson.clone(version.value),
                        sequence=version.sequence,
                    )
                )
            self._documents[self.document_key(document.kind, document.owner_id)] = state

        for key, value in snapshot.entries.items():
            self._entries[key] = list(value)

        for record in snapshot.entry_records:
            self._entry_records[record.id] = record


class StoreFileLock:
    """The exclusive lock a persistent provider holds on its store file.

    The lock is a sidecar file next to the store -- ``{path}.lock`` -- held for the
    store's lifetime. On POSIX the lock is an ``flock`` on the sidecar, so the operating
    system releases it when the process exits, even after a crash. The sidecar carries
    the owning process id and start time so a refused open can say who holds the file.
    """

    def __init__(self, path: str, handle: Any) -> None:
        self._path = path
        self._handle = handle

    @property
    def path(self) -> str:
        return self._path

    @staticmethod
    def acquire(path: str) -> StoreFileLock:
        lock_path = f"{path}.lock"
        directory = os.path.dirname(lock_path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        handle = open(lock_path, "a+", encoding="utf-8")
        try:
            _lock_handle(handle)
        except OSError:
            handle.seek(0)
            holder = handle.read().strip() or "(unknown)"
            handle.close()
            raise RuntimeError(
                f"The durable store file '{path}' is already open in another process "
                f"({holder}). A store file supports a single writer at a time; close the "
                "other store before opening this one."
            ) from None

        handle.seek(0)
        handle.truncate()
        handle.write(f"pid={os.getpid()} started={time.strftime('%Y-%m-%dT%H:%M:%S%z')}")
        handle.flush()
        return StoreFileLock(path, handle)

    def release(self) -> None:
        if self._handle is None:
            return
        with contextlib.suppress(OSError):
            _unlock_handle(self._handle)
        self._handle.close()
        self._handle = None

    def __enter__(self) -> StoreFileLock:
        return self

    def __exit__(self, *_: object) -> None:
        self.release()


def _lock_handle(handle: Any) -> None:
    if os.name == "nt":  # pragma: no cover - platform branch
        import msvcrt

        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        return

    import fcntl

    fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)


def _unlock_handle(handle: Any) -> None:
    if os.name == "nt":  # pragma: no cover - platform branch
        import msvcrt

        handle.seek(0)
        msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
        return

    import fcntl

    fcntl.flock(handle.fileno(), fcntl.LOCK_UN)


class LegacyJsonStoreProvider(InMemoryStoreProvider):
    """The single-file JSON snapshot format the store used before the JSONL backend."""

    def __init__(self) -> None:
        super().__init__()
        self._path: str | None = None
        self._lock: StoreFileLock | None = None

    @property
    def is_persistent(self) -> bool:
        return True

    def open(self, options: DurableStoreOptions) -> None:
        self._path = options.path
        if self._path is None:
            return

        if os.path.exists(self._path) and DurableStore.detect_format(self._path) is DurableStoreFormat.JSONL:
            raise ValueError(
                f"The durable store file '{self._path}' holds JSONL records, not a legacy JSON "
                "snapshot. Open it with DurableStoreFormat.JSONL or DurableStoreFormat.AUTO "
                "instead of DurableStoreFormat.LEGACY_JSON."
            )

        self._lock = StoreFileLock.acquire(self._path)
        if os.path.exists(self._path):
            self._restore_snapshot(DurableStore.read_legacy_snapshot(self._path))

    def close(self) -> None:
        if self._lock is not None:
            self._lock.release()
        self._lock = None

    def _persist(self) -> None:
        if self._path is None:
            return

        snapshot = self._create_snapshot()
        directory = os.path.dirname(self._path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        temporary = f"{self._path}.tmp"
        with open(temporary, "w", encoding="utf-8") as handle:
            handle.write(DurableJson.dumps(snapshot))
        os.replace(temporary, self._path)


@dataclass(slots=True)
class _JsonlRecord:
    """One JSONL record: the envelope plus its payload."""

    v: int
    seq: int
    type: str
    payload: Any

    @staticmethod
    def for_payload(kind: str, seq: int, payload: Any) -> _JsonlRecord:
        return _JsonlRecord(v=1, seq=seq, type=kind, payload=payload)


class JsonlStoreProvider(InMemoryStoreProvider):
    """Persists state as JSONL: one JSON record per line, appended on every commit.

    A torn trailing line -- a partial write from a crash -- is discarded on open and the
    file is truncated back to the last complete record, so recovery is silent and
    non-fatal.
    """

    def __init__(self) -> None:
        super().__init__()
        self._path: str | None = None
        self._lock: StoreFileLock | None = None
        self._emitted_conversations: set[str] = set()
        self._emitted_tasks: set[str] = set()
        self._emitted_documents: set[str] = set()
        self._committed_entries: dict[str, list[str]] = {}
        self._committed_versions: dict[str, int] = {}
        self._committed_tasks: dict[str, str] = {}
        self._replace_on_commit = False

    @property
    def is_persistent(self) -> bool:
        return True

    def open(self, options: DurableStoreOptions) -> None:
        self._path = options.path
        if self._path is None:
            return

        self._lock = StoreFileLock.acquire(self._path)
        if not os.path.exists(self._path):
            return

        if DurableStore.detect_format(self._path) is DurableStoreFormat.LEGACY_JSON:
            # Convert on open, into memory only. The committed baseline stays empty so
            # the first commit emits the whole state as JSONL records.
            self._restore_snapshot(DurableStore.read_legacy_snapshot(self._path))
            self._replace_on_commit = True
            return

        self._replay(self._path)

    def close(self) -> None:
        if self._lock is not None:
            self._lock.release()
        self._lock = None

    def _persist(self) -> None:
        if self._path is None:
            return

        records = self._take_pending_records()
        if not records:
            return

        directory = os.path.dirname(self._path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        mode = "w" if self._replace_on_commit else "a"
        with open(self._path, mode, encoding="utf-8", newline="\n") as handle:
            for record in records:
                handle.write(DurableJson.dumps(record))
                handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        self._replace_on_commit = False

    def _replay(self, path: str) -> None:
        """Replays every complete record, truncating a torn tail back to the last boundary."""
        with open(path, "rb") as handle:
            data = handle.read()

        offset = 0
        truncate_after = 0
        while offset < len(data):
            newline = data.find(b"\n", offset)
            terminated = newline != -1
            if not terminated:
                break
            end = newline if terminated else len(data)
            raw = data[offset:end]
            line_end = end + 1 if terminated else len(data)

            record = _parse_jsonl_record(raw)
            if record is None:
                # A line that does not parse is a torn tail only when it is the final
                # one; anything earlier means the file is not JSONL.
                if terminated:
                    raise ValueError(
                        f"The file '{path}' is not a valid JSONL durable store: line at byte "
                        f"offset {offset} does not parse as a JSONL record."
                    )
                break

            self._apply(record)
            offset = line_end
            truncate_after = offset

        if os.path.getsize(path) != truncate_after:
            with open(path, "r+b") as handle:
                handle.truncate(truncate_after)

        self._remember_committed()

    def _apply(self, record: _JsonlRecord) -> None:
        self._sequence = max(self._sequence, record.seq)
        payload = record.payload
        if record.type == "conversation":
            self.add_conversation(_conversation_from_wire(payload))
        elif record.type == "entry":
            conversation_id = payload["conversationId"]
            entry_id = payload["entryId"]
            self._entries.setdefault(conversation_id, []).append(entry_id)
        elif record.type == "entry-record":
            entry = _entry_record_from_wire(payload)
            self._entry_records[entry.id] = entry
            self._sequence = max(self._sequence, entry.sequence)
        elif record.type == "document":
            # Older records predate the initial value; a missing property is not an error.
            self.ensure_document(
                payload["kind"],
                payload["ownerId"],
                _fork_policy_from_wire(payload["fork"]),
                payload.get("initial"),
            )
        elif record.type == "document-version":
            # A version carries the sequence RecordDocumentVersion minted for it, which is
            # not the envelope sequence: a commit can mint several sequences.
            state = self.ensure_document(
                payload["kind"],
                payload["ownerId"],
                _fork_policy_from_wire(payload["fork"]),
            )
            state.versions.append(
                DocumentVersion(
                    entry_id=payload["entryId"],
                    value=payload.get("value"),
                    sequence=payload.get("sequence", record.seq),
                )
            )
        elif record.type == "task":
            self.add_task(_task_from_wire(payload))

    def _take_pending_records(self) -> list[_JsonlRecord]:
        """Captures the records this transaction wrote, in write order.

        The base class wrote straight into its collections, so the writes are read back
        off the state and diffed against what the last commit already persisted.
        """
        records: list[_JsonlRecord] = []
        for conversation in self.conversations:
            if conversation.id in self._emitted_conversations:
                continue
            self._emitted_conversations.add(conversation.id)
            records.append(_JsonlRecord.for_payload("conversation", self.sequence, conversation))

        for conversation_id, entry_ids in self._entries.items():
            known_count = len(self._committed_entries.get(conversation_id, []))
            for entry_id in entry_ids[known_count:]:
                records.append(
                    _JsonlRecord.for_payload(
                        "entry",
                        self.sequence,
                        {"conversationId": conversation_id, "entryId": entry_id},
                    )
                )
                record = self._entry_records.get(entry_id)
                if record is not None:
                    records.append(_JsonlRecord.for_payload("entry-record", self.sequence, record))

        for document in self.documents:
            key = self.document_key(document.kind, document.owner_id)
            if key not in self._emitted_documents:
                self._emitted_documents.add(key)
                records.append(
                    _JsonlRecord.for_payload(
                        "document",
                        self.sequence,
                        {
                            "kind": document.kind,
                            "ownerId": document.owner_id,
                            "fork": document.fork,
                            "initial": document.initial,
                        },
                    )
                )

            committed = self._committed_versions.get(key, 0)
            for version in document.versions[committed:]:
                records.append(
                    _JsonlRecord.for_payload(
                        "document-version",
                        self.sequence,
                        {
                            "kind": document.kind,
                            "ownerId": document.owner_id,
                            "fork": document.fork,
                            "entryId": version.entry_id,
                            "sequence": version.sequence,
                            "value": version.value,
                        },
                    )
                )

        for task in self.tasks:
            current = DurableJson.dumps(task)
            already_emitted = task.id in self._emitted_tasks
            self._emitted_tasks.add(task.id)
            if already_emitted and self._committed_tasks.get(task.id) == current:
                continue
            self._committed_tasks[task.id] = current
            records.append(_JsonlRecord.for_payload("task", self.sequence, task))

        self._remember_committed()
        return records

    def _remember_committed(self) -> None:
        """Records the state the next commit diffs against."""
        self._emitted_conversations = {conversation.id for conversation in self.conversations}
        self._emitted_tasks = {task.id for task in self.tasks}
        self._committed_tasks = {task.id: DurableJson.dumps(task) for task in self.tasks}
        self._committed_entries = {key: list(value) for key, value in self._entries.items()}
        self._committed_versions = {
            self.document_key(document.kind, document.owner_id): len(document.versions)
            for document in self.documents
        }


def _parse_jsonl_record(raw: bytes) -> _JsonlRecord | None:
    try:
        parsed = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if not isinstance(parsed, dict):
        return None
    kind = parsed.get("type")
    if not isinstance(kind, str):
        return None
    return _JsonlRecord(
        v=int(parsed.get("v", 1)),
        seq=int(parsed.get("seq", 0)),
        type=kind,
        payload=parsed.get("payload") or {},
    )


def _fork_policy_from_wire(value: Any) -> ForkPolicy:
    if isinstance(value, str):
        return ForkPolicy[value.upper()]
    return ForkPolicy(int(value))


def _conversation_from_wire(payload: Mapping[str, Any]) -> ConversationRecord:
    return ConversationRecord(
        id=payload["id"],
        parent_conversation_id=payload.get("parentConversationId"),
        fork_entry_id=payload.get("forkEntryId"),
        owner_task_id=payload.get("ownerTaskId"),
        owner_conversation_id=payload.get("ownerConversationId"),
    )


def _entry_record_from_wire(payload: Mapping[str, Any]) -> EntryRecord:
    return EntryRecord(
        id=payload["id"],
        conversation_id=payload["conversationId"],
        kind=payload.get("kind", "entry"),
        data=payload.get("data"),
        sequence=int(payload["sequence"]),
    )


def _task_from_wire(payload: Mapping[str, Any]) -> DurableTaskRecord:
    outcome = payload.get("outcome")
    return DurableTaskRecord(
        id=payload["id"],
        name=payload["name"],
        status=TaskStatus(int(payload.get("status", 0))),
        checkpoint=payload.get("checkpoint"),
        outcome=(
            None
            if outcome is None
            else TaskOutcome(
                status=outcome.get("status", "pending"),
                error=outcome.get("error"),
                result=outcome.get("result"),
            )
        ),
        waiting_on=list(payload.get("waitingOn") or []),
        memos=dict(payload.get("memos") or {}),
        restarts=int(payload.get("restarts", 0)),
        owner_conversation_id=payload.get("ownerConversationId"),
        owner_task_id=payload.get("ownerTaskId"),
        background=bool(payload.get("background", False)),
        input=payload.get("input"),
    )


def _store_snapshot_from_wire(payload: Mapping[str, Any]) -> StoreSnapshot:
    """Reads the legacy single-file snapshot shape."""
    documents: list[DocumentSnapshot] = []
    for document in payload.get("documents") or []:
        documents.append(
            DocumentSnapshot(
                kind=document.get("kind", ""),
                owner_id=document.get("ownerId", ""),
                fork=_fork_policy_from_wire(document.get("fork", 0)),
                initial=document.get("initial"),
                versions=[
                    DocumentVersionSnapshot(
                        entry_id=version.get("entryId", ""),
                        sequence=int(version.get("sequence", 0)),
                        value=version.get("value"),
                    )
                    for version in document.get("versions") or []
                ],
            )
        )

    return StoreSnapshot(
        sequence=int(payload.get("sequence", 0)),
        conversations=[
            _conversation_from_wire(conversation)
            for conversation in payload.get("conversations") or []
        ],
        tasks=[_task_from_wire(task) for task in payload.get("tasks") or []],
        documents=documents,
        entries={
            key: list(value) for key, value in (payload.get("entries") or {}).items()
        },
        entry_records=[
            _entry_record_from_wire(record) for record in payload.get("entryRecords") or []
        ],
    )


def _takes_transaction(commit: Callable[..., Any]) -> bool:
    """Whether a task commit callback takes the transaction it may write through.

    The C# store has two ``CommitTaskAsync`` overloads, one taking a transaction and one
    not. Python has no overloads, so the arity of the callback picks the arm.
    """
    try:
        signature = inspect.signature(commit)
    except (TypeError, ValueError):
        return True

    return any(
        parameter.kind
        in (inspect.Parameter.POSITIONAL_ONLY, inspect.Parameter.POSITIONAL_OR_KEYWORD)
        for parameter in signature.parameters.values()
    )


class DurableStore:
    """The durable store: the single writer of a conversation's durable state.

    The store owns the commit gate; the provider owns the state and the undo log that
    protects it. Every write goes through :meth:`commit_async`, so a transaction is
    saved whole or not at all.
    """

    #: Provider factories registered by name, keyed case-insensitively.
    _PROVIDER_FACTORIES: dict[str, Callable[[DurableStoreOptions], DurableStoreProvider]] = {}
    _PROVIDER_FACTORIES_LOCK = threading.Lock()

    def __init__(self, path: str | None = None) -> None:
        self._provider: DurableStoreProvider = (
            InMemoryStoreProvider() if path is None else LegacyJsonStoreProvider()
        )
        self._path = path
        self._format = (
            DurableStoreFormat.IN_MEMORY if path is None else DurableStoreFormat.LEGACY_JSON
        )
        self._gate = asyncio.Lock()
        self._scheduler_gate = threading.Lock()
        self._scheduler_owner: object | None = None
        self._task_scheduler: Callable[[str, asyncio.Event | None], None] | None = None
        self._closed = False

        self._provider.open(DurableStoreOptions(path=path, format=self._format))

    # -- provider registry ---------------------------------------------------

    @staticmethod
    def register_provider(
        name: str,
        factory: Callable[[DurableStoreOptions], DurableStoreProvider],
    ) -> None:
        """Registers a provider factory under a name, so a store can be opened over it.

        Registration is process-wide: a host registers once at startup and thereafter
        selects the provider by name in the options it already passes to
        :meth:`create`. Registering the same name again replaces the factory.
        """
        if not name or not name.strip():
            raise ValueError("A provider name is required.")
        if factory is None:
            raise ValueError("A provider factory is required.")

        with DurableStore._PROVIDER_FACTORIES_LOCK:
            DurableStore._PROVIDER_FACTORIES[name.lower()] = factory

    @staticmethod
    def unregister_provider(name: str) -> bool:
        """Removes a registered provider factory. Returns whether one was registered."""
        if not name or not name.strip():
            raise ValueError("A provider name is required.")

        with DurableStore._PROVIDER_FACTORIES_LOCK:
            return DurableStore._PROVIDER_FACTORIES.pop(name.lower(), None) is not None

    @staticmethod
    def is_provider_registered(name: str) -> bool:
        """Whether a provider is registered under the name."""
        if not name or not name.strip():
            raise ValueError("A provider name is required.")

        with DurableStore._PROVIDER_FACTORIES_LOCK:
            return name.lower() in DurableStore._PROVIDER_FACTORIES

    # -- construction --------------------------------------------------------

    @staticmethod
    def create(options: DurableStoreOptions) -> DurableStore:
        """Creates a store over the provider the options select.

        When the caller asks for a persistent store and supplies no path, the per-user
        default path is used. The default options select the durable tier; the ephemeral
        tier is opt-in through :meth:`create_ephemeral` or an explicit
        :attr:`DurableStoreFormat.IN_MEMORY` format.
        """
        if options.path is None and options.format is not DurableStoreFormat.IN_MEMORY:
            options = DurableStoreOptions(
                path=DurableStore.default_store_path(options.file_name),
                format=options.format,
                file_name=options.file_name,
                provider=options.provider,
                provider_name=options.provider_name,
            )

        provider = DurableStore.select_provider(options)
        provider.open(options)
        return DurableStore._over(provider, options.path, options.format)

    @staticmethod
    def create_ephemeral() -> DurableStore:
        """Creates an ephemeral store: state lives in the process and is never persisted."""
        return DurableStore.create(DurableStoreOptions(format=DurableStoreFormat.IN_MEMORY))

    @staticmethod
    def select_provider(options: DurableStoreOptions) -> DurableStoreProvider:
        """Picks the provider the options select.

        An explicit factory wins, then a registered provider name, then the built-in
        formats -- so the seam only ever adds a way to name a provider, and every
        existing caller keeps the provider it had.
        """
        if options.provider is not None:
            return options.provider(options)

        if options.provider_name is not None:
            with DurableStore._PROVIDER_FACTORIES_LOCK:
                factory = DurableStore._PROVIDER_FACTORIES.get(options.provider_name.lower())
            if factory is None:
                raise ValueError(
                    f"No durable store provider is registered under the name "
                    f"'{options.provider_name}'. Register one with "
                    "DurableStore.register_provider before creating the store."
                )
            return factory(options)

        if options.format is DurableStoreFormat.IN_MEMORY:
            return InMemoryStoreProvider()
        if options.format is DurableStoreFormat.LEGACY_JSON:
            return LegacyJsonStoreProvider()
        if options.format is DurableStoreFormat.JSONL:
            return JsonlStoreProvider()
        return DurableStore.create_provider_for_auto(options.path)

    @staticmethod
    def create_provider_for_auto(path: str | None) -> DurableStoreProvider:
        """Picks the provider for :attr:`DurableStoreFormat.AUTO`.

        A file that does not exist yet is written in the canonical JSONL format; a file
        that does exist is read in the format it was written in.
        """
        if path is None:
            return InMemoryStoreProvider()

        return (
            JsonlStoreProvider()
            if DurableStore.detect_format(path) is DurableStoreFormat.JSONL
            else LegacyJsonStoreProvider()
        )

    @staticmethod
    def detect_format(path: str) -> DurableStoreFormat:
        """Detects the format of the file at the path.

        A file that does not exist, or that holds no content, is written in the canonical
        JSONL format. A file whose first non-empty line is a JSON object with a string
        ``type`` property is a JSONL record envelope; a file that parses as a single JSON
        object is the legacy single-file snapshot. Anything else raises ``ValueError``
        naming the path and the reason, so a corrupt file is reported instead of opening
        as an empty store.
        """
        if not os.path.exists(path):
            return DurableStoreFormat.JSONL

        first_content_line: str | None = None
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                if line.strip():
                    first_content_line = line
                    break

        if first_content_line is None:
            return DurableStoreFormat.JSONL

        try:
            parsed = json.loads(first_content_line)
            if isinstance(parsed, dict) and isinstance(parsed.get("type"), str):
                return DurableStoreFormat.JSONL
        except json.JSONDecodeError:
            # Not a JSONL record envelope; the whole-file check below decides whether it
            # is a legacy snapshot or not a durable store at all.
            pass

        try:
            whole = json.loads(Path(path).read_text(encoding="utf-8"))
            if isinstance(whole, dict):
                return DurableStoreFormat.LEGACY_JSON
        except json.JSONDecodeError as error:
            raise ValueError(
                f"The file '{path}' is not a durable store: it is neither JSONL records nor "
                f"a legacy JSON snapshot ({error})."
            ) from error

        raise ValueError(
            f"The file '{path}' is not a durable store: it is neither JSONL records nor a "
            "legacy JSON snapshot."
        )

    @staticmethod
    def read_legacy_snapshot(path: str) -> StoreSnapshot:
        """Reads the legacy single-file snapshot at the path."""
        try:
            text = Path(path).read_text(encoding="utf-8")
        except OSError as error:
            raise ValueError(
                f"The legacy durable store file '{path}' could not be read: {error}"
            ) from error

        try:
            payload = json.loads(text)
        except json.JSONDecodeError as error:
            raise ValueError(
                f"The legacy durable store file '{path}' is not a valid JSON snapshot: {error}"
            ) from error

        if not isinstance(payload, dict):
            raise ValueError(
                f"The legacy durable store file '{path}' is not a valid JSON snapshot: the "
                "file holds no JSON object."
            )

        return _store_snapshot_from_wire(payload)

    @staticmethod
    def default_store_path(file_name: str = "durable-store.jsonl") -> str:
        """Resolves the per-user default store path.

        ``LocalApplicationData`` on Windows; ``$XDG_STATE_HOME`` when set and non-empty,
        otherwise ``~/.local/state``, elsewhere. The store file lives in a ``durable``
        subdirectory under that root. The directory itself is created on first write,
        not by this method.
        """
        if sys.platform == "win32":
            root = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        else:
            xdg = os.environ.get("XDG_STATE_HOME")
            root = xdg if xdg else str(Path.home() / ".local" / "state")

        return os.path.join(root, "durable", file_name)

    @staticmethod
    def _over(
        provider: DurableStoreProvider,
        path: str | None,
        format: DurableStoreFormat,
    ) -> DurableStore:
        """Wraps an already-opened provider, which is how ``create`` finishes."""
        store = DurableStore.__new__(DurableStore)
        store._provider = provider
        store._path = path
        store._format = format
        store._gate = asyncio.Lock()
        store._scheduler_gate = threading.Lock()
        store._scheduler_owner = None
        store._task_scheduler = None
        store._closed = False
        return store

    # -- reads ---------------------------------------------------------------

    @property
    def is_persistent(self) -> bool:
        """Whether the store's state is persisted.

        The tier is a property of the provider behind the store, so this reports what the
        store actually does rather than what the caller asked for.
        """
        return self._provider.is_persistent

    @property
    def path(self) -> str | None:
        """The file the store persists to, when it persists."""
        return self._path

    @property
    def format(self) -> DurableStoreFormat:
        """The format the store was opened in."""
        return self._format

    @property
    def provider(self) -> DurableStoreProvider:
        """The provider behind this store.

        The harness reaches for it to write the records a transaction owns -- a fork's
        inherited entries, a copied document.
        """
        return self._provider

    @property
    def conversations(self) -> Sequence[ConversationRecord]:
        """The conversations known to this store."""
        return self._provider.conversations

    @property
    def tasks(self) -> Sequence[DurableTaskRecord]:
        """The tasks known to this store."""
        return self._provider.tasks

    @property
    def documents(self) -> Sequence[DocumentState]:
        """The documents known to this store."""
        return self._provider.documents

    def document(self, kind: str, owner_id: str) -> DocumentState | None:
        """Reads a document's state."""
        return self._provider.document(kind, owner_id)

    def task(self, task_id: str) -> DurableTaskRecord | None:
        """Reads a task record."""
        return self._provider.task(task_id)

    def conversation(self, conversation_id: str) -> ConversationRecord | None:
        """Reads a conversation record."""
        return self._provider.conversation(conversation_id)

    def entries(self, conversation_id: str) -> Sequence[str]:
        """Reads the entry ids of a conversation, oldest first."""
        return self._provider.entries(conversation_id)

    # -- transactions --------------------------------------------------------

    async def commit_async(
        self,
        body: Callable[[Transaction], Awaitable[T]],
    ) -> T:
        """Runs a transaction. Everything written inside is saved together, or discarded."""
        async with self._gate:
            transaction = Transaction(self)
            self._provider.begin_transaction()
            try:
                result = await body(transaction)
            except BaseException:
                # The transaction failed: undo everything it wrote, so the store is left
                # exactly as it was before the transaction started.
                self._provider.rollback()
                raise

            self._provider.commit()
            return result

    async def commit_task_async(
        self,
        task_id: str,
        commit: Callable[..., Any],
    ) -> TaskCommit:
        """Advances a task's durable state.

        The callback may write other records in the same transaction: everything it
        writes is saved together with the checkpoint, or discarded if it throws.
        """
        if _takes_transaction(commit):
            return await self._commit_task_in_transaction(task_id, commit)
        return await self._commit_task_direct(task_id, commit)

    async def _commit_task_in_transaction(
        self,
        task_id: str,
        commit: Callable[[Transaction], Awaitable[TaskCommit]],
    ) -> TaskCommit:
        async with self._gate:
            task = self._provider.task(task_id)
            if task is None:
                return TaskCommit.failed("unknown task")

            # A terminal task's outcome is final. A commit that arrives after the task
            # finished -- a durable timer waking up, say -- must not overwrite it.
            if task.status is TaskStatus.TERMINAL:
                return TaskCommit.completed(
                    task.outcome.result if task.outcome is not None else None
                )

            transaction = Transaction(self)
            self._provider.begin_transaction()
            try:
                next_commit = await commit(transaction)
            except BaseException:
                # The phase failed: undo everything it wrote, including the task's own
                # state, so the checkpoint stays where it was and the task can resume.
                self._provider.rollback()
                raise

            # The task's own state is part of the transaction: the checkpoint, the
            # status, and the outcome are written together with everything the phase
            # wrote, or none of it is.
            self._provider.update_task(task)
            self._apply_commit(task, next_commit)
            self._provider.update_task(task)
            self._provider.commit()
            return next_commit

    async def _commit_task_direct(
        self,
        task_id: str,
        commit: Callable[[], TaskCommit],
    ) -> TaskCommit:
        async with self._gate:
            task = self._provider.task(task_id)
            if task is None:
                return TaskCommit.failed("unknown task")

            if task.status is TaskStatus.TERMINAL:
                return TaskCommit.completed(
                    task.outcome.result if task.outcome is not None else None
                )

            next_commit = commit()
            if inspect.isawaitable(next_commit):
                next_commit = await next_commit

            self._apply_commit(task, next_commit)
            self._provider.update_task(task)
            self._provider.commit()
            return next_commit

    @staticmethod
    def _apply_commit(task: DurableTaskRecord, next_commit: TaskCommit) -> None:
        """Writes a commit's fields onto the task record."""
        task.status = next_commit.status
        if next_commit.checkpoint is not None:
            task.checkpoint = DurableJson.clone(next_commit.checkpoint)
        if next_commit.waiting_on:
            task.waiting_on = list(next_commit.waiting_on)
        if next_commit.outcome is not None:
            task.outcome = next_commit.outcome

    async def memo_async(self, task_id: str, key: str, value: Any = None) -> Any:
        """Reads or writes an idempotency memo.

        ``None`` reads, which is what the C# ``JsonElement?`` parameter means: a memo
        holds a JSON value, and a JSON null is not a memo worth writing.
        """
        async with self._gate:
            task = self._provider.task(task_id)
            if task is None:
                return None

            if value is not None:
                task.memos[key] = DurableJson.clone(value)
                # A memo is written outside any transaction, so the provider is told
                # about the task's new state directly.
                self._provider.update_task(task)
                self._provider.commit()
                return value

            return task.memos.get(key)

    async def sleep_until_async(
        self,
        task_id: str,
        deadline: float,
        next_checkpoint: Callable[[], TaskCommit],
        cancellation: asyncio.Event | None = None,
    ) -> TaskCommit:
        """Sleeps until a deadline, saving the wake time in the checkpoint."""
        delay = deadline - time.time()
        if delay > 0:
            await _sleep(delay, cancellation)

        return await self.commit_task_async(task_id, next_checkpoint)

    async def outcomes_async(self, task_ids: Sequence[str]) -> list[TaskOutcome]:
        """Reads the outcomes of child tasks.

        The gate may already be held by the transaction that is committing this task's
        checkpoint, so this reads without taking it.
        """
        outcomes: list[TaskOutcome] = []
        for task_id in task_ids:
            task = self._provider.task(task_id)
            if task is None:
                continue
            outcomes.append(task.outcome if task.outcome is not None else TaskOutcome.pending())
        return outcomes

    # -- task scheduler ------------------------------------------------------

    @property
    def task_scheduler(self) -> Callable[[str, asyncio.Event | None], None] | None:
        """The runner that starts a scheduled task, when a runtime has claimed the slot."""
        return self._task_scheduler

    def claim_task_scheduler(
        self,
        owner: object,
        scheduler: Callable[[str, asyncio.Event | None], None],
    ) -> None:
        """Claims the store's single task-runner slot for ``owner``.

        A store has at most one runner. The slot is claimed by the runtime that drives
        the store and released when that runtime is disposed, so two runtimes over one
        store cannot both schedule the same task: the second claim fails instead of
        silently replacing the first.
        """
        with self._scheduler_gate:
            if self._scheduler_owner is not None and self._scheduler_owner is not owner:
                raise RuntimeError(
                    "This store is already driven by another durable runtime. A store has "
                    "one task runner, so share the runtime instead of opening a second one "
                    "over the same store."
                )

            self._scheduler_owner = owner
            self._task_scheduler = scheduler

    def release_task_scheduler(self, owner: object) -> None:
        """Releases the task-runner slot, if ``owner`` still holds it."""
        with self._scheduler_gate:
            if self._scheduler_owner is not owner:
                return

            self._scheduler_owner = None
            self._task_scheduler = None

    def schedule_task(self, task_id: str, cancellation: asyncio.Event | None = None) -> None:
        """Schedules a task to run. The runtime owns the runner, so the store asks it."""
        scheduler = self._task_scheduler
        if scheduler is not None:
            scheduler(task_id, cancellation)

    # -- lifecycle -----------------------------------------------------------

    async def refresh_async(self) -> None:
        """Re-reads the store file into memory, discarding the in-memory state.

        A persistent store holds the file's exclusive lock for its lifetime, so it is the
        only writer and its memory is already current: for such a store this is a no-op.
        """
        async with self._gate:
            if self._provider.is_persistent:
                return

            self._provider.open(
                DurableStoreOptions(path=None, format=DurableStoreFormat.IN_MEMORY)
            )

    def close(self) -> None:
        """Releases the store's hold on its file.

        A persistent store keeps an exclusive lock on the file for its lifetime, so close
        the store before another process opens the same path.
        """
        if self._closed:
            return

        self._closed = True
        self._provider.close()

    def __enter__(self) -> DurableStore:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


class Transaction:
    """A transaction: the only way to write durable state."""

    def __init__(self, store: DurableStore) -> None:
        self._provider = store.provider

    def create_conversation(
        self,
        owner_task_id: str | None = None,
        owner_conversation_id: str | None = None,
        parent_conversation_id: str | None = None,
        fork_entry_id: str | None = None,
    ) -> ConversationRecord:
        """Creates a conversation and returns its record."""
        record = ConversationRecord(
            id=f"c{self._provider.next_sequence()}",
            parent_conversation_id=parent_conversation_id,
            fork_entry_id=fork_entry_id,
            owner_task_id=owner_task_id,
            owner_conversation_id=owner_conversation_id,
        )
        self._provider.add_conversation(record)
        return record

    def append_entry(self, conversation_id: str, kind: str, data: Any = None) -> str:
        """Appends an entry to a conversation and returns its id.

        ``kind`` and ``data`` are the caller's labels for the entry. The store's own
        record of an entry carries the conversation and the write sequence; the label
        lives with the document version the entry anchors, which is why it is not
        persisted here.
        """
        entry_id = f"e{self._provider.next_sequence()}"
        self._provider.add_entry(conversation_id, entry_id)
        return entry_id

    def doc(self, definition: DocumentDefinition[T], owner_id: str) -> DocumentDraft[T]:
        """Reads an editable copy of a document, creating it on first use."""
        state = self._ensure_document_with_initial(definition, owner_id)
        current = (
            definition.deserialize(DurableJson.clone(state.latest))
            if state.latest is not None
            else definition.initial_value()
        )
        return DocumentDraft(self, definition, owner_id, current)

    def _ensure_document_with_initial(
        self,
        definition: DocumentDefinition[T],
        owner_id: str,
    ) -> DocumentState:
        """Ensures the document exists, recording the definition's initial value.

        A :attr:`ForkPolicy.INITIAL` fork inherits that value, so it has to be captured
        while the definition is in hand.
        """
        return self._provider.ensure_document(
            definition.kind,
            owner_id,
            definition.fork,
            DurableJson.to_wire(definition.initial_value()),
        )

    def create_task(
        self,
        definition: DurableTaskDefinition,
        input: Any = None,
        owner_conversation_id: str | None = None,
        owner_task_id: str | None = None,
        background: bool = False,
    ) -> DurableTaskRecord:
        """Creates a task and returns its record."""
        record = DurableTaskRecord(
            id=f"t{self._provider.next_sequence()}",
            name=definition.name,
            input=DurableJson.clone(input),
            checkpoint=DurableJson.clone(definition.initial()),
            owner_conversation_id=owner_conversation_id,
            owner_task_id=owner_task_id,
            background=background,
        )
        self._provider.add_task(record)
        return record

    def commit_document(
        self,
        definition: DocumentDefinition[T],
        owner_id: str,
        value: T | None,
        entry_id: str,
    ) -> None:
        """Records a document version anchored to ``entry_id``."""
        state = self._ensure_document_with_initial(definition, owner_id)
        self._provider.record_document_version(
            state, entry_id, DurableJson.to_wire(value)
        )


class DocumentDraft(Generic[T]):
    """An editable copy of a document. Assigning to it stages a write."""

    def __init__(
        self,
        transaction: Transaction,
        definition: DocumentDefinition[T],
        owner_id: str,
        value: T | None,
    ) -> None:
        self._transaction = transaction
        self._definition = definition
        self._owner_id = owner_id
        self._value = value

    @property
    def value(self) -> T | None:
        """The current value."""
        return self._value

    @value.setter
    def value(self, value: T | None) -> None:
        self._value = value

    def save(self, entry_id: str) -> None:
        """Stages the current value to be written when the transaction commits."""
        self._transaction.commit_document(
            self._definition, self._owner_id, self._value, entry_id
        )


def _from_wire(cls: type[T], value: Any) -> T | None:
    """Reads a stored JSON value back into a dataclass.

    The store writes camelCase property names, so the reverse mapping is derived from the
    dataclass's own fields rather than hand-written per type.
    """
    if value is None:
        return None
    if not isinstance(value, Mapping):
        return None

    fields = {_camel(f.name): f for f in dataclasses.fields(cls)}  # type: ignore[arg-type]
    hints = _field_types(cls)
    kwargs: dict[str, Any] = {}
    for key, item in value.items():
        field = fields.get(key)
        if field is None:
            continue
        # The store writes enums as their numeric value, so a field typed as an enum is
        # rebuilt from the number rather than left as a bare int.
        if isinstance(item, int) and not isinstance(item, bool):
            annotation = hints.get(field.name)
            if isinstance(annotation, type) and issubclass(annotation, IntEnum):
                item = annotation(item)
        if isinstance(item, Mapping):
            annotation = hints.get(field.name)
            candidates = (annotation, *getattr(annotation, "__args__", ()))
            nested = next((candidate for candidate in candidates
                           if isinstance(candidate, type) and dataclasses.is_dataclass(candidate)), None)
            if nested is not None:
                item = _from_wire(nested, item)
        kwargs[field.name] = item
    return cls(**kwargs)  # type: ignore[call-arg]


@functools.cache
def _field_types(cls: type[Any]) -> dict[str, Any]:
    """A dataclass's field annotations, resolved past ``from __future__ import annotations``."""
    try:
        return get_type_hints(cls)
    except Exception:
        return {field.name: field.type for field in dataclasses.fields(cls)}  # type: ignore[arg-type]


# ---------------------------------------------------------------------------
# Extensions
# ---------------------------------------------------------------------------


@dataclass(slots=True)
class ToolWrapper:
    """A wrapper that decorates whichever tool with a given name wins.

    A wrapper never captures a base tool: it names the tool it decorates, so whichever
    implementation of that name wins is the one that gets wrapped.
    """

    tool_name: str
    wrap: Callable[[Any], Any]


@dataclass(slots=True)
class PromptSection:
    """A named prompt section rendered into the system message."""

    key: str
    render: Callable[["SectionInput"], Awaitable[str | None]]
    tag: bool = True


@dataclass(slots=True)
class SectionInput:
    """The input available to a prompt section renderer."""

    conversation: "DurableConversation | None"
    tools: Sequence[Any]
    extensions: Sequence["Extension"]
    working_directory: str | None


@dataclass(slots=True)
class Extension:
    """One extension: the tools, prompt sections, tasks, and wrappers it contributes."""

    name: str
    tools: list[Any] = field(default_factory=list)
    sections: list[PromptSection] = field(default_factory=list)
    tasks: list[DurableTaskDefinition] = field(default_factory=list)
    wraps: list[ToolWrapper] = field(default_factory=list)


class ExtensionRegistry:
    """The registry of extensions. Installing the same name replaces the extension in place."""

    def __init__(self) -> None:
        self._extensions: dict[str, Extension] = {}

    @property
    def extensions(self) -> list[Extension]:
        """The installed extensions, in installation order."""
        return list(self._extensions.values())

    def install(self, extension: Extension) -> None:
        """Installs an extension, replacing any extension with the same name.

        Replacement keeps the original position, so installing a new version of an
        extension does not reorder the prompt sections it contributes.
        """
        self._extensions[extension.name] = extension

    def uninstall(self, name: str) -> None:
        """Removes an extension by name."""
        self._extensions.pop(name, None)

    def find(self, name: str) -> Extension | None:
        """Finds an extension by name."""
        return self._extensions.get(name)

    def find_task(self, name: str) -> DurableTaskDefinition | None:
        """Finds a task definition by name across all installed extensions."""
        for extension in self._extensions.values():
            for task in extension.tasks:
                if task.name == name:
                    return task
        return None


# ---------------------------------------------------------------------------
# Permissions
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class PermissionGrant:
    """A declared permission grant: a request kind, optionally narrowed to a scope.

    Grants are written as ``kind`` or ``kind:scope`` -- for example
    ``custom-tool:read_file``, ``read:/workspace``, ``shell:git status``, or
    ``url:https://example.com``. A grant with no scope allows every request of that kind;
    ``*`` in either position is a wildcard.

    A scope is a prefix, but only at a boundary: ``read:/tmp/foo`` grants
    ``/tmp/foo/bar`` and not ``/tmp/foobar``, and ``url:https://example.com`` does not
    grant ``https://example.com.evil.test``.
    """

    kind: str
    scope: str | None = None

    @staticmethod
    def parse(text: str) -> "PermissionGrant":
        """Parses the ``kind`` or ``kind:scope`` form."""
        separator = text.find(":")
        if separator < 0:
            return PermissionGrant(text.strip())
        return PermissionGrant(text[:separator].strip(), text[separator + 1 :].strip())

    def __str__(self) -> str:
        return self.kind if self.scope is None else f"{self.kind}:{self.scope}"


class PermissionPolicy:
    """The fail-closed permission policy: a request is denied unless a declared grant covers it.

    The Copilot SDK's own default approves every tool call, shell command, file write, and
    URL fetch the model asks for. That is the wrong default for a durable agent, whose
    whole point is that its behaviour is reproducible from stored state: an approval that
    is not written down is not reproducible. So a durable conversation starts with no
    grants at all and denies everything it was not explicitly given.
    """

    #: The request kinds the policy knows how to describe, mapped to the attribute that
    #: carries the scope. A kind absent from this table is described by its own ``kind``
    #: with no scope, which is what the C# default arm does.
    _SCOPE_ATTRIBUTES: Final[Mapping[str, tuple[str, ...]]] = {
        "custom-tool": ("tool_name",),
        "read": ("resolved_path", "path"),
        "write": ("resolved_path", "file_name"),
        "shell": ("full_command_text",),
        "url": ("url",),
        "hook": ("tool_name",),
        "workflow": ("name",),
        "extension-management": ("extension_name",),
        "extension-permission-access": ("extension_name",),
        "extension-env-access": ("extension_name",),
    }

    @staticmethod
    def describe(request: Any) -> tuple[str, str | None]:
        """Describes a request as a grant kind and an optional scope."""
        kind = getattr(request, "kind", None)
        if not isinstance(kind, str):
            raise TypeError("A permission request must carry a string 'kind'.")

        if kind == "mcp":
            server = getattr(request, "server_name", None)
            tool = getattr(request, "tool_name", None)
            return ("mcp", f"{server}/{tool}")

        if kind == "memory":
            return ("memory", None)

        for attribute in PermissionPolicy._SCOPE_ATTRIBUTES.get(kind, ()):
            value = getattr(request, attribute, None)
            if value is not None:
                return (kind, value)

        return (kind, None)

    @staticmethod
    def is_granted(grants: Sequence[str] | None, kind: str, scope: str | None) -> bool:
        """Whether a declared grant set covers a request."""
        if grants is None:
            return False

        for text in grants:
            grant = PermissionGrant.parse(text)
            if grant.kind != "*" and grant.kind.lower() != kind.lower():
                continue

            if grant.scope is None or grant.scope in ("*", ""):
                return True

            if scope is not None and PermissionPolicy._scope_matches(grant.scope, scope):
                return True

        return False

    @staticmethod
    def _scope_matches(grant: str, scope: str) -> bool:
        if grant == scope:
            return True

        if not scope.startswith(grant):
            return False

        # A prefix only counts at a boundary, so "/tmp/foo" does not grant "/tmp/foobar".
        return scope[len(grant)] in "/\\ :;,"

    @staticmethod
    def handler(
        grants: Sequence[str] | None,
    ) -> Callable[[Any, Any], Awaitable[Any]]:
        """Builds the permission handler for a declared grant set.

        The handler returns a decision object with ``approved`` and ``feedback``, which is
        the shape the SDK's approve-once and reject decisions carry.
        """

        async def decide(request: Any, _invocation: Any = None) -> Any:
            kind, scope = PermissionPolicy.describe(request)
            if PermissionPolicy.is_granted(grants, kind, scope):
                return PermissionDecisionApproveOnce()

            target = kind if scope is None else f"{kind} ({scope})"
            return PermissionDecisionReject(
                feedback=(
                    f"denied by durable policy: no grant for {target}. "
                    f'Add "{kind}" or "{kind}:{scope if scope is not None else "*"}" to the '
                    "conversation's Permissions to allow it."
                ),
            )

        return decide


@dataclass(frozen=True, slots=True)
class PermissionDecision:
    """The decision a permission handler returns.

    Denial carries feedback rather than a bare refusal: the SDK's other denial variants
    serialize to wire values the runtime's permission host does not accept, so they fail
    the tool call with a malformed-payload error instead of a clean denial.
    """

    approved: bool
    feedback: str | None = None


# ---------------------------------------------------------------------------
# Sandbox
# ---------------------------------------------------------------------------


@dataclass(slots=True)
class DurableSandboxAuth:
    """GitHub and git credential forwarding for a sandboxed session."""

    gh: bool | None = None
    git: bool | None = None


@dataclass(slots=True)
class DurableSandboxCredentials:
    """Credentials injected into a sandboxed session.

    A value is a list of HTTPS hostnames or ``*.example.com`` patterns; a bare ``*`` is
    not accepted, and an empty list is not a valid injection.
    """

    env_vars: dict[str, list[str]] | None = None


@dataclass(slots=True)
class DurableSandboxFilesystem:
    """The filesystem policy of a sandbox."""

    clear_policy_on_exit: bool | None = None
    denied_paths: list[str] | None = None
    readonly_paths: list[str] | None = None
    readwrite_paths: list[str] | None = None


@dataclass(slots=True)
class DurableSandboxProxy:
    """The proxy a sandboxed network is routed through."""

    url: str | None = None
    username: str | None = None
    password: str | None = None


@dataclass(slots=True)
class DurableSandboxNetwork:
    """The network policy of a sandbox."""

    allowed_hosts: list[str] | None = None
    blocked_hosts: list[str] | None = None
    allow_outbound: bool | None = None
    allow_local_network: bool | None = None
    proxy: DurableSandboxProxy | None = None


@dataclass(slots=True)
class DurableSandboxPolicy:
    """The durable sandbox policy for a conversation.

    This is the durable layer's own shape, deliberately not the SDK's experimental wire
    type. The wire type is what the runtime accepts today; the stored type is what a
    conversation keeps meaning. A field the SDK renames or removes cannot change what a
    stored conversation means, and a mapping that drops a field is a visible bug in this
    file rather than a silent change in the store.

    ``None`` on :attr:`AgentChoices.sandbox` means "no sandbox": the session is created
    with no sandbox configuration at all.

    Paths in :attr:`filesystem` may contain the :attr:`WORKSPACE_TOKEN` token
    (``{workspace}``). The token is expanded when the session is built, against the
    conversation's working directory, so a fork keeps the policy's shape but not the
    parent's absolute grants.
    """

    #: The token that stands for the conversation's working directory.
    WORKSPACE_TOKEN: ClassVar[str] = "{workspace}"

    enabled: bool = False
    add_current_working_directory: bool | None = None
    allow_bypass: bool | None = None
    allow_dev_tool_access: bool | None = None
    sandbox_mcp_servers: bool | None = None
    sandbox_lsp_servers: bool | None = None
    trust_working_directory: bool | None = None
    auth: DurableSandboxAuth | None = None
    credentials: DurableSandboxCredentials | None = None
    filesystem: DurableSandboxFilesystem | None = None
    network: DurableSandboxNetwork | None = None
    keychain_access: bool | None = None

    @staticmethod
    def expand_path(path: str, working_directory: str | None) -> str:
        """Expands the workspace token in a policy path against a working directory.

        A path without the token is returned unchanged, so an absolute grant stays
        absolute.
        """
        if DurableSandboxPolicy.WORKSPACE_TOKEN not in path:
            return path

        return path.replace(
            DurableSandboxPolicy.WORKSPACE_TOKEN,
            working_directory if working_directory is not None else os.getcwd(),
        )


def validate_sandbox_policy(policy: DurableSandboxPolicy) -> None:
    """Refuses a stored sandbox policy that would be a silent downgrade.

    A policy that asks for one thing and a session that gets another is a silent
    downgrade, and the store would go on claiming the protection the session does not
    have. Every rule here exists to refuse that rather than run it.
    """
    # A disabled sandbox enforces nothing, so a policy that disables it while still naming
    # restrictions describes a session that would run unsandboxed while the store claims
    # otherwise.
    if not policy.enabled and (
        policy.auth is not None
        or policy.credentials is not None
        or policy.filesystem is not None
        or policy.network is not None
        or policy.keychain_access is not None
    ):
        raise DurableSandboxPolicyException(
            "the policy disables the sandbox but still names credential, filesystem, "
            "network, or keychain restrictions, which a disabled sandbox cannot enforce. "
            "Enable the sandbox, or clear the restrictions to opt out deliberately.",
            policy,
        )

    env_vars = policy.credentials.env_vars if policy.credentials is not None else None
    if env_vars is None:
        return

    for key, hosts in env_vars.items():
        if not key or not key.strip():
            raise DurableSandboxPolicyException(
                "a credential is listed under an empty environment variable name.",
                policy,
            )

        if not hosts:
            raise DurableSandboxPolicyException(
                f"the credential '{key}' names no inject hosts, so it would be injected "
                "nowhere. Name at least one HTTPS host or host pattern.",
                policy,
            )

        for host in hosts:
            if not host or not host.strip():
                raise DurableSandboxPolicyException(
                    f"the credential '{key}' names an empty inject host.",
                    policy,
                )

            if host.strip() == "*":
                raise DurableSandboxPolicyException(
                    f"the credential '{key}' names a bare '*' inject host, which would "
                    "send the value to every host. Name the hosts it may reach.",
                    policy,
                )


def expand_paths(paths: Sequence[str] | None, working_directory: str | None) -> list[str]:
    """Expands the workspace token in every path of a policy list."""
    if paths is None:
        return []
    return [DurableSandboxPolicy.expand_path(path, working_directory) for path in paths]


# ---------------------------------------------------------------------------
# Agent choices
# ---------------------------------------------------------------------------


@dataclass(slots=True)
class AgentChoices:
    """The per-conversation agent choices. Stored by name, so they outlive the code."""

    extensions: list[str] | None = None
    tools: list[str] | None = None
    model: str | None = None
    reasoning_effort: str | None = None
    working_directory: str | None = None
    instructions: str | None = None
    streaming: bool | None = None
    infinite_sessions: Any = None
    permissions: list[str] | None = None
    sandbox: DurableSandboxPolicy | None = None
    #: Session hooks. Hooks are code-level configuration, so they are not persisted with
    #: the document; they are supplied when the conversation is created.
    hooks: Any = field(default=None, metadata={"json_ignore": True})


@dataclass(frozen=True, slots=True)
class ResolvedAgent:
    """The resolved agent: stored choices bound to live registry code."""

    model: str | None
    reasoning_effort: str | None
    working_directory: str | None
    instructions: str | None
    streaming: bool | None
    hooks: Any
    infinite_sessions: Any
    extensions: Sequence[Extension]
    tools: Sequence[Any]
    permissions: Sequence[str] | None
    sandbox: DurableSandboxPolicy | None


@dataclass(frozen=True, slots=True)
class DurableModelReport:
    """The model a runtime reported for a session or for one turn of it.

    This is the model the runtime *reported*, not the model the host asked for. A runtime
    that substitutes a model reports the substitute here, so the substitution is visible
    rather than hidden behind the request.

    When the runtime reports no model at all, :attr:`model` is ``None`` and
    :attr:`has_model` is ``False``. There is no fallback to the harness default: "the
    runtime said nothing" and "the runtime said the default" are different facts.
    """

    model: str | None = None
    source: str | None = None

    @property
    def has_model(self) -> bool:
        """Whether the runtime reported a model."""
        return bool(self.model)

    @property
    def required_model(self) -> str:
        """The reported model, or an error when the runtime reported none."""
        if not self.has_model:
            raise RuntimeError(
                "The runtime reported no model. Check the runtime's transcript: a "
                "deterministic runtime only reports a model when its recording carries one."
            )
        return self.model  # type: ignore[return-value]

    @staticmethod
    def from_event(model: str | None, source: str) -> "DurableModelReport":
        """Creates a report from a runtime event's model value, or the not-reported one."""
        if model is None or not model.strip():
            return DurableModelReport()
        return DurableModelReport(model, source)


#: The report for a runtime that has not reported a model.
NOT_REPORTED: Final[DurableModelReport] = DurableModelReport()


# ---------------------------------------------------------------------------
# Document definitions
# ---------------------------------------------------------------------------

AGENT_DOCUMENT: Final[DocumentDefinition[AgentChoices]] = DocumentDefinition(
    "durable.agent", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, AgentChoices,
    AgentChoices,
)
FORK_DOCUMENT: Final[DocumentDefinition[ForkIntent]] = DocumentDefinition(
    "durable.fork", DocumentHistory.REWINDABLE, ForkPolicy.INITIAL, ForkIntent,
    ForkIntent,
)
SESSION_BINDING_DOCUMENT: Final[DocumentDefinition[SessionBinding]] = DocumentDefinition(
    "durable.session-binding", DocumentHistory.REWINDABLE, ForkPolicy.INITIAL, SessionBinding,
    SessionBinding,
)
ADMISSION_DOCUMENT: Final[DocumentDefinition[InputAdmission]] = DocumentDefinition(
    "durable.admission", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, InputAdmission,
    InputAdmission,
)
CHECKPOINT_DOCUMENT: Final[DocumentDefinition[ConversationCheckpoint]] = DocumentDefinition(
    "durable.checkpoint", DocumentHistory.REWINDABLE, ForkPolicy.AS_OF, ConversationCheckpoint,
    ConversationCheckpoint,
)

#: The document kinds the durable layer writes for its own bookkeeping. An entry anchored
#: only by these is not part of the conversation's user-visible transcript.
INTERNAL_DOCUMENT_KINDS: Final[frozenset[str]] = frozenset(
    {
        AGENT_DOCUMENT.kind,
        FORK_DOCUMENT.kind,
        SESSION_BINDING_DOCUMENT.kind,
        ADMISSION_DOCUMENT.kind,
        CHECKPOINT_DOCUMENT.kind,
    }
)


# ---------------------------------------------------------------------------
# Conversations
# ---------------------------------------------------------------------------


class DurableConversation:
    """A conversation handle: durable identity plus the live session serving it.

    The handle exists before the session does, so prompt sections can read the
    conversation's durable documents while the session configuration is being built.
    """

    def __init__(
        self,
        store: DurableStore,
        record: ConversationRecord,
        registry: ExtensionRegistry | None = None,
        session: Any = None,
    ) -> None:
        self._store = store
        self._registry = registry if registry is not None else ExtensionRegistry()
        self._session = session
        self._answers: list[str] = []
        self._sandbox_decisions: list[Any] = []
        self._sandbox_waiters: list[tuple[Callable[[Any], bool], asyncio.Future[Any]]] = []
        self._turn_models: list[DurableModelReport] = []
        self._model_waiters: list[asyncio.Future[DurableModelReport]] = []
        self._busy = False
        self._unsubscribe: Callable[[], None] | None = None
        self._submit_gate = asyncio.Lock()
        self._pending_submissions = 0
        self._harness: DurableHarness | None = None
        self._hooks: Any = None
        self._effective_model = NOT_REPORTED
        self._pass_open = False
        self._checkpoint_chain: asyncio.Task[None] | None = None
        self._checkpoint_failure: BaseException | None = None
        self._tool_calls: dict[str, Any] = {}
        self.record = record

    # -- identity ------------------------------------------------------------

    @property
    def id(self) -> str:
        """The conversation identifier."""
        return self.record.id

    @property
    def session(self) -> Any:
        """The underlying session, or ``None`` before one is attached."""
        return self._session

    @property
    def session_id(self) -> str | None:
        """The session identifier, or ``None`` before one is attached."""
        if self._session is None:
            return None
        return getattr(self._session, "session_id", None)

    @property
    def answers(self) -> Sequence[str]:
        """The assistant's answers in this conversation, oldest first.

        A turn that calls tools produces one answer per pass, so a review loop shows every
        pass.
        """
        return list(self._answers)

    @property
    def busy(self) -> bool:
        """Whether the conversation has a run in flight, from an input until its final answer."""
        return self._busy or self._pending_submissions > 0

    @property
    def sandbox_decisions(self) -> Sequence[Any]:
        """The sandbox decisions this conversation has observed, oldest first.

        The list is in-memory: a decision is an observation about a live session, not
        durable state, and a restored conversation starts with an empty list.
        """
        return list(self._sandbox_decisions)

    @property
    def turn_models(self) -> Sequence[DurableModelReport]:
        """The model the runtime reported for each completed turn, oldest first."""
        return list(self._turn_models)

    # -- session attachment --------------------------------------------------

    def attach(self, session: Any) -> None:
        """Attaches the session serving this conversation."""
        if self._unsubscribe is not None:
            self._unsubscribe()
        self._session = session
        self._unsubscribe = session.on(self.observe)

    def detach(self) -> None:
        """Detaches the session, so an event arriving during disposal cannot write."""
        if self._unsubscribe is not None:
            self._unsubscribe()
            self._unsubscribe = None
        self._session = None

    def set_busy(self, busy: bool) -> None:
        """Tracks whether a run is in flight, so a caller can tell busy from idle."""
        self._busy = busy

    def observe(self, event: Any) -> None:
        """Records what one session event says about this conversation.

        Kept separate from the subscription so the observation rules can be exercised
        without a live session.
        """
        kind = _event_kind(event)
        data = _event_data(event)
        if kind == "tool.execution_start":
            self._tool_calls[_event_field(data, "tool_call_id")] = data
        elif kind == "tool.execution_complete":
            tool_call_id = _event_field(data, "tool_call_id")
            started = self._tool_calls.pop(tool_call_id, None)
            result = _event_field(data, "result")
            error = _event_field(data, "error")
            checkpoint = ConversationCheckpoint(
                checkpoint_id=f"c{uuid4().hex}", kind="tool", event_id=f"tool:{tool_call_id}",
                tool_name=_event_field(started, "tool_name"),
                tool_args=json.dumps(_event_field(started, "arguments")),
                success=_event_field(data, "success"), content=_event_field(result, "content"),
                error=_event_field(error, "message"), recorded_at=datetime.now(timezone.utc).isoformat(),
            )
            previous = self._checkpoint_chain
            async def write() -> None:
                if previous is not None:
                    await previous
                try:
                    async def body(tx: Transaction) -> None:
                        draft = tx.doc(CHECKPOINT_DOCUMENT, self.id)
                        if draft.value is not None and draft.value.event_id == checkpoint.event_id:
                            return
                        draft.value = checkpoint
                        draft.save(tx.append_entry(self.id, "checkpoint"))
                    await self.commit_async(body)
                except BaseException as error:
                    self._checkpoint_failure = self._checkpoint_failure or error
            self._checkpoint_chain = asyncio.create_task(write())

        if kind == "assistant.message":
            content = _event_field(data, "content")
            if isinstance(content, str) and content:
                self.record_answer(content)
            # The answer ends the pass being recorded, so the next model report starts a
            # new turn instead of refining this one.
            if not self._pass_open:
                self._turn_models.append(NOT_REPORTED)
            self._pass_open = False

        if kind == "sandbox.decision":
            self.record_sandbox_decision(event)

        model = None
        source = None
        if kind == "session.start":
            model, source = _event_field(data, "selected_model"), "session"
        elif kind == "session.model_change":
            model, source = _event_field(data, "new_model"), "session"
        elif kind in ("assistant.turn_start", "assistant.usage"):
            model = _event_field(data, "model")
            source = "usage" if kind == "assistant.usage" else "turn"
            report = DurableModelReport.from_event(model, source)
            if self._pass_open:
                if report.has_model:
                    self._turn_models[-1] = report
            else:
                self._turn_models.append(report)
                self._pass_open = True
        if source is not None and isinstance(model, str) and model.strip():
            report = DurableModelReport.from_event(model, source)
            self._effective_model = report
            if self._harness is not None:
                self._harness._effective_model = report
            self._release_model_waiters(report)

    def record_answer(self, answer: str) -> None:
        """Records an assistant answer."""
        self._answers.append(answer)

    def record_sandbox_decision(self, decision: Any) -> None:
        """Records a sandbox decision and releases every waiter it satisfies."""
        self._sandbox_decisions.append(decision)
        matched: list[tuple[Callable[[Any], bool], asyncio.Future[Any]]] = []
        for waiter in list(self._sandbox_waiters):
            match, future = waiter
            try:
                is_match = match(decision)
            except Exception:
                is_match = False
            if is_match:
                matched.append(waiter)
                self._sandbox_waiters.remove(waiter)

        for _, future in matched:
            if not future.done():
                future.set_result(decision)

    def _release_model_waiters(self, report: DurableModelReport) -> None:
        for future in list(self._model_waiters):
            if not future.done():
                future.set_result(report)
        self._model_waiters.clear()

    async def wait_for_sandbox_decision(
        self,
        match: Callable[[Any], bool],
        timeout: float = 30.0,
    ) -> Any:
        """Waits for a sandbox decision that matches a predicate.

        A decision that already arrived satisfies the wait immediately: the caller may be
        looking at a turn that has already finished.
        """
        for observed in self._sandbox_decisions:
            if match(observed):
                return observed

        future: asyncio.Future[Any] = asyncio.get_running_loop().create_future()
        self._sandbox_waiters.append((match, future))
        try:
            return await asyncio.wait_for(future, timeout)
        except asyncio.TimeoutError:
            self._sandbox_waiters = [
                waiter for waiter in self._sandbox_waiters if waiter[1] is not future
            ]
            observed = ", ".join(repr(decision) for decision in self._sandbox_decisions)
            raise TimeoutError(
                f"No sandbox decision matched within {timeout:g}s. Observed decisions: "
                f"{observed if observed else '(none)'}."
            ) from None

    # -- durable reads -------------------------------------------------------

    def snapshot(self, definition: DocumentDefinition[T]) -> T | None:
        """Reads the latest value of a document."""
        return definition.deserialize(self._raw_snapshot(definition))

    def snapshot_as_of(self, definition: DocumentDefinition[T], entry_id: str) -> T | None:
        """Reads the value a document had in the same transaction as the given entry."""
        state = self._store.document(definition.kind, self.id)
        if state is None:
            return None
        return definition.deserialize(state.as_of(entry_id))

    def binding(self) -> SessionBinding | None:
        """The binding recorded for this conversation, or ``None`` before one is written."""
        return _from_wire(SessionBinding, self._raw_snapshot(SESSION_BINDING_DOCUMENT))

    def admission(self) -> InputAdmission | None:
        """The most recent input admission, or ``None`` before one is written."""
        return _from_wire(InputAdmission, self._raw_snapshot(ADMISSION_DOCUMENT))

    def checkpoint(self) -> ConversationCheckpoint | None:
        """The most recent checkpoint, or ``None`` before one is written."""
        return _from_wire(ConversationCheckpoint, self._raw_snapshot(CHECKPOINT_DOCUMENT))

    def _raw_snapshot(self, definition: DocumentDefinition[Any]) -> Any:
        state = self._store.document(definition.kind, self.id)
        return None if state is None else state.latest

    def entries(self, limit: int = 20) -> Sequence[str]:
        """The conversation's transcript, newest first.

        The durable layer's own bookkeeping entries are left out: they anchor internal
        documents, not conversation.
        """
        entries = self._store.entries(self.id)
        bookkeeping = bookkeeping_entries(self._store, self.id, entries)
        return [entry for entry in reversed(entries) if entry not in bookkeeping][:limit]

    def choices(self) -> AgentChoices | None:
        """The agent choices this conversation was last configured with."""
        choices = self.snapshot(AGENT_DOCUMENT)
        if choices is not None:
            choices.hooks = self._hooks
        return choices

    @property
    def effective_model(self) -> DurableModelReport:
        return self._effective_model

    def agent(self) -> ResolvedAgent:
        if self._harness is None:
            raise RuntimeError("Conversation has no SDK-bound harness.")
        choices = self.choices() or AgentChoices()
        extensions = self._harness.resolve_extensions(choices.extensions)
        return ResolvedAgent(
            choices.model, choices.reasoning_effort, choices.working_directory,
            choices.instructions, choices.streaming, choices.hooks, choices.infinite_sessions,
            extensions, self._harness.resolve_tools(extensions, choices.tools),
            choices.permissions, choices.sandbox,
        )

    async def wait_for_effective_model_async(self, timeout: float = 30.0) -> DurableModelReport:
        if self._effective_model.has_model:
            return self._effective_model
        future: asyncio.Future[DurableModelReport] = asyncio.get_running_loop().create_future()
        self._model_waiters.append(future)
        try:
            return await asyncio.wait_for(future, timeout)
        finally:
            if future in self._model_waiters:
                self._model_waiters.remove(future)

    async def configure_async(self, choices: AgentChoices) -> None:
        self._hooks = choices.hooks
        async def body(tx: Transaction) -> None:
            draft = tx.doc(AGENT_DOCUMENT, self.id)
            draft.value = choices
            draft.save(tx.append_entry(self.id, "agent"))
        await self.commit_async(body)
        if self._session is not None:
            await self._session.set_tools(list(self.agent().tools))

    async def context_async(self) -> list[Any]:
        if self._session is None:
            raise RuntimeError(f"Conversation '{self.id}' has no attached SDK session.")
        return await self._session.get_events()

    async def checkpoint_barrier_async(self) -> None:
        if self._checkpoint_chain is not None:
            await self._checkpoint_chain
        if self._checkpoint_failure is not None:
            raise RuntimeError(f"Conversation '{self.id}' failed to persist a tool checkpoint.") from self._checkpoint_failure

    async def abort_async(self) -> None:
        if self._session is None:
            raise RuntimeError(f"Conversation '{self.id}' has no attached SDK session.")
        await self._session.abort()

    async def fork_async(self, entry_id: str) -> "DurableConversation":
        if self._harness is None:
            raise RuntimeError("Conversation has no SDK-bound harness.")
        return await self._harness.fork_async(self, entry_id)

    async def submit_async(self, prompt: str, timeout: float = 180.0) -> Any:
        if self._session is None:
            raise RuntimeError(f"Conversation '{self.id}' has no attached SDK session.")
        self._pending_submissions += 1
        try:
            async with self._submit_gate:
                admission_id = f"a{uuid4().hex}"
                async def admit(tx: Transaction) -> None:
                    draft = tx.doc(ADMISSION_DOCUMENT, self.id)
                    draft.value = InputAdmission(admission_id=admission_id, prompt=prompt,
                                                 state="admitted", admitted_at=datetime.now(timezone.utc).isoformat())
                    draft.save(tx.append_entry(self.id, "admission"))
                await self.commit_async(admit)
                try:
                    completion: asyncio.Future[Any] = asyncio.get_running_loop().create_future()
                    last: Any = None
                    def observe(event: Any) -> None:
                        nonlocal last
                        if _event_field(event, "agent_id"):
                            return
                        kind = _event_kind(event)
                        if kind == "assistant.message":
                            last = event
                        elif kind == "session.idle" and not completion.done():
                            completion.set_result(last)
                        elif kind == "session.error" and not completion.done():
                            completion.set_exception(RuntimeError(_event_field(_event_data(event), "message")))
                    stop = self._session.on(observe)
                    try:
                        await self._session.send(prompt)
                        answer = await asyncio.wait_for(completion, timeout)
                    finally:
                        stop()
                    await self.checkpoint_barrier_async()
                    async def complete(tx: Transaction) -> None:
                        if answer is not None:
                            data = _event_data(answer)
                            checkpoint = tx.doc(CHECKPOINT_DOCUMENT, self.id)
                            checkpoint.value = ConversationCheckpoint(
                                checkpoint_id=f"c{uuid4().hex}", kind="turn",
                                event_id=str(_event_field(answer, "id")),
                                message_id=_event_field(data, "message_id"),
                                content=_event_field(data, "content"),
                                recorded_at=datetime.now(timezone.utc).isoformat(),
                            )
                            checkpoint.save(tx.append_entry(self.id, "checkpoint"))
                        draft = tx.doc(ADMISSION_DOCUMENT, self.id)
                        assert draft.value is not None
                        draft.value.state = "completed"
                        draft.save(tx.append_entry(self.id, "admission"))
                    await self.commit_async(complete)
                    return answer
                except BaseException:
                    async def uncertain(tx: Transaction) -> None:
                        draft = tx.doc(ADMISSION_DOCUMENT, self.id)
                        assert draft.value is not None
                        draft.value.state = "uncertain"
                        draft.save(tx.append_entry(self.id, "admission"))
                    await self.commit_async(uncertain)
                    raise
        finally:
            self._pending_submissions -= 1

    # -- durable writes ------------------------------------------------------

    async def commit_async(self, body: Callable[[Transaction], Awaitable[T]]) -> T:
        """Runs a transaction against the durable store."""
        return await self._store.commit_async(body)

    async def append_entry_async(self, kind: str, data: str | None = None) -> str:
        """Appends an entry to this conversation's transcript."""

        async def body(tx: Transaction) -> str:
            return tx.append_entry(self.id, kind, data)

        return await self.commit_async(body)

    async def bind_async(self, session_id: str | None, model: str | None) -> None:
        """Writes the binding that records which native session serves this conversation.

        The binding is written in one transaction with the entry that records it, so the
        conversation's transcript and the session it names are never observed apart.
        Writing the same binding twice is a no-op, so a re-attach does not grow the log.
        """

        async def body(tx: Transaction) -> None:
            current = tx.doc(SESSION_BINDING_DOCUMENT, self.id)
            existing = current.value
            if isinstance(existing, SessionBinding) and existing.session_id == session_id:
                return

            current.value = SessionBinding(
                session_id=session_id,
                model=model,
                bound_at=datetime.now(timezone.utc).isoformat(),
            )
            current.save(tx.append_entry(self.id, "session-binding", session_id))

        await self.commit_async(body)


# ---------------------------------------------------------------------------
# Harness
# ---------------------------------------------------------------------------


@dataclass(slots=True)
class DurableHarnessOptions:
    """Host configuration for the sessions a harness creates.

    None of this is durable state: it is not persisted with a conversation, so it never
    travels with a fork.
    """

    #: The name of the environment variable that selects the runtime executable when
    #: :attr:`runtime_path` is not set. Its value is a path, exactly like
    #: :attr:`runtime_path`.
    RUNTIME_PATH_ENVIRONMENT_VARIABLE: ClassVar[str] = "DURABLE_RUNTIME_PATH"

    #: The permission handler installed on every session the harness creates, or ``None``
    #: to keep the durable default, which is fail-closed against the stored grants.
    permission_handler: Callable[[Any, Any], Awaitable[Any]] | None = None

    #: Whether repository-origin file hooks run for the sessions this harness creates, or
    #: ``None`` to leave the SDK default.
    enable_file_hooks: bool | None = None

    #: Whether a turn that fails only because a host hook was denied by the sandbox is
    #: reported as an answer instead of an exception. The default is ``False``: a session
    #: error is an error.
    tolerate_host_hook_failures: bool = False

    #: The runtime executable the harness connects to over stdio, or ``None`` to consult
    #: :attr:`RUNTIME_PATH_ENVIRONMENT_VARIABLE` and then spawn the bundled runtime.
    runtime_path: str | None = None

    #: Arguments passed to the runtime executable named by :attr:`runtime_path`.
    runtime_arguments: list[str] = field(default_factory=list)


def resolve_runtime_path(options: DurableHarnessOptions) -> str | None:
    """The runtime executable the options select.

    The explicit path wins, then the environment variable, then ``None`` for the bundled
    runtime. A named path that does not exist is an error that names it, so the caller is
    never left guessing why the open failed.
    """
    path = options.runtime_path
    origin = "DurableHarnessOptions.runtime_path"
    if path is None or not path.strip():
        path = os.environ.get(DurableHarnessOptions.RUNTIME_PATH_ENVIRONMENT_VARIABLE)
        origin = (
            f"the {DurableHarnessOptions.RUNTIME_PATH_ENVIRONMENT_VARIABLE} environment variable"
        )

    if path is None or not path.strip():
        return None

    if not os.path.isfile(path):
        raise FileNotFoundError(
            f"The runtime executable named by {origin} does not exist: '{path}'."
        )

    return path


class DurableHarness:
    """The durable harness: a store, a registry, and the conversations over them.

    The harness is the front door for a host that already has a store. It owns the client
    it is given and never the store, because the store's lifetime -- including the
    exclusive writer lock a persistent store holds -- stays with the caller.
    """

    #: The model used when a conversation does not name one. It is a cheap,
    #: reasoning-capable model: the examples are about durable mechanics, not model
    #: quality, and a fixed model makes an example's behaviour reproducible.
    DEFAULT_MODEL: Final[str] = "gpt-6-luna"

    def __init__(
        self,
        client: Any,
        store: DurableStore,
        registry: ExtensionRegistry,
        options: DurableHarnessOptions | None = None,
    ) -> None:
        self._client = client
        self._store = store
        self._registry = registry
        self._options = options if options is not None else DurableHarnessOptions()
        self._conversations: dict[str, DurableConversation] = {}
        self._effective_model = NOT_REPORTED
        self._running_tasks: set[str] = set()
        self._task_completions: dict[str, asyncio.Future[TaskOutcome]] = {}
        self._in_flight: dict[str, asyncio.Task[TaskOutcome]] = {}
        self._closed = False
        self._store.claim_task_scheduler(self, self._start_task)

    @property
    def client(self) -> Any:
        """The client the harness drives."""
        return self._client

    @property
    def store(self) -> DurableStore:
        """The durable store."""
        return self._store

    @property
    def registry(self) -> ExtensionRegistry:
        """The extension registry."""
        return self._registry

    @property
    def options(self) -> DurableHarnessOptions:
        """The options this harness was opened with."""
        return self._options

    @property
    def agent_document(self) -> DocumentDefinition[AgentChoices]:
        """The document holding per-conversation agent choices."""
        return AGENT_DOCUMENT

    @property
    def conversations(self) -> Sequence[DurableConversation]:
        """The conversations this harness knows about."""
        return list(self._conversations.values())

    @property
    def effective_model(self) -> DurableModelReport:
        """The model the runtime reported for the most recent session or turn."""
        return self._effective_model

    def find_conversation(self, conversation_id: str) -> DurableConversation | None:
        """Finds a conversation by id, restoring it from the store if not yet opened."""
        existing = self._conversations.get(conversation_id)
        if existing is not None:
            return existing

        record = self._store.conversation(conversation_id)
        if record is None:
            return None

        conversation = DurableConversation(self._store, record, self._registry)
        conversation._harness = self
        self._conversations[conversation_id] = conversation
        return conversation

    @staticmethod
    async def open_async(
        store: DurableStore,
        registry: ExtensionRegistry,
        options: DurableHarnessOptions | None = None,
        client_factory: Callable[[str | None, Sequence[str]], Awaitable[Any]] | None = None,
    ) -> "DurableHarness":
        """Opens a harness over a store and a registry.

        The runtime executable is resolved first -- the explicit path, else
        ``DURABLE_RUNTIME_PATH``, else ``None`` for the bundled runtime -- so a path that
        names no existing file fails the open before any process starts.

        ``client_factory`` receives the resolved runtime path and the runtime arguments and
        returns a started client. The Python SDK binding is not part of this port, so the
        factory is the seam a host supplies; without one the open fails rather than
        silently running against a different runtime than the one recorded.
        """
        resolved = options if options is not None else DurableHarnessOptions()
        runtime_path = resolve_runtime_path(resolved)

        if client_factory is not None:
            client = await client_factory(runtime_path, resolved.runtime_arguments)
        else:
            client = CopilotClient(connection=RuntimeConnection.for_stdio(
                path=runtime_path, args=resolved.runtime_arguments))
            await client.start()
        try:
            return DurableHarness(client, store, registry, resolved)
        except BaseException:
            await client.stop()
            raise

    async def create_conversation_async(
        self,
        choices: AgentChoices | None = None,
        owner_task_id: str | None = None,
        owner_conversation_id: str | None = None,
    ) -> DurableConversation:
        """Creates a conversation and its session."""
        selected = choices if choices is not None else AgentChoices()

        async def body(tx: Transaction) -> ConversationRecord:
            record = tx.create_conversation(
                owner_task_id=owner_task_id,
                owner_conversation_id=owner_conversation_id,
            )
            draft = tx.doc(AGENT_DOCUMENT, record.id)
            draft.value = selected
            draft.save(tx.append_entry(record.id, "agent", record.id))
            return record

        record = await self._store.commit_async(body)
        conversation = DurableConversation(self._store, record, self._registry)
        conversation._harness = self
        conversation._hooks = selected.hooks
        self._conversations[record.id] = conversation
        conversation.attach(await self._create_pinned_session_async(conversation, selected))
        for event in await conversation.context_async():
            conversation.observe(event)
        return conversation

    async def root_async(self, choices: AgentChoices | None = None) -> DurableConversation:
        """Returns the root conversation, creating it on first use."""
        for record in self._store.conversations:
            if (record.parent_conversation_id is None and record.owner_task_id is None
                    and record.owner_conversation_id is None):
                existing = self.find_conversation(record.id)
                if existing is not None:
                    return await self.resume_conversation_async(existing.id)

        return await self.create_conversation_async(choices)

    async def resume_conversation_async(self, conversation_id: str) -> DurableConversation:
        conversation = self.find_conversation(conversation_id)
        if conversation is None:
            raise ValueError(f"Unknown conversation '{conversation_id}'.")
        if conversation.session is not None:
            return conversation
        admission = conversation.admission()
        if admission is not None and admission.state != "completed":
            raise DurableRecoveryRequiredException(conversation_id, conversation.session_id)
        binding = conversation.binding()
        if binding is None or binding.session_id is None:
            session = await self._create_pinned_session_async(conversation, conversation.choices())
        else:
            config = await self.build_session_config(conversation.choices(), conversation)
            config.pop("trust_working_directory", None)
            session = await self._client.resume_session(
                binding.session_id, **config, suppress_resume_event=True, continue_pending_work=False)
        conversation.attach(session)
        for event in await conversation.context_async():
            conversation.observe(event)
        return conversation

    def resolve_extensions(self, names: Sequence[str] | None) -> list[Extension]:
        return self._registry.extensions if names is None else [
            extension for name in names if (extension := self._registry.find(name)) is not None]

    def resolve_tools(self, extensions: Sequence[Extension], names: Sequence[str] | None) -> list[Any]:
        tools: dict[str, Any] = {}
        for extension in extensions:
            for tool in extension.tools:
                name = tool["name"] if isinstance(tool, Mapping) else tool.name
                tools[name] = tool
        for extension in extensions:
            for wrapper in extension.wraps:
                if wrapper.tool_name in tools:
                    tools[wrapper.tool_name] = wrapper.wrap(tools[wrapper.tool_name])
        return list(tools.values()) if names is None else [tools[name] for name in names if name in tools]

    async def build_system_message(
        self, agent: ResolvedAgent, sections: Sequence[PromptSection],
        conversation: DurableConversation | None,
    ) -> str:
        parts: list[str] = []
        for section in sections:
            text = await section.render(SectionInput(conversation, agent.tools, agent.extensions, agent.working_directory))
            if text and text.strip():
                parts.append(f"<{section.key}>\n{text}\n</{section.key}>" if section.tag else text)
        if agent.instructions and agent.instructions.strip():
            parts.append(f"<instructions>\n{agent.instructions}\n</instructions>")
        return "\n\n".join(parts)

    async def render_sections_async(self, conversation: DurableConversation) -> str:
        agent = conversation.agent()
        return await self.build_system_message(agent, [
            section for extension in agent.extensions for section in extension.sections], conversation)

    @staticmethod
    def build_sandbox_config(policy: DurableSandboxPolicy, working_directory: str | None = None) -> SandboxConfig:
        validate_sandbox_policy(policy)
        wire = DurableJson.to_wire(policy)
        for name in ("trustWorkingDirectory", "filesystem", "network", "keychainAccess", "credentials"):
            wire.pop(name, None)
        user_policy: dict[str, Any] = {}
        if policy.filesystem is not None:
            user_policy["filesystem"] = {
                **DurableJson.to_wire(policy.filesystem),
                "deniedPaths": expand_paths(policy.filesystem.denied_paths, working_directory),
                "readonlyPaths": expand_paths(policy.filesystem.readonly_paths, working_directory),
                "readwritePaths": expand_paths(policy.filesystem.readwrite_paths, working_directory),
            }
        if policy.network is not None:
            network = DurableJson.to_wire(policy.network)
            if policy.network.proxy is not None and policy.network.proxy.url is None:
                network.pop("proxy", None)
            network.setdefault("allowedHosts", [])
            network.setdefault("blockedHosts", [])
            user_policy["network"] = network
        if policy.keychain_access is not None:
            user_policy["seatbelt"] = {"keychainAccess": policy.keychain_access}
        if user_policy:
            wire["userPolicy"] = user_policy
        if policy.credentials is not None and policy.credentials.env_vars is not None:
            wire["credentials"] = {"envVars": {
                name: {"injectHosts": list(hosts)} for name, hosts in policy.credentials.env_vars.items()}}
        return SandboxConfig.from_dict(wire)

    async def build_session_config(
        self, choices: AgentChoices | None = None, conversation: DurableConversation | None = None,
    ) -> dict[str, Any]:
        choices = choices or AgentChoices()
        extensions = self.resolve_extensions(choices.extensions)
        tools = self.resolve_tools(extensions, choices.tools)
        agent = ResolvedAgent(choices.model, choices.reasoning_effort, choices.working_directory,
            choices.instructions, choices.streaming, choices.hooks, choices.infinite_sessions,
            extensions, tools, choices.permissions, choices.sandbox)
        hooks = dict(choices.hooks or {})
        config: dict[str, Any] = {
            "model": choices.model or self.DEFAULT_MODEL,
            "tools": tools, "hooks": hooks,
            "on_permission_request": self._options.permission_handler or PermissionPolicy.handler(choices.permissions),
        }
        for name in ("working_directory", "streaming", "infinite_sessions"):
            value = getattr(choices, name)
            if value is not None:
                config[name] = value
        if self._options.enable_file_hooks is not None:
            config["enable_file_hooks"] = self._options.enable_file_hooks
        if choices.reasoning_effort is not None and choices.model not in (None, "auto"):
            models = await self._client.list_models()
            model = next((m for m in models if m.id == config["model"]), None)
            if model is not None and model.supported_reasoning_efforts:
                config["reasoning_effort"] = choices.reasoning_effort
        sections = [section for extension in extensions for section in extension.sections]
        system_message = await self.build_system_message(agent, sections, conversation)
        if system_message:
            config["system_message"] = {"mode": "append", "content": system_message}
        if sections and conversation is not None:
            original = hooks.get("on_user_prompt_submitted")
            async def on_prompt(input: Any, invocation: Any) -> Any:
                output = await original(input, invocation) if original else None
                output = dict(output or {})
                rendered = await self.render_sections_async(conversation)
                output["additionalContext"] = "\n\n".join(
                    part for part in (output.get("additionalContext"), rendered) if part)
                return output
            hooks["on_user_prompt_submitted"] = on_prompt
        if choices.sandbox is not None:
            config["sandbox_config"] = self.build_sandbox_config(choices.sandbox, choices.working_directory)
            if choices.sandbox.trust_working_directory is not None:
                config["trust_working_directory"] = "user" if choices.sandbox.trust_working_directory else "session"
        return config

    def task_graph(self) -> list[tuple[DurableTaskRecord, str | None]]:
        return [(task, task.owner_task_id or task.owner_conversation_id) for task in self._store.tasks]

    async def resume_async(self, cancellation: asyncio.Event | None = None) -> list[TaskOutcome]:
        return await asyncio.gather(*(self.run_task_async(task.id, cancellation) for task in self._store.tasks
            if task.status in (TaskStatus.RUNNING, TaskStatus.WAITING)))

    # -- forks ---------------------------------------------------------------

    def choices_as_of(self, conversation_id: str, entry_id: str) -> AgentChoices | None:
        """The agent choices a conversation had at an entry, not the ones it has now."""
        state = self._store.document(AGENT_DOCUMENT.kind, conversation_id)
        if state is None:
            return None
        value = state.as_of_or_before(entry_id, self._store.provider.sequence_of)
        return None if value is None else _from_wire(AgentChoices, value)

    def fork_intent_of(self, conversation_id: str) -> ForkIntent | None:
        """A conversation's fork intent, or ``None`` when it is not a fork."""
        state = self._store.document(FORK_DOCUMENT.kind, conversation_id)
        if state is None or state.latest is None:
            return None
        return _from_wire(ForkIntent, state.latest)

    async def fork_async(
        self,
        parent: DurableConversation,
        entry_id: str,
    ) -> DurableConversation:
        """Forks a conversation at an entry.

        The durable half of the fork -- the conversation record, the inherited entries,
        the copied documents, and the fork intent -- commits before the native fork is
        attempted, so a crash between the two is detectable rather than silent.
        """
        if parent is None:
            raise ValueError("A parent conversation is required.")

        if entry_id not in self._store.entries(parent.id):
            raise ValueError(f"'{entry_id}' is not an entry of conversation '{parent.id}'.")

        # The choices the parent had at the boundary, not the ones it has now.
        choices = self.choices_as_of(parent.id, entry_id)

        async def body(tx: Transaction) -> ConversationRecord:
            created = tx.create_conversation(
                parent_conversation_id=parent.id,
                fork_entry_id=entry_id,
            )
            # The fork inherits the parent's transcript up to the entry it was taken at.
            self._store.provider.copy_entries(parent.id, created.id, entry_id)
            # Materialize first: copying a document adds to the same collection being
            # enumerated.
            for document in [d for d in self._store.documents if d.owner_id == parent.id]:
                self._store.provider.copy_document(document, created.id, entry_id)

            # Durable before the native fork, so a crash between the two is detectable.
            intent = tx.doc(FORK_DOCUMENT, created.id)
            intent.value = ForkIntent(
                parent_conversation_id=parent.id,
                fork_entry_id=entry_id,
                state=ForkIntentState.PENDING,
            )
            intent.save(tx.append_entry(created.id, "fork"))
            return created

        record = await self._store.commit_async(body)

        conversation = DurableConversation(self._store, record, self._registry)
        conversation._harness = self
        session = await self._fork_session_async(parent, conversation, choices)
        conversation.attach(session)
        # A fork names its own session: the binding document is ForkPolicy.INITIAL, so a
        # fork never inherits its parent's session id, and this write records the new one.
        parent_choices = parent.choices()
        model = parent_choices.model if parent_choices is not None else None
        await conversation.bind_async(
            getattr(session, "session_id", None),
            model if model is not None else self.DEFAULT_MODEL,
        )
        self._conversations[record.id] = conversation
        return conversation

    async def _fork_session_async(
        self,
        parent: DurableConversation,
        conversation: DurableConversation,
        choices: AgentChoices | None,
    ) -> Any:
        """Gives a fork its native session.

        A runtime that can fork natively is asked to; one that cannot continues as a new
        session over the inherited transcript rather than failing outright, because the
        durable half of the fork is already committed. The session is then recorded on the
        intent so a later open can find it again.
        """
        if parent.session_id is None:
            raise DurableRecoveryRequiredException(parent.id, None)
        forked = await self._client.rpc.sessions.fork(SessionsForkRequest(session_id=parent.session_id))
        config = await self.build_session_config(choices, conversation)
        config.pop("trust_working_directory", None)
        session = await self._client.resume_session(
            forked.session_id, **config, suppress_resume_event=True, continue_pending_work=False)

        async def body(tx: Transaction) -> None:
            intent = tx.doc(FORK_DOCUMENT, conversation.id)
            current = intent.value if isinstance(intent.value, ForkIntent) else ForkIntent()
            current.session_id = getattr(session, "session_id", None)
            current.state = ForkIntentState.COMPLETED
            intent.value = current
            intent.save(tx.append_entry(conversation.id, "fork"))

        await self._store.commit_async(body)
        return session

    async def _create_pinned_session_async(
        self,
        conversation: DurableConversation,
        choices: AgentChoices | None,
    ) -> Any:
        """Creates a new session over a fork's inherited transcript."""
        binding = conversation.binding()
        if binding is None or not binding.pinned or not binding.session_id:
            async def pin(tx: Transaction) -> None:
                draft = tx.doc(SESSION_BINDING_DOCUMENT, conversation.id)
                draft.value = SessionBinding(session_id=f"s{uuid4().hex}", pinned=True,
                    model=choices.model if choices else self.DEFAULT_MODEL,
                    bound_at=datetime.now(timezone.utc).isoformat())
                draft.save(tx.append_entry(conversation.id, "session-binding"))
            await self._store.commit_async(pin)
            binding = conversation.binding()
        if binding is None or binding.session_id is None:
            raise DurableRecoveryRequiredException(conversation.id, None)
        config = await self.build_session_config(choices, conversation)
        return await self._client.create_session(session_id=binding.session_id, **config)

    def _session_config(
        self,
        conversation: DurableConversation,
        choices: AgentChoices | None,
    ) -> Any:
        """The session configuration a fork's session is created or resumed with."""
        raise RuntimeError("Use await build_session_config for the SDK keyword arguments.")

    async def abandon_fork_async(self, conversation_id: str) -> None:
        """Marks a fork abandoned, so it is never given a session."""

        async def body(tx: Transaction) -> None:
            intent = tx.doc(FORK_DOCUMENT, conversation_id)
            current = intent.value if isinstance(intent.value, ForkIntent) else ForkIntent()
            current.state = ForkIntentState.ABANDONED
            intent.value = current
            intent.save(tx.append_entry(conversation_id, "fork"))

        await self._store.commit_async(body)

    # -- tasks ---------------------------------------------------------------

    async def create_task_async(
        self,
        definition: DurableTaskDefinition,
        input: Any = None,
        owner_conversation_id: str | None = None,
        owner_task_id: str | None = None,
        background: bool = False,
        cancellation: asyncio.Event | None = None,
    ) -> DurableTaskRecord:
        """Creates a durable task and schedules it."""

        async def body(tx: Transaction) -> DurableTaskRecord:
            return tx.create_task(
                definition,
                input,
                owner_conversation_id=owner_conversation_id,
                owner_task_id=owner_task_id,
                background=background,
            )

        record = await self._store.commit_async(body)
        self._start_task(record.id, cancellation)
        return record

    def _start_task(self, task_id: str, cancellation: asyncio.Event | None) -> None:
        """Starts a task run without waiting for it, keeping a handle to drain on close."""
        run = asyncio.ensure_future(self.run_task_async(task_id, cancellation))
        self._in_flight[task_id] = run
        run.add_done_callback(lambda _: self._in_flight.pop(task_id, None))

    async def run_task_async(
        self,
        task_id: str,
        cancellation: asyncio.Event | None = None,
    ) -> TaskOutcome:
        """Runs a task to completion, resuming from its last checkpoint."""
        completion = self._task_completions.get(task_id)
        if completion is None:
            completion = asyncio.get_running_loop().create_future()
            self._task_completions[task_id] = completion

        record = self._store.task(task_id)
        if record is None:
            missing = TaskOutcome.failed("unknown task")
            if not completion.done():
                completion.set_result(missing)
            return missing

        # A task runs once at a time. A second caller joins the run in flight.
        if task_id in self._running_tasks:
            return await asyncio.shield(completion)

        self._running_tasks.add(task_id)
        try:
            return await self._run_task_core_async(task_id, record, completion, cancellation)
        finally:
            self._running_tasks.discard(task_id)
            self._in_flight.pop(task_id, None)

    async def _run_task_core_async(
        self,
        task_id: str,
        record: DurableTaskRecord,
        completion: asyncio.Future[TaskOutcome],
        cancellation: asyncio.Event | None,
    ) -> TaskOutcome:
        if record.status is TaskStatus.TERMINAL:
            existing = record.outcome if record.outcome is not None else TaskOutcome.completed()
            if not completion.done():
                completion.set_result(existing)
            return existing

        definition = self._registry.find_task(record.name)
        if definition is None:
            unknown = TaskOutcome.failed(f"unknown task definition {record.name}")
            if not completion.done():
                completion.set_result(unknown)
            return unknown

        if record.status is TaskStatus.RUNNING:
            record.restarts += 1
            # The restart count is part of the task's durable state, so the provider is
            # told about it before the transaction commits.
            self._store.provider.update_task(record)

            async def noop(tx: Transaction) -> None:
                return None

            await self._store.commit_async(noop)

        runtime = TaskRuntime(self._store, record, definition, cancellation)
        while cancellation is None or not cancellation.is_set():
            # The task may have been aborted while it was waiting on its children. An
            # abort is durable: the record is terminal, so the run stops here instead of
            # carrying on to the next phase.
            current = self._store.task(task_id)
            if current is not None and current.status is TaskStatus.TERMINAL:
                break

            checkpoint = record.checkpoint
            phase = "run"
            if isinstance(checkpoint, Mapping):
                raw = checkpoint.get("phase")
                if isinstance(raw, str):
                    phase = raw

            handler = definition.phases.get(phase)
            if handler is None:
                await self._store.commit_task_async(
                    task_id, lambda: TaskCommit.failed(f"unknown phase {phase}")
                )
                break

            try:
                commit = await handler(runtime)
            except asyncio.CancelledError:
                # The task was interrupted. Its last checkpoint stays in place, so a
                # later run continues from there.
                break
            except Exception as exception:
                await self._store.commit_task_async(
                    task_id, lambda: TaskCommit.failed(str(exception))
                )
                break

            if commit.outcome is not None:
                if not completion.done():
                    completion.set_result(commit.outcome)
                break

            # A task that is waiting on child tasks holds no invocation: it is resumed
            # when its children finish.
            if commit.status is TaskStatus.WAITING:
                await self._wait_for_children_async(task_id, commit.waiting_on, cancellation)
                continue

        final_record = self._store.task(task_id)
        final = (
            final_record.outcome
            if final_record is not None and final_record.outcome is not None
            else TaskOutcome.failed("task did not finish")
        )
        if not completion.done():
            completion.set_result(final)
        return final

    async def _wait_for_children_async(
        self,
        task_id: str,
        children: Sequence[str],
        cancellation: asyncio.Event | None,
    ) -> None:
        """Waits for child tasks to finish, then resumes the waiting parent.

        The parent holds no invocation while it waits: its checkpoint says which children
        it is waiting on.
        """
        for child in children:
            outcome = await self.wait_for_task_async(child, cancellation)
            if outcome.is_failed:
                # With fail-fast, the first failure aborts the others.
                for sibling in children:
                    if sibling != child:
                        await self.abort_task_async(sibling, cancellation)
                break

    async def wait_for_task_async(
        self,
        task_id: str,
        cancellation: asyncio.Event | None = None,
    ) -> TaskOutcome:
        """Waits for a task to finish."""
        record = self._store.task(task_id)
        if record is None:
            return TaskOutcome.failed("unknown task")

        if record.status is TaskStatus.TERMINAL:
            return record.outcome if record.outcome is not None else TaskOutcome.completed()

        completion = self._task_completions.get(task_id)
        if completion is None:
            completion = asyncio.get_running_loop().create_future()
            self._task_completions[task_id] = completion

        # A task runs once at a time. A second caller joins the run in flight instead of
        # starting a competing one.
        self._start_task(task_id, cancellation)
        return await asyncio.shield(completion)

    async def abort_task_async(
        self,
        task_id: str,
        cancellation: asyncio.Event | None = None,
    ) -> None:
        """Aborts a task, running its abort handler."""
        record = self._store.task(task_id)
        if record is None or record.status is TaskStatus.TERMINAL:
            return

        definition = self._registry.find_task(record.name)
        if definition is None:
            return

        # The children go first: a parent that is aborted while waiting on them must not
        # leave them running.
        for child in [task for task in self._store.tasks if task.owner_task_id == task_id]:
            await self.abort_task_async(child.id, cancellation)

        runtime = TaskRuntime(self._store, record, definition, cancellation)
        commit = await definition.run_abort(runtime)
        if record.status is not TaskStatus.TERMINAL:
            await self._store.commit_task_async(task_id, lambda: commit)

    def close(self) -> None:
        """Detaches every conversation handle. The store stays the caller's."""
        for conversation in self._conversations.values():
            conversation.detach()
        self._conversations.clear()
        self._store.release_task_scheduler(self)

    async def close_async(self) -> None:
        if self._closed:
            return
        self._closed = True
        conversations = list(self._conversations.values())
        self.close()
        await asyncio.gather(*(conversation.checkpoint_barrier_async() for conversation in conversations))
        if self._in_flight:
            await asyncio.gather(*self._in_flight.values())
        await self._client.stop()


# ---------------------------------------------------------------------------
# Facade
# ---------------------------------------------------------------------------


@dataclass(slots=True)
class DurableClientOptions:
    """The pieces a durable client is built from."""

    #: The store the client reads and writes durable state through, or ``None`` when
    #: :attr:`store_options` describes one instead. A store supplied here is borrowed.
    store: DurableStore | None = None

    #: How to create the store when :attr:`store` is ``None``. A store created from these
    #: options is owned by the durable layer.
    store_options: DurableStoreOptions | None = None

    #: The extension registry the client resolves code from, or ``None`` to build with an
    #: empty registry.
    registry: ExtensionRegistry | None = None

    #: The client the durable layer drives, or ``None`` when :attr:`client_options`
    #: describes one instead. A client supplied here is borrowed.
    client: Any = None

    #: How to create the client when :attr:`client` is ``None``.
    client_options: Any = None

    #: The harness options applied to every session the client configures.
    harness: DurableHarnessOptions = field(default_factory=DurableHarnessOptions)

    #: Whether the durable layer closes :attr:`store`, or ``None`` to apply the default
    #: policy: borrow what the caller supplied, own what the durable layer created.
    owns_store: bool | None = None

    #: Whether the durable layer closes :attr:`client`, or ``None`` to apply the default
    #: policy.
    owns_client: bool | None = None


class DurableClientBuilder:
    """Builds a :class:`DurableCopilotClient`.

    The builder exists so a host can state the pieces it already has -- its store, its
    client, its registry -- and let the durable layer supply the rest. Every ``use_*``
    method replaces what the previous call set; the builder is a description, so ordering
    does not matter beyond last-write-wins.

    :meth:`build` validates and constructs. It does not connect, and it performs no
    persistence I/O: it opens no store file, starts no client, resumes no task, and
    renders no prompt section. Everything that leaves the process happens in
    :meth:`DurableCopilotClient.initialize_async`.
    """

    def __init__(self) -> None:
        self.options = DurableClientOptions()

    def use_store(self, store: DurableStore) -> "DurableClientBuilder":
        """Borrows a store the caller owns."""
        if store is None:
            raise ValueError("A store is required.")
        self.options.store = store
        self.options.store_options = None
        return self

    def use_store_options(self, options: DurableStoreOptions) -> "DurableClientBuilder":
        """Asks the durable layer to create and own a store."""
        if options is None:
            raise ValueError("Store options are required.")
        self.options.store = None
        self.options.store_options = options
        return self

    def use_client(self, client: Any) -> "DurableClientBuilder":
        """Borrows a client the caller owns."""
        if client is None:
            raise ValueError("A client is required.")
        self.options.client = client
        self.options.client_options = None
        return self

    def use_client_options(self, options: Any = None) -> "DurableClientBuilder":
        """Asks the durable layer to create, own, and start a client."""
        self.options.client = None
        self.options.client_options = options if options is not None else {}
        return self

    def use_registry(self, registry: ExtensionRegistry) -> "DurableClientBuilder":
        """Sets the extension registry the client resolves code from."""
        if registry is None:
            raise ValueError("A registry is required.")
        self.options.registry = registry
        return self

    def use_harness_options(self, options: DurableHarnessOptions) -> "DurableClientBuilder":
        """Sets the harness options applied to every session the client configures."""
        if options is None:
            raise ValueError("Harness options are required.")
        self.options.harness = options
        return self

    def configure(self, configure: Callable[[DurableClientOptions], None]) -> "DurableClientBuilder":
        """Adjusts the options in place.

        The escape hatch for the options no ``use_*`` method names -- the ownership flags
        in particular. It runs immediately, not at build time.
        """
        if configure is None:
            raise ValueError("A configure callback is required.")
        configure(self.options)
        return self

    def build(self) -> "DurableCopilotClient":
        """Validates the options and constructs the client."""
        options = self.options

        # Validate and construct only. Nothing here opens a store, starts a client, or
        # touches the file system: everything that leaves the process happens in
        # initialize_async.
        if options.store is None and options.store_options is None:
            raise ValueError(
                "A durable client needs a store: call use_store(DurableStore) to borrow "
                "one, or use_store_options(DurableStoreOptions) to have the durable layer "
                "create and own one."
            )

        if options.store is not None and options.store_options is not None:
            raise ValueError(
                "A durable client takes one store source: store and store_options are both "
                "set. use_store replaces the previous choice, so this means the options "
                "were adjusted directly; clear one of them."
            )

        if options.client is not None and options.client_options is not None:
            raise ValueError(
                "A durable client takes one client source: client and client_options are "
                "both set. use_client replaces the previous choice, so this means the "
                "options were adjusted directly; clear one of them."
            )

        if options.owns_store is True and options.store is None:
            raise ValueError(
                "owns_store is set but no store was supplied. A store the durable layer "
                "creates is always owned; the flag only decides the fate of a store the "
                "caller supplied."
            )

        if options.owns_client is True and options.client is None:
            raise ValueError(
                "owns_client is set but no client was supplied. A client the durable layer "
                "creates is always owned; the flag only decides the fate of a client the "
                "caller supplied."
            )

        if options.harness is None:
            raise ValueError("Harness options cannot be None; use the default.")

        # The registry is resolved once, here, and the same instance is what options and
        # the client both report. A getter that allocated a fresh empty registry per read
        # would hand out a different object every time, so an extension installed through
        # one read would be invisible to the next.
        if options.registry is None:
            options.registry = ExtensionRegistry()

        return DurableCopilotClient(options)


class DurableCopilotClient:
    """The durable host: a client bound to a store, a registry, and conversation handles.

    A :class:`DurableCopilotClient` is what :meth:`DurableClientBuilder.build` returns, and
    it is the supported entry point for an application that wants durable conversations
    without assembling the pieces itself.
    """

    def __init__(self, options: DurableClientOptions) -> None:
        self._options = options
        self._store: DurableStore | None = options.store
        self._client: Any = options.client
        self._owns_store = False
        self._owns_client = False
        self._initialization: asyncio.Task[None] | None = None
        self._harness: DurableHarness | None = None
        self._closed = False

    @property
    def options(self) -> DurableClientOptions:
        """The options this client was built from."""
        return self._options

    @property
    def store(self) -> DurableStore | None:
        """The store, or ``None`` before initialization."""
        return self._store

    @property
    def registry(self) -> ExtensionRegistry:
        """The extension registry."""
        assert self._options.registry is not None
        return self._options.registry

    @property
    def native_client(self) -> Any:
        """The client the durable layer drives."""
        return self._client

    @property
    def is_initialized(self) -> bool:
        """Whether initialization has completed."""
        return self._initialization is not None and self._initialization.done()

    async def initialize_async(self) -> None:
        """Creates and starts whatever the caller did not supply.

        Safe when several first operations race: the work happens once and every caller
        awaits the same result.
        """
        if self._initialization is None:
            self._initialization = asyncio.ensure_future(self._initialize_core_async())
        await self._initialization

    async def _initialize_core_async(self) -> None:
        created_store: DurableStore | None = None
        created_client: Any = None
        try:
            if self._store is None:
                assert self._options.store_options is not None
                created_store = DurableStore.create(self._options.store_options)
                self._store = created_store
                self._owns_store = True
            else:
                self._owns_store = bool(self._options.owns_store)

            if self._client is None:
                created_client = await self._create_client_async()
                self._client = created_client
                self._owns_client = True
            else:
                self._owns_client = bool(self._options.owns_client)
            self._harness = DurableHarness(self._client, self._store, self.registry, self._options.harness)
        except BaseException:
            # A half-initialized client would hold a store lock with nothing driving it, so
            # whatever was created is released before the failure propagates.
            if created_client is not None:
                await self._close_client_async(created_client)
            if created_store is not None:
                created_store.close()
            self._store = self._options.store
            self._client = self._options.client
            raise

    async def _create_client_async(self) -> Any:
        factory = getattr(self._options.client_options, "factory", None)
        if factory is None:
            options = self._options.harness
            kwargs = self._options.client_options or {}
            if not isinstance(kwargs, Mapping):
                raise TypeError("SDK client_options must be keyword options or expose a factory.")
            kwargs = dict(kwargs)
            kwargs.setdefault("connection", RuntimeConnection.for_stdio(
                path=resolve_runtime_path(options), args=options.runtime_arguments))
            client = CopilotClient(**kwargs)
            await client.start()
        else:
            client = factory()
            if inspect.isawaitable(client):
                client = await client
        return client

    @staticmethod
    async def _close_client_async(client: Any) -> None:
        close = getattr(client, "stop", None) or getattr(client, "close", None) or getattr(client, "stop_async", None)
        if close is None:
            raise TypeError("The owned SDK client has no stop method.")
        result = close()
        if inspect.isawaitable(result):
            await result

    async def create_conversation_async(
        self,
        choices: AgentChoices | None = None,
        owner_task_id: str | None = None,
        owner_conversation_id: str | None = None,
    ) -> DurableConversation:
        """Creates a conversation and its session."""
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.create_conversation_async(
            choices, owner_task_id, owner_conversation_id
        )

    def find_conversation(self, conversation_id: str) -> DurableConversation | None:
        """Finds a conversation by id, restoring it from the store if not yet opened."""
        if self._harness is None:
            return None
        return self._harness.find_conversation(conversation_id)

    async def root_async(self, choices: AgentChoices | None = None) -> DurableConversation:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.root_async(choices)

    async def resume_conversation_async(self, conversation_id: str) -> DurableConversation:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.resume_conversation_async(conversation_id)

    async def fork_async(self, parent: DurableConversation, entry_id: str) -> DurableConversation:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.fork_async(parent, entry_id)

    async def resume_tasks_async(self, cancellation: asyncio.Event | None = None) -> list[TaskOutcome]:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.resume_async(cancellation)

    async def wait_for_task_async(self, task_id: str, cancellation: asyncio.Event | None = None) -> TaskOutcome:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.wait_for_task_async(task_id, cancellation)

    async def get_session_config_async(
        self, choices: AgentChoices | None = None, conversation: DurableConversation | None = None,
    ) -> dict[str, Any]:
        await self.initialize_async()
        assert self._harness is not None
        return await self._harness.build_session_config(choices, conversation)

    async def close_async(self) -> None:
        """Releases whatever the durable layer owns. Idempotent."""
        if self._closed:
            return
        self._closed = True

        if self._harness is not None:
            self._harness.close()
            if self._harness._in_flight:
                await asyncio.gather(*self._harness._in_flight.values())
        if self._owns_client and self._client is not None:
            await self._close_client_async(self._client)
        if self._owns_store and self._store is not None:
            self._store.close()


# ---------------------------------------------------------------------------
# Watch stream
# ---------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class DurableFrame:
    """One frame of the durable change feed."""

    #: ``baseline`` for the snapshot a subscriber starts from, ``change`` for a delta, and
    #: ``reset`` for the full-value frame that replaces a subscriber's accumulated state.
    kind: str
    #: The conversation the frame is about, or ``None`` for a store-wide frame.
    conversation_id: str | None
    #: The entry ids the frame covers, oldest first.
    entries: tuple[str, ...] = ()
    #: The document versions the frame covers, as ``(kind, ownerId, entryId)`` triples.
    documents: tuple[tuple[str, str, str], ...] = ()


class DurableWatch:
    """The durable change feed: a single-consumer stream of what the store has written.

    A subscriber starts from a baseline snapshot and then receives deltas, so it never has
    to replay the whole store to catch up. The feed is bounded: once a subscriber has
    fallen more than :attr:`capacity` frames behind, the accumulated deltas are replaced by
    one full-value ``reset`` frame rather than growing without limit.

    The feed is single-consumer by construction. A second consumer would have to share the
    queue, and two readers of one queue each see half the frames, so the second
    subscription fails instead of silently splitting the stream.
    """

    def __init__(
        self,
        store: DurableStore,
        conversation_id: str | None = None,
        capacity: int = 100,
        poll_interval: float = 0.05,
    ) -> None:
        if capacity < 1:
            raise ValueError("A watch needs a capacity of at least one frame.")
        self._store = store
        self._conversation_id = conversation_id
        self._capacity = capacity
        self._poll_interval = poll_interval
        self._queue: asyncio.Queue[DurableFrame] = asyncio.Queue()
        self._claimed = False
        self._seen_entries: dict[str, int] = {}
        self._seen_versions: dict[tuple[str, str], int] = {}
        self._pending = 0

    @property
    def conversation_id(self) -> str | None:
        """The conversation the feed is scoped to, or ``None`` for the whole store."""
        return self._conversation_id

    def _baseline(self) -> DurableFrame:
        """Captures the state a subscriber starts from."""
        entries: list[str] = []
        documents: list[tuple[str, str, str]] = []
        for record in self._store.conversations:
            if self._conversation_id is not None and record.id != self._conversation_id:
                continue
            conversation_entries = list(self._store.entries(record.id))
            self._seen_entries[record.id] = len(conversation_entries)
            entries.extend(conversation_entries)

        for document in self._store.documents:
            if self._conversation_id is not None and document.owner_id != self._conversation_id:
                continue
            key = (document.kind, document.owner_id)
            self._seen_versions[key] = len(document.versions)
            for version in document.versions:
                documents.append((document.kind, document.owner_id, version.entry_id))

        return DurableFrame(
            kind="baseline",
            conversation_id=self._conversation_id,
            entries=tuple(entries),
            documents=tuple(documents),
        )

    def _delta(self) -> DurableFrame | None:
        """Captures what the store has written since the last frame, or ``None``."""
        entries: list[str] = []
        documents: list[tuple[str, str, str]] = []

        for record in self._store.conversations:
            if self._conversation_id is not None and record.id != self._conversation_id:
                continue
            conversation_entries = list(self._store.entries(record.id))
            seen = self._seen_entries.get(record.id, 0)
            if len(conversation_entries) > seen:
                entries.extend(conversation_entries[seen:])
                self._seen_entries[record.id] = len(conversation_entries)

        for document in self._store.documents:
            if self._conversation_id is not None and document.owner_id != self._conversation_id:
                continue
            key = (document.kind, document.owner_id)
            seen = self._seen_versions.get(key, 0)
            if len(document.versions) > seen:
                for version in document.versions[seen:]:
                    documents.append((document.kind, document.owner_id, version.entry_id))
                self._seen_versions[key] = len(document.versions)

        if not entries and not documents:
            return None

        return DurableFrame(
            kind="change",
            conversation_id=self._conversation_id,
            entries=tuple(entries),
            documents=tuple(documents),
        )

    async def frames(self) -> AsyncIterator[DurableFrame]:
        """Yields the baseline frame, then one frame per change.

        The feed is single-consumer: the first caller to iterate owns it, and a second
        iteration fails rather than splitting the stream.
        """
        if self._claimed:
            raise RuntimeError(
                "This durable watch already has a consumer. A change feed is "
                "single-consumer: open a second watch instead of sharing this one."
            )
        self._claimed = True

        yield self._baseline()

        while True:
            frame = self._delta()
            if frame is None:
                await asyncio.sleep(self._poll_interval)
                continue

            if self._pending >= self._capacity:
                # The consumer has fallen too far behind to be handed deltas one at a
                # time, so the accumulated state is replaced by one full-value frame.
                self._pending = 0
                yield DurableFrame(
                    kind="reset",
                    conversation_id=self._conversation_id,
                    entries=frame.entries,
                    documents=frame.documents,
                )
                continue

            self._pending += 1
            yield frame


# ---------------------------------------------------------------------------
# Event helpers and assertions
# ---------------------------------------------------------------------------


class DurableExtensions:
    """Small readers for the session events the durable layer cares about."""

    @staticmethod
    def text(event: Any) -> str | None:
        """The text of an assistant or user message event, or ``None``."""
        content = getattr(event, "content", None)
        if content is None and isinstance(event, Mapping):
            content = event.get("content")
        return content if isinstance(content, str) else None

    @staticmethod
    def tool_name(event: Any) -> str | None:
        """The tool name of a tool-execution-start event, or ``None``."""
        name = getattr(event, "tool_name", None)
        if name is None and isinstance(event, Mapping):
            name = event.get("toolName")
        return name if isinstance(name, str) else None


class Check:
    """Assertions the durable layer's own checks use.

    They exist so a failed expectation names what was expected and what was found, rather
    than surfacing as an ``AttributeError`` three frames away.
    """

    @staticmethod
    def that(condition: bool, because: str) -> None:
        """Asserts a condition, naming why it must hold."""
        if not condition:
            raise AssertionError(f"Assertion failed: {because}")

    @staticmethod
    def equal(expected: Any, actual: Any, because: str) -> None:
        """Asserts that two values are equal."""
        if expected != actual:
            raise AssertionError(
                f"Assertion failed: {because} (expected {Check._format(expected)}, "
                f"found {Check._format(actual)})"
            )

    @staticmethod
    def sequence(expected: Sequence[Any], actual: Sequence[Any], because: str) -> None:
        """Asserts that two sequences hold the same values in the same order."""
        if list(expected) != list(actual):
            raise AssertionError(
                f"Assertion failed: {because} (expected {Check._format(list(expected))}, "
                f"found {Check._format(list(actual))})"
            )

    @staticmethod
    def not_none(value: Any, because: str) -> Any:
        """Asserts that a value is present, and returns it."""
        if value is None:
            raise AssertionError(f"Assertion failed: {because} (found (null))")
        return value

    @staticmethod
    def contains(haystack: str, needle: str, because: str) -> None:
        """Asserts that a string contains another, ignoring case."""
        if needle.lower() not in haystack.lower():
            raise AssertionError(
                f"Assertion failed: {because} (expected {Check._format(haystack)} to "
                f"contain {Check._format(needle)})"
            )

    @staticmethod
    def _format(value: Any) -> str:
        if value is None:
            return "(null)"
        if isinstance(value, str):
            return f'"{value}"'
        return str(value)
