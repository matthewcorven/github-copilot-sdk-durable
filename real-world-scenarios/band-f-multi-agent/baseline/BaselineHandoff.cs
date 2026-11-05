// Port of microsoft/agent-framework python/samples/03-workflows/orchestrations/handoff_simple.py
// to C# with the .NET port of the same Handoff orchestration (Microsoft.Agents.AI.Workflows).
//
// The port keeps the sample's shape: one triage/coordinator agent, three specialists that own a
// single tool each, a mesh of handoff edges with the triage agent as the start agent, the sample's
// "welcome" termination condition, and a scripted user-response cycle.
//
// The sample has no checkpoint manager (grep -ci checkpoint on either vendored .py file returns 0).
// Checkpointing here is the port's addition, as is the observation: every change of the agent that
// holds control is written to stdout and to a JSONL control-transfer log, so a later fault run can
// assert which agent was in control and where to resume from.
//
// Usage:
//   dotnet run BaselineHandoff.cs -- run
//   dotnet run BaselineHandoff.cs -- resume   # continue from the newest checkpoint in CHECKPOINT_DIR
//
// CHECKPOINT_DIR must name a scratch directory outside the repository.

#:package Microsoft.Agents.AI.OpenAI@1.27.0
#:package Microsoft.Agents.AI.Workflows@1.27.0
#:property NoWarn=NU1903;IL2026;IL3050;MAAIW001

using System.ClientModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;
using Microsoft.Extensions.AI;
using OpenAI;

string checkpointDir = Environment.GetEnvironmentVariable("CHECKPOINT_DIR")
    ?? throw new InvalidOperationException("CHECKPOINT_DIR must name a scratch directory.");
string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");
string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
string baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";

// The control-transfer log is the point of this port: it is what a fault run reads to answer
// "which agent held control, and which checkpoint was written while it did?".
string controlLogPath = Environment.GetEnvironmentVariable("CONTROL_LOG")
    ?? Path.Combine(checkpointDir, "control-transfer.jsonl");

DirectoryInfo dir = new(checkpointDir);
if (!dir.Exists)
{
    throw new InvalidOperationException($"Checkpoint directory '{dir.FullName}' does not exist.");
}

OpenAIClient openai = new(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

// Fixed agent ids and names, as in the sample: the handoff executor id is derived from both, and
// the checkpoint records those ids, so a resumed graph must produce the same ones.
AIAgent Agent(string id, string description, string instructions, IList<AITool>? tools = null) =>
    openai.GetChatClient(model).AsIChatClient().AsAIAgent(new ChatClientAgentOptions
    {
        Id = id,
        Name = id,
        Description = description,
        ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools },
    });

// The sample's three tools, with the sample's exact messages. Approval is never required there
// (`approval_mode="never_require"`), which is the .NET default for a plain AIFunction.
AITool processRefund = AIFunctionFactory.Create(
    ([Description("Order number to process refund for")] string orderNumber) =>
        $"Refund processed successfully for order {orderNumber}.",
    "process_refund",
    "Simulated function to process a refund for a given order number.");

AITool checkOrderStatus = AIFunctionFactory.Create(
    ([Description("Order number to check status for")] string orderNumber) =>
        $"Order {orderNumber} is currently being processed and will ship in 2 business days.",
    "check_order_status",
    "Simulated function to check the status of a given order number.");

AITool processReturn = AIFunctionFactory.Create(
    ([Description("Order number to process return for")] string orderNumber) =>
        $"Return initiated successfully for order {orderNumber}. You will receive return instructions via email.",
    "process_return",
    "Simulated function to process a return for a given order number.");

AIAgent triage = Agent(
    "triage_agent",
    "Frontline support triage that routes customer issues to specialist agents.",
    "You are frontline support triage. Route customer issues to the appropriate specialist agents " +
    "based on the problem described.");

AIAgent refund = Agent(
    "refund_agent",
    "Specialist that processes refund requests.",
    "You process refund requests.",
    [processRefund]);

AIAgent order = Agent(
    "order_agent",
    "Specialist that handles order and shipping inquiries.",
    "You handle order and shipping inquiries.",
    [checkOrderStatus]);

AIAgent returns = Agent(
    "return_agent",
    "Specialist that manages product return requests.",
    "You manage product return requests.",
    [processReturn]);

// The sample passes participants to HandoffBuilder and designates the triage agent as the start
// agent, without declaring edges. That is the mesh topology: every participant may hand off to
// every other participant. The .NET builder produces the same topology from AddParticipants alone.
//
// The termination condition is the sample's predicate, line for line: the workflow terminates when
// the last message of the conversation contains "welcome" (upstream handoff_simple.py,
// `termination_condition=`). In this package the condition governs *autonomous continuation*, and
// this workflow is not autonomous, so the port also honours the sample's observable effect - its
// request/response loop stops instead of asking for another user turn - in its own scripted loop.
Workflow BuildWorkflow() =>
    new HandoffWorkflowBuilder(triage)
        .AddParticipants([refund, order, returns])
        .WithTerminationCondition(conversation =>
            conversation.Count > 0 &&
            conversation[^1].Text.Contains("welcome", StringComparison.OrdinalIgnoreCase))
        .Build();

// The same JSON options in both processes, with the reflection resolver because the workflow
// snapshots types that have no source-generated type info. There is no AOT target here.
JsonSerializerOptions JsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

// The sample's scripted responses, verbatim and in the sample's order, with one extra turn inserted
// before the sample's closing "thanks". The insertion is the port's only change to the script: the
// sample's three responses never send the user to the order-status specialist, so the extra turn is
// what shows the third specialist and a second route from the coordinator. It comes before "thanks"
// because the sample's termination condition ends the conversation on that closing message.
string[] ScriptedResponses =
[
    "My order 1234 arrived damaged and the packaging was destroyed. I'd like to return it.",
    "Please also process a refund for order 1234.",
    "One more thing: what is the status of order 1234?",
    "Thanks for resolving this.",
];

string[] agentIds = [triage.Id, refund.Id, order.Id, returns.Id];

// Used to read the control-transfer log back on a resume.
JsonSerializerOptions LogJsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

string Mode() => args.Length > 0 ? args[0] : "run";

if (Mode() is not ("run" or "resume"))
{
    Console.Error.WriteLine($"Unknown mode '{Mode()}'. Use 'run' or 'resume'.");
    return 2;
}

// The store opens index.jsonl with FileShare.None, so the session id a resume needs has to be read
// before the store is created. The control-transfer log carries it, written on the first run.
string? resumeSessionId = Mode() == "resume" ? LoggedSessionId(controlLogPath) : null;

using FileSystemJsonCheckpointStore store = new(dir);
CheckpointManager checkpointManager = CheckpointManager.CreateJson(store, JsonOptions);
InProcessExecutionEnvironment environment = InProcessExecution.Default.WithCheckpointing(checkpointManager);

Workflow workflow = BuildWorkflow();
ControlLog log = new(controlLogPath, append: Mode() == "resume");

Console.WriteLine($"checkpoint directory: {dir.FullName}");
Console.WriteLine($"control-transfer log: {controlLogPath}");
Console.WriteLine($"model endpoint: {baseUrl} (model {model})");
Console.WriteLine();

StreamingRun run;
if (Mode() == "resume")
{
    if (resumeSessionId is null)
    {
        Console.WriteLine($"No session recorded in '{controlLogPath}'; nothing to resume.");
        return 1;
    }

    CheckpointInfo? latest = await checkpointManager.GetLatestCheckpointAsync(resumeSessionId);
    if (latest is null)
    {
        Console.WriteLine($"No checkpoint found in '{checkpointDir}'; nothing to resume.");
        return 1;
    }

    Console.WriteLine($"=== Resuming from checkpoint {latest.CheckpointId} (session {latest.SessionId}) ===");
    run = await environment.ResumeStreamingAsync(workflow, latest);
    Console.WriteLine();
}
else
{
    run = await environment.OpenStreamingAsync(workflow);
}

await using (run)
{
    log.TurnBoundary("session", run.SessionId, null);
    Console.WriteLine($"[control] session {run.SessionId}");

    // Every message the scripted user sends, in order. The initial greeting is message 0.
    List<string> userMessages = ["Hello, I need assistance with my recent purchase.", .. ScriptedResponses];

    // A resume skips the turns whose completion is already recorded in this directory's
    // control-transfer log, so it continues rather than replaying. The baseline's checkpoint holds
    // the workflow's own state; the log is what tells this port which user turn that state is at.
    int alreadySent = Mode() == "resume" ? CompletedTurnCount(controlLogPath) : 0;
    if (alreadySent > 0)
    {
        string resumedHolder = LastHolder(controlLogPath) ?? "(unknown)";
        Console.WriteLine($"[control] resumed with {resumedHolder} holding control; skipping {alreadySent} completed turn(s)");
        log.Resumed(run.SessionId, resumedHolder, alreadySent);
    }

    int turn = alreadySent;
    string? turnCheckpoint = null;
    string? lastAgentText = null;
    int status = 0;

    while (turn < userMessages.Count)
    {
        Console.WriteLine();
        Console.WriteLine($"- User: {userMessages[turn]}");
        await SendTurnAsync(userMessages[turn]);

        if (status != 0)
        {
            break;
        }

        if (LastHolder(controlLogPath) is null)
        {
            Console.WriteLine("No agent took control; stopping.");
            break;
        }

        // The sample's termination condition, as the sample observes it: the conversation closed, so
        // the loop stops rather than sending another user turn. RunStatus.Ended is the framework's own
        // signal for the autonomous path and is checked as well, so either shape ends the loop.
        bool closed = lastAgentText?.Contains("welcome", StringComparison.OrdinalIgnoreCase) == true;
        if (closed || await run.GetStatusAsync() is RunStatus.Ended)
        {
            Console.WriteLine(
                $"[control] termination condition fired on turn {turn} " +
                (closed ? "(the agent's closing message contains \"welcome\")" : "(the workflow ended)"));
            log.Terminated(turn);
            break;
        }

        turn++;
    }

    return status;

    async Task SendTurnAsync(string userMessage)
    {
        turnCheckpoint = null;
        lastAgentText = null;
        List<string> holdersThisTurn = [];
        string? handoffFrom = null;

        await run.TrySendMessageAsync(userMessage);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (WorkflowEvent evt in run.WatchStreamAsync())
        {
            switch (evt)
            {
                case ExecutorInvokedEvent invoked when AgentIdFor(invoked.ExecutorId) is string agentId:
                    if (holdersThisTurn.Count == 0 || holdersThisTurn[^1] != agentId)
                    {
                        holdersThisTurn.Add(agentId);
                        Console.WriteLine($"[control] {agentId} holds control");
                        log.Control(agentId);
                    }
                    break;

                // The framework's own signal that one agent asked another to take over: the
                // invoked assistant produced a `handoff_to_*` function call naming its target.
                case AgentResponseUpdateEvent update
                    when update.Update.Contents.OfType<FunctionCallContent>().FirstOrDefault(
                        c => c.Name.StartsWith(HandoffWorkflowBuilder.FunctionPrefix, StringComparison.Ordinal)) is { } call:
                    handoffFrom = update.Update.AgentId ?? update.Update.AuthorName;
                    Console.WriteLine($"[control] handoff requested by {handoffFrom} ({call.Name})");
                    log.HandoffRequested(handoffFrom, call.Name);
                    break;

                case RequestInfoEvent request:
                    Console.WriteLine($"[control] workflow is waiting on request {request.Request.RequestId}");
                    log.Waiting(request.Request.RequestId, request.Request.PortInfo.PortId);
                    break;

                case SuperStepCompletedEvent step:
                    turnCheckpoint = step.CompletionInfo?.Checkpoint?.CheckpointId ?? turnCheckpoint;
                    break;

                case WorkflowOutputEvent output when output.Data is List<ChatMessage> conversation:
                    lastAgentText = PrintConversation(conversation) ?? lastAgentText;
                    break;

                case WorkflowErrorEvent error:
                    Console.Error.WriteLine($"[control] workflow error: {error.Exception}");
                    status = 1;
                    break;
            }
        }

        // More than one agent took a turn: control actually moved. That is the delegation the
        // band is about, and it is the line a fault run asserts against.
        if (holdersThisTurn.Count > 1)
        {
            for (int i = 1; i < holdersThisTurn.Count; i++)
            {
                Console.WriteLine($"[control] handoff {holdersThisTurn[i - 1]} -> {holdersThisTurn[i]}");
                log.Handoff(holdersThisTurn[i - 1], holdersThisTurn[i]);
            }
        }

        if (holdersThisTurn.Count > 0)
        {
            log.TurnEnd(turn, holdersThisTurn[^1], turnCheckpoint);
        }
    }

    // Prints the sample's conversation snapshot and returns the last agent utterance, which is
    // what the sample's termination condition inspects.
    string? PrintConversation(List<ChatMessage> conversation)
    {
        Console.WriteLine();
        Console.WriteLine("=== Final Conversation Snapshot ===");

        string? lastAgent = null;
        foreach (ChatMessage message in conversation)
        {
            string text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                // A handoff or tool call carries no text; the sample skips these too.
                continue;
            }

            Console.WriteLine($"- {message.AuthorName ?? message.Role.ToString().ToLowerInvariant()}: {text}");
            if (message.Role != ChatRole.User)
            {
                lastAgent = text;
            }
        }

        Console.WriteLine("===================================");
        return lastAgent;
    }
}

// The handoff executor id is `{agent.Id}_{agent.Name}`, both of which are the fixed ids above.
string? AgentIdFor(string executorId) =>
    agentIds.FirstOrDefault(id => executorId == $"{id}_{id}");

string? LoggedSessionId(string path) =>
    File.Exists(path)
        ? File.ReadLines(path)
              .Select(line => JsonSerializer.Deserialize<ControlRecord>(line, LogJsonOptions))
              .FirstOrDefault(record => record is { Event: "session" })?.SessionId
        : null;

// The log is the port's own record, so both a fresh run and a resume read it back the same way.
int CompletedTurnCount(string path) =>
    File.Exists(path)
        ? File.ReadLines(path).Count(line => line.Contains("\"Event\":\"turn_end\"", StringComparison.Ordinal))
        : 0;

string? LastHolder(string path) =>
    File.Exists(path)
        ? File.ReadLines(path).Reverse()
              .Select(line => JsonSerializer.Deserialize<ControlRecord>(line, LogJsonOptions))
              .FirstOrDefault(record => record is { Event: "turn_end" })?.Agent
        : null;

/// <summary>One JSONL line per observed control event, plus the human-readable console line.</summary>
internal sealed class ControlLog
{
    private readonly string _path;

    // A resume appends to the existing log: the turns before the resume are part of the evidence.
    public ControlLog(string path, bool append)
    {
        _path = path;

        string? parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!append)
        {
            File.WriteAllText(_path, string.Empty);
        }
    }

    public void Terminated(int afterTurn) =>
        Write(new ControlRecord("terminated", afterTurn, null, null, null, null, null));

    public void Resumed(string sessionId, string holder, int completedTurns) =>
        Write(new ControlRecord("resumed", completedTurns, sessionId, holder, null, null, null));

    public void TurnBoundary(string @event, string sessionId, int? turn) =>
        Write(new ControlRecord(@event, turn, sessionId, null, null, null, null));

    public void Control(string agent) =>
        Write(new ControlRecord("control", null, null, agent, null, null, null));

    public void HandoffRequested(string? from, string functionName) =>
        Write(new ControlRecord("handoff_requested", null, null, from, null, functionName, null));

    public void Handoff(string from, string to) =>
        Write(new ControlRecord("handoff", null, null, to, from, null, null));

    public void Waiting(string requestId, string portId) =>
        Write(new ControlRecord("waiting", null, null, null, null, null, requestId));

    public void TurnEnd(int turn, string holder, string? checkpointId) =>
        Write(new ControlRecord("turn_end", turn, null, holder, null, null, checkpointId));

    // The file-based app has reflection-based serialization disabled, so the log's own options
    // carry an explicit resolver rather than relying on the default one.
    private static readonly JsonSerializerOptions LogOptions =
        new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private void Write(ControlRecord record) =>
        File.AppendAllText(_path, JsonSerializer.Serialize(record, LogOptions) + Environment.NewLine);

}

internal sealed record ControlRecord(
    string Event,
    int? Turn,
    string? SessionId,
    string? Agent,
    string? From,
    string? Function,
    string? Checkpoint);
