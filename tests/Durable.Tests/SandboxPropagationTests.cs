using System.Text.Json;
using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Durable.Tests;

/// <summary>
/// The stored sandbox policy reaches every session the durable layer opens, and a policy that
/// cannot be applied stops the session instead of being narrowed.
/// </summary>
/// <remarks>
/// The assertions read the configuration the runtime builds, before any session is created or
/// resumed. That is the only place the question "did the policy survive?" can be answered
/// without a live runtime: once a session exists, the SDK owns the answer.
/// </remarks>
public class SandboxPropagationTests
{
    private static readonly DocumentDefinition<AgentChoices> AgentDocument =
        new("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    private static readonly DocumentDefinition<SessionBinding> BindingDocument =
        new("durable.session-binding", DocumentHistory.Rewindable, ForkPolicy.Initial);

    private static DurableSandboxPolicy Policy() => new()
    {
        Enabled = true,
        AllowBypass = false,
        TrustWorkingDirectory = true,
        Credentials = new DurableSandboxCredentials
        {
            EnvVars = new Dictionary<string, List<string>>
            {
                ["NPM_TOKEN"] = ["registry.npmjs.org"],
            },
        },
        Filesystem = new DurableSandboxFilesystem
        {
            ReadwritePaths = ["{workspace}/src"],
        },
        Network = new DurableSandboxNetwork
        {
            AllowedHosts = ["api.github.com"],
        },
    };

    [Fact]
    public async Task Facade_StoredPolicy_IsInTheEffectiveConfigBeforeAnySessionIsCreated()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, Policy());

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(UnstartedClient())
            .Build();

        // No session has been created: the client is bound to a client that was never started,
        // so a session attempt would fail rather than succeed quietly.
        var config = await client.GetSessionConfigAsync(conversationId);

        var sandbox = Assert.IsType<SandboxConfig>(config.SandboxConfig);
        Assert.True(sandbox.Enabled);
        Assert.False(sandbox.AllowBypass);
        Assert.Equal(["api.github.com"], sandbox.UserPolicy!.Network!.AllowedHosts);
        Assert.Equal(["registry.npmjs.org"], sandbox.Credentials!.EnvVars["NPM_TOKEN"].InjectHosts);
        Assert.Equal(WorkingDirectoryTrust.User, config.TrustWorkingDirectory);
    }

    [Fact]
    public async Task Harness_StoredPolicy_IsInTheEffectiveConfigBeforeAnySessionIsCreated()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, Policy());

        var runtime = Runtime(store);
        var choices = StoredChoices(store, conversationId);

        var config = runtime.BuildSessionConfig(choices, null);

        var sandbox = Assert.IsType<SandboxConfig>(config.SandboxConfig);
        Assert.True(sandbox.Enabled);
        Assert.Equal(["api.github.com"], sandbox.UserPolicy!.Network!.AllowedHosts);
    }

    [Fact]
    public async Task ResumeConfig_CarriesTheStoredPolicy()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, Policy());

        var runtime = Runtime(store);
        var choices = StoredChoices(store, conversationId);

        // The same call ResumeConversationAsync makes, so the resumed session runs with the
        // configuration a created session would have been given.
        var config = runtime.BuildResumeSessionConfig(choices, null);

        var sandbox = Assert.IsType<SandboxConfig>(config.SandboxConfig);
        Assert.True(sandbox.Enabled);
        Assert.Equal(["api.github.com"], sandbox.UserPolicy!.Network!.AllowedHosts);
        Assert.Equal(["registry.npmjs.org"], sandbox.Credentials!.EnvVars["NPM_TOKEN"].InjectHosts);
    }

    [Fact]
    public async Task Resume_WithAnUnappliableStoredPolicy_FailsBeforeAnyRpc()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, UnappliablePolicy(), bindSession: "session-1");

        var runtime = Runtime(store);

        // The client is bound to an address nothing listens on. If the policy were checked after
        // the metadata probe, this would surface as a connection failure instead.
        var failure = await Assert.ThrowsAsync<DurableSandboxPolicyException>(
            () => runtime.ResumeConversationAsync(conversationId));

        Assert.Equal(conversationId, store.Conversations.Single().Id);
        Assert.Contains("disables the sandbox", failure.Reason);
    }

    [Fact]
    public async Task Create_WithAnUnappliablePolicy_WritesNoConversation()
    {
        var store = DurableStore.CreateEphemeral();
        var runtime = Runtime(store);

        await Assert.ThrowsAsync<DurableSandboxPolicyException>(
            () => runtime.CreateConversationAsync(new AgentChoices { Sandbox = UnappliablePolicy() }));

        // Nothing was committed: a conversation that can never be opened must not be in the store.
        Assert.Empty(store.Conversations);
    }

    [Fact]
    public async Task FacadeCreate_WithAnUnappliablePolicy_WritesNoConversation()
    {
        var store = DurableStore.CreateEphemeral();

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(UnstartedClient())
            .Build();

        await Assert.ThrowsAsync<DurableSandboxPolicyException>(
            () => client.CreateConversationAsync(new AgentChoices { Sandbox = UnappliablePolicy() }));

        Assert.Empty(store.Conversations);
    }

    [Fact]
    public async Task Facade_StoredUnappliablePolicy_FailsWhenTheConfigIsBuilt()
    {
        var store = DurableStore.CreateEphemeral();
        var conversationId = await SeedAsync(store, UnappliablePolicy());

        await using var client = new DurableClientBuilder()
            .UseStore(store)
            .UseClient(UnstartedClient())
            .Build();

        var failure = await Assert.ThrowsAsync<DurableSandboxPolicyException>(
            () => client.GetSessionConfigAsync(conversationId));

        Assert.Same(failure.Policy, failure.Policy);
        Assert.Contains("silent downgrade", failure.Message);
    }

    [Theory]
    [InlineData("disabled-with-restrictions")]
    [InlineData("empty-env-var-name")]
    [InlineData("no-inject-hosts")]
    [InlineData("empty-inject-host")]
    [InlineData("bare-wildcard-host")]
    public void BuildSandboxConfig_RejectsAPolicyItCannotApplyFaithfully(string shape)
    {
        var policy = shape switch
        {
            "disabled-with-restrictions" => new DurableSandboxPolicy
            {
                Enabled = false,
                Network = new DurableSandboxNetwork { AllowedHosts = ["api.github.com"] },
            },
            "empty-env-var-name" => CredentialPolicy(" ", ["api.github.com"]),
            "no-inject-hosts" => CredentialPolicy("NPM_TOKEN", []),
            "empty-inject-host" => CredentialPolicy("NPM_TOKEN", [" "]),
            "bare-wildcard-host" => CredentialPolicy("NPM_TOKEN", ["*"]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var failure = Assert.Throws<DurableSandboxPolicyException>(
            () => DurableHarness.BuildSandboxConfig(policy, "/work"));

        Assert.Same(policy, failure.Policy);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
    }

    [Fact]
    public void BuildSandboxConfig_AcceptsAHostPatternThatIsNotBareWildcard()
    {
        var config = DurableHarness.BuildSandboxConfig(
            CredentialPolicy("NPM_TOKEN", ["*.npmjs.org"]),
            "/work");

        Assert.Equal(["*.npmjs.org"], config.Credentials!.EnvVars["NPM_TOKEN"].InjectHosts);
    }

    [Fact]
    public void BuildSandboxConfig_AcceptsADisabledPolicyWithNoRestrictions()
    {
        var config = DurableHarness.BuildSandboxConfig(new DurableSandboxPolicy { Enabled = false }, "/work");

        Assert.False(config.Enabled);
    }

    private static DurableSandboxPolicy CredentialPolicy(string name, List<string> hosts) => new()
    {
        Enabled = true,
        Credentials = new DurableSandboxCredentials
        {
            EnvVars = new Dictionary<string, List<string>> { [name] = hosts },
        },
    };

    private static DurableSandboxPolicy UnappliablePolicy() => new()
    {
        Enabled = false,
        Network = new DurableSandboxNetwork { AllowedHosts = ["api.github.com"] },
    };

    private static async Task<string> SeedAsync(
        DurableStore store,
        DurableSandboxPolicy policy,
        string? bindSession = null)
    {
        var conversation = await store.CommitAsync(tx =>
        {
            var created = tx.CreateConversation();
            var draft = tx.Doc(AgentDocument, created.Id);
            draft.Value = new AgentChoices { Model = "replay-model", Sandbox = policy };
            draft.Save(tx.AppendEntry(created.Id, "agent"));

            if (bindSession is not null)
            {
                var binding = tx.Doc(BindingDocument, created.Id);
                binding.Value = new SessionBinding { SessionId = bindSession, Model = "replay-model" };
                binding.Save(tx.AppendEntry(created.Id, "session-binding"));
            }

            return Task.FromResult(created);
        });

        return conversation.Id;
    }

    private static AgentChoices? StoredChoices(DurableStore store, string conversationId) =>
        store.Document(AgentDocument.Kind, conversationId)?.Latest is { } latest
            ? latest.Deserialize<AgentChoices>(DurableJson.Options)
            : null;

    /// <summary>
    /// A runtime bound to a client that was never started. Nothing is spawned and no RPC is
    /// issued, so a test that reaches the SDK would fail rather than pass by accident.
    /// </summary>
    private static DurableRuntime Runtime(DurableStore store)
    {
        var runtime = new DurableRuntime(new ExtensionRegistry());
        runtime.Attach(UnstartedClient(), store, ownsClient: true, ownsStore: false);
        return runtime;
    }

    private static CopilotClient UnstartedClient() => new(new CopilotClientOptions
    {
        Connection = RuntimeConnection.ForUri("127.0.0.1:1"),
    });
}
