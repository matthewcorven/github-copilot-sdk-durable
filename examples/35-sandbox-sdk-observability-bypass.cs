// 35 — Sandbox observability, bypass, working-directory trust and keychain.
//
// A sandbox example that prints its configuration proves nothing: configuration is what the
// host asked for, not what the OS did. This example proves enforcement instead. One shell call
// is blocked, and the example asserts three separate facts about that single call:
//
//   (a) the permission layer did not deny it — the conversation was granted `shell`, so the
//       durable policy had nothing against the call;
//   (b) the sandbox reported an access denial for it, classified as a filesystem denial at the
//       shell enforcement point;
//   (c) the tool output shows the refusal, and a control command on an allowed path succeeds.
//
// Three facts, three layers: the permission policy, the sandbox's own decision stream, and the
// OS. If the denial had come from the permission layer, (a) would fail and the example would
// be demonstrating the wrong thing. If the sandbox had not engaged, (b) would be empty. If the
// OS had not blocked the write, (c) would fail because the file would hold the attempted text.
//
// The rest of the example covers the options only a host can decide. `AllowBypass` lets the
// model ask for one command to run outside the sandbox; the host then approves or declines
// through the ordinary permission flow, so the example replaces the durable layer's default
// handler with one that inspects the request and declines. `TrustWorkingDirectory` is the
// adjacent trust knob on the session. `UserPolicy.Seatbelt.KeychainAccess` is macOS-only, and
// its deprecated duplicate under `UserPolicy.Experimental.Seatbelt` is a migration hazard: the
// runtime reads the legacy block only when the top-level one is absent, which this example
// demonstrates at policy level so the rule is visible without a macOS host.
//
// The policy half runs on every host. The enforcement half runs only when a backend engaged: a
// host with no sandbox backend reports `Replayed`, and asserting a denial there would be
// asserting something that never happened.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs
#:include ../lib/SandboxSupport.cs

using Durable;
using Durable.Examples;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

var workspace = Path.Combine(Path.GetTempPath(), $"durable-sandbox-{Guid.NewGuid():N}");
var writable = Path.Combine(workspace, "writable");
var readonlyDirectory = Path.Combine(workspace, "readonly");
var secret = Path.Combine(workspace, "secret");
Directory.CreateDirectory(writable);
Directory.CreateDirectory(readonlyDirectory);
Directory.CreateDirectory(secret);
File.WriteAllText(Path.Combine(readonlyDirectory, "pinned.txt"), "read-only content");
File.WriteAllText(Path.Combine(secret, "leak.txt"), "do not read this");

// ─── The policy ─────────────────────────────────────────────────────────────
//
// Paths use the `{workspace}` token, expanded when the session is built, so the stored policy
// describes the shape of the grant and not one machine's directory layout. `KeychainAccess` is
// macOS-only and is a request, not a promise: the runtime reports what it actually granted in
// the `policy_resolved` decision, which is what this example asserts against.
var policy = new DurableSandboxPolicy
{
    Enabled = true,
    AddCurrentWorkingDirectory = false,
    AllowBypass = false,
    AllowDevToolAccess = false,
    TrustWorkingDirectory = true,
    KeychainAccess = true,
    Filesystem = new DurableSandboxFilesystem
    {
        ReadwritePaths = [$"{DurableSandboxPolicy.WorkspaceToken}/writable"],
        ReadonlyPaths = [$"{DurableSandboxPolicy.WorkspaceToken}/readonly"],
        DeniedPaths = [$"{DurableSandboxPolicy.WorkspaceToken}/secret"],
    },
};

var config = DurableHarness.BuildSandboxConfig(policy, workspace);
SandboxSupport.AssertPolicy(policy, config, workspace, "the observability policy");

Console.WriteLine("resolved filesystem policy:");
SandboxSupport.PrintList("readwrite", config.UserPolicy?.Filesystem?.ReadwritePaths);
SandboxSupport.PrintList("readonly", config.UserPolicy?.Filesystem?.ReadonlyPaths);
SandboxSupport.PrintList("denied", config.UserPolicy?.Filesystem?.DeniedPaths);
Console.WriteLine($"  allowBypass: {config.AllowBypass}, keychainAccess: {config.UserPolicy?.Seatbelt?.KeychainAccess}");

// ─── The deprecated `Experimental.Seatbelt` location ────────────────────────
//
// The durable layer writes the seatbelt policy to the top-level `seatbelt` block and never to
// `experimental.seatbelt`. The two locations are readable in opposite directions: the runtime
// consults the legacy block only when the top-level block is absent, so a host that writes the
// legacy location and later adds the top-level one silently loses the legacy value.
var legacyOnly = new DurableSandboxPolicy
{
    Enabled = true,
    Filesystem = policy.Filesystem,
};

var legacyConfig = DurableHarness.BuildSandboxConfig(legacyOnly, workspace);
Check.That(
    legacyConfig.UserPolicy?.Seatbelt is null,
    "a policy without KeychainAccess leaves the top-level seatbelt block unset");
Check.That(
    legacyConfig.UserPolicy?.Experimental?.Seatbelt is null,
    "the durable layer never writes the deprecated experimental.seatbelt block");

// The legacy location is what a pre-migration host would have set by hand. It is readable, but
// only while the top-level block stays absent — hence the migration hazard.
legacyConfig.UserPolicy!.Experimental = new SandboxConfigUserPolicyExperimental
{
    Seatbelt = new SandboxConfigUserPolicyExperimentalSeatbelt { KeychainAccess = true },
};
Check.Equal(
    true,
    EffectiveKeychainAccess(legacyConfig),
    "the deprecated location is consulted only when the top-level seatbelt block is absent");

// Now the same policy through the durable layer, with the legacy block hand-set to the opposite
// value: the top-level block wins, so the legacy value is not consulted at all.
var conflicting = DurableHarness.BuildSandboxConfig(policy, workspace);
conflicting.UserPolicy!.Experimental = new SandboxConfigUserPolicyExperimental
{
    Seatbelt = new SandboxConfigUserPolicyExperimentalSeatbelt { KeychainAccess = false },
};
Check.Equal(
    true,
    conflicting.UserPolicy.Seatbelt?.KeychainAccess,
    "the top-level seatbelt block carries the durable policy's keychain decision");
Check.Equal(
    true,
    EffectiveKeychainAccess(conflicting),
    "the legacy location is not consulted while the top-level seatbelt block is present");

// ─── TrustWorkingDirectory through the session configuration ────────────────
var store = new DurableStore();
var registry = new ExtensionRegistry();

// Host hooks are host code, and the runtime runs them inside the session's sandbox: a hook the
// policy does not grant would fail the tool call after it succeeded. This host runs sandboxed
// conversations and turns its own hooks off instead of widening the policy for them.
await using var harness = await DurableHarness.OpenAsync(
    store,
    registry,
    new DurableHarnessOptions { EnableFileHooks = false, TolerateHostHookFailures = true });

var choices = new AgentChoices
{
    WorkingDirectory = workspace,
    Sandbox = policy,
    // The durable layer approves the shell call. The sandbox is what refuses the write.
    Permissions = ["shell"],
};

var sessionConfig = harness.BuildSessionConfig(choices, null);
Check.Equal(
    WorkingDirectoryTrust.User,
    sessionConfig.TrustWorkingDirectory,
    "TrustWorkingDirectory = true maps to WorkingDirectoryTrust.User");
Check.Equal(
    policy.AllowBypass,
    sessionConfig.SandboxConfig?.AllowBypass,
    "the sandbox policy travels on the session configuration");

var trustSessionConfig = harness.BuildSessionConfig(
    new AgentChoices { WorkingDirectory = workspace, Permissions = ["shell"], Sandbox = new DurableSandboxPolicy { Enabled = true, TrustWorkingDirectory = false } },
    null);
Check.Equal(
    WorkingDirectoryTrust.Session,
    trustSessionConfig.TrustWorkingDirectory,
    "TrustWorkingDirectory = false maps to WorkingDirectoryTrust.Session");

// ─── A session that carries the policy and the trust decision ───────────────
var conversation = await harness.CreateConversationAsync(choices);
Check.NotNull(conversation.Session, "the session was created with TrustWorkingDirectory set");

// ─── The blocked call ───────────────────────────────────────────────────────
var deniedPath = Path.Combine(secret, "leak.txt");
await conversation.SubmitAsync(
    $"Use the shell tool to run exactly this command, then report its output: " +
    $"printf 'leaked' > '{deniedPath}'");

// ─── The control call ───────────────────────────────────────────────────────
var controlPath = Path.Combine(writable, "control.txt");
await conversation.SubmitAsync(
    $"Use the shell tool to run exactly this command, then report its output: " +
    $"printf 'control' > '{controlPath}'");

// ─── What the runtime reported ──────────────────────────────────────────────
var decisions = conversation.SandboxDecisions;
var report = SandboxSupport.Resolve(decisions);
var context = await conversation.ContextAsync();

Console.WriteLine();
Console.WriteLine(report.Describe());
Console.WriteLine($"observed decision kinds, in order ({decisions.Count}):");
foreach (var decision in decisions)
{
    Console.WriteLine($"  {decision.Data?.Kind ?? "?"}{Describe(decision.Data)}");
}

// ─── The three-part proof ───────────────────────────────────────────────────
Console.WriteLine();
if (report.Engaged)
{
    var denial = decisions
        .Select(decision => decision.Data)
        .OfType<SandboxDecisionDataAccessDenied>()
        .FirstOrDefault();
    var toolCallId = Check.NotNull(denial?.ToolCallId, "the denial names the tool call it refused");

    // The three decision kinds this example reads: the resolved policy, the spawn result, and
    // the enforcement state that says whether the backend engaged.
    Check.That(
        decisions.Select(decision => decision.Data).OfType<SandboxDecisionDataPolicyResolved>().Any(),
        "the runtime reported the resolved policy");
    Check.That(
        decisions.Select(decision => decision.Data).OfType<SandboxDecisionDataSpawnCompleted>().Any(),
        "the runtime reported the sandbox spawn result");
    Check.That(
        decisions.Select(decision => decision.Data).OfType<SandboxDecisionDataEnforcementState>().Any(),
        "the runtime reported the enforcement state");

    // (a) The permission layer approved the call: no permission completion for it is a denial.
    SandboxSupport.AssertNoPermissionDenial(context, toolCallId, "the blocked shell call");

    // (b) The sandbox denied it, and said what it denied.
    SandboxSupport.AssertDenied(
        decisions,
        "the blocked write",
        SandboxDenialClass.FilesystemWrite,
        SandboxDenialClass.FilesystemRead);
    Check.Equal(SandboxControl.Filesystem, denial!.Control, "the denial is a filesystem denial");
    Check.Equal(SandboxEnforcementPoint.Shell, denial.EnforcementPoint, "the denial came from the shell enforcement point");
    Check.Equal(SandboxOutcome.Denied, denial.Outcome, "the denial reports the denied outcome");
    Console.WriteLine(
        $"part (b): denied {denial.DenialClass.Value} at {denial.EnforcementPoint.Value} " +
        $"for tool call {toolCallId}, resource '{denial.DeniedResource}'");

    // (c) The OS refused: the tool output says so and the denied file still holds its content.
    var shellCall = context
        .OfType<ToolExecutionCompleteEvent>()
        .FirstOrDefault(call => call.Data.ToolCallId == toolCallId);
    Check.NotNull(shellCall, "the blocked call has a completion event");
    var toolText = string.Join(
        "\n",
        new[] { shellCall!.Data.Result?.Content, shellCall.Data.Result?.DetailedContent, shellCall.Data.Error?.Message }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    Check.That(LooksRefused(toolText), $"the tool output shows the refusal (saw: {Excerpt(toolText)})");
    Check.Equal(
        "do not read this",
        File.ReadAllText(deniedPath),
        "the denied path still holds its original content");
    Console.WriteLine($"part (c): tool output shows the refusal: {Excerpt(toolText)}");

    // A control command on an allowed path did succeed, so the refusal is about the path and
    // not about the sandbox refusing everything.
    Check.Equal("control", File.ReadAllText(controlPath), "the control write into the readwrite path succeeded");

    // Whether a tool call ran sandboxed is reported per call; it is an observation, so the
    // example asserts it only when the runtime reported one.
    if (shellCall.Data.Sandboxed is { } sandboxed)
    {
        Check.That(sandboxed, "the blocked shell call ran sandboxed");
        Console.WriteLine($"sandboxed tool call: {shellCall.Data.Sandboxed}");
    }
    else
    {
        Console.WriteLine("notice: the runtime did not report ToolExecutionCompleteData.Sandboxed for the blocked call");
    }

    // The host asked for keychain reachability. On macOS the runtime reports whether it granted
    // it; on any other platform the field is false by definition and there is nothing to assert.
    var resolvedPolicy = decisions
        .Select(decision => decision.Data)
        .OfType<SandboxDecisionDataPolicyResolved>()
        .First();
    if (OperatingSystem.IsMacOS())
    {
        Check.That(resolvedPolicy.KeychainAccess, "macOS granted the keychain access the policy requested");
        Console.WriteLine($"keychain access reported by the runtime: {resolvedPolicy.KeychainAccess}");
    }
    else
    {
        Console.WriteLine(
            $"notice: keychain reachability is macOS-only, so the assertion is skipped " +
            $"(platform: {resolvedPolicy.Platform.Value}, reported keychain access: {resolvedPolicy.KeychainAccess})");
    }
}
else
{
    Console.WriteLine("no sandbox backend engaged on this host: the policy was replayed, not enforced");
    Console.WriteLine("the three-part proof needs a backend that actually applies the policy, so it is skipped");
}

// ─── AllowBypass = false recorded no bypass decision ────────────────────────
var bypassesWhenDisabled = decisions
    .Select(decision => decision.Data)
    .OfType<SandboxDecisionDataBypassDecided>()
    .Count();
Check.Equal(0, bypassesWhenDisabled, "a session with AllowBypass = false recorded no bypass decision");

// ─── AllowBypass = true, with a host that declines ─────────────────────────
//
// The bypass decision is the one place where the durable layer's fail-closed permission handler
// is deliberately replaced: the sandbox asked the runtime for a command outside the sandbox, and
// the runtime asks the host. This host declines and lets everything else through.
var bypassPolicy = new DurableSandboxPolicy
{
    Enabled = true,
    AddCurrentWorkingDirectory = false,
    AllowBypass = true,
    AllowDevToolAccess = false,
    TrustWorkingDirectory = true,
    KeychainAccess = true,
    Filesystem = policy.Filesystem,
};
Check.That(bypassPolicy.AllowBypass == true, "the second policy asks for the bypass capability");

var hostInvocations = 0;
var declinedBypasses = 0;
var escalationPrompts = 0;
var inspected = new List<string>();

await using var bypassHarness = await DurableHarness.OpenAsync(
    new DurableStore(),
    new ExtensionRegistry(),
    new DurableHarnessOptions
    {
        EnableFileHooks = false,
        TolerateHostHookFailures = true,
        PermissionHandler = (request, invocation) =>
        {
            hostInvocations++;
            var prompt = invocation.PromptRequest;
            var bypassRequested = false;
            var permissiveRequested = false;
            PermissionSandboxPathGrant? grant = null;

            if (prompt is PermissionPromptRequestCommands commands)
            {
                bypassRequested = commands.RequestSandboxBypass == true;
                permissiveRequested = commands.RequestSandboxPermissive == true;
                grant = commands.SandboxPathGrant;
            }
            else if (request is PermissionRequestShell shell)
            {
                bypassRequested = shell.RequestSandboxBypass == true;
                permissiveRequested = shell.RequestSandboxPermissive == true;
                grant = shell.SandboxPathGrant;
            }

            // What the prompt said is printed, so the permission surface is exercised rather
            // than merely configured: `SandboxPathGrant` is the path the model wants added to
            // the policy, and `RequestSandboxPermissive` is the milder retry that keeps the
            // sandbox attached while recording accesses instead of blocking them.
            var seen =
                $"request #{hostInvocations}: kind={prompt?.Kind ?? request.Kind}, " +
                $"bypass={bypassRequested}, permissive={permissiveRequested}, " +
                $"pathGrant={(grant is null ? "none" : $"{grant.Access.Value} {grant.Path}")}" +
                $"{(grant?.DeniedPath is { } deniedPathFromGrant ? $" (denied: {deniedPathFromGrant})" : string.Empty)}" +
                $"{(grant?.RemovedReadonlyPaths is { Length: > 0 } removed ? $", removedReadonly={string.Join(", ", removed)}" : string.Empty)}";
            inspected.Add(seen);
            Console.WriteLine($"  host handler saw {seen}");

            if (bypassRequested || permissiveRequested || grant is not null)
            {
                escalationPrompts++;
                declinedBypasses++;
                return Task.FromResult<PermissionDecision>(new PermissionDecisionReject
                {
                    Feedback =
                        "declined by the host: this conversation runs every command inside the sandbox, " +
                        "and the policy grants its writable paths explicitly.",
                });
            }

            return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
        },
    });

var bypassConversation = await bypassHarness.CreateConversationAsync(new AgentChoices
{
    WorkingDirectory = workspace,
    Sandbox = bypassPolicy,
    Permissions = ["shell"],
});

var bypassPath = Path.Combine(secret, "bypass.txt");
var bypassCommand = $"printf 'leaked' > '{bypassPath}'";

// The model is asked to escalate: run the command, and if the sandbox refuses the write, request
// the bypass for the same command. Whether the runtime offers that escalation at all is the
// runtime's decision, not the host's — the CLI only surfaces a bypass prompt when its own sandbox
// settings allow one, and the SDK has no call that forces the prompt. So the example asks for the
// escalation and then asserts on what actually happened, rather than asserting that a prompt must
// appear. A detached background command is included because it is the one shape the tool cannot
// contain inside the sandbox, so it is the most likely to raise the escalation on its own.
await bypassConversation.SubmitAsync(
    $"Use the shell tool to run exactly this command, then report its output: {bypassCommand}. " +
    $"If the sandbox refuses the write, request the sandbox bypass for the same command and " +
    $"report what the host decides.");
await bypassConversation.SubmitAsync(
    $"Use the shell tool with mode=\"async\" and detach=true to start this command in the " +
    $"background, then report what happened: {bypassCommand}");

var bypassDecisions = bypassConversation.SandboxDecisions;
Console.WriteLine();
Console.WriteLine($"second session, AllowBypass = true: {bypassDecisions.Count} decision(s)");
foreach (var decision in bypassDecisions)
{
    Console.WriteLine($"  {decision.Data?.Kind ?? "?"}{Describe(decision.Data)}");
}

// The host handler is the only place a bypass request can be declined, so the decline assertion
// is conditional on the runtime having actually offered an escalation prompt. An escalation prompt
// is one whose request carried `RequestSandboxBypass`, `RequestSandboxPermissive`, or a
// `SandboxPathGrant` — the three fields the handler inspects above. When such a prompt was seen,
// the decline must be exactly one and must be visible in the decision stream as a `bypass_decided`
// event whose source is the model. When none was seen, the example says so plainly instead of
// failing on an event the runtime never had the chance to emit — a false failure would misreport
// the SDK's behaviour.
var bypassDecided = bypassDecisions
    .Select(decision => decision.Data)
    .OfType<SandboxDecisionDataBypassDecided>()
    .ToList();
var escalationOffered = escalationPrompts > 0 || bypassDecided.Count > 0;

if (escalationOffered)
{
    Check.Equal(1, declinedBypasses, "exactly one host-handler invocation declined the bypass");
    Check.That(hostInvocations >= declinedBypasses, "every declined bypass came from an inspected prompt");
    Check.Equal(1, bypassDecided.Count, "the declined bypass is recorded as one bypass_decided event");

    var decided = bypassDecided[0];
    Check.Equal(SandboxOutcome.Declined, decided.Outcome, "the bypass decision records the host's decline");
    Check.Equal(SandboxBypassSource.ModelRequested, decided.Source, "the bypass was requested by the model");
    Console.WriteLine($"bypass decided: outcome={decided.Outcome.Value}, source={decided.Source.Value}");
}
else
{
    // The host handler was installed and the policy asked for the capability; the runtime simply
    // never escalated. That is a finding about the SDK surface, not a failure of this example.
    Check.Equal(0, declinedBypasses, "no bypass was declined because none was offered");
    Check.Equal(0, bypassDecided.Count, "no bypass_decided event was reported because none was offered");
    Console.WriteLine(
        "notice: the runtime offered no sandbox-bypass escalation for this session, so the " +
        "decline path was not exercised. The host handler was installed and the policy set " +
        "AllowBypass = true; the runtime decides whether to surface the prompt, and the SDK " +
        "exposes no call that forces it. The handler's decline branch is therefore unproven here.");
}

// ─── Which assertion set ran ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine($"assertion set: {(report.Engaged ? "policy + enforcement" : "policy only")}");
Console.WriteLine(
    $"host-handler prompts inspected: {inspected.Count}, escalation prompts seen: {escalationPrompts}, " +
    $"bypasses declined: {declinedBypasses}");

Directory.Delete(workspace, recursive: true);

// Describes a decision's key fields, so the printed stream is evidence and not just names.
static string Describe(SandboxDecisionData? data) => data switch
{
    SandboxDecisionDataPolicyResolved resolved =>
        $"  (outcome={resolved.Outcome.Value}, backend={resolved.Backend.Value}, platform={resolved.Platform.Value}, " +
        $"source={resolved.PolicySource.Value}, keychain={resolved.KeychainAccess}, allowBypass={resolved.AllowBypass})",
    SandboxDecisionDataSpawnCompleted spawn =>
        $"  (backend={spawn.Backend.Value}, outcome={spawn.Outcome.Value}, {spawn.DurationMs:F0}ms)",
    SandboxDecisionDataEnforcementState state =>
        $"  (backend={state.Backend.Value}, outcome={state.Outcome.Value}, point={state.EnforcementPoint.Value})",
    SandboxDecisionDataAccessDenied denied =>
        $"  (class={denied.DenialClass.Value}, point={denied.EnforcementPoint.Value}, " +
        $"control={denied.Control.Value}, resource='{denied.DeniedResource}')",
    SandboxDecisionDataAccessRecorded recorded =>
        $"  (class={recorded.DenialClass.Value}, point={recorded.EnforcementPoint.Value}, resource='{recorded.DeniedResource}')",
    SandboxDecisionDataBypassDecided decided =>
        $"  (outcome={decided.Outcome.Value}, source={decided.Source.Value}, control={decided.Control.Value})",
    _ => string.Empty,
};

// The runtime reads the top-level seatbelt block first and the deprecated
// `experimental.seatbelt` block only when the top-level one is absent.
static bool? EffectiveKeychainAccess(SandboxConfig config) =>
    config.UserPolicy?.Seatbelt is { } seatbelt
        ? seatbelt.KeychainAccess
        : config.UserPolicy?.Experimental?.Seatbelt?.KeychainAccess;

static bool LooksRefused(string text) =>
    text.Length > 0
    && new[] { "denied", "not permitted", "permission", "sandbox", "refus", "blocked" }
        .Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

static string Excerpt(string text)
{
    var flattened = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return flattened.Length <= 160 ? flattened : $"{flattened[..160]}…";
}

// Mirrors the Pi Durable example `35-sandbox-sdk-observability-bypass.ts`: the decision stream is
// the evidence that the sandbox engaged, and every host capability around it — bypass, trust,
// keychain — is a host decision the runtime can report back.
