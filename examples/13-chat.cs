// 13 — Chat.
//
// A chat turn.
//
// Submit() durably admits user input. The built-in generation task prepares the system
// prompt from the agent's sections, calls the model, and appends the answer; waiting
// resolves once the input is answered or has failed.
//
// Mirrors the Pi Durable example `13-chat.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A prompt section: the app's own instructions, rendered into the system message.
registry.Install(new Extension(
    "terse",
    sections: [new PromptSection("preamble", _ => Task.FromResult<string?>("You answer in one word."), tag: false)]));

await using var harness = await DurableHarness.OpenAsync(store, registry);
var root = await harness.RootAsync(new AgentChoices { Extensions = ["terse"] });

// Submit() durably admits user input and returns a Submission. The built-in
// generation task prepares the system prompt from the agent's sections, calls the
// model, and appends the answer; wait() resolves once the input is answered.
var answer = await root.SubmitAsync("Capital of France? Answer in one word.");
Console.WriteLine($"answer: {answer?.Data.Content}");

// The transcript holds the user input, the positional system prompt, and the answer.
var context = await root.ContextAsync();
Console.WriteLine("transcript:");
foreach (var evt in context)
{
    switch (evt)
    {
        case UserMessageEvent user:
            Console.WriteLine($"  user: {user.Data.Content}");
            break;
        case AssistantMessageEvent assistant:
            Console.WriteLine($"  assistant: {assistant.Data.Content}");
            break;
        case SystemMessageEvent system:
            Console.WriteLine($"  system: {system.Data.Content.Length} chars");
            break;
    }
}

// The durable store keeps the conversation's own entries alongside the session.
Console.WriteLine($"stored entries: [{string.Join(", ", root.Entries(10))}]");