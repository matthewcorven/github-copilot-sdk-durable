// A small stdio client for the recorded runtime (tools/ReplayRuntime).
// Preserved interrupted prototype; not used by the native SDK examples.
//
// The runtime speaks JSON-RPC over stdin/stdout with LSP-style framing:
// `Content-Length: <bytes>\r\n\r\n<json>`. It answers a fixed set of methods and
// refuses anything it has no recording for, so this client only ever sends the
// methods the examples need.

import { spawn, type ChildProcess } from "node:child_process";

/** One event the runtime pushed for a session. */
export interface RuntimeEvent {
  readonly sessionId: string;
  readonly id: string;
  readonly timestamp: string;
  readonly parentId: string | null;
  readonly type: string;
  readonly data: Record<string, unknown>;
}

interface Pending {
  readonly resolve: (value: unknown) => void;
  readonly reject: (error: Error) => void;
}

interface Waiter {
  readonly match: (event: RuntimeEvent) => boolean;
  readonly resolve: (event: RuntimeEvent) => void;
}

/** A session the runtime is serving. */
export class RuntimeSession {
  constructor(
    readonly client: RuntimeClient,
    readonly sessionId: string,
  ) {}

  async sendAsync(prompt: string): Promise<void> {
    await this.client.request("session.sendMessages", {
      sessionId: this.sessionId,
      messages: [{ role: "user", content: prompt }],
    });
  }

  async getEventsAsync(): Promise<RuntimeEvent[]> {
    const result = (await this.client.request("session.getMessages", {
      sessionId: this.sessionId,
    })) as { events?: readonly Record<string, unknown>[] };
    return (result.events ?? []).map((item) => toEvent(this.sessionId, item));
  }

  async setToolsAsync(tools: readonly { readonly name: string; readonly description?: string }[]): Promise<void> {
    await this.client.request("session.tools.set", {
      sessionId: this.sessionId,
      tools: tools.map((tool) => ({ name: tool.name, description: tool.description ?? "" })),
    });
  }
}

function toEvent(sessionId: string, item: Record<string, unknown>): RuntimeEvent {
  const data = item["data"];
  return {
    sessionId,
    id: typeof item["id"] === "string" ? item["id"] : "",
    timestamp: typeof item["timestamp"] === "string" ? item["timestamp"] : "",
    parentId: typeof item["parentId"] === "string" ? item["parentId"] : null,
    type: typeof item["type"] === "string" ? item["type"] : "",
    data: data !== null && typeof data === "object" ? (data as Record<string, unknown>) : {},
  };
}

/** A live connection to the recorded runtime. */
export class RuntimeClient {
  private readonly child: ChildProcess;
  private buffer: Buffer = Buffer.alloc(0);
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private waiters: Waiter[] = [];
  private readonly seen: RuntimeEvent[] = [];
  private stderrText = "";
  private exited = false;

  private constructor(child: ChildProcess) {
    this.child = child;
    const stdout = child.stdout;
    const stderr = child.stderr;
    if (stdout === null || stderr === null) {
      throw new Error("the runtime must be spawned with piped stdio");
    }
    stdout.on("data", (chunk: Buffer) => this.onData(chunk));
    stderr.on("data", (chunk: Buffer) => {
      this.stderrText += chunk.toString("utf8");
    });
    child.on("exit", (code) => {
      this.exited = true;
      const detail = this.stderrText.trim();
      const message = `the runtime exited with code ${code ?? "null"}${detail === "" ? "" : `: ${detail}`}`;
      for (const entry of this.pending.values()) {
        entry.reject(new Error(message));
      }
      this.pending.clear();
    });
  }

  /** Spawn the runtime and complete the `connect` handshake. */
  static async start(command: string, args: readonly string[] = []): Promise<RuntimeClient> {
    const child = spawn(command, [...args], { stdio: ["pipe", "pipe", "pipe"] });
    const client = new RuntimeClient(child);
    await client.request("connect", {});
    return client;
  }

  /** Create a session. The runtime serves a recorded transcript for it. */
  async createSessionAsync(sessionId: string): Promise<RuntimeSession> {
    const result = (await this.request("session.create", { sessionId })) as { sessionId?: unknown };
    const id = typeof result.sessionId === "string" ? result.sessionId : sessionId;
    return new RuntimeSession(this, id);
  }

  /** How many session events have been observed so far. */
  get eventCount(): number {
    return this.seen.length;
  }

  /** Everything the runtime has pushed, oldest first. */
  get events(): readonly RuntimeEvent[] {
    return this.seen;
  }

  async request(method: string, params: Record<string, unknown> = {}): Promise<unknown> {
    if (this.exited) {
      throw new Error("the runtime has exited");
    }
    const id = this.nextId;
    this.nextId += 1;
    return new Promise<unknown>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      void this.write({ jsonrpc: "2.0", id, method, params });
    });
  }

  /**
   * Wait for the first event at or after `from` that `match` accepts. Returns
   * `null` on timeout rather than throwing, so callers can report "not reported".
   */
  async waitForEvent(
    match: (event: RuntimeEvent) => boolean,
    timeoutMs = 10_000,
    from = 0,
  ): Promise<RuntimeEvent | null> {
    for (const event of this.seen.slice(from)) {
      if (match(event)) {
        return event;
      }
    }
    return new Promise<RuntimeEvent | null>((resolve) => {
      let timer: ReturnType<typeof setTimeout> | null = null;
      const waiter: Waiter = {
        match,
        resolve: (event) => {
          if (timer !== null) {
            clearTimeout(timer);
          }
          resolve(event);
        },
      };
      timer = setTimeout(() => {
        this.waiters = this.waiters.filter((candidate) => candidate !== waiter);
        resolve(null);
      }, timeoutMs);
      this.waiters.push(waiter);
    });
  }

  /** Ask the runtime to shut down, then close its stdin. */
  async shutdown(): Promise<void> {
    try {
      await this.request("runtime.shutdown", {});
    } catch {
      // The runtime may already be gone; closing stdin is enough.
    }
    this.child.stdin?.end();
  }

  private async write(message: Record<string, unknown>): Promise<void> {
    const body = Buffer.from(JSON.stringify(message), "utf8");
    const header = Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, "utf8");
    this.child.stdin?.write(Buffer.concat([header, body]));
  }

  private onData(chunk: Buffer): void {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    for (;;) {
      const headerEnd = this.buffer.indexOf("\r\n\r\n");
      if (headerEnd < 0) {
        return;
      }
      const header = this.buffer.subarray(0, headerEnd).toString("utf8");
      const match = /^content-length:\s*(\d+)$/im.exec(header);
      const bodyStart = headerEnd + 4;
      if (match === null) {
        this.buffer = this.buffer.subarray(bodyStart);
        continue;
      }
      const length = Number(match[1]);
      if (this.buffer.length < bodyStart + length) {
        return;
      }
      const body = this.buffer.subarray(bodyStart, bodyStart + length).toString("utf8");
      this.buffer = this.buffer.subarray(bodyStart + length);
      this.onMessage(JSON.parse(body) as Record<string, unknown>);
    }
  }

  private onMessage(message: Record<string, unknown>): void {
    if (message["method"] === "session.event") {
      const params = message["params"] as { sessionId?: unknown; event?: Record<string, unknown> } | undefined;
      if (params === undefined || params.event === undefined) {
        return;
      }
      const sessionId = typeof params.sessionId === "string" ? params.sessionId : "";
      const event = toEvent(sessionId, params.event);
      this.seen.push(event);
      const ready = this.waiters.filter((waiter) => waiter.match(event));
      this.waiters = this.waiters.filter((waiter) => !ready.includes(waiter));
      for (const waiter of ready) {
        waiter.resolve(event);
      }
      return;
    }

    const id = message["id"];
    if (typeof id !== "number") {
      return;
    }
    const entry = this.pending.get(id);
    if (entry === undefined) {
      return;
    }
    this.pending.delete(id);
    const error = message["error"];
    if (error !== undefined && error !== null) {
      entry.reject(new Error(JSON.stringify(error)));
      return;
    }
    entry.resolve(message["result"]);
  }
}
