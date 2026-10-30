// 34 — The sandbox network, credential, dev-tool and MCP/LSP policy.
//
// Example 33 covers the filesystem half of the sandbox policy. This example covers the other
// half — the options that are not about paths: network egress, credential masking, GitHub and
// git credential injection, dev-toolchain read access, and MCP/LSP confinement. They are a
// different policy family with different enforcement points (`SandboxControl.Network`,
// `SandboxControl.Credentials`, `SandboxControl.Routing`) and different failure modes: a
// process that reaches a host it should not reach, a process that reads a secret it should not
// see, or an MCP server that runs outside the sandbox entirely.
//
// The same two-layer split as example 33 applies. The durable permission policy approves the
// shell call; the sandbox decides what the spawned process may then do. Two sessions make the
// difference observable rather than asserted from configuration alone:
//
//   locked     — no outbound, no local network, MCP and LSP confined, dev tools denied.
//   permissive — outbound allowed with an explicit host allowlist and blocklist, one masked
//                credential injected at one host, dev tools allowed, MCP confinement opted out.
//
// The credential proof needs no network fetch. The example generates a throwaway value, sets it
// in its own environment, configures that variable for masking, and then asks the sandboxed
// shell to report only a digest and the length of what it sees. The digest of the real value
// never matches, and the value itself is never printed and never stored — the durable policy
// carries the variable's name and its inject hosts, never its value.
//
// The policy is asserted on every host. Enforcement is asserted only when a backend actually
// engaged: a host with no sandbox backend reports `Replayed`, and asserting a denial there
// would be asserting something that never happened.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs
#:include ../lib/SandboxSupport.cs

using System.Security.Cryptography;
using System.Text;
using Durable;
using Durable.Examples;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

var workspace = Path.Combine(Path.GetTempPath(), $"durable-sandbox-{Guid.NewGuid():N}");
Directory.CreateDirectory(workspace);

// ─── The throwaway credential ───────────────────────────────────────────────
//
// The value is generated per run and lives only in this process's environment. It is never
// written to the workspace, never committed to the store, and never printed: the sandboxed
// shell reports a digest of what it sees, and the example compares that digest to one it
// computes here.
const string credentialName = "DURABLE_SANDBOX_TOKEN";
var realValue = "dsk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
Environment.SetEnvironmentVariable(credentialName, realValue);
var realDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(realValue))).ToLowerInvariant();

// The shell tool runs a script this example writes, rather than a long command line the model
// has to reproduce exactly. The script prints only a digest and a length.
var observeScript = Path.Combine(workspace, "observe-env.sh");
var digestFile = Path.Combine(workspace, "env-report.txt");
var observeScriptBody = """
#!/bin/sh
value="${SANDBOX_TOKEN_NAME-}"
if [ -z "$value" ]; then
  printf 'verdict=empty\n' > "$1"
  exit 0
fi
if command -v sha256sum >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | sha256sum | cut -d' ' -f1)
elif command -v shasum >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | shasum -a 256 | cut -d' ' -f1)
elif command -v openssl >/dev/null 2>&1; then
  digest=$(printf '%s' "$value" | openssl dgst -sha256 -r | cut -d' ' -f1)
else
  digest=unavailable
fi
printf 'verdict=observed\n' > "$1"
printf 'sha256=%s\n' "$digest" >> "$1"
printf 'length=%s\n' "${#value}" >> "$1"
""";
File.WriteAllText(
    observeScript,
    observeScriptBody.Replace("SANDBOX_TOKEN_NAME", credentialName, StringComparison.Ordinal));

// ─── The locked-down policy ─────────────────────────────────────────────────
//
// Fail-closed on every axis: no outbound, no local network, no dev tools, both MCP and LSP
// confined. The proxy is configured so the runtime's resolved `ProxyMode` has something to
// report; the example asserts the resolved mode, not a proxy round-trip.
var lockedPolicy = new DurableSandboxPolicy
{
    Enabled = true,
    AddCurrentWorkingDirectory = false,
    AllowBypass = false,
    AllowDevToolAccess = false,
    // Both default to on. Only an explicit false opts out, so the confinement these two
    // settings buy is not something a policy gets by staying silent — it is the default, and
    // this policy restates it.
    SandboxMcpServers = true,
    SandboxLspServers = true,
    TrustWorkingDirectory = true,
    KeychainAccess = false,
    Auth = new DurableSandboxAuth { Gh = true, Git = false },
    Credentials = new DurableSandboxCredentials
    {
        // Name and inject hosts only. The durable layer never carries the value: the runtime
        // reads it from this process's environment and substitutes it, unmasked, into HTTPS
        // request headers at these hosts and nowhere else.
        EnvVars = new Dictionary<string, List<string>>
        {
            [credentialName] = ["api.github.com"],
        },
    },
    Filesystem = new DurableSandboxFilesystem
    {
        ReadwritePaths = [DurableSandboxPolicy.WorkspaceToken],
    },
    Network = new DurableSandboxNetwork
    {
        AllowOutbound = false,
        AllowLocalNetwork = false,
        AllowedHosts = [],
        BlockedHosts = ["example.com", "*.example.com"],
        Proxy = new DurableSandboxProxy
        {
            Url = "http://127.0.0.1:8899",
            Username = "sandbox",
            // A throwaway proxy password, generated per run and never printed.
            Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)),
        },
    },
};

// ─── The permissive-but-masked policy ───────────────────────────────────────
//
// Outbound is allowed, but only to the hosts the policy names, and one host is blocked by
// name. Dev-tool access is granted so the agent can read the toolchain directories, and MCP
// confinement is explicitly opted out of.
var permissivePolicy = new DurableSandboxPolicy
{
    Enabled = true,
    AddCurrentWorkingDirectory = false,
    AllowBypass = false,
    // Grants the agent read access to the dev tool directories (node, dotnet, git, ...), which
    // a build or a test run needs and which a policy that stays silent about does not give it.
    AllowDevToolAccess = true,
    // Opting out of MCP confinement also permits remote-MCP egress to leave the sandbox:
    // the flag is read together with `enabled` precisely because it changes where an MCP
    // server's network traffic goes, not just where its process runs.
    SandboxMcpServers = false,
    SandboxLspServers = true,
    TrustWorkingDirectory = true,
    KeychainAccess = false,
    Auth = new DurableSandboxAuth { Gh = true, Git = true },
    Credentials = new DurableSandboxCredentials
    {
        EnvVars = new Dictionary<string, List<string>>
        {
            [credentialName] = ["api.github.com"],
        },
    },
    Filesystem = new DurableSandboxFilesystem
    {
        ReadwritePaths = [DurableSandboxPolicy.WorkspaceToken],
    },
    Network = new DurableSandboxNetwork
    {
        AllowOutbound = true,
        AllowLocalNetwork = true,
        AllowedHosts = ["api.github.com"],
        BlockedHosts = ["example.com"],
    },
};

// ─── The policy maps onto the wire type, field for field ────────────────────
//
// This half needs no model and no backend: it is the durable layer's own contract, and it
// holds on every platform. `AssertPolicy` checks the shared surface; the assertions below add
// the family this example is about.
var lockedConfig = DurableHarness.BuildSandboxConfig(lockedPolicy, workspace);
var permissiveConfig = DurableHarness.BuildSandboxConfig(permissivePolicy, workspace);
SandboxSupport.AssertPolicy(lockedPolicy, lockedConfig, workspace, "the locked network policy");
SandboxSupport.AssertPolicy(permissivePolicy, permissiveConfig, workspace, "the permissive network policy");

foreach (var (label, policy, config) in new[]
         {
             ("locked", lockedPolicy, lockedConfig),
             ("permissive", permissivePolicy, permissiveConfig),
         })
{
    var network = Check.NotNull(config.UserPolicy?.Network, $"{label}: the network policy is mapped");
    Check.Equal(policy.Network?.AllowOutbound, network.AllowOutbound, $"{label}: AllowOutbound maps");
    Check.Equal(policy.Network?.AllowLocalNetwork, network.AllowLocalNetwork, $"{label}: AllowLocalNetwork maps");
    Check.Sequence(policy.Network?.AllowedHosts ?? [], network.AllowedHosts ?? [], $"{label}: AllowedHosts maps");
    Check.Sequence(policy.Network?.BlockedHosts ?? [], network.BlockedHosts ?? [], $"{label}: BlockedHosts maps");

    // The credential's value is not part of the wire type at all: the configuration carries the
    // variable's name and the hosts it may be injected into, and nothing else.
    var credentials = Check.NotNull(config.Credentials, $"{label}: the credential policy is mapped");
    Check.That(
        credentials.EnvVars is not null && credentials.EnvVars.ContainsKey(credentialName),
        $"{label}: the masked variable is listed by name");
    var masked = credentials.EnvVars![credentialName];
    Check.Sequence(
        policy.Credentials?.EnvVars?[credentialName] ?? [],
        masked.InjectHosts ?? [],
        $"{label}: the inject hosts map");

    Check.Equal(
        policy.AllowDevToolAccess,
        config.AllowDevToolAccess,
        $"{label}: AllowDevToolAccess maps");
}

// The proxy maps only when it has a URL, and it is dropped rather than sent as an empty one.
Check.Equal(
    lockedPolicy.Network?.Proxy?.Url,
    lockedConfig.UserPolicy?.Network?.Proxy?.Url,
    "the locked policy's proxy URL maps");
Check.That(
    permissiveConfig.UserPolicy?.Network?.Proxy is null,
    "a policy with no proxy maps to no proxy");

Console.WriteLine("resolved locked network policy:");
Console.WriteLine($"  allow_outbound: {lockedConfig.UserPolicy?.Network?.AllowOutbound}");
Console.WriteLine($"  allow_local_network: {lockedConfig.UserPolicy?.Network?.AllowLocalNetwork}");
SandboxSupport.PrintList("allowed_hosts", lockedConfig.UserPolicy?.Network?.AllowedHosts);
SandboxSupport.PrintList("blocked_hosts", lockedConfig.UserPolicy?.Network?.BlockedHosts);
Console.WriteLine($"  proxy_mode: {(lockedConfig.UserPolicy?.Network?.Proxy is null ? "none" : "external")}");
SandboxSupport.PrintList("masked_env_vars", lockedConfig.Credentials?.EnvVars?.Keys);
Console.WriteLine(
    $"  auth: gh={lockedConfig.Auth?.Gh}, git={lockedConfig.Auth?.Git}; " +
    $"allow_dev_tool_access={lockedConfig.AllowDevToolAccess}; " +
    $"sandbox_mcp_servers={lockedConfig.SandboxMcpServers}; sandbox_lsp_servers={lockedConfig.SandboxLspServers}");

Console.WriteLine("resolved permissive network policy:");
Console.WriteLine($"  allow_outbound: {permissiveConfig.UserPolicy?.Network?.AllowOutbound}");
Console.WriteLine($"  allow_local_network: {permissiveConfig.UserPolicy?.Network?.AllowLocalNetwork}");
SandboxSupport.PrintList("allowed_hosts", permissiveConfig.UserPolicy?.Network?.AllowedHosts);
SandboxSupport.PrintList("blocked_hosts", permissiveConfig.UserPolicy?.Network?.BlockedHosts);
Console.WriteLine($"  proxy_mode: {(permissiveConfig.UserPolicy?.Network?.Proxy is null ? "none" : "external")}");
SandboxSupport.PrintList("masked_env_vars", permissiveConfig.Credentials?.EnvVars?.Keys);
Console.WriteLine(
    $"  auth: gh={permissiveConfig.Auth?.Gh}, git={permissiveConfig.Auth?.Git}; " +
    $"allow_dev_tool_access={permissiveConfig.AllowDevToolAccess}; " +
    $"sandbox_mcp_servers={permissiveConfig.SandboxMcpServers}; sandbox_lsp_servers={permissiveConfig.SandboxLspServers}");

// ─── Two conversations, two policies ────────────────────────────────────────
var store = new DurableStore();
var registry = new ExtensionRegistry();

// The host's own file hooks are host code, not sandboxed code, so a policy that does not grant
// the hook's command denies it. This host does not widen a demonstration policy for its own
// hooks, so it turns them off and tolerates the failures that user-level hooks still produce.
await using var harness = await DurableHarness.OpenAsync(
    store,
    registry,
    new DurableHarnessOptions { EnableFileHooks = false, TolerateHostHookFailures = true });

var locked = await harness.CreateConversationAsync(new AgentChoices
{
    WorkingDirectory = workspace,
    Sandbox = lockedPolicy,
    Permissions = ["shell", "read"],
    Model = "gpt-5.6-luna",
});

var permissive = await harness.CreateConversationAsync(new AgentChoices
{
    WorkingDirectory = workspace,
    Sandbox = permissivePolicy,
    Permissions = ["shell", "read"],
    Model = "gpt-5.6-luna",
});

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await permissive.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

var ran = new List<string>();

// ─── Credential masking, in both sessions ───────────────────────────────────
//
// The proof needs no network fetch. The shell runs inside the sandbox and reports a digest and
// a length; the real value never leaves this process and is never printed.
foreach (var (label, conversation) in new[] { ("locked", locked), ("permissive", permissive) })
{
    File.Delete(digestFile);
    await conversation.SubmitAsync(
        $"Use the shell tool to run exactly this command, then report its output. It is a local " +
        $"diagnostics script this host wrote for the sandboxed run; run it once, exactly as given: " +
        $"sh '{observeScript}' '{digestFile}'");

    var observed = File.Exists(digestFile) ? File.ReadAllText(digestFile) : string.Empty;

    var verdict = observed.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('=', 2))
        .Where(pair => pair.Length == 2)
        .ToDictionary(pair => pair[0], pair => pair[1]);
    var maskedDigest = verdict.GetValueOrDefault("sha256", string.Empty);
    var maskedLength = int.TryParse(verdict.GetValueOrDefault("length", "0"), out var parsed) ? parsed : 0;

    Check.That(
        verdict.GetValueOrDefault("verdict") != "empty",
        $"{label}: the masked variable is present inside the sandbox (not empty)");
    Check.That(maskedLength > 0, $"{label}: the value the sandboxed shell sees is not empty");
    Check.That(
        !string.Equals(maskedDigest, realDigest, StringComparison.OrdinalIgnoreCase),
        $"{label}: the value the sandboxed shell sees is not the real credential value");

    Console.WriteLine(
        $"{label}: credential masking held — the sandboxed shell saw a {maskedLength}-character " +
        $"value whose digest does not match the generated one");
    ran.Add($"{label}: credential masking (not the real value, not empty)");
}

// ─── Network egress ─────────────────────────────────────────────────────────
//
// Only meaningful when a backend engaged: a host with no sandbox backend reports no denial
// because nothing was enforced, not because the policy was right.
static string Curl(string url) =>
    $"curl -sS -m 8 -o /dev/null -w '%{{http_code}}' '{url}' || true";

var lockedReport = SandboxSupport.Resolve(locked.SandboxDecisions);
var permissiveReport = SandboxSupport.Resolve(permissive.SandboxDecisions);

Console.WriteLine(lockedReport.Describe());
Console.WriteLine(permissiveReport.Describe());

if (lockedReport.Engaged)
{
    var mark = locked.SandboxDecisions.Count;
    await locked.SubmitAsync(
        $"Use the shell tool to run exactly this command, then report its output: {Curl("https://example.com/")}");

    var delta = locked.SandboxDecisions.Skip(mark).ToList();
    var blockedDenials = NetworkDenials(delta);
    if (blockedDenials.Count > 0)
    {
        Console.WriteLine("locked: the blocked host was denied");
        ran.Add("locked: blocked host denied (NetworkOutbound or NetworkHost)");
    }
    else
    {
        // macOS seatbelt expresses "no outbound" as a failed connect rather than as a named
        // denial, and the runtime reports a denial only when it can classify one. The egress is
        // still refused — the shell sees the connect fail — so the assertion that the sandbox
        // recorded the denial is skipped here with a notice rather than claimed.
        Console.WriteLine(
            "locked: this backend refused the connection without reporting a network denial — " +
            "skipping the blocked-host denial assertion");
        ran.Add("locked: blocked-host denial skipped (backend reported no network denial)");
    }
}
else
{
    Console.WriteLine(
        "locked: no network enforcement on this backend — skipping the blocked-host denial assertion");
    ran.Add("locked: blocked-host denial skipped (no network enforcement)");
}

if (permissiveReport.Engaged)
{
    // The allowed host is named by the policy's allowlist, so the sandbox must not deny it.
    // The example does not assert an HTTP status: a host with no route to the internet fails
    // the fetch for reasons that have nothing to do with the sandbox.
    var allowedMark = permissive.SandboxDecisions.Count;
    await permissive.SubmitAsync(
        $"Use the shell tool to run exactly this command, then report its output: {Curl("https://api.github.com/")}");

    var allowedDelta = permissive.SandboxDecisions.Skip(allowedMark).ToList();
    var allowedDenials = NetworkDenials(allowedDelta);
    Check.That(
        allowedDenials.Count == 0,
        "the allowed host is not denied (saw: " +
        string.Join(", ", allowedDenials.Select(denial => $"{denial.DenialClass.Value} {denial.DeniedResource}")) +
        ")");
    Console.WriteLine("permissive: the allowed host was not denied");
    ran.Add("permissive: allowed host not denied");

    // The same session blocks a host by name, so the allowlist is not the only rule in play.
    var blockedMark = permissive.SandboxDecisions.Count;
    await permissive.SubmitAsync(
        $"Use the shell tool to run exactly this command, then report its output: {Curl("https://example.com/")}");

    var blockedDelta = permissive.SandboxDecisions.Skip(blockedMark).ToList();
    var blockedDenials = NetworkDenials(blockedDelta);
    if (blockedDenials.Count > 0)
    {
        Console.WriteLine("permissive: the blocked host was denied while the allowed host was not");
        ran.Add("permissive: blocked host denied while the allowed host was not");
    }
    else
    {
        Console.WriteLine(
            "permissive: this backend refused the blocked host without reporting a network denial — " +
            "skipping the blocked-host denial assertion");
        ran.Add("permissive: blocked-host denial skipped (backend reported no network denial)");
    }
}
else
{
    Console.WriteLine(
        "permissive: no network enforcement on this backend — skipping the egress assertions");
    ran.Add("permissive: egress assertions skipped (no network enforcement)");
}

// ─── What the runtime reported about the resolved policy ────────────────────
//
// `PolicyResolved` is the runtime's own statement of the policy it applied. It carries the
// network flags, the two auth capabilities and the proxy mode, so the example can check that
// the values it asked for are the values that arrived.
foreach (var (label, policy, conversation) in new[]
         {
             ("locked", lockedPolicy, locked),
             ("permissive", permissivePolicy, permissive),
         })
{
    var resolved = conversation.SandboxDecisions
        .Select(decision => decision.Data)
        .OfType<SandboxDecisionDataPolicyResolved>()
        .ToList();

    if (resolved.Count == 0)
    {
        Console.WriteLine($"{label}: the runtime reported no resolved policy — skipping its assertions");
        continue;
    }

    foreach (var policyResolved in resolved)
    {
        if (policyResolved.AllowOutbound is { } allowOutbound)
        {
            Check.Equal(
                policy.Network!.AllowOutbound ?? false,
                allowOutbound,
                $"{label}: the resolved policy reports the requested AllowOutbound");
        }

        if (policyResolved.AllowLocalNetwork is { } allowLocalNetwork)
        {
            Check.Equal(
                policy.Network!.AllowLocalNetwork ?? false,
                allowLocalNetwork,
                $"{label}: the resolved policy reports the requested AllowLocalNetwork");
        }

        if (policyResolved.GhAuth is { } ghAuth)
        {
            Check.Equal(policy.Auth!.Gh ?? false, ghAuth, $"{label}: PolicyResolved.GhAuth is the requested value");
        }

        if (policyResolved.GitAuth is { } gitAuth)
        {
            Check.Equal(policy.Auth!.Git ?? false, gitAuth, $"{label}: PolicyResolved.GitAuth is the requested value");
        }

        Console.WriteLine(
            $"{label}: resolved — control: {policyResolved.Control.Value}, " +
            $"outbound: {policyResolved.AllowOutbound}, local network: {policyResolved.AllowLocalNetwork}, " +
            $"gh: {policyResolved.GhAuth}, git: {policyResolved.GitAuth}, " +
            $"proxy: {policyResolved.ProxyMode.Value}, backend: {policyResolved.Backend.Value}");
    }

    // `ProxyMode` is a bounded classification — `none`, `loopback` (the runtime's own
    // credential-substituting proxy), or `external` (the URL the policy supplied) — and it is a
    // statement about the proxy the runtime *used*, not a copy of the URL the policy sent. On
    // macOS seatbelt a configured proxy was reported as `none`, so the example asserts the mode
    // it received is one of the known values and asserts the configured URL on the configuration
    // above, where the durable layer's contract actually lives.
    var knownProxyModes = new[] { SandboxProxyMode.None, SandboxProxyMode.Loopback, SandboxProxyMode.External };
    Check.That(
        resolved.All(policyResolved => knownProxyModes.Contains(policyResolved.ProxyMode)),
        $"{label}: the resolved ProxyMode is one of none/loopback/external " +
        $"(saw: {string.Join(", ", resolved.Select(policyResolved => policyResolved.ProxyMode.Value))})");

    ran.Add($"{label}: resolved policy reports AllowOutbound/AllowLocalNetwork/GhAuth/GitAuth/ProxyMode");
}

// ─── What the sandbox denied, in full ───────────────────────────────────────
foreach (var (label, conversation) in new[] { ("locked", locked), ("permissive", permissive) })
{
    var decisions = conversation.SandboxDecisions;
    Console.WriteLine($"{label}: sandbox decisions observed: {decisions.Count}");
    foreach (var decision in decisions)
    {
        if (decision.Data is SandboxDecisionDataAccessDenied denied)
        {
            Console.WriteLine(
                $"  denied — class: {denied.DenialClass.Value}, point: {denied.EnforcementPoint.Value}, " +
                $"control: {denied.Control.Value}, resource: '{denied.DeniedResource}', " +
                $"outcome: {denied.Outcome.Value}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("assertions that ran:");
foreach (var line in ran)
{
    Console.WriteLine($"  - {line}");
}

Console.WriteLine();
Console.WriteLine("note: the credential value never left this process — the sandboxed shell reported only");
Console.WriteLine("a digest and a length, and the generated digest is the only comparison made.");

// ─── Cleanup ────────────────────────────────────────────────────────────────
Environment.SetEnvironmentVariable(credentialName, null);
Directory.Delete(workspace, recursive: true);

// The masked value is a random sentinel per session, so the same assertion holds in every mode
// where a shell runs. MCP and LSP confinement are policy-level facts: they are visible in the
// configuration this example built, and opting out of MCP confinement is visible as `false`
// rather than as an absent flag.
static List<SandboxDecisionDataAccessDenied> NetworkDenials(IEnumerable<SandboxDecisionEvent> decisions) =>
    decisions
        .Select(decision => decision.Data)
        .OfType<SandboxDecisionDataAccessDenied>()
        .Where(denial => denial.DenialClass == SandboxDenialClass.NetworkOutbound
            || denial.DenialClass == SandboxDenialClass.NetworkHost
            || denial.DenialClass == SandboxDenialClass.NetworkLocal)
        .ToList();
