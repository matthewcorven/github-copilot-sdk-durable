using Durable;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Xunit;

namespace Durable.Tests;

public class PermissionPolicyTests
{
    [Fact]
    public void NoGrants_DeniesEverything()
    {
        Assert.False(PermissionPolicy.IsGranted(null, "custom-tool", "read"));
        Assert.False(PermissionPolicy.IsGranted([], "custom-tool", "read"));
        Assert.False(PermissionPolicy.IsGranted(["custom-tool:write"], "custom-tool", "read"));
    }

    [Fact]
    public void KindOnlyGrant_AllowsEveryScopeOfThatKind()
    {
        Assert.True(PermissionPolicy.IsGranted(["custom-tool"], "custom-tool", "read"));
        Assert.True(PermissionPolicy.IsGranted(["custom-tool:*"], "custom-tool", "read"));
        Assert.False(PermissionPolicy.IsGranted(["custom-tool"], "shell", "ls"));
    }

    [Fact]
    public void ScopedGrant_MatchesOnlyAtAPathBoundary()
    {
        Assert.True(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/work"));
        Assert.True(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/work/file.txt"));
        Assert.False(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp/workshop/file.txt"));
        Assert.False(PermissionPolicy.IsGranted(["read:/tmp/work"], "read", "/tmp"));
    }

    [Fact]
    public void ScopedGrant_DoesNotAllowAHostSuffixAttack()
    {
        Assert.True(PermissionPolicy.IsGranted(
            ["url:https://example.com"], "url", "https://example.com/a"));
        Assert.False(PermissionPolicy.IsGranted(
            ["url:https://example.com"], "url", "https://example.com.evil.test/a"));
    }

    [Fact]
    public void WildcardKind_GrantsEveryKind()
    {
        Assert.True(PermissionPolicy.IsGranted(["*"], "shell", "rm -rf /"));
        Assert.True(PermissionPolicy.IsGranted(["*:read"], "custom-tool", "read"));
    }

    [Fact]
    public void GrantParsing_RoundTripsKindAndScope()
    {
        var grant = PermissionGrant.Parse("  read : /tmp/work  ");
        Assert.Equal("read", grant.Kind);
        Assert.Equal("/tmp/work", grant.Scope);
        Assert.Equal("read:/tmp/work", grant.ToString());

        var bare = PermissionGrant.Parse("custom-tool");
        Assert.Equal("custom-tool", bare.Kind);
        Assert.Null(bare.Scope);
        Assert.Equal("custom-tool", bare.ToString());
    }

    [Fact]
    public async Task Handler_RejectsAnUngrantedToolAndApprovesAGrantedOne()
    {
        var deny = PermissionPolicy.Handler(null);
        var denied = await deny(
            new PermissionRequestCustomTool
            {
                ToolName = "delete_everything",
                ToolDescription = "Deletes every file.",
            },
            new PermissionInvocation());
        var rejection = Assert.IsType<PermissionDecisionReject>(denied);
        Assert.Contains("custom-tool:delete_everything", rejection.Feedback);

        var allow = PermissionPolicy.Handler(["custom-tool:delete_everything"]);
        var approved = await allow(
            new PermissionRequestCustomTool
            {
                ToolName = "delete_everything",
                ToolDescription = "Deletes every file.",
            },
            new PermissionInvocation());
        Assert.IsType<PermissionDecisionApproveOnce>(approved);
    }

    [Fact]
    public async Task Grants_SurviveAStoreReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"durable-perm-{Guid.NewGuid():N}.jsonl");
        try
        {
            string conversationId;
            using (var store = new DurableStore(path))
            {
                var conversation = await store.CommitAsync(async tx =>
                {
                    var created = tx.CreateConversation();
                    var draft = tx.Doc(
                        new DocumentDefinition<AgentChoices>("durable.agent"),
                        created.Id);
                    draft.Value = new AgentChoices { Permissions = ["custom-tool:read_file"] };
                    draft.Save(tx.AppendEntry(created.Id, "agent"));
                    await Task.CompletedTask;
                    return created;
                });
                conversationId = conversation.Id;
            }

            using (var reopened = new DurableStore(path))
            {
                var latest = reopened.Document("durable.agent", conversationId)?.Latest;
                Assert.NotNull(latest);
                var choices = DurableJson.FromElement<AgentChoices>(latest!.Value);
                Assert.Equal(["custom-tool:read_file"], choices!.Permissions);
            }
        }
        finally
        {
            foreach (var sidecar in new[] { path, path + ".lock", path + ".lock.owner" })
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }
            }
        }
    }
}
