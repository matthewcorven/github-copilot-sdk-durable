// Interrupted prototype preserved for its original author's intent.
// Numbered examples do not import this module: they use the native library.
// Its hand-written runtime/session layer is not SDK parity evidence.
//
// `lib/ts/durable.ts` ports the store, the documents, the transactions and the
// extension registry, but not the harness/conversation layer that the C#
// originals use (see docs/COMPARATIVE_ANALYSIS.md §8.2). This module supplies
// that layer for the examples: it creates a conversation, pins a session for it
// on the recorded runtime, binds the two together, and offers the same
// operations the C# `DurableConversation` offers.

import { randomBytes } from "node:crypto";

import {
  DurableHarnessOptions,
  DurableJson,
  DurableStore,
  DocumentDefinition,
  DocumentHistory,
  Extension,
  ExtensionRegistry,
  ForkPolicy,
  PromptSection,
  SectionInput,
  type ConversationRecord,
  type DurableTool,
  type JsonObject,
  type JsonValue,
  type Transaction,
} from "../../lib/ts/durable.ts";
import { RuntimeClient, type RuntimeEvent, type RuntimeSession } from "./runtime.ts";

/** The choices a conversation records for itself. */
export type AgentChoices = {
  extensions: string[] | null;
  tools: string[] | null;
  model: string | null;
  reasoningEffort: string | null;
  workingDirectory: string | null;
  instructions: string | null;
  streaming: boolean | null;
  infiniteSessions: boolean | null;
  permissions: string[] | null;
  sandbox: JsonValue;
};

/** Choices with every field unset. */
export function agentChoices(overrides: Partial<AgentChoices> = {}): AgentChoices {
  return {
    extensions: null,
    tools: null,
    model: null,
    reasoningEffort: null,
    workingDirectory: null,
    instructions: null,
    streaming: null,
    infiniteSessions: null,
    permissions: null,
    sandbox: null,
    ...overrides,
  };
}

/** The choices resolved against the registry. */
export class ResolvedAgent {
  constructor(
    readonly model: string | null,
    readonly reasoningEffort: string | null,
    readonly workingDirectory: string | null,
    readonly instructions: string | null,
    readonly extensions: readonly Extension[],
    readonly tools: readonly DurableTool[],
    readonly permissions: readonly string[],
  ) {}
}

/** What the runtime said the effective model is. */
export class DurableModelReport {
  static readonly notReported = new DurableModelReport(null, null);

  constructor(
    readonly model: string | null,
    readonly source: string | null,
  ) {}

  get hasModel(): boolean {
    return this.model !== null && this.model.trim() !== "";
  }

  get requiredModel(): string {
    if (!this.hasModel) {
      throw new Error("the runtime did not report an effective model");
    }
    return this.model as string;
  }

  toString(): string {
    return this.hasModel ? (this.model as string) : "(no model reported)";
  }
}

export const AGENT_DOCUMENT = new DocumentDefinition<AgentChoices>(
  "durable.agent",
  DocumentHistory.Rewindable,
  ForkPolicy.AsOf,
  () => agentChoices(),
);

export type ForkIntent = {
  parentConversationId: string | null;
  forkEntryId: string | null;
  sessionId: string | null;
  state: string;
};

export const FORK_DOCUMENT = new DocumentDefinition<ForkIntent>(
  "durable.fork",
  DocumentHistory.Rewindable,
  ForkPolicy.Initial,
  () => ({ parentConversationId: null, forkEntryId: null, sessionId: null, state: "pending" }),
);

export type SessionBinding = {
  sessionId: string;
  model: string | null;
  boundAt: string;
  pinned: boolean;
};

export const SESSION_BINDING_DOCUMENT = new DocumentDefinition<SessionBinding>(
  "durable.session-binding",
  DocumentHistory.Rewindable,
  ForkPolicy.Initial,
  () => ({ sessionId: "", model: null, boundAt: "", pinned: false }),
);

const INTERNAL_DOCUMENT_KINDS: ReadonlySet<string> = new Set([
  AGENT_DOCUMENT.kind,
  FORK_DOCUMENT.kind,
  SESSION_BINDING_DOCUMENT.kind,
  "durable.admission",
  "durable.checkpoint",
]);

/**
 * The entries a conversation wrote for its own bookkeeping rather than for the
 * user: entries anchored only by internal documents, minus the entries a user
 * document anchors and minus the fork boundaries.
 */
export function bookkeepingEntries(
  store: DurableStore,
  conversationId: string,
  entries: readonly string[],
): Set<string> {
  const boundaries = new Set<string>();
  const anchored = new Set<string>();
  const userAnchored = new Set<string>();
  const visited = new Set<string>();

  let current: string | null = conversationId;
  while (current !== null && !visited.has(current)) {
    visited.add(current);
    const record = store.conversation(current);
    if (record === null) {
      break;
    }
    if (record.forkEntryId !== null) {
      boundaries.add(record.forkEntryId);
    }
    for (const document of store.documents) {
      if (document.ownerId !== current) {
        continue;
      }
      for (const version of document.versions) {
        if (version.entryId === "") {
          continue;
        }
        anchored.add(version.entryId);
        if (!INTERNAL_DOCUMENT_KINDS.has(document.kind)) {
          userAnchored.add(version.entryId);
        }
      }
    }
    current = record.parentConversationId;
  }

  const bookkeeping = new Set<string>();
  for (const entry of entries) {
    if (anchored.has(entry)) {
      bookkeeping.add(entry);
    }
  }
  for (const entry of userAnchored) {
    bookkeeping.delete(entry);
  }
  for (const entry of boundaries) {
    bookkeeping.delete(entry);
  }
  return bookkeeping;
}

/** Resolve extension names against the registry, dropping unknown names. */
export function resolveExtensions(registry: ExtensionRegistry, names: readonly string[] | null): Extension[] {
  if (names === null) {
    return [...registry.extensions];
  }
  const resolved: Extension[] = [];
  for (const name of names) {
    const extension = registry.find(name);
    if (extension !== null) {
      resolved.push(extension);
    }
  }
  return resolved;
}

/** Flatten the tools of the given extensions, then order them by `names`. */
export function resolveTools(extensions: readonly Extension[], names: readonly string[] | null): DurableTool[] {
  const ordered: DurableTool[] = [];
  const wraps = new Map<string, (tool: DurableTool) => DurableTool>();
  for (const extension of extensions) {
    for (const tool of extension.tools) {
      const index = ordered.findIndex((candidate) => candidate.name === tool.name);
      if (index >= 0) {
        ordered[index] = tool;
      } else {
        ordered.push(tool);
      }
    }
    for (const wrapper of extension.wraps) {
      wraps.set(wrapper.toolName, wrapper.wrap);
    }
  }
  const wrapped = ordered.map((tool) => {
    const wrap = wraps.get(tool.name);
    return wrap === undefined ? tool : wrap(tool);
  });
  if (names === null) {
    return wrapped;
  }
  const selected: DurableTool[] = [];
  for (const name of names) {
    const tool = wrapped.find((candidate) => candidate.name === name);
    if (tool !== undefined) {
      selected.push(tool);
    }
  }
  return selected;
}

/** A conversation plus the session the runtime is serving for it. */
export class DurableConversation {
  private readonly _answers: string[] = [];
  private session: RuntimeSession | null = null;
  private busy = false;

  constructor(
    private readonly harness: DurableHarness,
    readonly record: ConversationRecord,
  ) {}

  get id(): string {
    return this.record.id;
  }

  get sessionId(): string | null {
    return this.session === null ? null : this.session.sessionId;
  }

  get isBusy(): boolean {
    return this.busy;
  }

  /** The assistant messages this conversation has received, oldest first. */
  get answers(): readonly string[] {
    return [...this._answers];
  }

  attach(session: RuntimeSession): void {
    this.session = session;
  }

  /** The choices this conversation recorded, or `null` when it recorded none. */
  choices(): AgentChoices | null {
    return this.snapshot(AGENT_DOCUMENT);
  }

  /** The choices resolved against the registry. */
  agent(): ResolvedAgent {
    const choices = this.choices() ?? agentChoices();
    const extensions = resolveExtensions(this.harness.registry, choices.extensions);
    const tools = resolveTools(extensions, choices.tools);
    return new ResolvedAgent(
      choices.model,
      choices.reasoningEffort,
      choices.workingDirectory,
      choices.instructions,
      extensions,
      tools,
      choices.permissions ?? [],
    );
  }

  snapshot<T extends JsonValue>(definition: DocumentDefinition<T>): T | null {
    const latest = this.harness.store.document(definition.kind, this.id)?.latest ?? null;
    return latest === null ? null : DurableJson.fromElement<T>(latest);
  }

  snapshotAsOf<T extends JsonValue>(definition: DocumentDefinition<T>, entryId: string): T | null {
    const value = this.harness.store.document(definition.kind, this.id)?.asOf(entryId) ?? null;
    return value === null ? null : DurableJson.fromElement<T>(value);
  }

  /** The conversation's own entries, newest first, without bookkeeping. */
  entries(limit = 20): string[] {
    const all = this.harness.store.entries(this.id);
    const bookkeeping = bookkeepingEntries(this.harness.store, this.id, all);
    return [...all]
      .reverse()
      .filter((entry) => !bookkeeping.has(entry))
      .slice(0, limit);
  }

  async commitAsync<T>(body: (tx: Transaction) => T | Promise<T>): Promise<T> {
    return await this.harness.store.commitAsync(body);
  }

  async appendEntryAsync(kind: string, data: JsonValue | null = null): Promise<string> {
    return await this.commitAsync((tx) => tx.appendEntry(this.id, kind, data));
  }

  /** Record the session the runtime is serving for this conversation. */
  async bindAsync(sessionId: string, model: string | null): Promise<void> {
    const existing = this.snapshot(SESSION_BINDING_DOCUMENT);
    if (existing !== null && existing.sessionId === sessionId) {
      return;
    }
    await this.commitAsync((tx) => {
      const draft = tx.doc(SESSION_BINDING_DOCUMENT, this.id);
      draft.value = {
        sessionId,
        model,
        boundAt: new Date().toISOString(),
        pinned: true,
      };
      draft.save(tx.appendEntry(this.id, "session-binding", sessionId));
    });
  }

  /**
   * The model the runtime reported for this conversation. The recording reports
   * it on `session.start`, so that is where this looks first.
   */
  async waitForEffectiveModelAsync(timeoutMs = 10_000): Promise<DurableModelReport> {
    if (this.session === null) {
      return DurableModelReport.notReported;
    }
    const sessionId = this.session.sessionId;
    const event = await this.harness.client.waitForEvent(
      (candidate) => candidate.sessionId === sessionId && candidate.type === "session.start",
      timeoutMs,
    );
    if (event === null) {
      return DurableModelReport.notReported;
    }
    const model = event.data["selectedModel"];
    if (typeof model !== "string" || model.trim() === "") {
      return DurableModelReport.notReported;
    }
    return new DurableModelReport(model, "session");
  }

  /** Send one prompt and return the assistant message it produced. */
  async submitAsync(prompt: string): Promise<RuntimeEvent | null> {
    if (this.session === null) {
      throw new Error(`conversation '${this.id}' has no session`);
    }
    const session = this.session;
    const from = this.harness.client.eventCount;
    this.busy = true;
    try {
      await session.sendAsync(prompt);
      const message = await this.harness.client.waitForEvent(
        (candidate) => candidate.sessionId === session.sessionId && candidate.type === "assistant.message",
        10_000,
        from,
      );
      await this.harness.client.waitForEvent(
        (candidate) => candidate.sessionId === session.sessionId && candidate.type === "session.idle",
        10_000,
        from,
      );
      const content = message?.data["content"];
      if (typeof content === "string" && content.length > 0) {
        this._answers.push(content);
      }
      return message;
    } finally {
      this.busy = false;
    }
  }

  /** Everything the runtime has recorded for this session. */
  async contextAsync(): Promise<RuntimeEvent[]> {
    if (this.session === null) {
      return [];
    }
    return await this.session.getEventsAsync();
  }

  /** Replace the recorded choices and push the resolved tools to the session. */
  async configureAsync(choices: AgentChoices): Promise<void> {
    await this.commitAsync((tx) => {
      const draft = tx.doc(AGENT_DOCUMENT, this.id);
      draft.value = choices;
      draft.save(tx.appendEntry(this.id, "agent"));
    });
    if (this.session !== null) {
      await this.session.setToolsAsync(this.agent().tools);
    }
  }
}

/** The harness: a store, a registry, and a connection to the recorded runtime. */
export class DurableHarness {
  static readonly defaultModel = "gpt-6-luna";

  private readonly conversations = new Map<string, DurableConversation>();

  private constructor(
    readonly client: RuntimeClient,
    readonly store: DurableStore,
    readonly registry: ExtensionRegistry,
  ) {}

  /** Open a harness against the runtime named by `DURABLE_RUNTIME_PATH`. */
  static async openAsync(
    store: DurableStore = DurableStore.createEphemeral(),
    registry: ExtensionRegistry = new ExtensionRegistry(),
  ): Promise<DurableHarness> {
    const runtimePath = DurableHarnessOptions.runtimePath();
    if (runtimePath === null) {
      throw new Error(
        "DURABLE_RUNTIME_PATH is not set; point it at tools/ReplayRuntime/bin/Debug/net10.0/ReplayRuntime",
      );
    }
    const client = await RuntimeClient.start(runtimePath);
    return new DurableHarness(client, store, registry);
  }

  /** Create a conversation and pin a session for it. */
  async createConversationAsync(choices: AgentChoices | null = null): Promise<DurableConversation> {
    const record = await this.store.commitAsync((tx) => {
      const created = tx.createConversation();
      if (choices !== null) {
        const draft = tx.doc(AGENT_DOCUMENT, created.id);
        draft.value = choices;
        draft.save(tx.appendEntry(created.id, "agent", created.id));
      }
      return created;
    });
    const conversation = new DurableConversation(this, record);
    const session = await this.client.createSessionAsync(newSessionId());
    conversation.attach(session);
    await conversation.bindAsync(session.sessionId, choices?.model ?? DurableHarness.defaultModel);
    this.conversations.set(conversation.id, conversation);
    return conversation;
  }

  /** The first conversation with no parent and no owner task, else a new one. */
  async rootAsync(choices: AgentChoices | null = null): Promise<DurableConversation> {
    for (const record of this.store.conversations) {
      if (record.parentConversationId === null && record.ownerTaskId === null) {
        const existing = this.conversations.get(record.id);
        if (existing !== undefined) {
          return existing;
        }
        const conversation = new DurableConversation(this, record);
        this.conversations.set(record.id, conversation);
        return conversation;
      }
    }
    return await this.createConversationAsync(choices);
  }

  /** Fork a conversation at one of its entries. */
  async forkAsync(parent: DurableConversation, entryId: string): Promise<DurableConversation> {
    if (!this.store.entries(parent.id).includes(entryId)) {
      throw new Error(`'${entryId}' is not an entry of conversation '${parent.id}'.`);
    }
    const record = await this.store.commitAsync((tx) => {
      const created = tx.createConversation({
        parentConversationId: parent.id,
        forkEntryId: entryId,
      });
      this.store.storeProvider.copyEntries(parent.id, created.id, entryId);
      for (const document of this.store.documents) {
        if (document.ownerId === parent.id) {
          this.store.storeProvider.copyDocument(document, created.id, entryId);
        }
      }
      const intent = tx.doc(FORK_DOCUMENT, created.id);
      intent.value = {
        parentConversationId: parent.id,
        forkEntryId: entryId,
        sessionId: null,
        state: "pending",
      };
      intent.save(tx.appendEntry(created.id, "fork"));
      return created;
    });

    const conversation = new DurableConversation(this, record);
    const session = await this.client.createSessionAsync(newSessionId());
    conversation.attach(session);
    const parentModel = parent.choices()?.model ?? null;
    await conversation.bindAsync(session.sessionId, parentModel ?? DurableHarness.defaultModel);
    await conversation.commitAsync((tx) => {
      const intent = tx.doc(FORK_DOCUMENT, conversation.id);
      intent.value = {
        parentConversationId: parent.id,
        forkEntryId: entryId,
        sessionId: session.sessionId,
        state: "completed",
      };
      intent.save(tx.appendEntry(conversation.id, "fork"));
    });
    this.conversations.set(conversation.id, conversation);
    return conversation;
  }

  /** Render the prompt sections the conversation's extensions contribute. */
  async renderSectionsAsync(conversation: DurableConversation): Promise<string> {
    const agent = conversation.agent();
    const input = new SectionInput(
      conversation.record,
      agent.tools,
      agent.extensions,
      agent.workingDirectory,
    );
    const rendered: string[] = [];
    for (const extension of agent.extensions) {
      for (const section of extension.sections) {
        const text = await section.render(input);
        if (text === null || text.trim() === "") {
          continue;
        }
        rendered.push(section.tag ? `<${section.key}>\n${text}\n</${section.key}>` : text);
      }
    }
    return rendered.join("\n\n");
  }

  async closeAsync(): Promise<void> {
    await this.client.shutdown();
  }
}

function newSessionId(): string {
  return `s${randomBytes(16).toString("hex")}`;
}

export { Extension, ExtensionRegistry, PromptSection, SectionInput };
export type { DurableTool, JsonObject, JsonValue };
