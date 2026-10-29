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

## Permissions

`SessionConfig.OnPermissionRequest` is a
`Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>`. `PermissionHandler.ApproveAll`
is the SDK's default and it approves **everything** — every custom tool call, every file read and
write, every shell command. A host that leaves it in place has no durable record of what its agent
was allowed to do.

### Custom tools are permission-gated

A tool registered with `DefineTool` raises a `PermissionRequestCustomTool` before it runs. This is
easy to miss because the default handler approves silently, so the request is invisible until you
install a handler that logs or denies. Any fail-closed policy therefore has to grant `custom-tool`
by name, or every tool call is denied.

### Only some denial variants work

The Copilot CLI's permission host accepts these decision variants: `approve-once`,
`approve-for-session`, `approve-for-location`, `reject`, `user-not-available`, `approved`.

`PermissionDecisionDeniedByRules` and `PermissionDecisionDeniedNoApprovalRuleAndCouldNotRequestFromUser`
serialize to variants the CLI does not know, so they fail at the runtime boundary with
`permission host returned malformed payload: unknown variant '...'` and surface to the model as a
`GenericFailure` tool error. Use `PermissionDecisionReject { Feedback = "..." }` instead: it
produces a clean `The user rejected this tool call. User feedback: <text>` error that the model can
read and explain to the user.

### There is no per-session permission API

`CopilotSession` exposes `SetToolsAsync` and nothing else in the permission/tool surface. The
permission handler is fixed when the `SessionConfig` is constructed, so a host cannot widen or
narrow grants on a live session — only on the next session creation, restore, or fork.

### Request variants and their kinds

`PermissionRequest` is an abstract base with twelve concrete variants, each with a parameterless
constructor and `required` members. `Kind` is a plain `string`, not an enum.

| Variant | `Kind` | Required members |
|---|---|---|
| `PermissionRequestCustomTool` | `custom-tool` | `ToolName`, `ToolDescription` |
| `PermissionRequestRead` | `read` | `Path`, `Intention` |
| `PermissionRequestWrite` | `write` | `FileName`, `Diff`, `Intention`, `CanOfferSessionApproval` |
| `PermissionRequestShell` | `shell` | `FullCommandText`, `Commands`, `Intention`, `PossiblePaths`, `PossibleUrls`, `HasWriteFileRedirection`, `CanOfferSessionApproval` |
| `PermissionRequestUrl` | `url` | `Url`, `Intention` |
| `PermissionRequestMcp` | `mcp` | `ServerName`, `ToolName`, `ToolTitle`, `ReadOnly` |
| `PermissionRequestMemory` | `memory` | `Fact` |
| `PermissionRequestHook` | `hook` | `ToolName` |
| `PermissionRequestWorkflow` | `workflow` | `Name`, `Description`, `Operation`, `Phases`, `ApprovalKey`, `CanPersistApproval` |
| `PermissionRequestExtensionManagement` | `extension-management` | `Operation` |
| `PermissionRequestExtensionPermissionAccess` | `extension-permission-access` | `ExtensionName`, `Capabilities` |
| `PermissionRequestExtensionEnvAccess` | `extension-env-access` | `ExtensionName`, `EnvironmentVariables` |

`PermissionRequest`, `PermissionInvocation`, and the request variants are in `GitHub.Copilot`;
`PermissionDecision` and its variants are in `GitHub.Copilot.Rpc`.

### Sandbox credential masking and MCP/LSP confinement

`SandboxCredentialsConfig.EnvVars` maps a **variable name** to its injection hosts. The value is
never part of the wire type and never part of the durable policy (`DurableSandboxCredentials`
carries the same name-to-hosts shape). The runtime reads the value from the host process's
environment, replaces it inside the sandbox with a random sentinel, and substitutes the real value
only into HTTPS request headers addressed to an injection host. Consequences worth remembering:

- A sandboxed command that echoes the variable sees the sentinel, not the secret — a length and a
  digest that do not match the real value, and never an empty string. `examples/34-sandbox-sdk-network-credentials.cs`
  proves masking that way, without ever printing the value.
- Substitution does not apply to plaintext HTTP, URLs, request bodies, encoded credentials, or
  signed requests, so a masked variable is not a general-purpose secret channel.
- Injection grants never widen the network policy: `Network.AllowOutbound = false` still refuses
  the connection, and `Network.AllowedHosts`/`BlockedHosts` still apply.

`SandboxConfig.SandboxMcpServers` and `SandboxLspServers` **default to on**: an MCP or LSP server
the session starts runs inside the session sandbox unless the policy explicitly sets the flag to
`false`. Opting out is not just "run this server outside the sandbox" — it also lets that server's
own remote-MCP egress leave the sandbox, which is why the flag is read together with `Enabled`.
`SandboxConfig.ManagedMcpRoutingLocked`/`ManagedLspRoutingLocked` are the runtime's own routing
locks and are not part of the durable policy surface.

A policy's configured `Network.Proxy` is not echoed back: on macOS seatbelt a policy with a proxy
URL resolved with `SandboxDecisionDataPolicyResolved.ProxyMode == none`. Assert the URL on the
configuration the durable layer built, and assert only that the resolved mode is one of
`none`/`loopback`/`external`.

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
