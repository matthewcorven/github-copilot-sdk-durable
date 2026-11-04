# Sandbox CI: what the advisory job proves

The sandbox examples (`33`, `34`, `35`) demonstrate a policy the runtime hands to a real
OS-level backend. Continuous integration has no Copilot sign-in and, on a stock runner, no
guarantee that the platform's backend can engage. The advisory job therefore reports what the
runtime detected on each platform and asserts nothing about enforcement. This note records the
platform matrix, what each CI level proves, and the rule that bounds the job.

- Workflow: `.github/workflows/build.yml`, job `sandbox-advisory`
- Script: `scripts/sandbox-advisory.sh`
- Report format: `SandboxReport.Describe()` in `lib/SandboxSupport.cs`
- Backends and capabilities: [`SANDBOX_MATRIX.md`](SANDBOX_MATRIX.md)
- Deterministic run: [`DESIGN.md` §6.1](DESIGN.md#61-the-deterministic-run)

## 1. Platform matrix

The runtime picks a backend; the policy does not choose one. `SandboxBackend` is a
string-wrapper value type, so the reported value is the authority. The names below are the
`SandboxBackend` values in `GitHub.Copilot.SDK` 1.0.26, which are also the values in
`docs/SANDBOX_MATRIX.md` §1.

| Runner | Platform reported | Backend when a backend engages | Prerequisite |
|---|---|---|---|
| `ubuntu-latest` | `linux` | `bubblewrap` | User namespaces and a `bwrap` binary. |
| `macos-latest` | `macos` | `seatbelt` | `/usr/bin/sandbox-exec`, present on stock macOS. |
| `windows-latest` | `windows` | `process_container` | An OS build whose `BaseContainer` supports it. |

When no backend engages — which is what the recorded runtime reports — the backend is
`unsupported` and the platform is `other`. Those two values are what the advisory job's log
shows on every runner in replay mode, and they are the runtime's own answer, not a guess.

The job prints the backend the platform *would* report next to the backend the runtime *did*
report, and labels the first as an expectation. It is not a detection, and it is never used as
proof.

## 2. What each CI level proves

| Level | Mode | What it proves | Where it runs |
|---|---|---|---|
| `build` job, `Validate examples` | live, recorded runtime | Every example compiles; the deterministic set runs against the recording. The sandbox examples are skipped (`SKIP_LIVE=1`). | `ubuntu-latest` only, required. |
| `build` job, `Validate examples (deterministic)` | `Replayed` | Policy propagation: the sandbox policy is built, the `{workspace}` token expands, and the wire mapping is field-for-field. Policy assertions only. | `ubuntu-latest` only, required. |
| `sandbox-advisory` job | `Replayed` | The same policy propagation, on three platforms, plus the runtime's reported backend and platform per example. Advisory only. | All three runners, `continue-on-error: true`. |
| A signed-in developer host with a real backend | `Enforced` | The three-part enforcement proof: the permission layer did not deny the call, the sandbox did deny it, and the durable layer and the sandbox did their separate jobs. | Not in CI. |

**Replay/offline is not enforcement.** `SandboxSupport.Resolve()` reports
`SandboxRunMode.Enforced` only when an enforcement event says the backend engaged with an
outcome of `engaged` and a backend other than `none` or `unsupported`. Anything else is
`Replayed`, and in `Replayed` mode `SandboxSupport.AssertDenied` never runs — asserting a denial
there would be asserting something that never happened.

**The three-part enforcement proof** is the assertion set in `35-sandbox-sdk-observability-bypass`
(and the denied-write half of `33-sandbox-sdk-filesystem`): (a) no permission completion for
the blocked call reports a denial, (b) the sandbox reported an access denial of an acceptable
class at an enforcement point, and (c) the denied path still holds its original content. All
three are facts about the OS, so all three require `Enforced`.

## 3. The example set

The advisory job runs the three examples that carry a `DurableSandboxPolicy`:

| # | File | What its policy half asserts |
|---|---|---|
| 33 | `33-sandbox-sdk-filesystem.cs` | `ReadwritePaths`, `ReadonlyPaths`, and `DeniedPaths` map with the token expanded; no path still carries `{workspace}`. |
| 34 | `34-sandbox-sdk-network-credentials.cs` | Network egress, host allowlist/blocklist, credential masking and injection, dev-tool access, and MCP/LSP confinement map to the wire type. |
| 35 | `35-sandbox-sdk-observability-bypass.cs` | The policy maps, and the example prints its `run mode:` line, which is the line this job reports. |

These three are listed in `LIVE_EXAMPLES` and `SANDBOX_EXAMPLES` in `scripts/validate.sh`
because they touch a live model or a real sandbox in the live path. That script is not changed
by this job; the advisory job drives the examples directly.

In `Replayed` mode the recording answers the model's side of the conversation, so no tool call
runs. Examples `33` and `34` assert an on-disk effect in that window (a written file, a masked
value seen by a shell), so they stop before printing their `run mode:` line. The job reports
that outcome as `run mode: not reported` with the exit status rather than as a pass, and still
prints the platform. Example `35` prints its `run mode:` line because its policy half completes
before any on-disk assertion. All three print their resolved policy, which is the half the job
exists to show.

## 4. Degrade, do not fail

The job is `continue-on-error: true`, and that is the rule, not a workaround:

> A platform gap degrades an assertion set; it never fails a build.

The reason is that the gap is a property of the machine, not of the change under review. Linux
may lack user namespaces; a Windows build may predate `BaseContainer` support; a container may
forbid the backend entirely. In every one of those cases the policy mapping is still correct and
worth exercising, and a pull request that touches nothing in `lib/` should not turn red because
one runner's kernel cannot start a sandbox. Failing the build would also teach reviewers to
ignore the job, which is worse than not having it.

The corollary is that a green advisory job is not enforcement proof. Read the log: a line that
says `Replayed` means the policy half ran and no backend engaged. Only `Enforced` means the OS
applied the policy.

Promoting this job to required is out of scope. If it is ever promoted, it must first be shown
to produce `Enforced` on each runner, which requires a signed-in runtime on the runner and is
not the case today.
