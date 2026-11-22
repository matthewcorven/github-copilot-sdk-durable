// 28 — One sandbox per conversation.
//
// The app records each conversation's sandbox in its own document, and the tool looks it
// up for every call. Here a sandbox is a directory; a hosted product would return an
// execution environment that runs inside the conversation's container.
//
// The tool finds its conversation through the `ToolInvocation` the SDK binds into every
// tool call, so one tool definition serves every conversation and still writes into the
// right sandbox.
//
// Mirrors the Pi Durable example `28-sandbox-per-conversation.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using System.ComponentModel;

// Which sandbox a conversation runs in. `ForkPolicy.Initial`: a fork gets no sandbox
// until the app assigns one.
var Sandbox = new DocumentDefinition<SandboxState>(
    "example.sandbox",
    DocumentHistory.Latest,
    ForkPolicy.Initial,
    () => new SandboxState());

var store = new DurableStore();
var registry = new ExtensionRegistry();

// The harness maps a session id to the conversation that owns it, so the tool can find
// its sandbox without the model having to say which conversation it is in.
var conversationsBySession = new Dictionary<string, DurableConversation>();

await using var harness = await DurableHarness.OpenAsync(store, registry);

// The tool writes into the sandbox of the conversation that called it.
var writeFile = CopilotTool.DefineTool(
    ([Description("File to write")] string path, [Description("Content to write")] string content, ToolInvocation invocation) =>
    {
        var conversation = conversationsBySession.GetValueOrDefault(invocation.SessionId);
        if (conversation is null)
        {
            return "(no conversation for this session)";
        }

        var sandbox = conversation.Snapshot(Sandbox);
        if (sandbox?.Path is null)
        {
            // A conversation without a sandbox gets no environment, so its tools fail
            // cleanly instead of writing somewhere unexpected.
            return "(this conversation has no sandbox)";
        }

        var full = Path.Combine(sandbox.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"wrote {path} ({content.Length} bytes)";
    },
    factoryOptions: new AIFunctionFactoryOptions { Name = "write_file", Description = "Write a file into your sandbox" });

registry.Install(new Extension("sandbox", tools: [writeFile]));

// Each user's conversation gets a fresh sandbox in the creating commit.
async Task<(DurableConversation Conversation, string Path)> ConversationFor(string user)
{
    var path = Path.Combine(Path.GetTempPath(), $"durable-sandbox-{user}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(path);

    var conversation = await harness.CreateConversationAsync(new AgentChoices
    {
        Extensions = ["sandbox"],
        Tools = ["write_file"],
        Permissions = ["custom-tool:write_file"],
    });

    // The sandbox is assigned in its own commit, right after the conversation exists.
    await conversation.CommitAsync(async tx =>
    {
        var draft = tx.Doc(Sandbox, conversation.Id);
        draft.Value = new SandboxState { Path = path };
        draft.Save(tx.AppendEntry(conversation.Id, "sandbox", path));
    });

    conversationsBySession[conversation.SessionId] = conversation;
    return (conversation, path);
}

var alice = await ConversationFor("alice");
var bob = await ConversationFor("bob");

// The model the runtime reported for this session, not the one the host asked for.
var effectiveModel = await alice.Conversation.WaitForEffectiveModelAsync();
Console.WriteLine($"effective model: {effectiveModel.Model ?? "(not reported)"}");

await alice.Conversation.SubmitAsync("Use the write_file tool to write 'from alice' into note.txt.");
await bob.Conversation.SubmitAsync("Use the write_file tool to write 'from bob' into note.txt.");

Console.WriteLine($"alice's sandbox: {File.ReadAllText(Path.Combine(alice.Path, "note.txt"))}");
Console.WriteLine($"bob's sandbox: {File.ReadAllText(Path.Combine(bob.Path, "note.txt"))}");

// The sandboxes are separate directories, and each conversation's document points at
// its own.
Console.WriteLine($"alice's document: {alice.Conversation.Snapshot(Sandbox)?.Path}");
Console.WriteLine($"bob's document: {bob.Conversation.Snapshot(Sandbox)?.Path}");

// ─── Assertions ─────────────────────────────────────────────────────────────
//
// Each conversation wrote into its own sandbox: the tool found the sandbox
// from the conversation that called it.
Check.Equal("from alice", File.ReadAllText(Path.Combine(alice.Path, "note.txt")), "alice's note landed in alice's sandbox");
Check.Equal("from bob", File.ReadAllText(Path.Combine(bob.Path, "note.txt")), "bob's note landed in bob's sandbox");
Check.That(alice.Path != bob.Path, "the two conversations have different sandboxes");
Check.Equal(alice.Path, alice.Conversation.Snapshot(Sandbox)?.Path, "alice's document points at her sandbox");
Check.Equal(bob.Path, bob.Conversation.Snapshot(Sandbox)?.Path, "bob's document points at his sandbox");

Directory.Delete(alice.Path, recursive: true);
Directory.Delete(bob.Path, recursive: true);

/// <summary>The sandbox a conversation runs in.</summary>
public sealed class SandboxState
{
    /// <summary>The sandbox directory. Null until the app assigns one.</summary>
    public string? Path { get; set; }
}
