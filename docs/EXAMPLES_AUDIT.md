# Example Suite Fidelity Audit

A strict code audit of all 31 examples (`00` through `30`) in `examples/` against the original reference implementations in Pi Durable (`packages/durable/test/examples/`).

---

## Executive Summary Table

| # | Name | Actual Fidelity | Model / Backend Mode | Audit Notes |
|---|---|---|---|---|
| **00** | `00-conversation` | **100% Real** | Offline mechanics | Real transactional commit and rollback. |
| **01** | `01-documents` | **100% Real** | Offline mechanics | Full document history and `SnapshotAsOf` verification. |
| **02** | `02-forks` | **100% Real** | Offline mechanics | Verifies entry cutoff and parent-fork state independence. |
| **03** | `03-owned-conversations` | **100% Real** | Offline mechanics | Atomic multi-entity creation (task + child session + document). |
| **04** | `04-watches` | **Simplified** | Offline mechanics | Uses `Channel<T>` for async deliveries; does not implement Pi's 100-frame buffer drop protocol. |
| **05** | `05-harness` | **100% Real** | Offline mechanics | Registry resolution and root conversation stability. |
| **06** | `06-configuration` | **100% Real** | Offline mechanics | Tool selection, deselection, and live settings decoupling. |
| **07** | `07-harness-conversations` | **100% Real** | Offline mechanics | Multiple conversation handles, fork model inheritance. |
| **08** | `08-context` | **100% Real** | Live Copilot Model | Verified contrast between stored entries and model context events. |
| **09** | `09-registry-reload` | **100% Real** | Offline mechanics | In-place extension hot-reload and tool resolution. |
| **10** | `10-extension-state` | **100% Real** | Live Copilot Model | Tool writes document; section injects state into next prompt. |
| **11** | `11-tasks` | **100% Real** | Offline mechanics | Phase transitions, checkpoints, and durable receipts. |
| **12** | `12-recovery` | **100% Real** | Offline mechanics | Process crash/reopen; memo idempotency verified across re-executions. |
| **13** | `13-chat` | **100% Real** | Live Copilot Model | Real turn execution and transcript persistence. |
| **14** | `14-system-prompt` | **100% Real** | Offline mechanics | Prompt construction across subagent vs root boundaries. |
| **15** | `15-real-model` | **100% Real** | Live Copilot Model | Real streaming tokens verified against final assembled content. |
| **16** | `16-coding-tools` | **100% Real** | Live Copilot Model | File system read/edit/bash tool loop with tool timing hooks. |
| **17** | `17-print` | **100% Real** | Live Copilot Model | Single-shot prompt submission awaiting specific completion. |
| **18** | `18-json` | **100% Real** | Live Copilot Model | Streaming JSONL session event bus. |
| **19** | `19-inbox` | **100% Real** | Live Copilot Model | Concurrent submission queuing while turn is in-flight. |
| **20** | `20-late-join` | **100% Real** | Live Copilot Model | Snapshot retrieval followed by dynamic streaming event subscription. |
| **21** | `21-subagent-foreground` | **100% Real** | Live Copilot Model | Parent tool spawns child session, awaits result, records ownership. |
| **22** | `22-subagent-background` | **100% Real** | Live Copilot Model | Background subagents persist across host restart via document registry. |
| **23** | `23-child-tasks` | **100% Real** | Offline mechanics | Task trees, parallel child wait, fail-fast abort cascades. |
| **24** | `24-compaction` | **100% Real** | Live Copilot Model | Native infinite sessions compaction across multiple conversation turns. |
| **25** | `25-coding-agent` | **100% Real** | Live Copilot Model | Dynamic `WorkingDirectory` synchronization across tool invocations. |
| **26** | `26-plan-mode` | **100% Real** | Live Copilot Model | Live `SetToolsAsync` mode switching; plan written to document. |
| **27** | `27-reviewer` | **100% Real** | Live Copilot Model | Autonomous review loop using `OnAgentStop` block decision. |
| **28** | `28-sandbox-per-conversation` | **100% Real** | Live Copilot Model | Multi-conversation sandbox routing via `ToolInvocation.SessionId`. |
| **29** | `29-tool-override` | **100% Real** | Live Copilot Model | Extension precedence replacement + `AIFunction` wrapper timing. |
| **30** | `30-reload-and-restart` | **100% Real** | Live Copilot Model | Dual-process execution; in-flight call completes on v1, next on v2, restart re-binds. |

---

## Detailed Notes on Fidelity

### Real Components Across the Suite
- **No Mock Engine:** No tests or examples use mocked in-memory stubs pretending to be the Copilot SDK. All live examples connect over JSON-RPC to the actual Copilot CLI runtime and stream real LLM completions from GitHub Copilot models (`gpt-6-luna`, `mai-code-1.1-flash`, etc.).
- **Self-Asserting Verification:** Every example terminates with explicit programmatic `Check.*` assertions that enforce invariants (state values, task statuses, document history, and event presence). A regression causes the process to exit non-zero.

### The Single Simplification: Example 04 (`04-watches`)
- In Pi Durable, document watches utilize an internal 100-frame ring-buffer protocol. If a slow consumer lags behind by more than 100 updates, Pi discards intermediate frames and emits a full snapshot reset.
- In `04-watches.cs`, this is implemented using .NET's native `System.Threading.Channels.Channel<T>`. It enforces the core durable invariant—that asynchronous notification handlers do not overlap, and always observe committed transactional states—without needing Pi's custom ring-buffer spillover heuristic.
