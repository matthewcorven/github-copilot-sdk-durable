// 15 — A real model.
//
// Stream an answer from a real model.
//
// While the answer streams, the runtime emits assistant message deltas. The final
// assistant message carries the complete content.
//
// Mirrors the Pi Durable example `15-real-model.ts`.

#:package GitHub.Copilot.SDK@1.0.26
#:property TargetFramework=net10.0
#:property NoWarn=GHCP001
#:include ../lib/Durable.cs

using Durable;
using GitHub.Copilot;

var store = new DurableStore();
var registry = new ExtensionRegistry();

// A concise preamble, rendered into the system message.
registry.Install(new Extension(
    "concise",
    sections: [new PromptSection("preamble", _ => Task.FromResult<string?>("You are a concise assistant."), tag: false)]));

await using var harness = await DurableHarness.OpenAsync(store, registry);

// Streaming is enabled, so the session emits assistant message deltas as the
// answer is generated.
var root = await harness.RootAsync(new AgentChoices
{
    Extensions = ["concise"],
    Streaming = true,
});

// Print only what each delta adds to the text printed so far.
var printed = 0;
using var subscription = root.Session.On<SessionEvent>(evt =>
{
    if (evt is not AssistantMessageDeltaEvent delta || string.IsNullOrEmpty(delta.Data.DeltaContent))
    {
        return;
    }

    Console.Write(delta.Data.DeltaContent);
    printed += delta.Data.DeltaContent.Length;
});

Console.Write("answer: ");
var answer = await root.SubmitAsync("In one sentence, what makes a durable agent different from a stateless one?");
Console.WriteLine();
Console.WriteLine($"streamed {printed} chars; final message: {answer?.Data.Content}");

// The final assistant message always carries the complete content, whether or not
// streaming is enabled.
Console.WriteLine($"final length: {answer?.Data.Content.Length}");