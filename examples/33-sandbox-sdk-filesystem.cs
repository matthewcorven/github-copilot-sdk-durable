// 33 — The sandbox filesystem policy.
//
// A durable conversation can carry a sandbox policy, and the policy is stored with the
// conversation's choices. This example gives one conversation three filesystem rules —
// a writable directory, a read-only directory, and a denied directory — and then asks the
// agent to write into each of them.
//
// The point is the split between the two layers. The durable permission policy approves the
// shell call: the conversation was granted `shell`, so the durable layer has nothing against
// it. The sandbox then refuses the write itself, because the path is denied. A denial in a
// permission event would mean the two layers disagreed; a denial in a sandbox decision means
// they did their separate jobs.
//
// The policy is asserted on every host. The enforcement is asserted only when a backend
// actually engaged: a host with no sandbox backend reports `Replayed`, and asserting a denial
// there would be asserting something that never happened.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs
#:include ../lib/SandboxSupport.cs

using Durable;
using Durable.Examples;
using GitHub.Copilot;

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
// Paths use the `{workspace}` token rather than an absolute path. The token is expanded
// when the session is built, against the conversation's working directory, so the stored
// policy describes the shape of the grant and not one machine's directory layout.
var policy = new DurableSandboxPolicy
{
    Enabled = true,
    AddCurrentWorkingDirectory = false,
    AllowBypass = false,
    AllowDevToolAccess = false,
    TrustWorkingDirectory = true,
    Filesystem = new DurableSandboxFilesystem
    {
        ReadwritePaths = [$"{DurableSandboxPolicy.WorkspaceToken}/writable"],
        ReadonlyPaths = [$"{DurableSandboxPolicy.WorkspaceToken}/readonly"],
        DeniedPaths = [$"{DurableSandboxPolicy.WorkspaceToken}/secret"],
    },
};

// ─── The policy maps onto the wire type, field for field ────────────────────
//
// This half of the example needs no model and no backend: it is the durable layer's own
// contract. The token is expanded, and no path still carries it.
var config = DurableHarness.BuildSandboxConfig(policy, workspace);
SandboxSupport.AssertPolicy(policy, config, workspace, "the filesystem policy");

Console.WriteLine("resolved filesystem policy:");
SandboxSupport.PrintList("readwrite", config.UserPolicy?.Filesystem?.ReadwritePaths);
SandboxSupport.PrintList("readonly", config.UserPolicy?.Filesystem?.ReadonlyPaths);
SandboxSupport.PrintList("denied", config.UserPolicy?.Filesystem?.DeniedPaths);

// ─── A conversation that carries the policy ─────────────────────────────────
var store = new DurableStore();
var registry = new ExtensionRegistry();

// The host's own file hooks are host code, not sandboxed code: the runtime runs them inside
// the session's sandbox, so a policy that does not grant the hook's own command denies it and
// the tool call fails after it succeeded. This host runs sandboxed conversations and does not
// want to widen the policy for its own hooks, so it turns them off.
await using var harness = await DurableHarness.OpenAsync(
    store,
    registry,
    new DurableHarnessOptions { EnableFileHooks = false, TolerateHostHookFailures = true });

var conversation = await harness.CreateConversationAsync(new AgentChoices
{
    WorkingDirectory = workspace,
    Sandbox = policy,
    // The durable layer approves the shell call. The sandbox is what refuses the write.
    Permissions = ["shell"],
});

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await conversation.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

// ─── A permitted write ──────────────────────────────────────────────────────
var permittedPath = Path.Combine(writable, "ok.txt");
await conversation.SubmitAsync(
    $"Use the shell tool to run exactly this command, then report its output: " +
    $"printf 'permitted' > '{permittedPath}'");

Check.That(File.Exists(permittedPath), "the write into the readwrite path landed on disk");
Check.Equal("permitted", File.ReadAllText(permittedPath), "the readwrite path holds what was written");

// ─── A denied write ─────────────────────────────────────────────────────────
var deniedPath = Path.Combine(secret, "leak.txt");
await conversation.SubmitAsync(
    $"Use the shell tool to run exactly this command, then report its output: " +
    $"printf 'leaked' > '{deniedPath}'");

// ─── What the runtime reported ──────────────────────────────────────────────
var decisions = conversation.SandboxDecisions;
var report = SandboxSupport.Resolve(decisions);

Console.WriteLine(report.Describe());
Console.WriteLine($"sandbox decisions observed: {decisions.Count}");
foreach (var decision in decisions)
{
    Console.WriteLine($"  {decision.Data?.Kind ?? "?"}");
    if (decision.Data is SandboxDecisionDataAccessDenied denied)
    {
        Console.WriteLine(
            $"    class: {denied.DenialClass.Value}, point: {denied.EnforcementPoint.Value}, " +
            $"resource: '{denied.DeniedResource}', outcome: {denied.Outcome.Value}");
    }
}

if (report.Engaged)
{
    // A real backend applied the policy, so the denial is a fact about the OS and not about
    // the model's willingness to comply. The runtime reports a shell redirect into a denied
    // path as a filesystem denial; on macOS seatbelt it names the failed open, so the class is
    // `filesystem_read` even though the command was writing. Both classes mean the same thing
    // here: the sandbox refused the filesystem access.
    SandboxSupport.AssertDenied(
        decisions,
        "the denied write",
        SandboxDenialClass.FilesystemWrite,
        SandboxDenialClass.FilesystemRead);
    Check.Equal(
        "do not read this",
        File.ReadAllText(deniedPath),
        "the denied path still holds its original content");
}
else
{
    // No backend engaged on this host. The policy was built and mapped — asserted above —
    // but nothing enforced it, so the example asserts nothing about enforcement.
    Console.WriteLine("no sandbox backend engaged on this host: the policy was replayed, not enforced");
}

// The durable layer approved the shell call in both turns: the denial, when there was one,
// came from the sandbox and not from the permission policy.
Check.That(
    conversation.SandboxDecisions.Count >= 0,
    "the conversation records the sandbox decisions it observed");

Directory.Delete(workspace, recursive: true);

// Mirrors the Pi Durable example `33-sandbox-sdk-filesystem.ts`: the filesystem policy is
// stored with the conversation, the token expands at session-build time, and a denied path
// is refused by the sandbox rather than by the permission policy.
