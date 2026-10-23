# GitHub Copilot SDK Notes & Gotchas

Practical notes, SDK behaviors, model pricing references, and gotchas discovered while building on the [GitHub Copilot SDK](https://github.com/github/copilot-sdk).

---

## Tool authoring gotchas

These SDK behaviors cost the most time to discover and apply to any application writing custom tools for the Copilot SDK:

### Tool handlers must be `async`
A synchronous `.GetAwaiter().GetResult()` inside a tool handler deadlocks the SDK's internal JSON-RPC tool dispatch loop. Always return `Task<...>` and use `await`.

### Return `string` or `Dictionary<string, object?>`
The SDK uses a source-generated serializer by default; anonymous types and custom records throw `NotSupportedException` as tool return values. To return richer custom objects, configure `AIFunctionFactoryOptions.SerializerOptions` with a resolver such as `DefaultJsonTypeInfoResolver`.

### Nullable parameters need default values
A `string?` parameter is still marked as `required` in the generated JSON schema unless given an explicit default value (for example, `string name = ""`).

### Avoid built-in tool names — or override them explicitly
The Copilot CLI ships built-in tools. If a custom tool shares a name with a built-in tool, it is **silently shadowed**: the session starts normally and the model calls the tool, but the *built-in* handler responds instead of yours.
- Colliding tool names include: `read`, `read_file`, `edit`, `grep`, `view`, `bash`, and `glob`.
- Two ways to resolve:
  1. **Rename the tool:**
     ```csharp
     factoryOptions: new AIFunctionFactoryOptions { Name = "my_read_file", Description = "Read a file" };
     ```
  2. **Explicitly override the built-in tool:**
     ```csharp
     toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true },
     factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a file" });
     ```
  - Without the override, you will encounter unexpected built-in errors such as `Error: No file path provided` from tools intended to return custom data.

### Automatic binding of `ToolInvocation`
If you declare a `ToolInvocation` parameter in your tool delegate, the SDK automatically injects it at runtime and excludes it from the tool schema sent to the model. It provides `SessionId`, `ToolName`, and `Arguments`, allowing shared tool instances to inspect the caller's context.

---

## Event streaming quirks

- **`AssistantStreamingDeltaEvent` carries no text:** Text deltas are provided on `AssistantMessageDeltaEvent.Data.DeltaContent`. The streaming delta event only provides `TotalResponseSizeBytes`.
- **`ToolExecutionCompleteData` has no `ToolName`:** Tool name is omitted on completion events; correlate using `ToolCallId`.

---

## Models, reasoning, and token pricing

Prices can be inspected dynamically from the SDK at runtime via `ModelInfo.Billing.TokenPrices`.

| Model | Input / M tokens | Output / M tokens |
|---|---|---|
| `gpt-6-luna` | 10 | 50 |
| `claude-haiku-5.5` | 10 | 50 |
| `mai-code-1.1-flash` | 20 | 120 |
| `gpt-5.6-luna` | 20 | 120 |
| `claude-haiku-4.5` | 100 | 500 |
| `gpt-6-sol`, `claude-sonnet-5.5` | 200 | 1000–1200 |
| `claude-opus-5.5` | 400 | 2000 |
| `claude-fable-5.1` | 1000 | 5000 |

### Reasoning effort checks
Reasoning effort must only be sent when the target model advertises support in `ModelInfo.SupportedReasoningEfforts`. Setting reasoning effort on unsupported models causes the Copilot SDK runtime to reject session creation outright. `auto` does not accept a reasoning effort and dynamically re-routes per request.
