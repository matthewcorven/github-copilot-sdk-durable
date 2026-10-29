# Sandbox capability matrix

What the durable sandbox policy can express, what each host actually enforces, and what the
runtime reports back. The policy is a durable-layer object: it is stored with a
conversation's choices, expanded at session-build time, and handed to the SDK as
`GitHub.Copilot.Rpc.SandboxConfig`. Everything below the wire type is the SDK's and the
host's business, not ours.

- Policy type: `DurableSandboxPolicy` (`lib/Durable.cs`)
- Wire type: `GitHub.Copilot.Rpc.SandboxConfig`
- Mapper: `DurableHarness.BuildSandboxConfig(policy, workingDirectory)`
- Examples: `examples/33-sandbox-sdk-filesystem.cs` (filesystem), `examples/34-sandbox-sdk-network-credentials.cs` (network, credentials, dev tools, MCP/LSP)
- SDK: `GitHub.Copilot.SDK` 1.0.26, sandbox types annotated `GHCP001` (evaluation only)

## 1. Backends by platform

The runtime picks a backend; the policy does not choose one. `SandboxBackend` is a
string-wrapper value type, so the reported value is the authority.

| Platform | Backend | Reported as | Notes |
|---|---|---|---|
| macOS | Seatbelt (`sandbox-exec`) | `seatbelt` | The only backend on macOS. Denials surface as `EPERM` from the sandboxed process. |
| Linux | Bubblewrap | `bubblewrap` | Requires user namespaces; a host without them degrades. |
| Windows | Process container | `process_container` | Job-object + restricted-token containment. |
| Any | none | `none` / `unsupported` | No backend engaged. The policy is built and mapped but nothing enforces it. |

`SandboxPlatform` reports `macos`, `linux`, `windows`, or `other`.

## 2. Capability × enforcement

"Enforced" means the OS refuses the operation. "Advisory" means the runtime records the
intent and reports it, but the operation is not blocked by the kernel. "Not reported" means
the runtime does not tell us either way.

| Capability | Policy member | macOS seatbelt | Linux bubblewrap | Windows process container | Reported back |
|---|---|---|---|---|---|
| Writable paths | `Filesystem.ReadwritePaths` | Enforced | Enforced | Enforced | `SandboxFilesystemPolicyDetails.ReadwritePaths` |
| Read-only paths | `Filesystem.ReadonlyPaths` | Enforced | Enforced | Enforced | `SandboxFilesystemPolicyDetails.ReadonlyPaths` |
| Denied paths | `Filesystem.DeniedPaths` | Enforced | Enforced | Enforced | `SandboxFilesystemPolicyDetails.DeniedPaths` |
| Clear policy on exit | `Filesystem.ClearPolicyOnExit` | Enforced | Enforced | Enforced | not reported |
| Network proxy | `Network.Proxy` | Enforced | Enforced | Enforced | `SandboxDecisionDataEnforcementState` |
| MCP servers in sandbox | `SandboxMcpServers` | Enforced | Enforced | Enforced | `SandboxDecisionDataSpawnCompleted` |
| LSP servers in sandbox | `SandboxLspServers` | Enforced | Enforced | Enforced | `SandboxDecisionDataSpawnCompleted` |
| GitHub auth in sandbox | `Auth.Gh` | Enforced | Enforced | Enforced | not reported |
| Git auth in sandbox | `Auth.Git` | Enforced | Enforced | Enforced | not reported |
| Injected credentials | `Credentials.EnvVars` | Enforced | Enforced | Enforced | not reported |
| Keychain access | `KeychainAccess` | Enforced | n/a (no keychain) | n/a | not reported |
| Bypass | `AllowBypass` | Advisory | Advisory | Advisory | `SandboxDecisionDataBypassDecided` |
| Dev-tool access | `AllowDevToolAccess` | Advisory | Advisory | Advisory | `SandboxDecisionDataPolicyResolved` |
| Trust working directory | `TrustWorkingDirectory` | Advisory | Advisory | Advisory | `SandboxDecisionDataPolicyResolved` |
| Outbound network | `Network.AllowOutbound` | Enforced (see §8) | Enforced | Enforced | `SandboxDecisionDataPolicyResolved.AllowOutbound` |
| Local network | `Network.AllowLocalNetwork` | Enforced (see §8) | Enforced | Enforced | `SandboxDecisionDataPolicyResolved.AllowLocalNetwork` |
| Host allowlist | `Network.AllowedHosts` | Enforced | Enforced | Enforced | `SandboxDecisionDataAccessDenied` |
| Host blocklist | `Network.BlockedHosts` | Enforced | Enforced | Enforced | `SandboxDecisionDataAccessDenied` |

Advisory rows are not a gap in the durable layer: they are settings the runtime consults
when it decides whether to spawn inside the sandbox at all. They change *whether* the
sandbox engages, not what the kernel blocks once it has.

## 3. Enforcement points

A denial names the enforcement point that refused it (`SandboxEnforcementPoint`):

| Point | What it covers |
|---|---|
| `shell` | A process spawned by the shell tool. |
| `builtin_filesystem` | The SDK's own read/write/edit tools. |
| `mcp` | An MCP server call. |
| `lsp` | An LSP server call. |
| `search` | A search tool. |
| `web_fetch` | A web fetch. |

## 4. What `SandboxFilesystemPolicyDetails` reports — and what it does not

`SandboxFilesystemPolicyDetails` is the *effective* filesystem policy, in policy order. It
is populated only when content capture is enabled on the session; when capture is off the
object is absent and the example must not assert on it.

It reports:

- `ReadwritePaths`, `ReadonlyPaths`, `DeniedPaths` — the resolved, expanded path lists, in
  the order the policy declared them.

It does **not** report:

- **Which rule matched a given denial.** `SandboxDecisionDataAccessDenied.DeniedResource`
  is empty for a denial the runtime inferred from the denied process's output rather than
  from a path it named. On macOS seatbelt a shell redirect into a denied path is reported
  with an empty resource.
- **The denial class the caller expects.** The runtime classifies a denial by what the
  denied process tried to do. A redirect that writes into a denied path is reported as
  `filesystem_read` on seatbelt, because the process's failed `open` is what the sandbox
  saw. `SandboxSupport.AssertDenied` therefore accepts a set of classes rather than one.
- **Per-path allow/deny decisions for paths outside the policy.** A path the policy never
  mentions is not reported at all; the backend's default applies.
- **Whether the policy was *honoured* versus merely *accepted*.** That is what
  `SandboxDecisionDataEnforcementState.Outcome` is for: `engaged`, `inactive`, `degraded`,
  or `failed`.

## 5. Run modes in the examples

Sandbox examples use exactly two words, resolved by `SandboxSupport.Resolve`:

| Mode | Meaning | What the example asserts |
|---|---|---|
| `Enforced` | An enforcement state reported `engaged` with a real backend. | The policy **and** the enforcement. |
| `Replayed` | No enforcement state, or an outcome other than `engaged`, or backend `none`/`unsupported`. | The policy only. |

There is no third mode. A host with no backend does not pretend: it reports `Replayed` and
the example asserts nothing about enforcement, because nothing was enforced.

## 6. Known limitation: host hooks inside the sandbox

`DurableHarnessOptions.EnableFileHooks` controls **repository-origin** hooks — the ones in
`.github/hooks/` and trusted repository-configured roots. It does **not** control
user-level hooks in `~/.copilot/hooks/`, and it does not control hooks passed explicitly
through `hookDirectories` or registered as SDK callbacks.

The runtime runs hooks *inside* the session's sandbox. A user-level hook whose command is
not granted by the policy is therefore denied, and the runtime reports the failure as a
session error attributed to the hook:

```
postToolUse hook from "~/.copilot/hooks/aspire-telemetry.json" failed in the session
sandbox: Hook command failed with code 126 (bash: ...: Operation not permitted)
```

This is correct behaviour — the hook is host code running in a sandbox that did not grant
it — but it is surprising, because the tool call itself succeeded. The raw SDK
`SendAndWaitAsync` path surfaces the same failure as an ordinary answer; the durable
`SubmitAsync` path turns a session error into an exception, so the turn fails.

Two ways out, both supported:

1. **Widen the policy** to grant the hook's own command and the paths it touches. Correct
   when the hook is part of what the sandboxed session is supposed to do.
2. **Tolerate the failure** with `DurableHarnessOptions.TolerateHostHookFailures = true`.
   A session error that is attributed to a hook *and* carries a sandbox denial signature
   (`sandbox`, `Operation not permitted`, or `code 126`) is then treated as the turn's
   answer instead of an exception. The default is `false`, so existing behaviour is
   unchanged.

`examples/33-sandbox-sdk-filesystem.cs` takes option 2: it runs on hosts that have
user-level hooks installed, and it does not want to widen a demonstration policy to cover
somebody else's telemetry script. The tolerance is narrow on purpose — it matches only
hook-attributed sandbox denials, so a genuine tool failure still throws.

## 7. Verifying on a new host

```sh
dotnet run examples/33-sandbox-sdk-filesystem.cs
```

Exit 0 means the policy mapped and, if a backend engaged, the denial was observed. The
example prints the resolved policy, the run mode, and every decision the runtime reported,
so a host that degrades is visible in the output rather than silent.

## 8. Network, credential, dev-tool and MCP/LSP options by example

`examples/34-sandbox-sdk-network-credentials.cs` is the reference for every option in this
section. "CI level" says what CI proves about the option: **policy** means the mapping and the
resolved policy are asserted on every host, **enforcement** means a denial is also asserted when a
backend engaged, and **observed** means the example prints what happened and asserts only what the
backend reports.

| Option | Example | Enforceable on | CI level |
|---|---|---|---|
| `Network.AllowOutbound` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + observed enforcement |
| `Network.AllowLocalNetwork` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container (needs PSEC host-loopback for `network_filtering`) | policy |
| `Network.AllowedHosts` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + enforcement (not-denied) |
| `Network.BlockedHosts` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + enforcement (denied) |
| `Network.Proxy` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy (mapping only) |
| `Credentials.EnvVars` (masking + inject hosts) | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + enforcement (masked value observed in the shell) |
| `Auth.Gh` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + resolved `PolicyResolved.GhAuth` |
| `Auth.Git` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy + resolved `PolicyResolved.GitAuth` |
| `AllowDevToolAccess` | 34 | Advisory everywhere — it changes whether the sandbox engages, not what it blocks | policy |
| `SandboxMcpServers` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy |
| `SandboxLspServers` | 34 | macOS seatbelt, Linux bubblewrap, Windows process container | policy |

### `network.allowOutbound` is not always reported as a denial

On macOS seatbelt, `allowOutbound = false` is expressed by the backend as a failed `connect`, and
the runtime reports `access_denied` only when it can classify the refusal. The locked session in
example 34 therefore sees `curl: (7) Failed to connect ...` with no `access_denied` decision, while
the same session with a host *blocklist* entry produces
`denialClass: network_host, enforcementPoint: shell, control: network`.

That is why example 34 treats the two cases differently:

- A **blocked host** (`Network.BlockedHosts`) must be reported as `NetworkOutbound`/`NetworkHost`.
  If the backend refuses the connection without naming a denial, the example prints a notice and
  skips the assertion instead of claiming evidence it does not have.
- An **allowed host** is asserted negatively: no `NetworkOutbound`/`NetworkHost`/`NetworkLocal`
  denial may appear for it.

A configured `Network.Proxy` is not echoed back either: a policy with a proxy URL resolved with
`PolicyResolved.ProxyMode == none` on seatbelt. Assert the URL on the configuration
(`DurableHarness.BuildSandboxConfig`) and assert only that the resolved mode is one of
`none`/`loopback`/`external`.

### Credential masking is observed, not asserted from configuration

The masking assertion is behavioural: the example generates a value, sets it in its own
environment, configures the variable for masking, and asks the sandboxed shell to write a digest
and a length to a file. The value the shell sees must be non-empty and must not match the generated
digest. It runs in every mode where a shell runs, `Enforced` or `Replayed`, because the sentinel is
the runtime's, not the backend's. The value itself is never printed, never written to the
workspace, and never part of the durable policy.
