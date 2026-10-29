# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). While the
major version is `0`, the minor version may contain breaking changes; those are called out
explicitly.

The version here, the git tag, and the `Copilot.Durable` package version are one number.
See [`docs/RELEASING.md`](docs/RELEASING.md) for the release procedure.

## [Unreleased]

### Added

- **Durable sandbox policy.** `DurableSandboxPolicy` is a stored, per-conversation sandbox
  grant: `SandboxMcpServers`, `SandboxLspServers`, `Auth` (`Gh`, `Git`), `Credentials`
  (`EnvVars`), `Filesystem` (`ReadwritePaths`, `ReadonlyPaths`, `DeniedPaths`,
  `ClearPolicyOnExit`), `Network` (`Proxy`), plus `Enabled`, `AddCurrentWorkingDirectory`,
  `AllowBypass`, `AllowDevToolAccess`, `TrustWorkingDirectory`, and `KeychainAccess`. It
  rides on `AgentChoices.Sandbox`; `null` means no sandbox, so every existing conversation
  behaves exactly as before. `ResolvedAgent.Sandbox` is the resolved form. (#16)
- **`DurableHarness.BuildSandboxConfig(policy, workingDirectory)`.** A public static mapper
  from the durable policy to the SDK's `GitHub.Copilot.Rpc.SandboxConfig` wire type.
  `SessionConfig` has no sandbox property, so the config is carried on
  `SessionConfigBase.SandboxConfig`; `BuildSessionConfig` wires it in alongside
  `TrustWorkingDirectory` and `EnableFileHooks`. (#16)
- **`{workspace}` token expansion.** Paths written as `{workspace}/…`
  (`DurableSandboxPolicy.WorkspaceToken`) are expanded against the conversation's
  `WorkingDirectory` when the session is built, not when the policy is stored. A fork into a
  different working directory resolves the same stored grant against its own directory. The
  token never reaches the wire type. (#16)
- **`SandboxDecisions` and `WaitForSandboxDecisionAsync`.** A conversation exposes the
  `sandbox.decision` events its existing `Attach` subscription already receives, so a caller
  can assert on what the runtime reported without a second subscription. (#16)
- **`DurableHarnessOptions.TolerateHostHookFailures`** (opt-in, default `false`). The runtime
  runs hooks inside the session's sandbox, and `EnableFileHooks` only controls
  *repository-origin* hooks — user-level hooks in `~/.copilot/hooks/` still run and are
  denied by a policy that does not grant them. With this option on, a session error that is
  attributed to a hook and carries a sandbox denial signature (`sandbox`,
  `Operation not permitted`, or `code 126`) becomes the turn's answer instead of an
  `InvalidOperationException`. The default preserves existing behaviour, and the match is
  narrow enough that a genuine tool failure still throws. (#16)
- **`examples/33-sandbox-sdk-filesystem.cs`** — a real filesystem policy with a writable, a
  read-only, and a denied directory; a permitted write that lands and a denied write that
  does not; assertions on the `SandboxDecisionEvent` surface. (#16)
- **`docs/SANDBOX_MATRIX.md`** — sandbox capability × platform/backend, what is enforced
  versus advisory, what `SandboxFilesystemPolicyDetails` does and does not report, and the
  host-hook limitation above. (#16)

### Changed

- `DurableHarnessOptions.EnableFileHooks` documentation now states that it controls
  repository-origin hooks only and that user-level hooks still run inside the sandbox. (#16)

## [0.4.0] - 2026-10-28

The first release. It turns the storage layer from a single atomically-replaced JSON
snapshot into a pluggable, path-configurable, append-only store, and packages the durable
layer for consumption as `Copilot.Durable`.

### Added

- **Pluggable store provider abstraction.** `DurableStore` is now a facade over an
  `IDurableStoreProvider`. The provider owns the state and the undo log; the store keeps
  the commit gate. `DurableStore`'s existing public surface is unchanged and remains
  source-compatible. (#9, #10)
- **JSONL append-only backend.** A durable store file holds one JSON record per line
  (`{"v":1,"seq":N,"type":"…","payload":{…}}`), appended in write-sequence order on every
  commit. A crash mid-write costs the torn tail and nothing else: on reopen the final
  incomplete line is discarded and the file is truncated back to the last complete record,
  while every earlier record survives. (#11)
- **`DurableStoreOptions` and per-user default path resolution.** A store can be opened
  from an explicit `Path` and `Format`, or from no path at all, in which case the store
  resolves a per-user location (`%LOCALAPPDATA%` on Windows, `$XDG_STATE_HOME` or
  `~/.local/state` elsewhere). `DurableStore(string? path = null)` still compiles
  unchanged. (#12)
- **An explicit ephemeral tier.** `DurableStore.CreateEphemeral()` names the in-memory
  store, `DurableStore.Create(new DurableStoreOptions())` selects the durable tier by
  default, and `DurableStore.IsPersistent` reports which tier the store actually got.
  (#12)
- **A backward-compatible legacy JSON reader.** Stores written in the old single-file
  snapshot format still open and round-trip. Format detection parses the file rather than
  sniffing the first line, so a file that is neither JSONL nor a legacy snapshot raises an
  `InvalidDataException` naming the path and the reason instead of opening as an empty
  store; a legacy file is converted to JSONL on the first commit, never on open. (#13)
- **Single-writer stores.** A persistent store takes an exclusive lock on a `{path}.lock`
  sidecar for its lifetime. A second process opening the same path fails fast with an
  `InvalidOperationException` naming the store file, the lock file, and the owning process
  id, rather than interleaving appends. The OS releases the lock on process exit, so a
  crash leaves no stale lock.
- **Fail-closed permissions.** A durable conversation may only use what it was explicitly
  granted. Grants live on `AgentChoices.Permissions` and are persisted with the
  conversation's choices, so a denial survives a restart and a fork inherits the parent's
  grants as a snapshot. The SDK's approve-everything default is no longer in effect for
  durable conversations.
- **`Copilot.Durable` NuGet packaging.** `lib/Copilot.Durable.csproj` builds the package
  from the same `lib/Durable.cs` the examples include. CI builds, tests, compiles all
  examples, and uploads the `.nupkg` as a build artifact. (#26)
- **`examples/32-jsonl-store.cs`**, plus rows for examples 31 and 32 in the README table.
  The example shows an explicit path round-tripping across a reopen, a torn tail being
  truncated away, the per-user default path resolving, and the ephemeral tier writing
  nothing. (#14)
- **Documentation.** `docs/DESIGN.md` §3.1 and `docs/COMPARATIVE_ANALYSIS.md` §1 describe
  the two tiers, the default path rule, the JSONL record envelope, and the format table.
  The README prerequisites now state which examples need a signed-in Copilot CLI and why
  (`07` queries `models.list` to confirm a reasoning effort is supported). (#14, #32)
- **`docs/RELEASING.md`**, the release strategy: release unit, cadence, branch roles, and
  the tag procedure. (#30)

### Changed

- **The durable tier is the default for `Create(DurableStoreOptions)`.** Passing no path
  no longer means "in memory"; it means "the per-user default path". Use
  `CreateEphemeral()` for the in-memory tier.
- **A file that does not exist is written as JSONL**, not as a legacy snapshot, so new
  stores get append-only writes and torn-tail recovery from the start.
- **CI runs the offline examples, not just compiles them**, so their `Check` assertions are
  enforced on every push, and every example is compiled on every push.

### Notes

- `main` is the release branch and the repository default branch; `dev` is the integration
  branch. See [`docs/RELEASING.md`](docs/RELEASING.md).
- The `WatchHandle<T>` live-document watch API that briefly landed on `dev` was reverted
  before this release because it fell outside the storage work's scope. It is not part of
  0.4.0.

[Unreleased]: https://github.com/matthewcorven/github-copilot-sdk-durable/compare/v0.4.0...dev
[0.4.0]: https://github.com/matthewcorven/github-copilot-sdk-durable/releases/tag/v0.4.0
