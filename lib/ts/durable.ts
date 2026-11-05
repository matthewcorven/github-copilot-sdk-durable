// TypeScript port of the durable core in `lib/Durable.cs`.
//
// The C# file is the reference implementation; this file is a port, not a redesign.
// Every exported type keeps the C# name (PascalCase) so a reader can move between the
// two files by name alone. The language-neutral contract is `docs/DESIGN.md` §3, and
// the places where TypeScript genuinely lacks a C# equivalent are recorded in
// `docs/COMPARATIVE_ANALYSIS.md` §8.
//
import * as fs from "node:fs";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import { createRequire } from "node:module";
import { AsyncLocalStorage } from "node:async_hooks";
import { CopilotClient, RuntimeConnection } from "@github/copilot-sdk";
import type { AssistantMessageEvent, CopilotSession, SessionConfig, SessionEvent, Tool, SandboxConfig } from "@github/copilot-sdk";
export { defineTool } from "@github/copilot-sdk";
export type { Tool, ToolInvocation, SessionEvent, SessionConfig, SandboxConfig, SessionHooks } from "@github/copilot-sdk";

// ─────────────────────────────────────────────────────────────────────────────
// JSON
// ─────────────────────────────────────────────────────────────────────────────

/** A JSON value. The counterpart of `System.Text.Json.JsonElement`. */
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };

/** A JSON object. */
export type JsonObject = { [key: string]: JsonValue };

function cloneJson<T extends JsonValue>(value: T): T {
  if (value === null || typeof value !== "object") {
    return value;
  }

  if (Array.isArray(value)) {
    return value.map((item) => cloneJson(item)) as T;
  }

  const copy: JsonObject = {};
  for (const [key, item] of Object.entries(value as JsonObject)) {
    Object.defineProperty(copy, key, {
      value: cloneJson(item), enumerable: true, writable: true, configurable: true,
    });
  }

  return copy as T;
}

/**
 * JSON helpers shared by the durable layer.
 *
 * The C# layer serializes to `JsonElement` because its state is reflection-serialized.
 * TypeScript values are already JSON, so the port's helpers clone instead of serialize:
 * a stored value must not alias the caller's object, which is the property the C#
 * `JsonElement` boundary gives for free.
 */
export const DurableJson = {
  /** Serializes a value to a JSON value. */
  toElement<T extends JsonValue>(value: T): JsonValue {
    return cloneJson(value);
  },

  /** Deserializes a JSON value to a value. */
  fromElement<T>(element: JsonValue | null | undefined): T | null {
    return element === null || element === undefined ? null : (cloneJson(element) as T);
  },

  /** Clones a JSON value. */
  clone<T extends JsonValue>(value: T): T {
    return cloneJson(value);
  },
};

// ─────────────────────────────────────────────────────────────────────────────
// Documents
// ─────────────────────────────────────────────────────────────────────────────

/** How a document behaves when a conversation is forked. */
export const ForkPolicy = {
  /** The fork starts with the value the document had at the fork entry. */
  AsOf: "asOf",
  /** The fork starts with the parent's current value. */
  Current: "current",
  /** The fork starts with the document's initial value. */
  Initial: "initial",
} as const;

export type ForkPolicy = (typeof ForkPolicy)[keyof typeof ForkPolicy];

/** How a document's history is retained. */
export const DocumentHistory = {
  /** Only the latest value is retained. */
  Latest: "latest",
  /** Every committed value is retained and can be read as of a past entry. */
  Rewindable: "rewindable",
} as const;

export type DocumentHistory = (typeof DocumentHistory)[keyof typeof DocumentHistory];

/** Defines a typed document: durable JSON state attached to a conversation. */
export class DocumentDefinition<T extends JsonValue> {
  /** The persisted document kind, e.g. `example.notes`. */
  readonly kind: string;

  /** How the document's history is retained. */
  readonly history: DocumentHistory;

  /** What a forked conversation starts with. */
  readonly fork: ForkPolicy;

  /** The value a document starts with when first written. */
  readonly initial: () => T;

  constructor(
    kind: string,
    history: DocumentHistory = DocumentHistory.Rewindable,
    fork: ForkPolicy = ForkPolicy.AsOf,
    initial?: () => T,
  ) {
    this.kind = kind;
    this.history = history;
    this.fork = fork;
    this.initial = initial ?? (() => null as T);
  }
}

/** One committed value of a document, with the entry that produced it. */
export class DocumentVersion {
  constructor(
    readonly entryId: string,
    readonly value: JsonValue,
    readonly sequence: number,
  ) {}
}

/** One immutable transcript entry: a kind, an optional payload, and the conversation it belongs to. */
export class EntryRecord {
  constructor(
    readonly id: string,
    readonly conversationId: string,
    readonly kind: string,
    readonly data: JsonValue | null,
    readonly sequence: number,
  ) {}
}

/** The durable state of one document. */
export class DocumentState {
  /** The committed versions, oldest first. */
  readonly versions: DocumentVersion[] = [];

  constructor(
    readonly kind: string,
    readonly ownerId: string,
    readonly fork: ForkPolicy,
    readonly initial: JsonValue | null = null,
  ) {}

  /** The latest committed value, or `null` if never written. */
  get latest(): JsonValue | null {
    return this.versions.length === 0 ? null : this.versions[this.versions.length - 1]!.value;
  }

  /** The value committed in the same transaction as the given entry. */
  asOf(entryId: string): JsonValue | null {
    for (let i = this.versions.length - 1; i >= 0; i--) {
      const version = this.versions[i]!;
      if (version.entryId === entryId) {
        return version.value;
      }
    }

    return null;
  }

  /**
   * The value as of the given entry: the last version committed at or before that entry.
   * A version committed in the same transaction as the entry counts as "at" it.
   *
   * With no `sequenceOf`, this is the C# `AsOfOrBefore(entryId)` overload: the value
   * committed with the entry, or the document's first version.
   */
  asOfOrBefore(entryId: string, sequenceOf?: (entryId: string) => number): JsonValue | null {
    if (sequenceOf === undefined) {
      for (let i = this.versions.length - 1; i >= 0; i--) {
        const version = this.versions[i]!;
        if (version.entryId === entryId) {
          return version.value;
        }
      }

      return this.versions.length === 0 ? null : this.versions[0]!.value;
    }

    const boundary = sequenceOf(entryId);
    for (let i = this.versions.length - 1; i >= 0; i--) {
      const version = this.versions[i]!;
      if (version.entryId === entryId) {
        return version.value;
      }

      if (sequenceOf(version.entryId) <= boundary) {
        return version.value;
      }
    }

    return null;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Durable tasks
// ─────────────────────────────────────────────────────────────────────────────

/** The lifecycle status of a durable task. */
export const TaskStatus = {
  /** Created but not yet started. */
  Pending: "pending",
  /** Currently executing a phase. */
  Running: "running",
  /** Waiting on child tasks. */
  Waiting: "waiting",
  /** Sleeping until a durable deadline. */
  Sleeping: "sleeping",
  /** Finished, successfully or not. */
  Terminal: "terminal",
} as const;

export type TaskStatus = (typeof TaskStatus)[keyof typeof TaskStatus];

/** The durable record of one task. */
export class DurableTaskRecord {
  /** The current lifecycle status. */
  status: TaskStatus = TaskStatus.Pending;

  /** The terminal outcome, once the task finishes. */
  outcome: JsonValue | null = null;

  /** How many times the task has been restarted after an interruption. */
  restarts = 0;

  /** The owning conversation, when the task is conversation-owned. */
  ownerConversationId: string | null = null;

  /** The owning task, when the task is task-owned. */
  ownerTaskId: string | null = null;

  /** Whether the task is background work that does not block its owner. */
  background = false;

  /** The child tasks this task waits on. */
  waitingOn: string[] = [];

  /** Idempotency memos, so a rerun after a crash does not repeat an effect. */
  memos: Record<string, JsonValue> = {};

  constructor(
    readonly id: string,
    readonly name: string,
    /** The task's immutable input. */
    readonly input: JsonValue,
    /** The last saved checkpoint. */
    public checkpoint: JsonValue,
  ) {}
}

/** The result of a durable task. */
export class TaskOutcome {
  constructor(
    readonly status: string,
    readonly result: JsonValue | null = null,
    readonly error: string | null = null,
  ) {}

  /** Whether the task completed successfully. */
  get isCompleted(): boolean {
    return this.status === "completed";
  }

  /** Whether the task was aborted. */
  get isAborted(): boolean {
    return this.status === "aborted";
  }

  /** Whether the task failed. */
  get isFailed(): boolean {
    return this.status === "failed";
  }

  /** The persisted shape of the outcome. */
  toJson(): JsonObject {
    const json: JsonObject = { status: this.status };
    if (this.result !== null) {
      json["result"] = this.result;
    }

    if (this.error !== null) {
      json["error"] = this.error;
    }

    return json;
  }

  /** Reads a persisted outcome. */
  static fromJson(value: JsonValue | null | undefined): TaskOutcome | null {
    if (value === null || value === undefined || typeof value !== "object" || Array.isArray(value)) {
      return null;
    }

    const json = value as JsonObject;
    const status = json["status"];
    if (typeof status !== "string") {
      return null;
    }

    const result = json["result"] ?? null;
    const error = json["error"];
    return new TaskOutcome(status, result, typeof error === "string" ? error : null);
  }
}

/** A commit that advances a task's durable state. */
export class TaskCommit {
  private constructor(
    /** The status to move to. */
    readonly status: TaskStatus,
    /** The next checkpoint, when the task continues. */
    readonly checkpoint: JsonValue | null,
    /** The terminal outcome, when the task finishes. */
    readonly outcome: TaskOutcome | null,
    /** The child tasks this commit waits on. */
    readonly waitingOn: readonly string[],
  ) {}

  /** Creates a commit that continues the task with a new checkpoint. */
  static running(checkpoint: JsonValue): TaskCommit {
    return new TaskCommit(TaskStatus.Running, checkpoint, null, []);
  }

  /** Creates a commit that continues the task, waiting on child tasks. */
  static waiting(checkpoint: JsonValue, on: readonly string[]): TaskCommit {
    return new TaskCommit(TaskStatus.Waiting, checkpoint, null, [...on]);
  }

  /** Creates a commit that finishes the task successfully. */
  static completed(result: JsonValue): TaskCommit {
    return new TaskCommit(TaskStatus.Terminal, null, new TaskOutcome("completed", result), []);
  }

  /** Creates a commit that finishes the task with a failure. */
  static failed(error: string): TaskCommit {
    return new TaskCommit(TaskStatus.Terminal, null, new TaskOutcome("failed", null, error), []);
  }

  /** Creates a commit that finishes the task as aborted. */
  static aborted(): TaskCommit {
    return new TaskCommit(TaskStatus.Terminal, null, new TaskOutcome("aborted"), []);
  }
}

/** A phase handler: reads the typed input and checkpoint, and returns the next commit. */
export type PhaseHandler<TInput extends JsonValue, TCheckpoint extends JsonValue> = (
  input: TInput,
  checkpoint: TCheckpoint,
  runtime: TaskRuntime,
) => TaskCommit | Promise<TaskCommit>;

/** The definition of a durable task: a checkpointed state machine. */
export class DurableTaskDefinition {
  constructor(
    /** The task name, used to find the definition in the registry. */
    readonly name: string,
    /** The starting checkpoint. */
    readonly initial: () => JsonValue,
    /** One handler per phase. */
    readonly phases: ReadonlyMap<string, (runtime: TaskRuntime) => TaskCommit | Promise<TaskCommit>>,
    /** Runs instead of the phases after an abort. */
    readonly abort: (runtime: TaskRuntime) => TaskCommit | Promise<TaskCommit>,
  ) {}
}

/** Builds durable task definitions with typed input, checkpoint, and outcome. */
export const DurableTask = {
  /** Defines a single-phase task whose checkpoint and outcome are typed. */
  define<TInput extends JsonValue, TCheckpoint extends JsonValue>(
    name: string,
    initial: () => TCheckpoint,
    phase: PhaseHandler<TInput, TCheckpoint>,
    abort?: PhaseHandler<TInput, TCheckpoint>,
  ): DurableTaskDefinition {
    const phases = new Map<string, (runtime: TaskRuntime) => TaskCommit | Promise<TaskCommit>>([
      [
        "run",
        (runtime) =>
          phase(
            DurableJson.fromElement<TInput>(runtime.task.input) as TInput,
            DurableJson.fromElement<TCheckpoint>(runtime.task.checkpoint) as TCheckpoint,
            runtime,
          ),
      ],
    ]);

    return new DurableTaskDefinition(
      name,
      () => DurableJson.toElement(initial()),
      phases,
      abort === undefined
        ? () => TaskCommit.aborted()
        : (runtime) =>
            abort(
              DurableJson.fromElement<TInput>(runtime.task.input) as TInput,
              DurableJson.fromElement<TCheckpoint>(runtime.task.checkpoint) as TCheckpoint,
              runtime,
            ),
    );
  },

  /** Defines a multi-phase task from a phase-name to handler map. */
  definePhases<TInput extends JsonValue, TCheckpoint extends JsonValue>(
    name: string,
    initial: () => TCheckpoint,
    phases: ReadonlyMap<string, PhaseHandler<TInput, TCheckpoint>>,
    abort?: PhaseHandler<TInput, TCheckpoint>,
  ): DurableTaskDefinition {
    const handlers = new Map<string, (runtime: TaskRuntime) => TaskCommit | Promise<TaskCommit>>();
    for (const [phaseName, handler] of phases) {
      handlers.set(phaseName, (runtime) =>
        handler(
          DurableJson.fromElement<TInput>(runtime.task.input) as TInput,
          DurableJson.fromElement<TCheckpoint>(runtime.task.checkpoint) as TCheckpoint,
          runtime,
        ),
      );
    }

    return new DurableTaskDefinition(
      name,
      () => DurableJson.toElement(initial()),
      handlers,
      abort === undefined
        ? () => TaskCommit.aborted()
        : (runtime) =>
            abort(
              DurableJson.fromElement<TInput>(runtime.task.input) as TInput,
              DurableJson.fromElement<TCheckpoint>(runtime.task.checkpoint) as TCheckpoint,
              runtime,
            ),
    );
  },
};

/** The context a task phase runs with. */
export class TaskRuntime {
  constructor(
    private readonly store: DurableStore,
    /** The task record being executed. */
    readonly task: DurableTaskRecord,
    /** The task definition being executed. */
    readonly definition: DurableTaskDefinition,
    /**
     * Cancelled when the task should stop. A task that is cancelled mid-phase leaves
     * its last checkpoint in place, so a later run continues from there.
     */
    readonly cancellationToken: AbortSignal = new AbortController().signal,
  ) {}

  /** The current wall-clock time. */
  get now(): Date {
    return new Date();
  }

  /** Saves a checkpoint, or finishes the task with an outcome. */
  commitAsync(commit: () => TaskCommit | Promise<TaskCommit>): Promise<TaskCommit> {
    return this.store.commitTaskCheckpointAsync(this.task.id, commit);
  }

  /**
   * Saves a checkpoint, or finishes the task with an outcome. The callback may
   * create child tasks: they are written in the same transaction as the checkpoint
   * that records them.
   */
  commitInTransactionAsync(
    commit: (tx: Transaction) => TaskCommit | Promise<TaskCommit>,
  ): Promise<TaskCommit> {
    return this.store.commitTaskAsync(this.task.id, commit);
  }

  /**
   * Creates a child task owned by this task. The child is written in the same
   * transaction as the checkpoint that records it, so a crash leaves either both
   * the child and the checkpoint, or neither.
   */
  createChildTask(tx: Transaction, definition: DurableTaskDefinition, input: JsonValue): DurableTaskRecord {
    return tx.createTask(definition, input, { ownerTaskId: this.task.id });
  }

  /** Schedules a task to run. */
  scheduleTask(taskId: string, cancellationToken: AbortSignal = this.cancellationToken): void {
    this.store.scheduleTask(taskId, cancellationToken);
  }

  /**
   * Reads an idempotency memo, or writes one when `value` is supplied.
   *
   * Call this from a phase handler, not from inside a `commitAsync` callback: it takes the
   * store's commit gate, which the commit already holds, so it does not return. Use
   * `transaction.memo` inside a transaction.
   */
  memoAsync(key: string, value?: JsonValue): Promise<JsonValue | null> {
    return this.store.memoAsync(this.task.id, key, value);
  }

  /** Sleeps until a durable deadline, saving the wake time in the checkpoint. */
  sleepUntilAsync(deadline: Date, nextCheckpoint: () => TaskCommit): Promise<TaskCommit> {
    return this.store.sleepUntilAsync(this.task.id, deadline, nextCheckpoint, this.cancellationToken);
  }

  /** Reads the outcomes of child tasks. */
  outcomesAsync(taskIds: readonly string[]): Promise<TaskOutcome[]> {
    return this.store.outcomesAsync(taskIds);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Conversations
// ─────────────────────────────────────────────────────────────────────────────

const activeTask = new AsyncLocalStorage<{ store: DurableStore; taskId: string; active: boolean }>();
function isActiveTask(store: DurableStore): boolean {
  const task = activeTask.getStore();
  return task?.store === store && task.active;
}

/** The record of one conversation. */
export class ConversationRecord {
  /** The owning task, when the conversation is task-owned. */
  ownerTaskId: string | null = null;

  /** The owning conversation, when the conversation is conversation-owned. */
  ownerConversationId: string | null = null;

  constructor(
    readonly id: string,
    /** The parent this conversation was forked from, or `null` for a root. */
    readonly parentConversationId: string | null = null,
    /** The parent entry the fork was taken at, or `null` for a root. */
    readonly forkEntryId: string | null = null,
  ) {}
}

/** Where a fork is in its two-phase (durable, then native) creation. */
export const ForkIntentState = {
  /** The durable half is committed; the native fork has not been attempted. */
  Pending: "pending",
  /** The native fork succeeded. */
  Completed: "completed",
  /** The native fork failed and the fork was abandoned. */
  Abandoned: "abandoned",
} as const;

export type ForkIntentState = (typeof ForkIntentState)[keyof typeof ForkIntentState];

/** The durable half of a fork, written before the native fork is attempted. */
export class ForkIntent {
  constructor(
    readonly parentConversationId: string,
    readonly forkEntryId: string,
    readonly sessionId: string | null,
    public state: ForkIntentState,
  ) {}
}

/** The session a conversation is bound to. */
export class SessionBinding {
  constructor(
    readonly sessionId: string,
    readonly model: string | null,
    readonly boundAt: string,
    /**
     * Whether the durable layer chose the id. A pinned id can be asked for again; an
     * id the runtime allocated cannot, so a runtime that does not hold it has lost the
     * transcript.
     */
    readonly pinned: boolean,
  ) {}
}

/** The state of one admitted user input. */
export const InputAdmissionState = {
  Admitted: "admitted",
  Completed: "completed",
  Failed: "failed",
  Uncertain: "uncertain",
} as const;

export type InputAdmissionState = (typeof InputAdmissionState)[keyof typeof InputAdmissionState];

/** One admitted user input, and how far it got. */
export class InputAdmission {
  constructor(
    readonly admissionId: string,
    readonly prompt: string,
    public state: InputAdmissionState,
    readonly admittedAt: string,
    public settledAt: string | null = null,
  ) {}
}

/** One recorded conversation checkpoint event. */
export class ConversationCheckpoint {
  constructor(
    readonly checkpointId: string,
    readonly kind: string,
    /** The dedup key: the same event is recorded once. */
    readonly eventId: string,
    readonly messageId: string | null,
    readonly toolName: string | null,
    readonly toolArgs: JsonValue | null,
    readonly success: boolean | null,
    readonly error: string | null,
    readonly content: string | null,
    readonly recordedAt: string,
  ) {}
}

/**
 * Raised when a conversation's session cannot be resumed and re-creating it would
 * silently replace the transcript.
 */
export class DurableRecoveryRequiredError extends Error {
  constructor(
    readonly conversationId: string,
    readonly sessionId: string,
  ) {
    super(
      `Conversation '${conversationId}' is bound to session '${sessionId}', which the runtime does not hold. ` +
        "The transcript is gone, so re-create the conversation deliberately rather than continuing with an empty replacement.",
    );
    this.name = "DurableRecoveryRequiredError";
  }
}

/**
 * Walks a conversation's parent chain, oldest ancestor last, yielding each record.
 * The counterpart of the `ParentConversationId` traversal the C# layer performs inline.
 */
export function conversationLineage(
  store: DurableStore,
  conversationId: string,
): ConversationRecord[] {
  const lineage: ConversationRecord[] = [];
  const visited = new Set<string>();

  for (let current: string | null = conversationId; current !== null && !visited.has(current); ) {
    visited.add(current);
    const record = store.conversation(current);
    if (record === null) {
      break;
    }

    lineage.push(record);
    current = record.parentConversationId;
  }

  return lineage;
}

// ─────────────────────────────────────────────────────────────────────────────
// Store
// ─────────────────────────────────────────────────────────────────────────────

/** The backend a store is written by. */
export const DurableStoreFormat = {
  /** Sniff the file. */
  Auto: "auto",
  /** No file at all. */
  InMemory: "inMemory",
  /** JSONL, one record per line. */
  Jsonl: "jsonl",
  /** The legacy single-file JSON snapshot. */
  LegacyJson: "legacyJson",
} as const;

export type DurableStoreFormat = (typeof DurableStoreFormat)[keyof typeof DurableStoreFormat];

/** The options a store is created with. */
export class DurableStoreOptions {
  /** The store file. `null` selects the per-user default for a persistent format. */
  path: string | null = null;

  /** The backend. */
  format: DurableStoreFormat = DurableStoreFormat.Auto;

  /** The file name used when the per-user default path is resolved. */
  fileName = "durable-store.jsonl";

  /** A provider factory. Wins over `providerName`, which wins over `format`. */
  provider: ((options: DurableStoreOptions) => IDurableStoreProvider) | null = null;

  /** A registered provider name. */
  providerName: string | null = null;
}

/** The seam a store is written through. */
export interface IDurableStoreProvider {
  /** Whether the provider's state is persisted. */
  readonly isPersistent: boolean;

  /** Opens the provider over the options. */
  open(options: DurableStoreOptions): void;

  /** Releases whatever the provider holds for the store's lifetime. */
  dispose(): void;

  /** Starts the undo log for the transaction in flight. */
  beginTransaction(): void;

  /** Undoes everything the failed transaction wrote. */
  rollback(): void;

  /** Ends the transaction and flushes the state to the backend. */
  commit(): void;

  /** The monotonic write sequence. */
  readonly sequence: number;

  /** The conversations known to the store. */
  readonly conversations: readonly ConversationRecord[];

  /** The tasks known to the store. */
  readonly tasks: readonly DurableTaskRecord[];

  /** The documents known to the store. */
  readonly documents: readonly DocumentState[];

  /** Reads a conversation record. */
  conversation(id: string): ConversationRecord | null;

  /** Reads a task record. */
  task(id: string): DurableTaskRecord | null;

  /** Reads a document's state. */
  document(kind: string, ownerId: string): DocumentState | null;

  /** Reads the entry ids of a conversation, oldest first. */
  entries(conversationId: string): readonly string[];

  /** The write sequence of an entry, used to order document versions against it. */
  sequenceOf(entryId: string): number;

  /** Advances the write sequence and returns the new value. */
  nextSequence(): number;

  /** Adds a conversation record, replacing any record with the same id. */
  addConversation(record: ConversationRecord): void;

  /** Appends an entry to a conversation. */
  addEntry(conversationId: string, entryId: string): void;

  /** Copies the parent's entries up to and including `forkEntryId` into the fork. */
  copyEntries(fromConversationId: string, toConversationId: string, forkEntryId: string | null): void;

  /** Reads a document's state, creating it on first use and remembering its initial value. */
  ensureDocument(
    kind: string,
    ownerId: string,
    fork: ForkPolicy,
    initial?: JsonValue | null,
  ): DocumentState;

  /** Records a committed version of a document. */
  recordDocumentVersion(state: DocumentState, entryId: string, value: JsonValue): void;

  /** Copies a document into another owner, honouring its fork policy. */
  copyDocument(source: DocumentState, toOwner: string, forkEntryId: string | null): void;

  /** Adds a task record, replacing any record with the same id. */
  addTask(record: DurableTaskRecord): DurableTaskRecord;

  /** Records the current state of a task that was mutated in place. */
  updateTask(record: DurableTaskRecord): void;
}

/** The persisted shape of one document version. */
export class DocumentVersionSnapshot {
  constructor(
    readonly entryId: string,
    readonly sequence: number,
    readonly value: JsonValue,
  ) {}
}

/** The persisted shape of one document. */
export class DocumentSnapshot {
  constructor(
    readonly kind: string,
    readonly ownerId: string,
    readonly fork: ForkPolicy,
    readonly initial: JsonValue | null,
    readonly versions: readonly DocumentVersionSnapshot[],
  ) {}
}

/** The persisted shape of the durable store. */
export class StoreSnapshot {
  constructor(
    readonly sequence: number,
    readonly conversations: readonly ConversationRecord[],
    readonly tasks: readonly DurableTaskRecord[],
    readonly documents: readonly DocumentSnapshot[],
    readonly entries: Readonly<Record<string, readonly string[]>>,
    readonly entryRecords: readonly EntryRecord[],
  ) {}
}

/** The state of a task that a transaction can put back. */
export class TaskStateSnapshot {
  constructor(
    readonly status: TaskStatus,
    readonly checkpoint: JsonValue,
    readonly outcome: JsonValue | null,
    readonly waitingOn: readonly string[],
    readonly memos: Readonly<Record<string, JsonValue>>,
  ) {}

  /** Captures a task's current state. */
  static capture(task: DurableTaskRecord): TaskStateSnapshot {
    return new TaskStateSnapshot(
      task.status,
      DurableJson.clone(task.checkpoint),
      task.outcome === null ? null : DurableJson.clone(task.outcome),
      [...task.waitingOn],
      DurableJson.clone(task.memos),
    );
  }

  /** Puts the captured state back onto the task. */
  applyTo(task: DurableTaskRecord): void {
    task.status = this.status;
    task.checkpoint = DurableJson.clone(this.checkpoint);
    task.outcome = this.outcome === null ? null : DurableJson.clone(this.outcome);
    task.waitingOn = [...this.waitingOn];
    task.memos = DurableJson.clone(this.memos);
  }
}

/**
 * The undo log of the transaction in flight. A transaction writes straight into the
 * provider's collections, so a throw has to undo what it wrote. The log is `null` when
 * no transaction is running.
 */
class UndoLog {
  private readonly inverses: Array<() => void> = [];

  /** Undoes the addition of a key. */
  remove<K, V>(map: Map<K, V>, key: K): void {
    this.inverses.push(() => {
      map.delete(key);
    });
  }

  /** Undoes the appends to a list. */
  append<T>(list: T[]): void {
    const length = list.length;
    this.inverses.push(() => {
      list.length = length;
    });
  }

  /** Undoes a sequence advance. */
  sequence(restore: () => void): void {
    this.inverses.push(restore);
  }

  /** Undoes an in-place task mutation. */
  restore(snapshot: TaskStateSnapshot, task: DurableTaskRecord, states: Map<string, TaskStateSnapshot>): void {
    this.inverses.push(() => {
      snapshot.applyTo(task);
      states.set(task.id, snapshot);
    });
  }

  /** Replays the inverses in reverse, then clears the log. */
  undo(): void {
    for (let i = this.inverses.length - 1; i >= 0; i--) {
      this.inverses[i]!();
    }

    this.inverses.length = 0;
  }
}

/** The ephemeral tier: state lives in the process and the filesystem is never touched. */
export class InMemoryStoreProvider implements IDurableStoreProvider {
  private readonly documentStates = new Map<string, DocumentState>();
  private readonly taskRecords = new Map<string, DurableTaskRecord>();
  private readonly conversationRecords = new Map<string, ConversationRecord>();

  // The entry state is shared with a persistent provider, which diffs the committed
  // entry lists and records against the file at commit time.
  protected readonly entryLists = new Map<string, string[]>();
  protected readonly entryRecords = new Map<string, EntryRecord>();

  private readonly taskStates = new Map<string, TaskStateSnapshot>();
  protected sequenceCounter = 0;

  private undoLog: UndoLog | null = null;

  /** Whether the provider's state is persisted. The in-memory provider is the ephemeral tier. */
  get isPersistent(): boolean {
    return false;
  }

  /** Starts empty. The in-memory provider never touches the filesystem. */
  open(_options: DurableStoreOptions): void {}

  /** The in-memory provider holds nothing, so this is a no-op. */
  dispose(): void {}

  /** Starts the undo log for the transaction in flight. */
  beginTransaction(): void {
    this.undoLog = new UndoLog();
  }

  /** Undoes everything the failed transaction wrote. */
  rollback(): void {
    this.undoLog?.undo();
    this.undoLog = null;
  }

  /** Ends the transaction and flushes the state to the backend. */
  commit(): void {
    this.undoLog = null;
    this.persist();
  }

  /**
   * Ends the transaction in flight without undoing it. A provider whose `persist` can
   * fail keeps the undo log until the backend has accepted the write, then calls this
   * to discard it.
   */
  protected endTransaction(): void {
    this.undoLog = null;
  }

  /** The monotonic write sequence. */
  get sequence(): number {
    return this.sequenceCounter;
  }

  /** The conversations known to the store. */
  get conversations(): readonly ConversationRecord[] {
    return [...this.conversationRecords.values()];
  }

  /** The tasks known to the store. */
  get tasks(): readonly DurableTaskRecord[] {
    return [...this.taskRecords.values()];
  }

  /** The documents known to the store. */
  get documents(): readonly DocumentState[] {
    return [...this.documentStates.values()];
  }

  /** Reads a conversation record. */
  conversation(id: string): ConversationRecord | null {
    return this.conversationRecords.get(id) ?? null;
  }

  /** Reads a task record. */
  task(id: string): DurableTaskRecord | null {
    return this.taskRecords.get(id) ?? null;
  }

  /** Reads a document's state. */
  document(kind: string, ownerId: string): DocumentState | null {
    return this.documentStates.get(InMemoryStoreProvider.documentKey(kind, ownerId)) ?? null;
  }

  /** Reads the entry ids of a conversation, oldest first. */
  entries(conversationId: string): readonly string[] {
    return this.entryLists.get(conversationId) ?? [];
  }

  /** The write sequence of an entry, used to order document versions against it. */
  sequenceOf(entryId: string): number {
    return this.entryRecords.get(entryId)?.sequence ?? Number.MAX_SAFE_INTEGER;
  }

  /** Advances the write sequence and returns the new value. */
  nextSequence(): number {
    const sequence = ++this.sequenceCounter;
    this.undoLog?.sequence(() => {
      this.sequenceCounter = sequence - 1;
    });
    return sequence;
  }

  /** Adds a conversation record, replacing any record with the same id. */
  addConversation(record: ConversationRecord): void {
    this.undoLog?.remove(this.conversationRecords, record.id);
    this.conversationRecords.set(record.id, record);
  }

  /** Appends an entry to a conversation. */
  addEntry(conversationId: string, entryId: string): void {
    let entries = this.entryLists.get(conversationId);
    if (entries === undefined) {
      entries = [];
      this.entryLists.set(conversationId, entries);
      this.undoLog?.remove(this.entryLists, conversationId);
    }

    this.undoLog?.append(entries);
    entries.push(entryId);
    this.undoLog?.remove(this.entryRecords, entryId);
    this.entryRecords.set(entryId, new EntryRecord(entryId, conversationId, "entry", null, this.nextSequence()));
  }

  /**
   * Copies the parent's entries up to and including `forkEntryId` into the fork.
   * A fork inherits the parent's transcript up to the entry it was taken at.
   */
  copyEntries(fromConversationId: string, toConversationId: string, forkEntryId: string | null): void {
    const source = this.entries(fromConversationId);
    const inherited: string[] = [];
    if (forkEntryId === null) {
      inherited.push(...source);
    } else {
      for (const entry of source) {
        if (entry === forkEntryId) {
          break;
        }

        inherited.push(entry);
      }

      inherited.push(forkEntryId);
    }

    this.undoLog?.remove(this.entryLists, toConversationId);
    this.entryLists.set(toConversationId, inherited);
  }

  /** Reads a document's state, creating it on first use and remembering its initial value. */
  ensureDocument(
    kind: string,
    ownerId: string,
    fork: ForkPolicy,
    initial: JsonValue | null = null,
  ): DocumentState {
    const key = InMemoryStoreProvider.documentKey(kind, ownerId);
    let state = this.documentStates.get(key);
    if (state === undefined) {
      state = new DocumentState(kind, ownerId, fork, initial === null ? null : DurableJson.clone(initial));
      this.documentStates.set(key, state);
      this.undoLog?.remove(this.documentStates, key);
    }

    return state;
  }

  /** Records a committed version of a document. */
  recordDocumentVersion(state: DocumentState, entryId: string, value: JsonValue): void {
    this.undoLog?.append(state.versions);
    state.versions.push(new DocumentVersion(entryId, DurableJson.clone(value), this.nextSequence()));
  }

  /** Copies a document into another owner, honouring its fork policy. */
  copyDocument(source: DocumentState, toOwner: string, forkEntryId: string | null): void {
    const target = this.ensureDocument(source.kind, toOwner, source.fork, source.initial);

    let value: JsonValue | null;
    switch (source.fork) {
      case ForkPolicy.AsOf:
        value = forkEntryId === null ? source.latest : source.asOfOrBefore(forkEntryId, (id) => this.sequenceOf(id));
        break;
      case ForkPolicy.Current:
        value = source.latest;
        break;
      case ForkPolicy.Initial:
        value = source.initial;
        break;
      default:
        value = null;
        break;
    }

    if (value !== null) {
      this.undoLog?.append(target.versions);
      target.versions.push(new DocumentVersion(forkEntryId ?? "", DurableJson.clone(value), this.nextSequence()));
    }
  }

  /** Adds a task record, replacing any record with the same id. */
  addTask(record: DurableTaskRecord): DurableTaskRecord {
    this.undoLog?.remove(this.taskRecords, record.id);
    this.taskRecords.set(record.id, record);
    this.taskStates.set(record.id, TaskStateSnapshot.capture(record));
    return record;
  }

  /**
   * Records the current state of a task that was mutated in place. The record was
   * changed after it was added, so the provider is told about the new state; a
   * transaction that fails puts the task back where it was.
   */
  updateTask(record: DurableTaskRecord): void {
    const current = TaskStateSnapshot.capture(record);
    const previous = this.taskStates.get(record.id);
    if (this.undoLog !== null && previous !== undefined) {
      this.undoLog.restore(previous, record, this.taskStates);
    }

    this.taskStates.set(record.id, current);
  }

  /** The key a document is stored under. */
  static documentKey(kind: string, ownerId: string): string {
    return `${kind}@${ownerId}`;
  }

  /** Flushes the state to the backend. The in-memory provider has nothing to flush. */
  protected persist(): void {}

  /** Builds a snapshot of the current state, for providers that persist a whole file. */
  protected createSnapshot(): StoreSnapshot {
    return new StoreSnapshot(
      this.sequenceCounter,
      [...this.conversationRecords.values()],
      [...this.taskRecords.values()],
      [...this.documentStates.values()].map(
        (document) =>
          new DocumentSnapshot(
            document.kind,
            document.ownerId,
            document.fork,
            document.initial === null ? null : DurableJson.clone(document.initial),
            document.versions.map(
              (version) =>
                new DocumentVersionSnapshot(version.entryId, version.sequence, DurableJson.clone(version.value)),
            ),
          ),
      ),
      Object.fromEntries([...this.entryLists.entries()].map(([key, value]) => [key, [...value]])),
      [...this.entryRecords.values()],
    );
  }

  /** Restores the state from a snapshot. */
  protected restoreSnapshot(snapshot: StoreSnapshot): void {
    this.documentStates.clear();
    this.taskRecords.clear();
    this.conversationRecords.clear();
    this.taskStates.clear();
    this.entryLists.clear();
    this.entryRecords.clear();
    this.sequenceCounter = snapshot.sequence;

    for (const conversation of snapshot.conversations) {
      this.conversationRecords.set(conversation.id, conversation);
    }

    for (const task of snapshot.tasks) {
      this.taskRecords.set(task.id, task);
      this.taskStates.set(task.id, TaskStateSnapshot.capture(task));
    }
    for (const document of snapshot.documents) {
      const state = new DocumentState(document.kind, document.ownerId, document.fork, document.initial);
      for (const version of document.versions)
        state.versions.push(new DocumentVersion(version.entryId, DurableJson.clone(version.value), version.sequence));
      this.documentStates.set(InMemoryStoreProvider.documentKey(document.kind, document.ownerId), state);
    }
    for (const [key, value] of Object.entries(snapshot.entries)) this.entryLists.set(key, [...value]);
    for (const record of snapshot.entryRecords) this.entryRecords.set(record.id, record);
  }
}

    /** Append-only, fsynced JSONL storage using the shared v1 record envelope. */
    export class JsonlStoreProvider extends InMemoryStoreProvider {
      private file = "";
      private lock: number | null = null;
      private flock: typeof import("fs-ext") | null = null;
      private readonly committedEntries = new Map<string, number>();
      private readonly committedVersions = new Map<DocumentState, number>();
      private readonly emittedRecords = new Set<string>();
      private readonly emittedDocuments = new Set<DocumentState>();
      private readonly pendingConversations = new Map<string, ConversationRecord>();
      private readonly pendingEntries = new Set<string>();
      private readonly pendingDocuments = new Set<DocumentState>();
      private readonly pendingTasks = new Map<string, DurableTaskRecord>();

      override get isPersistent(): boolean { return true; }

      override open(options: DurableStoreOptions): void {
        if (!options.path) throw new Error("A JSONL store requires a path.");
        this.file = path.resolve(options.path);
        fs.mkdirSync(path.dirname(this.file), { recursive: true });
        this.acquireLock();
        try {
          if (!fs.existsSync(this.file)) return;
          const bytes = fs.readFileSync(this.file);
          let whole: unknown;
          try { whole = JSON.parse(bytes.toString("utf8")); }
          catch (error) { if (!(error instanceof SyntaxError)) throw error; }
          if (whole !== null && typeof whole === "object" && !Array.isArray(whole) &&
              ("conversations" in whole || "documents" in whole)) {
            throw new Error("Legacy JSON snapshots are unsupported by the TypeScript JSONL provider.");
          }
          if (options.format === DurableStoreFormat.Auto && bytes.toString("utf8").trim()) {
            const first = bytes.toString("utf8").split("\n").find(line => line.trim())!;
            let identifiable = false;
            try {
              const record: unknown = JSON.parse(first);
              identifiable = record !== null && typeof record === "object" &&
                "type" in record && typeof record.type === "string";
            } catch (error) {
              if (!(error instanceof SyntaxError)) throw error;
              identifiable = !bytes.includes(10) && /^\s*\{\s*"v"\s*:\s*1(?:\s*[,}]|\s*$)/.test(first);
            }
            if (!identifiable) throw new Error("The file is neither identifiable JSONL nor a supported durable store.");
          }
          let offset = 0;
          const snapshot = {
            sequence: 0, conversations: [] as ConversationRecord[], tasks: [] as DurableTaskRecord[],
            documents: [] as Array<{ kind: string; ownerId: string; fork: ForkPolicy; initial: JsonValue; versions: DocumentVersionSnapshot[] }>,
            entries: {} as Record<string, string[]>, entryRecords: [] as EntryRecord[],
          };
          while (offset < bytes.length) {
            const newline = bytes.indexOf(10, offset);
            if (newline < 0) {
              fs.truncateSync(this.file, offset);
              break;
            }
            const end = newline;
            let record: { v: number; seq: number; type: string; payload: Record<string, unknown> };
            try {
              record = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes.subarray(offset, end)));
            } catch (error) {
              if (newline >= 0) throw new Error(`Invalid JSONL record at byte ${offset}.`, { cause: error });
              fs.truncateSync(this.file, offset);
              break;
            }
            if (record.v !== 1 || !Number.isSafeInteger(record.seq) || !record.payload) {
              throw new Error(`Unsupported JSONL record at byte ${offset}.`);
            }
            const p = record.payload;
            snapshot.sequence = Math.max(snapshot.sequence, record.seq);
            switch (record.type) {
              case "conversation": {
                const item = Object.assign(new ConversationRecord(String(p["id"])), p);
                snapshot.conversations = snapshot.conversations.filter(c => c.id !== item.id);
                snapshot.conversations.push(item);
                break;
              }
              case "entry":
                (snapshot.entries[String(p["conversationId"])] ??= []).push(String(p["entryId"]));
                break;
              case "entry-record":
                snapshot.entryRecords.push(Object.assign(new EntryRecord("", "", "", null, 0), p));
                break;
              case "document": {
                const fork = typeof p["fork"] === "number"
                  ? [ForkPolicy.AsOf, ForkPolicy.Current, ForkPolicy.Initial][p["fork"]] : p["fork"];
                if (!snapshot.documents.some(d => d.kind === p["kind"] && d.ownerId === p["ownerId"]))
                  snapshot.documents.push({ kind: String(p["kind"]), ownerId: String(p["ownerId"]),
                    fork: fork as ForkPolicy, initial: (p["initial"] ?? null) as JsonValue, versions: [] });
                break;
              }
              case "document-version": {
                const doc = snapshot.documents.find(d => d.kind === p["kind"] && d.ownerId === p["ownerId"]);
                if (!doc) throw new Error("JSONL document version precedes its definition.");
                doc.versions.push(new DocumentVersionSnapshot(String(p["entryId"]), Number(p["sequence"]),
                  p["value"] as JsonValue));
                break;
              }
              case "task": {
                const task = Object.assign(new DurableTaskRecord("", "", null, null), p);
                if (typeof p["status"] === "number") task.status =
                  [TaskStatus.Pending, TaskStatus.Running, TaskStatus.Waiting, TaskStatus.Sleeping, TaskStatus.Terminal][p["status"]]!;
                snapshot.tasks = snapshot.tasks.filter(t => t.id !== task.id);
                snapshot.tasks.push(task);
                break;
              }
              default: throw new Error(`Unknown JSONL record type '${record.type}'.`);
            }
            offset = newline < 0 ? bytes.length : end + 1;
          }
          this.restoreSnapshot(snapshot);
          for (const [id, entries] of this.entryLists) this.committedEntries.set(id, entries.length);
          for (const id of this.entryRecords.keys()) this.emittedRecords.add(id);
          for (const document of this.documents) {
            this.emittedDocuments.add(document);
            this.committedVersions.set(document, document.versions.length);
          }
        } catch (error) {
          this.dispose();
          throw error;
        }
      }

      private acquireLock(): void {
        if (this.lock !== null) return;
        try {
          this.flock = createRequire(import.meta.url)("fs-ext") as typeof import("fs-ext");
        } catch (error) {
          throw new Error("JSONL requires the optional fs-ext native file-lock module on this platform; no unlocked fallback is used.", { cause: error });
        }
        const fd = fs.openSync(`${this.file}.lock`, "a", 0o600);
        try { this.flock.flockSync(fd, "exnb"); this.lock = fd; }
        catch (error) { fs.closeSync(fd); throw new Error(`JSONL store '${this.file}' is already locked.`, { cause: error }); }
      }

      override addConversation(record: ConversationRecord): void {
        super.addConversation(record);
        this.pendingConversations.set(record.id, record);
      }

      override addEntry(conversationId: string, entryId: string): void {
        super.addEntry(conversationId, entryId);
        this.pendingEntries.add(conversationId);
      }

      override copyEntries(from: string, to: string, entryId: string | null): void {
        super.copyEntries(from, to, entryId);
        this.pendingEntries.add(to);
      }

      override ensureDocument(kind: string, ownerId: string, fork: ForkPolicy, initial: JsonValue = null): DocumentState {
        const document = super.ensureDocument(kind, ownerId, fork, initial);
        this.pendingDocuments.add(document);
        return document;
      }

      override recordDocumentVersion(document: DocumentState, entryId: string, value: JsonValue): void {
        super.recordDocumentVersion(document, entryId, value);
        this.pendingDocuments.add(document);
      }

      override addTask(record: DurableTaskRecord): DurableTaskRecord {
        const task = super.addTask(record);
        this.pendingTasks.set(record.id, record);
        return task;
      }

      override updateTask(record: DurableTaskRecord): void {
        super.updateTask(record);
        this.pendingTasks.set(record.id, record);
      }

      override rollback(): void {
        super.rollback();
        this.clearPending();
      }

      private clearPending(): void {
        this.pendingConversations.clear();
        this.pendingEntries.clear();
        this.pendingDocuments.clear();
        this.pendingTasks.clear();
      }

      private records(): string[] {
        const records: string[] = [];
        const add = (type: string, payload: unknown) =>
          records.push(JSON.stringify({ v: 1, seq: this.sequence, type, payload }));
        for (const c of this.pendingConversations.values()) add("conversation", c);
        const emitted = new Set<string>();
        for (const id of this.pendingEntries) {
          const entries = this.entryLists.get(id)!;
          for (let i = this.committedEntries.get(id) ?? 0; i < entries.length; i++) {
            const entryId = entries[i]!;
            add("entry", { conversationId: id, entryId });
            const record = this.entryRecords.get(entryId);
            if (record && !this.emittedRecords.has(entryId) && !emitted.has(entryId)) {
              add("entry-record", record);
              emitted.add(entryId);
            }
          }
        }
        for (const d of this.pendingDocuments) {
          const fork = [ForkPolicy.AsOf, ForkPolicy.Current, ForkPolicy.Initial].indexOf(d.fork);
          if (!this.emittedDocuments.has(d))
            add("document", { kind: d.kind, ownerId: d.ownerId, fork, initial: d.initial });
          for (let i = this.committedVersions.get(d) ?? 0; i < d.versions.length; i++)
            add("document-version", { kind: d.kind, ownerId: d.ownerId, fork, ...d.versions[i]! });
        }
        for (const t of this.pendingTasks.values()) add("task", {
          ...t, status: Object.values(TaskStatus).indexOf(t.status),
        });
        return records;
      }

      override commit(): void {
        try { this.persist(); this.endTransaction(); }
        catch (error) { this.rollback(); throw error; }
      }

      protected override persist(): void {
        fs.mkdirSync(path.dirname(this.file), { recursive: true });
        this.acquireLock();
        const records = this.records();
        if (!records.length) { this.clearPending(); return; }
        const fd = fs.openSync(this.file, "a+", 0o600);
        const size = fs.fstatSync(fd).size;
        try {
          fs.writeFileSync(fd, `${records.join("\n")}\n`);
          fs.fsyncSync(fd);
          for (const id of this.pendingEntries) {
            const entries = this.entryLists.get(id)!;
            for (let i = this.committedEntries.get(id) ?? 0; i < entries.length; i++)
              this.emittedRecords.add(entries[i]!);
            this.committedEntries.set(id, entries.length);
          }
          for (const document of this.pendingDocuments) {
            this.emittedDocuments.add(document);
            this.committedVersions.set(document, document.versions.length);
          }
          this.clearPending();
        } catch (error) {
          fs.ftruncateSync(fd, size);
          fs.fsyncSync(fd);
          throw error;
        } finally { fs.closeSync(fd); }
      }

      override dispose(): void {
        if (this.lock !== null) {
          this.flock!.flockSync(this.lock, "un");
          fs.closeSync(this.lock);
          this.lock = null;
        }
      }
    }

/** A listener told about every successful commit. */
export type CommitListener = () => void;

/**
 * A store: the only way to read and write durable state.
 *
 * The C# store serializes commits with a `SemaphoreSlim(1, 1)`. TypeScript has no
 * semaphore, so the port chains commits onto a promise tail: a commit waits for the
 * previous one to settle, which gives the same one-at-a-time contract.
 */
export class DurableStore {
  private static readonly providerFactories = new Map<string, (options: DurableStoreOptions) => IDurableStoreProvider>();

  private readonly provider: IDurableStoreProvider;
  private readonly path: string | null;
  private readonly format: DurableStoreFormat;
  private readonly listeners = new Set<CommitListener>();

  private gateTail: Promise<void> = Promise.resolve();
  private schedulerOwner: object | null = null;
  private taskScheduler: ((taskId: string, cancellationToken: AbortSignal) => void) | null = null;

  /**
   * Registers a provider factory under a name, so a store can be opened over that
   * provider with `DurableStoreOptions.providerName`. Registering the same name again
   * replaces the factory.
   */
  static registerProvider(
    name: string,
    factory: (options: DurableStoreOptions) => IDurableStoreProvider,
  ): void {
    if (name.trim().length === 0) {
      throw new Error("A provider name is required.");
    }

    DurableStore.providerFactories.set(name.toLowerCase(), factory);
  }

  /** Removes a provider factory registered with `registerProvider`. */
  static unregisterProvider(name: string): boolean {
    return DurableStore.providerFactories.delete(name.toLowerCase());
  }

  /** Whether a provider is registered under the name. */
  static isProviderRegistered(name: string): boolean {
    return DurableStore.providerFactories.has(name.toLowerCase());
  }

  /**
   * Creates a store over the provider the options select. When the caller asks for a
   * persistent store and supplies no path, the per-user default path is used.
   */
  static create(options: DurableStoreOptions = new DurableStoreOptions()): DurableStore {
    let resolved = options;
    if (resolved.path === null && resolved.format !== DurableStoreFormat.InMemory) {
      resolved = new DurableStoreOptions();
      resolved.path = DurableStore.defaultStorePath(options.fileName);
      resolved.format = options.format;
      resolved.fileName = options.fileName;
      resolved.provider = options.provider;
      resolved.providerName = options.providerName;
    }

    const provider = DurableStore.selectProvider(resolved);
    provider.open(resolved);
    return new DurableStore(provider, resolved.path, resolved.format);
  }

  /**
   * Creates an ephemeral store: the in-memory tier. Its state lives in the process,
   * the filesystem is never touched, and the state is lost when the process ends.
   */
  static createEphemeral(): DurableStore {
    return DurableStore.create(Object.assign(new DurableStoreOptions(), { format: DurableStoreFormat.InMemory }));
  }

  /**
   * Picks the provider the options select. An explicit factory wins, then a registered
   * provider name, then the built-in formats.
   */
  private static selectProvider(options: DurableStoreOptions): IDurableStoreProvider {
    if (options.provider !== null) {
      return options.provider(options);
    }

    if (options.providerName !== null) {
      const factory = DurableStore.providerFactories.get(options.providerName.toLowerCase());
      if (factory === undefined) {
        throw new Error(
          `No durable store provider is registered under the name '${options.providerName}'. ` +
            "Register one with DurableStore.registerProvider before creating the store.",
        );
      }

      return factory(options);
    }

    switch (options.format) {
      case DurableStoreFormat.InMemory:
        return new InMemoryStoreProvider();
      case DurableStoreFormat.Jsonl:
        return new JsonlStoreProvider();
      case DurableStoreFormat.LegacyJson:
        throw new Error("Legacy JSON is unsupported in the TypeScript port; use JSONL.");
      default:
        // Auto without a path means the in-memory tier. `create` resolves the per-user
        // default path for every non-InMemory format, so a path here means a file.
        if (options.path === null) {
          return new InMemoryStoreProvider();
        }

        return new JsonlStoreProvider();
    }
  }

  /**
   * Resolves the per-user default store path: `%LOCALAPPDATA%` on Windows;
   * `$XDG_STATE_HOME` when set and non-empty, otherwise `~/.local/state`, elsewhere.
   * The store file lives in a `durable` subdirectory under that root.
   */
  static defaultStorePath(fileName = "durable-store.jsonl"): string {
    const environment = DurableStore.environment();
    const root =
      environment.platform === "win32"
        ? (environment.env["LOCALAPPDATA"] ?? "")
        : (environment.env["XDG_STATE_HOME"] ?? "").length > 0
          ? environment.env["XDG_STATE_HOME"]!
          : `${environment.env["HOME"] ?? ""}/.local/state`;

    return `${root}/durable/${fileName}`;
  }

  private static environment(): { env: Record<string, string | undefined>; platform: string } {
    const processLike = (globalThis as { process?: { env?: Record<string, string | undefined>; platform?: string } })
      .process;

    return {
      env: processLike?.env ?? {},
      platform: processLike?.platform ?? "",
    };
  }

  private constructor(provider: IDurableStoreProvider, path: string | null, format: DurableStoreFormat) {
    this.provider = provider;
    this.path = path;
    this.format = format;
  }

  /** The conversations known to this store. */
  get conversations(): readonly ConversationRecord[] {
    return this.provider.conversations;
  }

  /** The tasks known to this store. */
  get tasks(): readonly DurableTaskRecord[] {
    return this.provider.tasks;
  }

  /** The documents known to this store. */
  get documents(): readonly DocumentState[] {
    return this.provider.documents;
  }

  /** Whether the store's state is persisted. */
  get isPersistent(): boolean {
    return this.provider.isPersistent;
  }

  /** The store file, when the store has one. */
  get storePath(): string | null {
    return this.path;
  }

  /** The backend the store was opened with. */
  get storeFormat(): DurableStoreFormat {
    return this.format;
  }

  /** The provider behind this store. */
  get storeProvider(): IDurableStoreProvider {
    return this.provider;
  }

  /** The monotonic write sequence. */
  get sequence(): number {
    return this.provider.sequence;
  }

  /** Runs a transaction. Everything written inside is saved together, or discarded if it throws. */
  async commitAsync<T>(body: (tx: Transaction) => T | Promise<T>): Promise<T> {
    return this.withGate(async () => {
      const transaction = new Transaction(this);
      this.provider.beginTransaction();
      let result: T;
      try {
        result = await body(transaction);
      } catch (error) {
        // The transaction failed: undo everything it wrote, so the store is left
        // exactly as it was before the transaction started.
        this.provider.rollback();
        throw error;
      }

      this.provider.commit();
      this.notifyCommitted();
      return result;
    });
  }

  /** Reads a document's state. */
  document(kind: string, ownerId: string): DocumentState | null {
    return this.provider.document(kind, ownerId);
  }

  /** Reads a task record. */
  task(id: string): DurableTaskRecord | null {
    return this.provider.task(id);
  }

  /** Reads a conversation record. */
  conversation(id: string): ConversationRecord | null {
    return this.provider.conversation(id);
  }

  /** Reads the entry ids of a conversation, oldest first. */
  entries(conversationId: string): readonly string[] {
    return this.provider.entries(conversationId);
  }

  /** The write sequence of an entry. */
  sequenceOf(entryId: string): number {
    return this.provider.sequenceOf(entryId);
  }

  /**
   * Advances a task's durable state, letting the callback write other records in the
   * same transaction. Everything the callback writes is saved together with the
   * checkpoint, or discarded if the callback throws.
   */
  async commitTaskAsync(
    taskId: string,
    commit: (tx: Transaction) => TaskCommit | Promise<TaskCommit>,
  ): Promise<TaskCommit> {
    return this.withGate(async () => {
      const task = this.provider.task(taskId);
      if (task === null) {
        return TaskCommit.failed("unknown task");
      }

      // A terminal task's outcome is final. A commit that arrives after the task
      // finished -- a durable timer waking up, say -- must not overwrite it.
      if (task.status === TaskStatus.Terminal) {
        return task.outcome === null
          ? TaskCommit.completed(DurableJson.toElement(new TaskOutcome("completed").toJson()))
          : TaskCommit.completed(task.outcome);
      }

      const transaction = new Transaction(this);
      this.provider.beginTransaction();
      let next: TaskCommit;
      try {
        next = await commit(transaction);
      } catch (error) {
        // The phase failed: undo everything it wrote, including the task's own state,
        // so the checkpoint stays where it was and the task can be resumed.
        this.provider.rollback();
        throw error;
      }

      // The task's own state is part of the transaction: the checkpoint, the status,
      // and the outcome are written together with everything the phase wrote, or none
      // of it is.
      this.provider.updateTask(task);
      task.status = next.status;
      if (next.checkpoint !== null) {
        task.checkpoint = next.checkpoint;
      }

      if (next.waitingOn.length > 0) {
        task.waitingOn = [...next.waitingOn];
      }

      if (next.outcome !== null) {
        task.outcome = DurableJson.toElement(next.outcome.toJson());
      }

      this.provider.updateTask(task);
      this.provider.commit();
      this.notifyCommitted();
      return next;
    });
  }

  /**
   * Advances a task's durable state from a callback that writes nothing else. The
   * counterpart of the C# `CommitTaskAsync(taskId, Func<TaskCommit>)` overload: it does
   * not touch the task's `waitingOn` list.
   */
  async commitTaskCheckpointAsync(
    taskId: string,
    commit: () => TaskCommit | Promise<TaskCommit>,
  ): Promise<TaskCommit> {
    return this.withGate(async () => {
      const task = this.provider.task(taskId);
      if (task === null) {
        return TaskCommit.failed("unknown task");
      }

      // A terminal task's outcome is final.
      if (task.status === TaskStatus.Terminal) {
        return task.outcome === null
          ? TaskCommit.completed(DurableJson.toElement(new TaskOutcome("completed").toJson()))
          : TaskCommit.completed(task.outcome);
      }

      this.provider.beginTransaction();
      try {
        const next = await commit();
        task.status = next.status;
        if (next.checkpoint !== null) {
          task.checkpoint = next.checkpoint;
        }

        if (next.outcome !== null) {
          task.outcome = DurableJson.toElement(next.outcome.toJson());
        }

        this.provider.updateTask(task);
        this.provider.commit();
        this.notifyCommitted();
        return next;
      } catch (error) {
        this.provider.rollback();
        throw error;
      }
    });
  }

  /** Reads or writes an idempotency memo. */
  async memoAsync(taskId: string, key: string, value?: JsonValue): Promise<JsonValue | null> {
    return this.withGate(async () => {
      const task = this.provider.task(taskId);
      if (task === null) {
        return null;
      }

      if (value !== undefined) {
        this.provider.beginTransaction();
        try {
          task.memos[key] = DurableJson.clone(value);
          this.provider.updateTask(task);
          this.provider.commit();
          this.notifyCommitted();
          return value;
        } catch (error) {
          this.provider.rollback();
          throw error;
        }
      }

      return task.memos[key] ?? null;
    });
  }

  /** Sleeps until a deadline, saving the wake time in the checkpoint. */
  async sleepUntilAsync(
    taskId: string,
    deadline: Date,
    nextCheckpoint: () => TaskCommit,
    cancellationToken: AbortSignal = new AbortController().signal,
  ): Promise<TaskCommit> {
    const delay = deadline.getTime() - Date.now();
    if (delay > 0) {
      await sleep(delay, cancellationToken);
    }

    return this.commitTaskCheckpointAsync(taskId, nextCheckpoint);
  }

  /** Reads the outcomes of child tasks. */
  async outcomesAsync(taskIds: readonly string[]): Promise<TaskOutcome[]> {
    // The gate may already be held by the transaction that is committing this task's
    // checkpoint, so read without taking it.
    const outcomes: TaskOutcome[] = [];
    for (const id of taskIds) {
      const task = this.provider.task(id);
      if (task === null) {
        continue;
      }

      outcomes.push(
        task.outcome === null
          ? new TaskOutcome("pending")
          : (TaskOutcome.fromJson(task.outcome) ?? new TaskOutcome("pending")),
      );
    }

    return outcomes;
  }

  /**
   * Claims the store's single task-runner slot for `owner`.
   *
   * A store has at most one runner. The slot is claimed by the runtime that drives the
   * store and released when that runtime is disposed, so two runtimes over one store
   * cannot both schedule the same task: the second claim fails instead of silently
   * replacing the first.
   */
  claimTaskScheduler(owner: object, scheduler: (taskId: string, cancellationToken: AbortSignal) => void): void {
    if (this.schedulerOwner !== null && this.schedulerOwner !== owner) {
      throw new Error(
        "This store is already driven by another durable runtime. A store has one task " +
          "runner, so share the runtime instead of opening a second one over the same store.",
      );
    }

    this.schedulerOwner = owner;
    this.taskScheduler = scheduler;
  }

  /** Releases the task-runner slot, if `owner` still holds it. */
  releaseTaskScheduler(owner: object): void {
    if (this.schedulerOwner !== owner) {
      return;
    }

    this.schedulerOwner = null;
    this.taskScheduler = null;
  }

  /** Schedules a task to run. */
  scheduleTask(taskId: string, cancellationToken: AbortSignal): void {
    this.taskScheduler?.(taskId, cancellationToken);
  }

  /**
   * Re-reads the store into memory, discarding the in-memory state. A persistent store
   * holds the file's exclusive lock for its lifetime, so for such a store this is a
   * no-op.
   */
  async refreshAsync(): Promise<void> {
    await this.withGate(async () => {
      if (this.provider.isPersistent) {
        return;
      }

      const options = new DurableStoreOptions();
      options.path = null;
      options.format = DurableStoreFormat.InMemory;
      this.provider.open(options);
    });
  }

  /** Registers a listener told about every successful commit. */
  subscribe(listener: CommitListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** Releases the store's hold on its backend. */
  dispose(): void {
    this.provider.dispose();
    this.listeners.clear();
  }

  private notifyCommitted(): void {
    for (const listener of [...this.listeners]) {
      listener();
    }
  }

  private async withGate<T>(body: () => Promise<T>): Promise<T> {
    const previous = this.gateTail;
    let release!: () => void;
    this.gateTail = new Promise<void>((resolve) => {
      release = resolve;
    });

    await previous;
    try {
      return await body();
    } finally {
      release();
    }
  }
}

/** A transaction: the only way to write durable state. */
export class Transaction {
  constructor(private readonly store: DurableStore) {}

  private get provider(): IDurableStoreProvider {
    return this.store.storeProvider;
  }

  /** Creates a conversation and returns its record. */
  createConversation(
    options: {
      ownerTaskId?: string | null;
      ownerConversationId?: string | null;
      parentConversationId?: string | null;
      forkEntryId?: string | null;
    } = {},
  ): ConversationRecord {
    const id = `c${this.provider.nextSequence()}`;
    const record = new ConversationRecord(
      id,
      options.parentConversationId ?? null,
      options.forkEntryId ?? null,
    );
    record.ownerTaskId = options.ownerTaskId ?? null;
    record.ownerConversationId = options.ownerConversationId ?? null;
    this.provider.addConversation(record);
    return record;
  }

  /**
   * Appends an entry to a conversation and returns its id.
   *
   * `kind` and `data` are accepted for parity with the C# signature and are not stored:
   * the C# `AddEntry` records a fixed `"entry"` kind with a `null` payload, so the
   * arguments are unused there too. The port keeps the shape rather than inventing a
   * payload the reference does not write.
   */
  appendEntry(conversationId: string, _kind: string, _data: JsonValue | null = null): string {
    const entryId = `e${this.provider.nextSequence()}`;
    this.provider.addEntry(conversationId, entryId);
    return entryId;
  }

  /** Reads an editable copy of a document, creating it on first use. */
  doc<T extends JsonValue>(definition: DocumentDefinition<T>, ownerId: string): DocumentDraft<T> {
    const state = this.ensureDocumentWithInitial(definition, ownerId);
    const current = state.latest === null ? definition.initial() : (DurableJson.fromElement<T>(state.latest) as T);
    return new DocumentDraft<T>(this, definition, ownerId, current);
  }

  /**
   * Ensures the document exists, recording the definition's initial value the first
   * time it is seen. A `ForkPolicy.Initial` fork inherits that value, so it has to be
   * captured while the definition is in hand.
   */
  private ensureDocumentWithInitial<T extends JsonValue>(
    definition: DocumentDefinition<T>,
    ownerId: string,
  ): DocumentState {
    return this.provider.ensureDocument(
      definition.kind,
      ownerId,
      definition.fork,
      DurableJson.toElement(definition.initial()),
    );
  }

  /**
   * Reads or writes an idempotency memo as part of this transaction.
   *
   * `taskRuntime.memoAsync` is the form to call from a phase handler, outside a transaction.
   * It takes the store's commit gate, and so does the commit that is running the handler, so
   * calling it from inside a commit callback waits for a release that cannot happen: the gate
   * is not reentrant and the callback cannot return. That is a hang, not an error.
   *
   * This method exists for the one shape the other cannot express: an external effect whose
   * memo has to commit with the checkpoint, so a crash between the effect and its record
   * leaves either both or neither.
   */
  memo(taskId: string, key: string, value?: JsonValue): JsonValue | null {
    const task = this.provider.task(taskId);
    if (task === null) {
      return null;
    }

    if (value !== undefined) {
      task.memos[key] = DurableJson.clone(value);
      // The task record changed inside the transaction, so the provider is told about it; that is
      // what arms the undo with the pre-transaction memo map. A rollback then discards this write —
      // including on the *next* failed transaction, which restores the snapshot the undo put back
      // into the provider's baseline rather than the state this write produced.
      // `UndoLog.restore` is where both halves of that live.
      this.provider.updateTask(task);
      return task.memos[key];
    }

    return task.memos[key] ?? null;
  }

  /** Creates a task and returns its record. */
  createTask(
    definition: DurableTaskDefinition,
    input: JsonValue,
    options: {
      ownerConversationId?: string | null;
      ownerTaskId?: string | null;
      background?: boolean;
    } = {},
  ): DurableTaskRecord {
    const record = new DurableTaskRecord(
      `t${this.provider.nextSequence()}`,
      definition.name,
      DurableJson.clone(input),
      definition.initial(),
    );
    record.ownerConversationId = options.ownerConversationId ?? null;
    record.ownerTaskId = options.ownerTaskId ?? null;
    record.background = options.background ?? false;
    this.provider.addTask(record);
    return record;
  }

  /** Stages a document value to be written when the transaction commits. */
  commitDocument<T extends JsonValue>(
    definition: DocumentDefinition<T>,
    ownerId: string,
    value: T,
    entryId: string,
  ): void {
    const state = this.ensureDocumentWithInitial(definition, ownerId);
    this.provider.recordDocumentVersion(state, entryId, DurableJson.toElement(value));
  }
}

/** An editable copy of a document. Assigning to it stages a write. */
export class DocumentDraft<T extends JsonValue> {
  constructor(
    private readonly transaction: Transaction,
    private readonly definition: DocumentDefinition<T>,
    private readonly ownerId: string,
    public value: T,
  ) {}

  /** Stages the current value to be written when the transaction commits. */
  save(entryId: string): void {
    this.transaction.commitDocument(this.definition, this.ownerId, this.value, entryId);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Extensions
// ─────────────────────────────────────────────────────────────────────────────

/** A tool the agent can be offered. The C# layer uses `AIFunction`. */
export interface DurableTool {
  readonly name: string;
  readonly description?: string;
  readonly parameters?: Tool["parameters"];
  readonly handler?: Tool["handler"];
  readonly overridesBuiltInTool?: boolean;
}

/** A wrapper that decorates whichever tool with a given name wins. */
export class ToolWrapper {
  constructor(
    /** The name of the tool this wrapper decorates. */
    readonly toolName: string,
    /** Builds the wrapper around the winning tool. */
    readonly wrap: (tool: DurableTool) => DurableTool,
  ) {}
}

/** The input available to a prompt section renderer. */
export class SectionInput {
  constructor(
    /** The conversation the prompt is being built for. */
    readonly conversation: DurableConversation | null,
    /** The tools the agent will be offered. */
    readonly tools: readonly DurableTool[],
    /** The selected extensions. */
    readonly extensions: readonly Extension[],
    /** The working directory for this conversation. */
    readonly workingDirectory: string | null,
  ) {}
}

/** A named prompt section rendered into the system message. */
export class PromptSection {
  constructor(
    /** The section key. */
    readonly key: string,
    /** Renders the section's text, or `null` to omit it. */
    readonly render: (input: SectionInput) => Promise<string | null>,
    /** Whether the text is wrapped in a `<key>` tag. */
    readonly tag: boolean = true,
  ) {}
}

/** An extension: a named bundle of tools, prompt sections, and tasks. */
export class Extension {
  constructor(
    /** The extension name. Stored configuration references extensions by this name. */
    readonly name: string,
    /** The tools this extension contributes. */
    readonly tools: readonly DurableTool[] = [],
    /** The prompt sections this extension contributes. */
    readonly sections: readonly PromptSection[] = [],
    /** The durable tasks this extension contributes. */
    readonly tasks: readonly DurableTaskDefinition[] = [],
    /**
     * The tools this extension wraps. A wrapper never captures a base tool: it names
     * the tool it decorates, so whichever implementation of that name wins is the one
     * that gets wrapped.
     */
    readonly wraps: readonly ToolWrapper[] = [],
  ) {}
}

/** The registry of extensions. Installing the same name replaces the extension in place. */
export class ExtensionRegistry {
  private readonly installed = new Map<string, Extension>();

  /** The installed extensions, in installation order. */
  get extensions(): readonly Extension[] {
    return [...this.installed.values()];
  }

  /**
   * Installs an extension, replacing any extension with the same name. A `Map` keeps
   * the original insertion position when a key is overwritten, which is the in-place
   * replacement the C# `Dictionary` gives.
   */
  install(extension: Extension): void {
    this.installed.set(extension.name, extension);
  }

  /** Removes an extension by name. */
  uninstall(name: string): void {
    this.installed.delete(name);
  }

  /** Finds an extension by name. */
  find(name: string): Extension | null {
    return this.installed.get(name) ?? null;
  }

  /** Finds a task definition by name across all installed extensions. */
  findTask(name: string): DurableTaskDefinition | null {
    for (const extension of this.installed.values()) {
      for (const task of extension.tasks) {
        if (task.name === name) {
          return task;
        }
      }
    }

    return null;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Task runner
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The task runner. The C# runner lives inside `DurableHarness`; the port keeps it as
 * its own type so the store, the registry, and the runner can be exercised without a
 * Copilot session.
 */
export class DurableTaskRunner {
  private readonly completions = new Map<string, Deferred<TaskOutcome>>();
  private readonly running = new Set<string>();
  private readonly inFlight = new Map<string, Promise<TaskOutcome>>();
  private readonly creations = new Set<Promise<void>>();
  private disposed = false;
  private disposal: Promise<void> | undefined;
  private readonly failures: unknown[] = [];

  constructor(
    private readonly store: DurableStore,
    private readonly registry: ExtensionRegistry,
  ) {
    this.store.claimTaskScheduler(this, (taskId, cancellationToken) => {
      this.assertRunAllowed(taskId);
      this.startTask(taskId, cancellationToken);
    });
  }

  /** Creates and schedules a task; shutdown joins creations accepted before it starts. */
  async createTaskAsync(
    definition: DurableTaskDefinition,
    input: JsonValue,
    options: {
      ownerConversationId?: string | null;
      ownerTaskId?: string | null;
      background?: boolean;
      cancellationToken?: AbortSignal;
    } = {},
  ): Promise<DurableTaskRecord> {
    if (this.disposed && !(isActiveTask(this.store) && options.ownerTaskId === activeTask.getStore()?.taskId))
      throw new Error("The durable task runner is closing.");
    const creation = new Deferred<void>();
    this.creations.add(creation.promise);
    try {
      const record = await this.store.commitAsync((tx) =>
        tx.createTask(definition, input, {
          ownerConversationId: options.ownerConversationId ?? null,
          ownerTaskId: options.ownerTaskId ?? null,
          background: options.background ?? false,
        }),
      );

      this.startTask(record.id, options.cancellationToken, true);
      return record;
    } finally {
      this.creations.delete(creation.promise);
      creation.resolve();
    }
  }

  /** Runs a task to completion, resuming from its last checkpoint. */
  async runTaskAsync(taskId: string, cancellationToken: AbortSignal = new AbortController().signal): Promise<TaskOutcome> {
    this.assertRunAllowed(taskId);
    return this.runAdmittedTaskAsync(taskId, cancellationToken);
  }

  private async runAdmittedTaskAsync(taskId: string, cancellationToken: AbortSignal = new AbortController().signal): Promise<TaskOutcome> {
    const completion = this.completionFor(taskId);

    const record = this.store.task(taskId);
    if (record === null) {
      const missing = new TaskOutcome("failed", null, "unknown task");
      completion.resolve(missing);
      return missing;
    }

    // A task runs once at a time. A second caller joins the run in flight.
    if (this.running.has(taskId)) {
      return this.inFlight.get(taskId) ?? completion.promise;
    }

    this.running.add(taskId);
    const context = { store: this.store, taskId, active: true };
    const run = activeTask.run(context, () => this.runTaskCoreAsync(taskId, record, completion, cancellationToken));
    // Recorded before the first await so disposal can drain a run that is mid-commit.
    this.inFlight.set(taskId, run);
    try {
      return await run;
    } finally {
      context.active = false;
      this.inFlight.delete(taskId);
      this.running.delete(taskId);
    }
  }

  /** Waits for a task to finish, starting it when it is not already running. */
  async waitForTaskAsync(
    taskId: string,
    cancellationToken: AbortSignal = new AbortController().signal,
  ): Promise<TaskOutcome> {
    const record = this.store.task(taskId);
    if (record === null) {
      return new TaskOutcome("failed", null, "unknown task");
    }

    if (record.status === TaskStatus.Terminal) {
      return record.outcome === null
        ? new TaskOutcome("completed")
        : (TaskOutcome.fromJson(record.outcome) ?? new TaskOutcome("completed"));
    }

    return this.runTaskAsync(taskId, cancellationToken);
  }

  private assertRunAllowed(taskId: string): void {
    if (!this.disposed || this.running.has(taskId)) return;
    const parent = this.store.task(taskId)?.ownerTaskId;
    if (isActiveTask(this.store) && parent === activeTask.getStore()?.taskId && this.running.has(parent!)) return;
    throw new Error("The durable task runner is closing.");
  }

  private startTask(taskId: string, cancellationToken?: AbortSignal, admitted = false): void {
    const run = admitted ? this.runAdmittedTaskAsync(taskId, cancellationToken) : this.runTaskAsync(taskId, cancellationToken);
    void run.catch(error => { this.failures.push(error); });
  }

  /**
   * Aborts a task and its children. Children are aborted first, so a parent's abort
   * handler never runs while a child is still live.
   */
  async abortTaskAsync(
    taskId: string,
    cancellationToken: AbortSignal = new AbortController().signal,
  ): Promise<void> {
    const record = this.store.task(taskId);
    if (record === null || record.status === TaskStatus.Terminal) {
      return;
    }

    const definition = this.registry.findTask(record.name);
    if (definition === null) {
      return;
    }

    for (const child of this.store.tasks.filter((task) => task.ownerTaskId === taskId)) {
      await this.abortTaskAsync(child.id, cancellationToken);
    }

    const runtime = new TaskRuntime(this.store, record, definition, cancellationToken);
    const commit = await definition.abort(runtime);
    await this.store.commitTaskCheckpointAsync(taskId, () => commit);
  }

  /** The task graph: every task with the children it owns. */
  taskGraph(): Array<{ task: DurableTaskRecord; children: DurableTaskRecord[] }> {
    return this.store.tasks.map((task) => ({
      task,
      children: this.store.tasks.filter((child) => child.ownerTaskId === task.id),
    }));
  }

  /** Re-enters committed pending work and tasks interrupted while running or waiting. */
  async resumeAsync(cancellationToken: AbortSignal = new AbortController().signal): Promise<TaskOutcome[]> {
    const interrupted = this.store.tasks.filter(
      (task) => task.status === TaskStatus.Pending || task.status === TaskStatus.Running || task.status === TaskStatus.Waiting,
    );

    return Promise.all(interrupted.map((task) => this.runTaskAsync(task.id, cancellationToken)));
  }

  /** Drains active runs and final writes before releasing the store's task-runner slot. */
  disposeAsync(finalize?: () => Promise<void>): Promise<void> {
    this.disposed = true;
    return this.disposal ??= this.drainAsync(finalize);
  }

  private async drainAsync(finalize?: () => Promise<void>): Promise<void> {
    const failures: unknown[] = [];
    try {
      while (this.creations.size || this.inFlight.size) {
        const results = await Promise.allSettled([...this.creations, ...this.inFlight.values()]);
        for (const result of results)
          if (result.status === "rejected" && !failures.includes(result.reason)) failures.push(result.reason);
      }
      try {
        await finalize?.();
      } catch (error) {
        failures.push(error);
      }
    } finally {
      this.store.releaseTaskScheduler(this);
    }
    for (const error of this.failures) if (!failures.includes(error)) failures.push(error);
    if (failures.length === 1) throw failures[0];
    if (failures.length > 1)
      throw new AggregateError(failures, "Task runs failed.", { cause: failures[0] });
  }

  private completionFor(taskId: string): Deferred<TaskOutcome> {
    let completion = this.completions.get(taskId);
    if (completion === undefined) {
      completion = new Deferred<TaskOutcome>();
      this.completions.set(taskId, completion);
    }

    return completion;
  }

  private async runTaskCoreAsync(
    taskId: string,
    record: DurableTaskRecord,
    completion: Deferred<TaskOutcome>,
    cancellationToken: AbortSignal,
  ): Promise<TaskOutcome> {
    if (record.status === TaskStatus.Terminal) {
      const existing =
        record.outcome === null
          ? new TaskOutcome("completed")
          : (TaskOutcome.fromJson(record.outcome) ?? new TaskOutcome("completed"));
      completion.resolve(existing);
      return existing;
    }

    const definition = this.registry.findTask(record.name);
    if (definition === null) {
      const unknown = new TaskOutcome("failed", null, `unknown task definition ${record.name}`);
      completion.resolve(unknown);
      return unknown;
    }

    if (record.status === TaskStatus.Running) {
      record.restarts++;
      // The restart count is part of the task's durable state, so the provider is told
      // about it before the transaction commits.
      this.store.storeProvider.updateTask(record);
      await this.store.commitAsync(() => undefined);
    }

    try {
      return await this.runPhasesAsync(taskId, record, definition, completion, cancellationToken);
    } catch (error) {
      // Nothing observes this run: it is started fire-and-forget by the create, start
      // and resume paths. A fault that escapes the phase loop -- a store commit that
      // rejects, say -- has to settle the completion source, or every waiter waits for
      // an outcome that never comes.
      const failure = error instanceof Error ? error : new Error(String(error));
      this.failures.push(failure);
      completion.resolve(new TaskOutcome("failed", null, failure.message));
      throw failure;
    }
  }

  /** Runs the phase loop, returning the task's outcome. */
  private async runPhasesAsync(
    taskId: string,
    record: DurableTaskRecord,
    definition: DurableTaskDefinition,
    completion: Deferred<TaskOutcome>,
    cancellationToken: AbortSignal,
  ): Promise<TaskOutcome> {
    const runtime = new TaskRuntime(this.store, record, definition, cancellationToken);
    while (!cancellationToken.aborted) {
      // The task may have been aborted while it was waiting on its children. An abort
      // is durable: the record is terminal, so the run stops here instead of carrying
      // on to the next phase.
      if (this.store.task(taskId)?.status === TaskStatus.Terminal) {
        break;
      }

      const resolved = phaseOf(record.checkpoint);
      if ("error" in resolved) {
        await this.store.commitTaskCheckpointAsync(taskId, () => TaskCommit.failed(resolved.error));
        break;
      }

      const phase = resolved.phase;
      const handler = definition.phases.get(phase);
      if (handler === undefined) {
        await this.store.commitTaskCheckpointAsync(taskId, () => TaskCommit.failed(`unknown phase ${phase}`));
        break;
      }

      let commit: TaskCommit;
      try {
        commit = await handler(runtime);
      } catch (error) {
        if (cancellationToken.aborted) {
          // The task was interrupted. Its last checkpoint stays in place, so a later
          // run continues from there.
          break;
        }

        await this.store.commitTaskCheckpointAsync(taskId, () =>
          TaskCommit.failed(error instanceof Error ? error.message : String(error)),
        );
        break;
      }

      if (commit.outcome !== null) {
        completion.resolve(commit.outcome);
        break;
      }

      // A task that is waiting on child tasks holds no invocation: it is resumed when
      // its children finish.
      if (commit.status === TaskStatus.Waiting) {
        await this.waitForChildrenAsync(taskId, commit.waitingOn, cancellationToken);
        continue;
      }
    }

    const final =
      this.store.task(taskId)?.outcome == null
        ? new TaskOutcome("failed", null, "task did not finish")
        : (TaskOutcome.fromJson(this.store.task(taskId)!.outcome) ??
          new TaskOutcome("failed", null, "task did not finish"));
    completion.resolve(final);
    return final;
  }

  /**
   * Waits for child tasks to finish, then resumes the waiting parent. The parent holds
   * no invocation while it waits: its checkpoint says which children it is waiting on.
   */
  private async waitForChildrenAsync(
    _taskId: string,
    children: readonly string[],
    cancellationToken: AbortSignal,
  ): Promise<void> {
    for (const child of children) {
      const outcome = await this.waitForTaskAsync(child, cancellationToken);
      if (outcome.status === "failed") {
        // With fail-fast, the first failure aborts the others.
        for (const sibling of children) {
          if (sibling !== child) {
            await this.abortTaskAsync(sibling, cancellationToken);
          }
        }

        break;
      }
    }
  }
}

/**
 * The phase a checkpoint names, defaulting to `run`. A phase is the name of a handler,
 * so a checkpoint that carries anything but a string is a definition error: it is
 * reported rather than silently treated as `run`.
 */
function phaseOf(checkpoint: JsonValue): { phase: string } | { error: string } {
  if (checkpoint === null || typeof checkpoint !== "object" || Array.isArray(checkpoint)) {
    return { phase: "run" };
  }

  const phase = (checkpoint as JsonObject)["phase"];
  if (phase === undefined || phase === null) {
    return { phase: "run" };
  }

  return typeof phase === "string"
    ? { phase }
    : { error: `checkpoint phase must be a string, found ${jsonKindOf(phase)}` };
}

/** The JSON name of a parsed value's kind, for error messages. */
function jsonKindOf(value: JsonValue): string {
  if (value === null) return "Null";
  if (Array.isArray(value)) return "Array";
  switch (typeof value) {
    case "number":
      return "Number";
    case "boolean":
      return "Boolean";
    case "object":
      return "Object";
    default:
      return typeof value;
  }
}

/** A promise with its resolver exposed. The counterpart of `TaskCompletionSource<T>`. */
class Deferred<T> {
  private settled = false;
  private readonly inner: Promise<T>;
  private resolveInner!: (value: T) => void;

  constructor() {
    this.inner = new Promise<T>((resolve) => {
      this.resolveInner = resolve;
    });
  }

  get promise(): Promise<T> {
    return this.inner;
  }

  resolve(value: T): void {
    if (this.settled) {
      return;
    }

    this.settled = true;
    this.resolveInner(value);
  }
}

/** Resolves after `milliseconds`, or rejects when the signal aborts. */
function sleep(milliseconds: number, cancellationToken: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (cancellationToken.aborted) {
      reject(abortReason(cancellationToken));
      return;
    }

    const timer = setTimeout(() => {
      cancellationToken.removeEventListener("abort", onAbort);
      resolve();
    }, milliseconds);

    const onAbort = (): void => {
      clearTimeout(timer);
      reject(abortReason(cancellationToken));
    };

    cancellationToken.addEventListener("abort", onAbort, { once: true });
  });
}

function abortReason(cancellationToken: AbortSignal): unknown {
  return cancellationToken.reason ?? new Error("The operation was aborted.");
}

// ─────────────────────────────────────────────────────────────────────────────
// Watch stream
// ─────────────────────────────────────────────────────────────────────────────

/** One frame delivered to a watch consumer. */
export class WatchFrame<T> {
  constructor(
    /** The committed value the frame carries. */
    readonly value: T,
    /**
     * Whether the frame is a full-value reset. A reset replaces every frame that was
     * dropped, so a consumer that fell behind still converges on the committed value.
     */
    readonly reset: boolean,
    /** How many frames the reset replaced. */
    readonly dropped: number,
  ) {}
}

/** The options a watch is acquired with. */
export class WatchOptions {
  /** How many exact frames buffer before a reset replaces them. */
  capacity = 100;

  /** Stops the watch. */
  cancellationToken: AbortSignal = new AbortController().signal;
}

/**
 * A watch over one document's committed value.
 *
 * A watch starts from one stable acquisition revision: the baseline is the value as of
 * the moment the watch was acquired, and only a later commit produces a frame. Frames
 * are delivered to a single consumer, one at a time, so a slow callback never overlaps
 * the next delivery. Exact frames buffer up to `capacity`; past that the buffer is
 * replaced by one full-value reset frame, which is the bounded-frame recovery the
 * design calls for.
 */
export class WatchHandle<T> {
  private readonly queue: WatchFrame<T>[] = [];
  private draining = false;
  private closed = false;
  private last: T;
  private unsubscribe: (() => void) | null = null;

  constructor(
    private readonly read: () => T,
    private readonly onFrame: (frame: WatchFrame<T>) => void | Promise<void>,
    private readonly options: WatchOptions,
  ) {
    this.last = read();
    this.options.cancellationToken.addEventListener("abort", () => this.close(), { once: true });
  }

  /** The value the watch was acquired at. */
  get baseline(): T {
    return this.last;
  }

  /** Whether the watch is still delivering. */
  get isOpen(): boolean {
    return !this.closed;
  }

  /** The frames waiting for the consumer. */
  get pending(): number {
    return this.queue.length;
  }

  /** Records a committed value, delivering it when it differs from the last one seen. */
  push(value: T): void {
    if (this.closed) {
      return;
    }

    if (Object.is(value, this.last)) {
      return;
    }

    this.last = value;

    if (this.queue.length >= this.options.capacity) {
      const dropped = this.queue.length;
      this.queue.length = 0;
      this.queue.push(new WatchFrame<T>(value, true, dropped));
    } else {
      this.queue.push(new WatchFrame<T>(value, false, 0));
    }

    void this.drain();
  }

  /** Stops the watch. Frames already queued are still delivered. */
  close(): void {
    if (this.closed) {
      return;
    }

    this.closed = true;
    this.unsubscribe?.();
    this.unsubscribe = null;
  }

  /** Attaches the store subscription that feeds the watch. */
  attach(store: DurableStore): this {
    this.unsubscribe = store.subscribe(() => this.push(this.read()));
    return this;
  }

  private async drain(): Promise<void> {
    if (this.draining) {
      return;
    }

    this.draining = true;
    try {
      while (this.queue.length > 0) {
        const frame = this.queue.shift()!;
        await this.onFrame(frame);
      }
    } finally {
      this.draining = false;
    }
  }
}

/**
 * Watches a document's committed value.
 *
 * The watch reads the document's latest committed value on every commit, so it only
 * ever sees committed state: a value staged inside a transaction that has not committed
 * is invisible to it.
 */
export function watchDocument<T extends JsonValue>(
  store: DurableStore,
  definition: DocumentDefinition<T>,
  ownerId: string,
  onFrame: (frame: WatchFrame<T>) => void | Promise<void>,
  options: WatchOptions = new WatchOptions(),
): WatchHandle<T> {
  const read = (): T => {
    const state = store.document(definition.kind, ownerId);
    const latest = state?.latest ?? null;
    return latest === null ? definition.initial() : (DurableJson.fromElement<T>(latest) as T);
  };

  return new WatchHandle<T>(read, onFrame, options).attach(store);
}

// ─────────────────────────────────────────────────────────────────────────────
// Harness options
// ─────────────────────────────────────────────────────────────────────────────

/** The options a harness is opened with. */
export class DurableHarnessOptions {
  runtimePath: string | null = null;
  runtimeArguments: string[] = [];
  permissionHandler?: SessionConfig["onPermissionRequest"];
  enableFileHooks?: boolean;
  tolerateHostHookFailures = false;
  constructor(options: Partial<DurableHarnessOptions> = {}) { Object.assign(this, options); }
  /**
   * The environment variable that names the runtime the harness targets. The port
   * reads the runtime from here rather than assuming one.
   */
  static readonly runtimePathEnvironmentVariable = "DURABLE_RUNTIME_PATH";

  /** The runtime path the environment names, or `null` when it names none. */
  static runtimePath(): string | null {
    const processLike = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process;
    const value = processLike?.env?.[DurableHarnessOptions.runtimePathEnvironmentVariable];
    return value === undefined || value.length === 0 ? null : value;
  }
}

export class DurableSandboxAuth {
  gh?: boolean; git?: boolean;
  constructor(value: Partial<DurableSandboxAuth> = {}) { Object.assign(this, value); }
}
export class DurableSandboxCredentials {
  envVars?: Record<string, string[]>;
  constructor(value: Partial<DurableSandboxCredentials> = {}) { Object.assign(this, value); }
}
export class DurableSandboxFilesystem {
  clearPolicyOnExit?: boolean; deniedPaths?: string[]; readonlyPaths?: string[]; readwritePaths?: string[];
  constructor(value: Partial<DurableSandboxFilesystem> = {}) { Object.assign(this, value); }
}
export class DurableSandboxProxy {
  url?: string; username?: string; password?: string;
  constructor(value: Partial<DurableSandboxProxy> = {}) { Object.assign(this, value); }
}
export class DurableSandboxNetwork {
  allowedHosts?: string[]; blockedHosts?: string[]; allowOutbound?: boolean; allowLocalNetwork?: boolean;
  proxy?: DurableSandboxProxy;
  constructor(value: Partial<DurableSandboxNetwork> = {}) { Object.assign(this, value); }
}
export class DurableSandboxPolicy {
  static readonly workspaceToken = "{workspace}";
  enabled = false;
  addCurrentWorkingDirectory?: boolean; allowBypass?: boolean; allowDevToolAccess?: boolean;
  sandboxMcpServers?: boolean; sandboxLspServers?: boolean; trustWorkingDirectory?: boolean;
  auth?: DurableSandboxAuth; credentials?: DurableSandboxCredentials; filesystem?: DurableSandboxFilesystem;
  network?: DurableSandboxNetwork; keychainAccess?: boolean;
  constructor(value: Partial<DurableSandboxPolicy> = {}) { Object.assign(this, value); }
  static expandPath(value: string, cwd?: string | null): string {
    return value.replaceAll(this.workspaceToken, cwd ?? process.cwd());
  }
}

export class AgentChoices {
  extensions: string[] | null = null; tools: string[] | null = null;
  model: string | null = null; reasoningEffort: string | null = null;
  workingDirectory: string | null = null; instructions: string | null = null;
  streaming?: boolean; infiniteSessions?: SessionConfig["infiniteSessions"];
  permissions: string[] | null = null; sandbox: DurableSandboxPolicy | null = null;
  hooks?: SessionConfig["hooks"];
  constructor(value: Partial<AgentChoices> = {}) { Object.assign(this, value); }
}
export interface ResolvedAgent extends Omit<AgentChoices, "extensions" | "tools"> {
  extensions: readonly Extension[];
  tools: readonly DurableTool[];
}
export class DurableModelReport {
  static readonly NotReported = new DurableModelReport();
  constructor(readonly model: string | null = null, readonly source: string | null = null) {}
  get hasModel(): boolean { return !!this.model?.trim(); }
  get requiredModel(): string {
    if (!this.hasModel) throw new Error("The runtime reported no model.");
    return this.model!;
  }
}
export class PermissionGrant {
  constructor(readonly kind: string, readonly scope: string | null = null) {}
  static parse(text: string): PermissionGrant {
    const at = text.indexOf(":");
    return at < 0 ? new PermissionGrant(text.trim()) : new PermissionGrant(text.slice(0, at).trim(), text.slice(at + 1).trim());
  }
  toString(): string { return this.scope === null ? this.kind : `${this.kind}:${this.scope}`; }
}
export const PermissionPolicy = {
  isGranted(grants: readonly string[] | null, kind: string, scope: string | null): boolean {
    return (grants ?? []).some(text => {
      const grant = PermissionGrant.parse(text);
      if (grant.kind !== "*" && grant.kind.toLowerCase() !== kind.toLowerCase()) return false;
      return !grant.scope || grant.scope === "*" || scope !== null &&
        (scope === grant.scope || scope.startsWith(grant.scope) && "/\\ :;,".includes(scope.charAt(grant.scope.length)));
    });
  },
  handler(grants: readonly string[] | null): NonNullable<SessionConfig["onPermissionRequest"]> {
    const captured = grants?.slice() ?? null;
    return request => {
      const fields = request as unknown as Record<string, unknown>;
      const scope = request.kind === "mcp"
        ? `${request.serverName}/${request.toolName}`
        : ["toolName", "resolvedPath", "path", "fileName", "fullCommandText", "url", "name", "extensionName"]
          .map(key => fields[key]).find(value => typeof value === "string");
      return PermissionPolicy.isGranted(captured, request.kind, typeof scope === "string" ? scope : null)
        ? { kind: "approve-once" } : { kind: "reject", feedback: `denied by durable policy: no grant for ${request.kind}` };
    };
  },
};

const agentDocument = new DocumentDefinition<JsonObject>("durable.agent");
const forkDocument = new DocumentDefinition<JsonObject>("durable.fork", DocumentHistory.Rewindable, ForkPolicy.Initial);
const bindingDocument = new DocumentDefinition<JsonObject>("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);
const admissionDocument = new DocumentDefinition<JsonObject>("durable.admission");
const checkpointDocument = new DocumentDefinition<JsonObject>("durable.checkpoint");
const internalKinds = new Set([agentDocument, forkDocument, bindingDocument, admissionDocument, checkpointDocument].map(d => d.kind));
function wireChoices(choices: AgentChoices): JsonObject {
  const { hooks: _hooks, ...data } = choices;
  return JSON.parse(JSON.stringify(data));
}
function hasUnsettledAdmission(store: DurableStore, record: ConversationRecord): boolean {
  const document = store.document(admissionDocument.kind, record.id);
  const version = document?.versions.at(-1);
  const admission = document?.latest;
  // Copied document snapshots are anchored at the fork boundary, not a child submission.
  return !!admission && typeof admission === "object" && !Array.isArray(admission) &&
    admission["state"] !== "completed" &&
    (!record.parentConversationId || version?.entryId !== record.forkEntryId);
}

export class DurableConversation {
  static isHostHookFailure(message: string | null | undefined): boolean {
    return typeof message === "string" && /hook/i.test(message)
      && /sandbox|operation not permitted|code 126/i.test(message);
  }
  private attached: CopilotSession | null = null;
  private unsubscribe?: () => void;
  private pending = 0;
  private tail: Promise<unknown> = Promise.resolve();
  private hooks?: SessionConfig["hooks"];
  private passOpen = false;
  private checkpointTail: Promise<void> = Promise.resolve();
  private checkpointFailure: unknown = null;
  private readonly toolCalls = new Map<string, Record<string, unknown>>();
  private readonly observedEvents = new Set<string>();
  readonly answers: string[] = [];
  readonly turnModels: DurableModelReport[] = [];
  readonly sandboxDecisions: SessionEvent[] = [];
  effectiveModel = DurableModelReport.NotReported;
  constructor(readonly harness: DurableHarness, readonly record: ConversationRecord, hooks?: SessionConfig["hooks"]) {
    this.hooks = hooks;
  }
  get id(): string { return this.record.id; }
  get session(): CopilotSession {
    if (!this.attached) throw new Error(`Conversation '${this.id}' has no attached SDK session.`);
    return this.attached;
  }
  get sessionId(): string | null { return this.attached?.sessionId ?? null; }
  get busy(): boolean { return this.pending > 0; }
  attach(session: CopilotSession): void {
    this.unsubscribe?.();
    this.attached = session;
    this.unsubscribe = session.on(event => this.observe(event));
  }
  detach(): void { this.unsubscribe?.(); this.attached = null; }
  /** Hydrate history with `live = false` to update observations without writing checkpoints. */
  observe(event: SessionEvent, live = true): void {
    if (event.id) {
      if (this.observedEvents.has(event.id)) return;
      this.observedEvents.add(event.id);
    }
    if (event.type === "assistant.message" && event.data.content) this.answers.push(event.data.content);
    const data = event.data as unknown as Record<string, unknown>;
    if (event.type === "tool.execution_start") this.toolCalls.set(event.data.toolCallId, data);
    if (event.type === "tool.execution_complete") {
      const started = this.toolCalls.get(event.data.toolCallId);
      this.toolCalls.delete(event.data.toolCallId);
      const result = data["result"] as Record<string, unknown> | undefined;
      const error = data["error"] as Record<string, unknown> | undefined;
      const checkpoint: JsonObject = {
        checkpointId: randomUUID(), kind: "tool", eventId: `tool:${event.data.toolCallId}`,
        toolName: typeof started?.["toolName"] === "string" ? started["toolName"] : null,
        toolArgs: started?.["arguments"] === undefined ? null : JSON.stringify(started["arguments"]),
        success: event.data.success, content: typeof result?.["content"] === "string" ? result["content"] : null,
        error: typeof error?.["message"] === "string" ? error["message"] : null,
        recordedAt: new Date().toISOString(),
      };
      if (live) {
        this.checkpointTail = this.checkpointTail.then(() => this.commitAsync(tx => {
          const versions = this.harness.store.document(checkpointDocument.kind, this.id)?.versions ?? [];
          if (versions.some(version => version.value !== null && typeof version.value === "object" &&
              !Array.isArray(version.value) && version.value["eventId"] === checkpoint["eventId"])) return;
          const draft = tx.doc(checkpointDocument, this.id);
          draft.value = checkpoint; draft.save(tx.appendEntry(this.id, "checkpoint"));
        })).catch(error => {
          this.checkpointFailure ??= new Error(`Conversation '${this.id}' failed to persist a tool checkpoint.`, { cause: error });
        });
      }
    }
    const model = event.type === "session.start" ? data["selectedModel"] :
      event.type === "session.model_change" ? data["newModel"] :
      ["assistant.turn_start", "assistant.usage"].includes(event.type) ? data["model"] : null;
    const source = ["session.start", "session.model_change"].includes(event.type) ? "session" :
      event.type === "assistant.usage" ? "usage" : "turn";
    if (["assistant.turn_start", "assistant.usage"].includes(event.type)) {
      const report = typeof model === "string" && model.trim() ? new DurableModelReport(model, source) : DurableModelReport.NotReported;
      if (this.passOpen) {
        if (report.hasModel) this.turnModels[this.turnModels.length - 1] = report;
      } else { this.turnModels.push(report); this.passOpen = true; }
    }
    if (typeof model === "string" && model.trim()) {
      this.effectiveModel = new DurableModelReport(model, source);
      this.harness.effectiveModel = this.effectiveModel;
    }
    if (event.type === "assistant.message") {
      if (!this.passOpen) this.turnModels.push(DurableModelReport.NotReported);
      this.passOpen = false;
    }
    if (String(event.type) === "sandbox.decision") this.sandboxDecisions.push(event);
  }
  snapshot<T extends JsonValue>(definition: DocumentDefinition<T>): T | null {
    return DurableJson.fromElement<T>(this.harness.store.document(definition.kind, this.id)?.latest);
  }
  snapshotAsOf<T extends JsonValue>(definition: DocumentDefinition<T>, entryId: string): T | null {
    return DurableJson.fromElement<T>(this.harness.store.document(definition.kind, this.id)?.asOf(entryId));
  }
  binding(): JsonObject | null { return this.snapshot(bindingDocument); }
  admission(): JsonObject | null { return this.snapshot(admissionDocument); }
  checkpoint(): JsonObject | null { return this.snapshot(checkpointDocument); }
  async checkpointBarrierAsync(): Promise<void> {
    await this.checkpointTail;
    if (this.checkpointFailure) throw this.checkpointFailure;
  }
  choices(): AgentChoices {
    return new AgentChoices({ ...this.snapshot(agentDocument), hooks: this.hooks });
  }
  agent(): ResolvedAgent {
    const choices = this.choices();
    const extensions = this.harness.resolveExtensions(choices.extensions);
    return { ...choices, extensions, tools: this.harness.resolveTools(extensions, choices.tools) };
  }
  entries(limit = 20): string[] {
    const anchors = new Map<string, Set<string>>();
    const lineage = conversationLineage(this.harness.store, this.id);
    const owners = new Set(lineage.map(c => c.id));
    for (const doc of this.harness.store.documents) {
      if (!owners.has(doc.ownerId)) continue;
      for (const version of doc.versions) {
        const kinds = anchors.get(version.entryId) ?? new Set<string>();
        kinds.add(doc.kind); anchors.set(version.entryId, kinds);
      }
    }
    const boundaries = new Set(lineage.map(c => c.forkEntryId));
    return [...this.harness.store.entries(this.id)].reverse().filter(entry => {
      const kinds = anchors.get(entry);
      return boundaries.has(entry) || !kinds || [...kinds].some(kind => !internalKinds.has(kind));
    }).slice(0, limit);
  }
  commitAsync<T>(body: (tx: Transaction) => T | Promise<T>): Promise<T> { return this.harness.store.commitAsync(body); }
  appendEntryAsync(kind: string, data: JsonValue | null = null): Promise<string> {
    return this.commitAsync(tx => tx.appendEntry(this.id, kind, data));
  }
  configureAsync(choices: AgentChoices): Promise<void> {
    return conversationOperation(this.harness, async () => {
      this.hooks = choices.hooks;
      await this.commitAsync(tx => {
        const draft = tx.doc(agentDocument, this.id); draft.value = wireChoices(choices);
        draft.save(tx.appendEntry(this.id, "agent"));
      });
      if (this.attached) await this.session.setTools([...this.agent().tools]);
    });
  }
  submitAsync(prompt: string, timeoutMs = 180000): Promise<AssistantMessageEvent | undefined> {
    if (closingHarnesses.has(this.harness) && !isActiveTask(this.harness.store))
      return Promise.reject(new Error("The durable harness is closing."));
    this.pending++;
    const cancellation = new AbortController();
    // Serialize legacy runtimes too: their idle event is session-wide, not input-correlated.
    const run = this.tail.then(async () => {
      cancellation.signal.throwIfAborted();
      if (hasUnsettledAdmission(this.harness.store, this.record))
        throw new Error(`Conversation '${this.id}' has an unsettled admission; explicit recovery is required before submitting.`);
      const admissionId = randomUUID();
      await this.commitAsync(tx => {
        const draft = tx.doc(admissionDocument, this.id);
        draft.value = { admissionId, prompt, state: "admitted", admittedAt: new Date().toISOString() };
        draft.save(tx.appendEntry(this.id, "admission"));
      });
      try {
        const answer = await new Promise<AssistantMessageEvent | undefined>((resolve, reject) => {
          let last: AssistantMessageEvent | undefined;
          let sent = false, completed = false;
          const finish = () => {
            if (sent && completed) { cleanup(); resolve(last); }
          };
          const stop = this.session.on(event => {
            if (event.agentId) return;
            if (event.type === "assistant.message") last = event;
            if (event.type === "session.idle") { completed = true; finish(); }
            if (event.type === "session.error") {
              if (this.harness.options.tolerateHostHookFailures && DurableConversation.isHostHookFailure(event.data.message)) {
                completed = true; finish();
              } else { cleanup(); reject(new Error(event.data.message)); }
            }
          });
          const abort = () => { cleanup(); reject(cancellation.signal.reason); };
          const timer = setTimeout(() => { cleanup(); reject(new Error(`Submission timed out after ${timeoutMs}ms.`)); }, timeoutMs);
          const cleanup = () => {
            clearTimeout(timer); stop();
            cancellation.signal.removeEventListener("abort", abort);
          };
          cancellation.signal.addEventListener("abort", abort, { once: true });
          if (cancellation.signal.aborted) { abort(); return; }
          void this.session.send({ prompt }).then(() => { sent = true; finish(); },
            error => { cleanup(); reject(error); });
        });
        await this.checkpointBarrierAsync();
        await this.commitAsync(tx => {
          if (answer) {
            const checkpoint = tx.doc(checkpointDocument, this.id);
            checkpoint.value = { checkpointId: randomUUID(), kind: "turn", eventId: answer.id,
              messageId: answer.data.messageId ?? null, content: answer.data.content ?? null,
              recordedAt: new Date().toISOString() };
            checkpoint.save(tx.appendEntry(this.id, "checkpoint"));
          }
          const admission = tx.doc(admissionDocument, this.id);
          admission.value = { ...admission.value, state: "completed" };
          admission.save(tx.appendEntry(this.id, "admission"));
        });
        return answer;
      } catch (error) {
        try {
          await this.commitAsync(tx => {
            const admission = tx.doc(admissionDocument, this.id);
            admission.value = { ...admission.value, state: "uncertain" };
            admission.save(tx.appendEntry(this.id, "admission"));
          });
        } catch (settlementError) {
          throw new AggregateError([error, settlementError], "Submission failed and its admission could not be marked uncertain.", { cause: error });
        }
        throw error;
      }
    });
    this.tail = run.catch(() => {});
    const submission = run.finally(() => { this.pending--; submissions.get(this.harness)!.delete(submission); });
    submissions.get(this.harness)!.add(submission);
    submissionStops.set(submission, cancellation);
    return submission;
  }
  contextAsync(): Promise<SessionEvent[]> { return this.session.getEvents(); }
  abortAsync(): Promise<void> { return this.session.abort(); }
  forkAsync(entryId: string): Promise<DurableConversation> { return this.harness.forkAsync(this, entryId); }
  async waitForEffectiveModelAsync(timeoutMs = 30000): Promise<DurableModelReport> {
    if (this.effectiveModel.hasModel) return this.effectiveModel;
    return this.waitForEvent(() => this.effectiveModel.hasModel, () => this.effectiveModel, timeoutMs, "effective model");
  }
  async waitForSandboxDecisionAsync(match: (event: SessionEvent) => boolean, timeoutMs = 30000): Promise<SessionEvent> {
    const existing = this.sandboxDecisions.find(match);
    if (existing) return existing;
    return this.waitForEvent(() => this.sandboxDecisions.some(match), () => this.sandboxDecisions.find(match)!, timeoutMs, "sandbox decision");
  }
  private waitForEvent<T>(match: () => boolean, value: () => T, timeoutMs: number, label: string): Promise<T> {
    return new Promise((resolve, reject) => {
      const stop = this.session.on(() => {
        if (match()) { clearTimeout(timer); stop(); resolve(value()); }
      });
      const timer = setTimeout(() => { stop(); reject(new Error(`No ${label} reported within ${timeoutMs}ms.`)); }, timeoutMs);
    });
  }
}

const closingHarnesses = new WeakSet<DurableHarness>();
const conversationOperations = new WeakMap<DurableHarness, Set<Promise<void>>>();
const submissions = new WeakMap<DurableHarness, Set<Promise<unknown>>>();
const submissionStops = new WeakMap<Promise<unknown>, AbortController>();

async function conversationOperation<T>(harness: DurableHarness, operation: () => Promise<T>): Promise<T> {
  if (closingHarnesses.has(harness) && !isActiveTask(harness.store))
    throw new Error("The durable harness is closing.");
  let settle!: () => void;
  const pending = new Promise<void>(resolve => { settle = resolve; });
  const operations = conversationOperations.get(harness)!;
  operations.add(pending);
  try { return await operation(); }
  finally { operations.delete(pending); settle(); }
}

export class DurableHarness {
  static readonly DefaultModel = "gpt-6-luna";
  readonly agentDocument = agentDocument;
  readonly runner: DurableTaskRunner;
  private readonly handles = new Map<string, DurableConversation>();
  private readonly failedSessions = new Map<string, CopilotSession>();
  private readonly initializations = new Map<string, Promise<DurableConversation>>();
  private readonly conversationOperations = new Set<Promise<void>>();
  private rootCreation: Promise<DurableConversation> | undefined;
  private disposal: Promise<void> | undefined;
  private closed = false;
  effectiveModel = DurableModelReport.NotReported;
  constructor(readonly client: CopilotClient, readonly store: DurableStore,
    readonly registry: ExtensionRegistry, readonly options = new DurableHarnessOptions()) {
    this.runner = new DurableTaskRunner(store, registry);
    conversationOperations.set(this, this.conversationOperations);
    submissions.set(this, new Set());
  }
  static async openAsync(store: DurableStore, registry: ExtensionRegistry,
    options = new DurableHarnessOptions()): Promise<DurableHarness> {
    const runtimePath = options.runtimePath ?? DurableHarnessOptions.runtimePath();
    if (runtimePath && !fs.existsSync(runtimePath)) throw new Error(`Runtime executable does not exist: '${runtimePath}'.`);
    const client = new CopilotClient({ connection: RuntimeConnection.forStdio({
      path: runtimePath ?? undefined, args: options.runtimeArguments,
    }) });
    try { await client.start(); return new DurableHarness(client, store, registry, options); }
    catch (error) { await client.stop(); throw error; }
  }
  get conversations(): DurableConversation[] { return [...this.handles.values()]; }
  findConversation(id: string): DurableConversation | null {
    const record = this.store.conversation(id);
    if (!record) return null;
    let handle = this.handles.get(id);
    if (!handle) { handle = new DurableConversation(this, record); this.handles.set(id, handle); }
    return handle;
  }
  resolveExtensions(names: readonly string[] | null): Extension[] {
    return names === null ? [...this.registry.extensions] :
      names.map(name => this.registry.find(name)).filter((e): e is Extension => e !== null);
  }
  resolveTools(extensions: readonly Extension[], names: readonly string[] | null): DurableTool[] {
    const tools = new Map<string, DurableTool>();
    for (const extension of extensions) for (const tool of extension.tools) tools.set(tool.name, tool);
    for (const extension of extensions) for (const wrapper of extension.wraps) {
      const tool = tools.get(wrapper.toolName);
      if (tool) tools.set(wrapper.toolName, wrapper.wrap(tool));
    }
    return names === null ? [...tools.values()] : names.map(name => tools.get(name)).filter((t): t is DurableTool => !!t);
  }
  async buildSystemMessage(agent: ResolvedAgent, sections: readonly PromptSection[],
    conversation: DurableConversation | null): Promise<string> {
    const parts: string[] = [];
    for (const section of sections) {
      const text = await section.render(new SectionInput(conversation, agent.tools, agent.extensions, agent.workingDirectory));
      if (text?.trim()) parts.push(section.tag ? `<${section.key}>\n${text}\n</${section.key}>` : text);
    }
    if (agent.instructions?.trim()) parts.push(`<instructions>\n${agent.instructions}\n</instructions>`);
    return parts.join("\n\n");
  }
  renderSectionsAsync(conversation: DurableConversation): Promise<string> {
    const agent = conversation.agent();
    return this.buildSystemMessage(agent, agent.extensions.flatMap(e => [...e.sections]), conversation);
  }
  static buildSandboxConfig(policy: DurableSandboxPolicy, cwd: string | null = null): SandboxConfig {
    if (!policy.enabled && (policy.auth || policy.credentials || policy.filesystem || policy.network || policy.keychainAccess !== undefined)) {
      throw new Error("A disabled sandbox cannot enforce configured restrictions.");
    }
    const { trustWorkingDirectory: _trust, filesystem, network, keychainAccess, credentials, ...base } = policy;
    const config: SandboxConfig = { ...base };
    if (credentials?.envVars) {
      config.credentials = { envVars: {} };
      for (const [name, hosts] of Object.entries(credentials.envVars)) {
        if (!name.trim() || !hosts.length || hosts.some(host => !host.trim() || host.trim() === "*")) {
          throw new Error(`Invalid credential inject hosts for '${name}'.`);
        }
        config.credentials.envVars[name] = { injectHosts: [...hosts] };
      }
    }
    if (filesystem || network || keychainAccess !== undefined) config.userPolicy = {};
    if (filesystem) config.userPolicy!.filesystem = {
      clearPolicyOnExit: filesystem.clearPolicyOnExit,
      deniedPaths: (filesystem.deniedPaths ?? []).map(p => DurableSandboxPolicy.expandPath(p, cwd)),
      readonlyPaths: (filesystem.readonlyPaths ?? []).map(p => DurableSandboxPolicy.expandPath(p, cwd)),
      readwritePaths: (filesystem.readwritePaths ?? []).map(p => DurableSandboxPolicy.expandPath(p, cwd)),
    };
    if (network) {
      const { proxy, ...rest } = network;
      config.userPolicy!.network = { ...rest, allowedHosts: network.allowedHosts ?? [], blockedHosts: network.blockedHosts ?? [] };
      if (proxy?.url) config.userPolicy!.network.proxy = { ...proxy, url: proxy.url };
    }
    if (keychainAccess !== undefined) config.userPolicy!.seatbelt = { keychainAccess };
    return config;
  }
  async buildSessionConfig(choices = new AgentChoices(), conversation: DurableConversation | null = null): Promise<SessionConfig> {
    const extensions = this.resolveExtensions(choices.extensions);
    const agent: ResolvedAgent = { ...choices, extensions, tools: this.resolveTools(extensions, choices.tools) };
    const sections = extensions.flatMap(e => [...e.sections]);
    const config: SessionConfig = {
      model: choices.model ?? DurableHarness.DefaultModel, workingDirectory: choices.workingDirectory ?? undefined,
      streaming: choices.streaming, infiniteSessions: choices.infiniteSessions,
      tools: [...agent.tools], hooks: { ...choices.hooks },
      enableFileHooks: this.options.enableFileHooks,
      onPermissionRequest: this.options.permissionHandler ?? PermissionPolicy.handler(choices.permissions),
    };
    if (choices.reasoningEffort && choices.model && choices.model !== "auto") {
      // ReplayRuntime returns {list}; the SDK convenience method expects {models}
      // while generated RPC types declare an array. Normalize at the wire boundary.
      const result: unknown = await this.client.rpc.models.list({});
      const models: readonly unknown[] | null = Array.isArray(result) ? result :
        result !== null && typeof result === "object" && "list" in result && Array.isArray(result.list) ? result.list :
        result !== null && typeof result === "object" && "models" in result && Array.isArray(result.models) ? result.models : null;
      if (!models) throw new Error("Runtime models.list returned no model array in its list/models envelope.");
      const model = models.find((item: unknown): item is Record<string, unknown> =>
        item !== null && typeof item === "object" && "id" in item && item.id === choices.model);
      const effort = choices.reasoningEffort;
      if (Array.isArray(model?.["supportedReasoningEfforts"]) && model["supportedReasoningEfforts"].length) {
        // The SDK serializes this string unchanged; its static union omits the
        // C# reference's "minimal", which the native runtime accepts.
        config.reasoningEffort = effort as SessionConfig["reasoningEffort"];
      }
    }
    const systemMessage = await this.buildSystemMessage(agent, sections, conversation);
    if (systemMessage) config.systemMessage = { mode: "append", content: systemMessage };
    if (sections.length && conversation) {
      const original = choices.hooks?.onUserPromptSubmitted;
      config.hooks!.onUserPromptSubmitted = async (input, invocation) => {
        const output = await original?.(input, invocation);
        const rendered = await this.renderSectionsAsync(conversation);
        return { ...output, additionalContext: [output?.additionalContext, rendered].filter(Boolean).join("\n\n") };
      };
    }
    if (choices.sandbox) {
      config.sandboxConfig = DurableHarness.buildSandboxConfig(choices.sandbox, choices.workingDirectory);
      if (choices.sandbox.trustWorkingDirectory !== undefined)
        config.trustWorkingDirectory = choices.sandbox.trustWorkingDirectory ? "user" : "session";
    }
    return config;
  }
  private async conversationOperation<T>(operation: () => Promise<T>): Promise<T> {
    if (this.closed) throw new Error("The durable harness is closing.");
    return conversationOperation(this, operation);
  }
  createConversationAsync(choices = new AgentChoices(), ownerTaskId: string | null = null,
    ownerConversationId: string | null = null): Promise<DurableConversation> {
    return this.conversationOperation(() => this.createConversationCore(choices, ownerTaskId, ownerConversationId));
  }
  private createConversationCore(choices: AgentChoices, ownerTaskId: string | null,
    ownerConversationId: string | null): Promise<DurableConversation> {
    if (choices.sandbox) DurableHarness.buildSandboxConfig(choices.sandbox, choices.workingDirectory);
    const creation: Promise<DurableConversation> = this.store.commitAsync(tx => {
      const record = tx.createConversation({ ownerTaskId, ownerConversationId });
      const draft = tx.doc(agentDocument, record.id);
      draft.value = wireChoices(choices);
      draft.save(tx.appendEntry(record.id, "agent"));
      this.trackInitialization(record.id, creation);
      return record;
    }).then(async record => {
      const conversation = new DurableConversation(this, record, choices.hooks);
      this.handles.set(record.id, conversation);
      await this.createSession(conversation);
      return conversation;
    });
    return creation;
  }
  private trackInitialization(id: string, initialization: Promise<DurableConversation>): void {
    this.initializations.set(id, initialization);
    const clear = () => {
      if (this.initializations.get(id) === initialization) this.initializations.delete(id);
    };
    void initialization.then(clear, clear);
  }
  private async createSession(conversation: DurableConversation): Promise<void> {
    let binding = conversation.snapshot(bindingDocument);
    if (!binding?.["sessionId"]) {
      await this.store.commitAsync(tx => {
        const draft = tx.doc(bindingDocument, conversation.id);
        draft.value = { sessionId: `s${randomUUID()}`, pinned: true, model: conversation.choices().model, boundAt: new Date().toISOString() };
        draft.save(tx.appendEntry(conversation.id, "session-binding"));
      });
      binding = conversation.snapshot(bindingDocument);
    }
    const config = await this.buildSessionConfig(conversation.choices(), conversation);
    config.sessionId = String(binding!["sessionId"]);
    await this.initializeSession(conversation, await this.client.createSession(config));
  }
  private async initializeSession(conversation: DurableConversation, session: CopilotSession): Promise<void> {
    conversation.attach(session);
    try {
      for (const event of await conversation.contextAsync()) conversation.observe(event, false);
    } catch (error) {
      conversation.detach();
      try { await session.disconnect(); }
      catch (cleanupError) {
        this.failedSessions.set(conversation.id, session);
        throw new AggregateError([error, cleanupError], "Session initialization and partial-session cleanup failed.", { cause: error });
      }
      throw error;
    }
  }
  resumeConversationAsync(id: string): Promise<DurableConversation> {
    return this.conversationOperation(() => this.resumeConversationCore(id));
  }
  private resumeConversationCore(id: string): Promise<DurableConversation> {
    const pending = this.initializations.get(id);
    if (pending) return pending;
    const initialization = Promise.resolve().then(() => this.resumeSession(id));
    this.trackInitialization(id, initialization);
    return initialization;
  }
  private async resumeSession(id: string): Promise<DurableConversation> {
    const conversation = this.findConversation(id);
    if (!conversation) throw new Error(`Unknown conversation '${id}'.`);
    const failedSession = this.failedSessions.get(id);
    if (failedSession) {
      await failedSession.disconnect();
      this.failedSessions.delete(id);
    }
    if (conversation.sessionId) return conversation;
    const binding = conversation.snapshot(bindingDocument);
    const intent = conversation.snapshot(forkDocument);
    if ((conversation.record.parentConversationId || intent) &&
        ((intent?.["state"] !== 1 && intent?.["state"] !== ForkIntentState.Completed) ||
          !binding?.["sessionId"] || intent?.["sessionId"] !== binding["sessionId"])) {
      throw new Error(`Conversation '${id}' has an incomplete or abandoned native fork; explicit recovery is required.`);
    }
    if (hasUnsettledAdmission(this.store, conversation.record))
      throw new Error(`Conversation '${id}' has an unsettled admission; automatic replay is unsafe.`);
    if (!binding?.["sessionId"]) { await this.createSession(conversation); return conversation; }
    const config = await this.buildSessionConfig(conversation.choices(), conversation);
    const sessionId = String(binding["sessionId"]);
    const metadata = await this.client.getSessionMetadata(sessionId);
    let session: CopilotSession;
    if (!metadata) {
      if (binding["pinned"] !== true || intent || conversation.record.parentConversationId)
        throw new Error(`Conversation '${id}' lost its native transcript; explicit recovery is required.`);
      session = await this.client.createSession({ ...config, sessionId });
    } else {
      const { sessionId: _id, trustWorkingDirectory: _trust, ...resumeConfig } = config;
      session = await this.client.resumeSession(sessionId, {
        ...resumeConfig, suppressResumeEvent: true, continuePendingWork: false,
      });
    }
    if (session.sessionId !== sessionId)
      throw new Error(`Conversation '${id}' received a mismatched native session; explicit recovery is required.`);
    await this.initializeSession(conversation, session);
    return conversation;
  }
  rootAsync(choices = new AgentChoices()): Promise<DurableConversation> {
    return this.conversationOperation(async () => {
      if (!this.rootCreation) {
        const root = this.store.conversations.find(c => !c.parentConversationId && !c.ownerTaskId && !c.ownerConversationId);
        this.rootCreation = root ? this.resumeConversationCore(root.id) : this.createConversationCore(choices, null, null);
      }
      const creation = this.rootCreation;
      try { return await creation; }
      finally {
        if (this.rootCreation === creation) this.rootCreation = undefined;
      }
    });
  }
  forkAsync(parent: DurableConversation, entryId: string): Promise<DurableConversation> {
    return this.conversationOperation(() => this.forkCore(parent, entryId));
  }
  private forkCore(parent: DurableConversation, entryId: string): Promise<DurableConversation> {
    if (!this.store.entries(parent.id).includes(entryId)) throw new Error(`Unknown fork entry '${entryId}'.`);
    const creation: Promise<DurableConversation> = this.store.commitAsync(tx => {
      const fork = tx.createConversation({ parentConversationId: parent.id, forkEntryId: entryId });
      this.store.storeProvider.copyEntries(parent.id, fork.id, entryId);
      for (const doc of [...this.store.documents].filter(d => d.ownerId === parent.id))
        this.store.storeProvider.copyDocument(doc, fork.id, entryId);
      const intent = tx.doc(forkDocument, fork.id);
      intent.value = { parentConversationId: parent.id, forkEntryId: entryId, state: 0 };
      intent.save(tx.appendEntry(fork.id, "fork"));
      this.trackInitialization(fork.id, creation);
      return fork;
    }).then(async record => {
      const conversation = this.findConversation(record.id)!;
      // SDK boundary is exclusive. App-created entries have no SDK event identity; the
      // reference C# fork uses all native context for those boundaries.
      const forked = await this.client.rpc.sessions.fork({ sessionId: parent.session.sessionId });
      await this.store.commitAsync(tx => {
        const binding = tx.doc(bindingDocument, record.id);
        binding.value = { sessionId: forked.sessionId, pinned: false };
        binding.save(tx.appendEntry(record.id, "session-binding"));
        const intent = tx.doc(forkDocument, record.id);
        intent.value = { ...intent.value, sessionId: forked.sessionId, state: 1 };
        intent.save(tx.appendEntry(record.id, "fork"));
      });
      return this.resumeSession(conversation.id);
    });
    return creation;
  }
  createTaskAsync(...args: Parameters<DurableTaskRunner["createTaskAsync"]>): ReturnType<DurableTaskRunner["createTaskAsync"]> { return this.runner.createTaskAsync(...args); }
  runTaskAsync(...args: Parameters<DurableTaskRunner["runTaskAsync"]>): ReturnType<DurableTaskRunner["runTaskAsync"]> { return this.runner.runTaskAsync(...args); }
  waitForTaskAsync(...args: Parameters<DurableTaskRunner["waitForTaskAsync"]>): ReturnType<DurableTaskRunner["waitForTaskAsync"]> { return this.runner.waitForTaskAsync(...args); }
  abortTaskAsync(...args: Parameters<DurableTaskRunner["abortTaskAsync"]>): ReturnType<DurableTaskRunner["abortTaskAsync"]> { return this.runner.abortTaskAsync(...args); }
  resumeAsync(...args: Parameters<DurableTaskRunner["resumeAsync"]>): ReturnType<DurableTaskRunner["resumeAsync"]> { return this.runner.resumeAsync(...args); }
  taskGraph(): Array<{ task: DurableTaskRecord; owner: string | null }> {
    return this.store.tasks.map(task => ({ task, owner: task.ownerTaskId ?? task.ownerConversationId }));
  }
  disposeAsync(): Promise<void> {
    closingHarnesses.add(this);
    return this.disposal ??= (async () => {
      const failures: unknown[] = [];
      const checkpoints = Promise.allSettled(this.conversations.map(conversation => conversation.checkpointBarrierAsync()));
      try {
        await this.runner.disposeAsync(async () => {
          while (this.conversationOperations.size)
            await Promise.all([...this.conversationOperations]);
          for (const submission of submissions.get(this)!)
            submissionStops.get(submission)!.abort(new Error("The durable harness is closing."));
          while (submissions.get(this)!.size) {
            // Submission errors are returned to their callers; shutdown still awaits settlement.
            await Promise.allSettled([...submissions.get(this)!]);
          }
          for (const result of await checkpoints)
            if (result.status === "rejected") failures.push(result.reason);
          for (const conversation of this.handles.values()) conversation.detach();
          const finalCheckpoints = await Promise.allSettled(this.conversations.map(conversation => conversation.checkpointBarrierAsync()));
          for (const result of finalCheckpoints)
            if (result.status === "rejected" && !failures.includes(result.reason)) failures.push(result.reason);
        });
      } catch (error) {
        failures.push(error);
      }
      try {
        failures.push(...await this.client.stop());
      } catch (error) {
        failures.push(error);
      } finally {
        this.closed = true;
      }
      if (failures.length === 1) throw failures[0];
      if (failures.length > 1)
        throw new AggregateError(failures, "Durable shutdown failed.", { cause: failures[0] });
    })();
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Assertions
// ─────────────────────────────────────────────────────────────────────────────

/** The assertion helpers the examples use. */
export const Check = {
  /** Asserts that a condition holds. */
  that(condition: boolean, because: string): void {
    if (!condition) {
      throw new Error(`Assertion failed: ${because}`);
    }
  },

  /** Asserts that two values are equal. */
  equal<T>(expected: T, actual: T, because: string): void {
    if (!Object.is(expected, actual)) {
      throw new Error(
        `Assertion failed: ${because}\n  expected: ${format(expected)}\n  actual:   ${format(actual)}`,
      );
    }
  },

  /** Asserts that two sequences are equal, element by element. */
  sequence<T>(expected: readonly T[], actual: readonly T[], because: string): void {
    const same =
      expected.length === actual.length && expected.every((value, index) => Object.is(value, actual[index]));
    if (!same) {
      throw new Error(
        `Assertion failed: ${because}\n  expected: ${format(expected)}\n  actual:   ${format(actual)}`,
      );
    }
  },

  /** Asserts that a value is not null or undefined. */
  notNull<T>(value: T | null | undefined, because: string): T {
    if (value === null || value === undefined) {
      throw new Error(
        `Assertion failed: ${because}\n  expected: a value\n  actual:   ${format(value)}`,
      );
    }

    return value;
  },

  /** Asserts that a sequence contains a value. */
  contains<T>(values: readonly T[], value: T, because: string): void {
    if (!values.some((candidate) => Object.is(candidate, value))) {
      throw new Error(
        `Assertion failed: ${because}\n  expected to contain: ${format(value)}\n  actual:              ${format(values)}`,
      );
    }
  },
};

function format(value: unknown): string {
  if (value === null || value === undefined) {
    return "(null)";
  }

  if (typeof value === "string") {
    return `"${value}"`;
  }

  if (Array.isArray(value)) {
    return `[${value.map((item) => format(item)).join(", ")}]`;
  }

  if (typeof value === "object") {
    return JSON.stringify(value);
  }

  return String(value);
}
