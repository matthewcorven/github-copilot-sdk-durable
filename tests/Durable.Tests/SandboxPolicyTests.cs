using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Durable.Tests;

public class SandboxPolicyTests
{
    private static readonly DocumentDefinition<AgentChoices> AgentDocument =
        new("durable.agent", DocumentHistory.Rewindable, ForkPolicy.AsOf);

    private static DurableSandboxPolicy FullyPopulatedPolicy() => new()
    {
        Enabled = true,
        AddCurrentWorkingDirectory = false,
        AllowBypass = false,
        AllowDevToolAccess = true,
        SandboxMcpServers = false,
        SandboxLspServers = true,
        TrustWorkingDirectory = true,
        KeychainAccess = false,
        Auth = new DurableSandboxAuth { Gh = true, Git = false },
        Credentials = new DurableSandboxCredentials
        {
            EnvVars = new Dictionary<string, List<string>>
            {
                ["NPM_TOKEN"] = ["registry.npmjs.org", "*.npmjs.org"],
                ["GITHUB_TOKEN"] = ["api.github.com"],
            },
        },
        Filesystem = new DurableSandboxFilesystem
        {
            ClearPolicyOnExit = true,
            DeniedPaths = ["{workspace}/secrets", "/var/private"],
            ReadonlyPaths = ["{workspace}/docs"],
            ReadwritePaths = ["{workspace}/src", "{workspace}/tests"],
        },
        Network = new DurableSandboxNetwork
        {
            AllowedHosts = ["api.github.com"],
            BlockedHosts = ["*.tracking.test"],
            AllowOutbound = true,
            AllowLocalNetwork = false,
            Proxy = new DurableSandboxProxy
            {
                Url = "http://proxy.test:8080",
                Username = "proxy-user",
                Password = "proxy-password",
            },
        },
    };

    [Fact]
    public async Task NoSandbox_ProducesNoSandboxConfig()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(new AgentChoices(), null);

        Assert.Null(config.SandboxConfig);
        Assert.Null(config.TrustWorkingDirectory);
    }

    [Fact]
    public async Task NoChoicesAtAll_ProducesNoSandboxConfig()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(null, null);

        Assert.Null(config.SandboxConfig);
    }

    [Fact]
    public async Task FullyPopulatedPolicy_RoundTripsThroughTheStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "state.jsonl");

        try
        {
            string conversationId;
            using (var store = new DurableStore(storePath))
            {
                var conversation = await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
                conversationId = conversation.Id;

                await store.CommitAsync(async tx =>
                {
                    var draft = tx.Doc(AgentDocument, conversationId);
                    draft.Value = new AgentChoices { Sandbox = FullyPopulatedPolicy() };
                    draft.Save(tx.AppendEntry(conversationId, "agent"));
                    await Task.CompletedTask;
                });
            }

            using var reopened = new DurableStore(storePath);
            var restored = DurableJson.FromElement<AgentChoices>(
                reopened.Document(AgentDocument.Kind, conversationId)!.Latest!.Value);

            var sandbox = Assert.IsType<DurableSandboxPolicy>(restored!.Sandbox);
            Assert.Equal(FullyPopulatedPolicy(), sandbox);
            Assert.Equal(FullyPopulatedPolicy().GetHashCode(), sandbox.GetHashCode());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyStoreWithoutSandbox_ReopensWithNoSandboxConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"durable-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var storePath = Path.Combine(directory, "state.jsonl");

        try
        {
            string conversationId;
            using (var store = new DurableStore(storePath))
            {
                var conversation = await store.CommitAsync(tx => Task.FromResult(tx.CreateConversation()));
                conversationId = conversation.Id;

                // A conversation written before the sandbox existed: choices with no Sandbox.
                await store.CommitAsync(async tx =>
                {
                    var draft = tx.Doc(AgentDocument, conversationId);
                    draft.Value = new AgentChoices { Model = "gpt-6-luna", Permissions = ["read"] };
                    draft.Save(tx.AppendEntry(conversationId, "agent"));
                    await Task.CompletedTask;
                });
            }

            using var reopened = new DurableStore(storePath);
            await using var harness = await DurableHarness.OpenAsync(reopened, new ExtensionRegistry());

            var choices = DurableJson.FromElement<AgentChoices>(
                reopened.Document(AgentDocument.Kind, conversationId)!.Latest!.Value);

            Assert.NotNull(choices);
            Assert.Null(choices!.Sandbox);

            var config = harness.BuildSessionConfig(choices, null);
            Assert.Null(config.SandboxConfig);
            Assert.Null(config.TrustWorkingDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BuildSessionConfig_MapsEveryPolicyField()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(
            new AgentChoices { WorkingDirectory = "/work/space", Sandbox = FullyPopulatedPolicy() },
            null);

        var sandbox = Assert.IsType<SandboxConfig>(config.SandboxConfig);
        Assert.True(sandbox.Enabled);
        Assert.False(sandbox.AddCurrentWorkingDirectory);
        Assert.False(sandbox.AllowBypass);
        Assert.True(sandbox.AllowDevToolAccess);
        Assert.False(sandbox.SandboxMcpServers);
        Assert.True(sandbox.SandboxLspServers);

        Assert.True(sandbox.Auth!.Gh);
        Assert.False(sandbox.Auth.Git);

        Assert.Equal(
            ["registry.npmjs.org", "*.npmjs.org"],
            sandbox.Credentials!.EnvVars["NPM_TOKEN"].InjectHosts);
        Assert.Equal(
            ["api.github.com"],
            sandbox.Credentials.EnvVars["GITHUB_TOKEN"].InjectHosts);

        var filesystem = sandbox.UserPolicy!.Filesystem!;
        Assert.True(filesystem.ClearPolicyOnExit);
        Assert.Equal(["/work/space/secrets", "/var/private"], filesystem.DeniedPaths);
        Assert.Equal(["/work/space/docs"], filesystem.ReadonlyPaths);
        Assert.Equal(["/work/space/src", "/work/space/tests"], filesystem.ReadwritePaths);

        var network = sandbox.UserPolicy.Network!;
        Assert.Equal(["api.github.com"], network.AllowedHosts);
        Assert.Equal(["*.tracking.test"], network.BlockedHosts);
        Assert.True(network.AllowOutbound);
        Assert.False(network.AllowLocalNetwork);
        Assert.Equal("http://proxy.test:8080", network.Proxy!.Url);
        Assert.Equal("proxy-user", network.Proxy.Username);
        Assert.Equal("proxy-password", network.Proxy.Password);

        Assert.False(sandbox.UserPolicy.Seatbelt!.KeychainAccess);

        // TrustWorkingDirectory is a session-level flag, not part of the sandbox config.
        Assert.Equal(WorkingDirectoryTrust.User, config.TrustWorkingDirectory);
    }

    [Fact]
    public async Task TrustWorkingDirectory_FalseMapsToSessionTrust()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(
            new AgentChoices
            {
                Sandbox = new DurableSandboxPolicy { Enabled = true, TrustWorkingDirectory = false },
            },
            null);

        Assert.Equal(WorkingDirectoryTrust.Session, config.TrustWorkingDirectory);
    }

    [Fact]
    public async Task TrustWorkingDirectory_AbsentLeavesFolderTrustUnchanged()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(
            new AgentChoices { Sandbox = new DurableSandboxPolicy { Enabled = true } },
            null);

        Assert.Null(config.TrustWorkingDirectory);
    }

    [Fact]
    public async Task WorkspaceToken_ExpandsAgainstTheConversationWorkingDirectory()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var config = harness.BuildSessionConfig(
            new AgentChoices
            {
                WorkingDirectory = "/parent/workspace",
                Sandbox = new DurableSandboxPolicy
                {
                    Enabled = true,
                    Filesystem = new DurableSandboxFilesystem
                    {
                        DeniedPaths = ["{workspace}/secrets"],
                        ReadonlyPaths = ["{workspace}/docs"],
                        ReadwritePaths = ["{workspace}/src"],
                    },
                },
            },
            null);

        var filesystem = config.SandboxConfig!.UserPolicy!.Filesystem!;
        Assert.Equal(["/parent/workspace/secrets"], filesystem.DeniedPaths);
        Assert.Equal(["/parent/workspace/docs"], filesystem.ReadonlyPaths);
        Assert.Equal(["/parent/workspace/src"], filesystem.ReadwritePaths);
    }

    [Fact]
    public void WorkspaceToken_LeavesAbsolutePathsAlone()
    {
        Assert.Equal("/var/private", DurableSandboxPolicy.ExpandPath("/var/private", "/work"));
        Assert.Equal("/work/a", DurableSandboxPolicy.ExpandPath("{workspace}/a", "/work"));
        Assert.Equal("/work/a//work/b", DurableSandboxPolicy.ExpandPath("{workspace}/a/{workspace}/b", "/work"));
    }

    [Fact]
    public async Task ForkedConversation_ExpandsTheTokenAgainstItsOwnWorkingDirectory()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var parentDirectory = Path.Combine(Path.GetTempPath(), $"durable-parent-{Guid.NewGuid():N}");
        var childDirectory = Path.Combine(Path.GetTempPath(), $"durable-child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parentDirectory);
        Directory.CreateDirectory(childDirectory);

        try
        {
            var parent = await harness.CreateConversationAsync(new AgentChoices
            {
                WorkingDirectory = parentDirectory,
                Sandbox = new DurableSandboxPolicy
                {
                    Enabled = true,
                    Filesystem = new DurableSandboxFilesystem
                    {
                        ReadwritePaths = ["{workspace}/src"],
                    },
                },
            });

            var entry = await parent.CommitAsync(async tx =>
            {
                var appended = tx.AppendEntry(parent.Id, "note", "fork point");
                await Task.CompletedTask;
                return appended;
            });

            var fork = await harness.ForkAsync(parent, entry);

            // The fork inherits the policy shape, not the parent's absolute grants.
            var forkChoices = fork.Choices();
            Assert.NotNull(forkChoices?.Sandbox);
            Assert.Equal(
                ["{workspace}/src"],
                forkChoices!.Sandbox!.Filesystem!.ReadwritePaths);

            // The fork's own working directory is what the token resolves against.
            await fork.ConfigureAsync(new AgentChoices
            {
                WorkingDirectory = childDirectory,
                Sandbox = forkChoices.Sandbox,
            });

            var parentConfig = harness.BuildSessionConfig(parent.Choices(), parent);
            var forkConfig = harness.BuildSessionConfig(fork.Choices(), fork);

            var parentPaths = parentConfig.SandboxConfig!.UserPolicy!.Filesystem!.ReadwritePaths;
            var forkPaths = forkConfig.SandboxConfig!.UserPolicy!.Filesystem!.ReadwritePaths;

            Assert.Equal([Path.Combine(parentDirectory, "src")], parentPaths);
            Assert.Equal([Path.Combine(childDirectory, "src")], forkPaths);
            Assert.NotEqual(parentPaths, forkPaths);
        }
        finally
        {
            Directory.Delete(parentDirectory, recursive: true);
            Directory.Delete(childDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WaitForSandboxDecisionAsync_TimesOutWithTheObservedDecisions()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        var conversation = await harness.CreateConversationAsync();

        Assert.Empty(conversation.SandboxDecisions);

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            conversation.WaitForSandboxDecisionAsync(
                decision => decision.Data is SandboxDecisionDataAccessDenied,
                TimeSpan.FromMilliseconds(50)));

        Assert.Contains("No sandbox decision matched", timeout.Message);
        Assert.Contains("no sandbox decisions were reported at all", timeout.Message);
    }

    [Fact]
    public async Task HarnessOptions_DefaultPermissionHandlerIsTheDurablePolicy()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(store, new ExtensionRegistry());

        Assert.Null(harness.Options.PermissionHandler);

        var config = harness.BuildSessionConfig(new AgentChoices { Permissions = ["read"] }, null);
        Assert.NotNull(config.OnPermissionRequest);
        Assert.NotSame(PermissionHandler.ApproveAll, config.OnPermissionRequest);
    }

    [Fact]
    public async Task HarnessOptions_PermissionHandlerOverridesTheDurablePolicy()
    {
        var store = DurableStore.CreateEphemeral();
        await using var harness = await DurableHarness.OpenAsync(
            store,
            new ExtensionRegistry(),
            new DurableHarnessOptions { PermissionHandler = PermissionHandler.ApproveAll });

        Assert.Same(PermissionHandler.ApproveAll, harness.Options.PermissionHandler);

        var config = harness.BuildSessionConfig(new AgentChoices(), null);
        Assert.Same(PermissionHandler.ApproveAll, config.OnPermissionRequest);
    }
}
