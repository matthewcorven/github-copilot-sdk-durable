// Port of microsoft/agent-framework python/samples/03-workflows/orchestrations/magentic_checkpoint.py
// to C# with the .NET port of the same Magentic orchestration (Microsoft.Agents.AI.Workflows).
//
// The port keeps the sample's shape: a Magentic manager with two participants, plan review enabled,
// a file-backed checkpoint store, and a resume that supplies the approval for the pending
// plan-review request from a second process. It does not add anything to the baseline's checkpointing.
//
// Usage:
//   dotnet run BaselineCheckpoint.cs -- run      # run to the plan-review gate, record the checkpoint, exit
//   dotnet run BaselineCheckpoint.cs -- resume   # resume from the recorded checkpoint, approve, complete
//
// CHECKPOINT_DIR must name a scratch directory outside the repository.

#:package Microsoft.Agents.AI.OpenAI@1.27.0
#:package Microsoft.Agents.AI.Workflows@1.27.0
#:property NoWarn=NU1903;IL2026;IL3050

using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.InProc;
using Microsoft.Extensions.AI;
using OpenAI;

const string Task =
    "Draft a concise internal brief describing how our research and implementation teams should " +
    "collaborate to launch a beta feature for data-driven email summarization. Highlight the key " +
    "milestones, risks, and communication cadence.";

string checkpointDir = Environment.GetEnvironmentVariable("CHECKPOINT_DIR")
    ?? throw new InvalidOperationException("CHECKPOINT_DIR must name a scratch directory.");
string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");
string model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
string baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1";

// The checkpoint directory is created by run.sh; a missing one means the two-process flow was
// started wrong, not that the store should silently create it.
DirectoryInfo dir = new(checkpointDir);
if (!dir.Exists)
{
    throw new InvalidOperationException($"Checkpoint directory '{dir.FullName}' does not exist.");
}

string mode = args.Length > 0 ? args[0] : "run";
string gateFile = Path.Combine(dir.FullName, "gate.json");

OpenAIClient openai = new(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

// Fixed agent ids: the checkpoint records executor ids, and the resumed graph must produce the
// same ones or the checkpoint is rejected as incompatible.
AIAgent Agent(string id, string description, string instructions) =>
    openai.GetChatClient(model).AsIChatClient().AsAIAgent(new ChatClientAgentOptions
    {
        Id = id,
        Name = id,
        Description = description,
        ChatOptions = new ChatOptions { Instructions = instructions },
    });

AIAgent researcher = Agent(
    "ResearcherAgent",
    "Collects background facts and references for the project.",
    "You are the research lead. Gather crisp bullet points the team should know.");

AIAgent writer = Agent(
    "WriterAgent",
    "Synthesizes the final brief for stakeholders.",
    "You convert the research notes into a structured brief with milestones and risks.");

AIAgent manager = Agent(
    "MagenticManager",
    "Orchestrator that coordinates the research and writing workflow",
    "You coordinate a team to complete complex tasks efficiently.");

Workflow BuildWorkflow() =>
    new MagenticWorkflowBuilder(manager)
        .AddParticipants([researcher, writer])
        .RequirePlanSignoff(true)
        .WithMaxRounds(10)
        .WithMaxStalls(3)
        .Build();

// The same JSON options are needed in both processes, and they must use the reflection resolver
// because the workflow snapshots a Dictionary<string, ToolApprovalRequestContent> that has no
// source-generated type info. The file-based app therefore opts out of the trim/AOT warnings for
// this one resolver; there is no AOT target here.
JsonSerializerOptions JsonOptions = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

if (mode == "run")
{
    FileSystemJsonCheckpointStore store = new(dir);
    CheckpointManager checkpointManager =
        CheckpointManager.CreateJson(store, JsonOptions);
    InProcessExecutionEnvironment environment =
        InProcessExecution.Default.WithCheckpointing(checkpointManager);

    Workflow workflow = BuildWorkflow();

    Console.WriteLine("=== Stage 1: run until plan review request (checkpointing active) ===");

    ExternalRequest? planReviewRequest = null;
    CheckpointInfo? gateCheckpoint = null;

    await using (StreamingRun run = await environment.OpenStreamingAsync(workflow))
    {
        await run.TrySendMessageAsync(new List<ChatMessage> { new(ChatRole.User, Task) });
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
        {
            LogEvent(evt);

            switch (evt)
            {
                case RequestInfoEvent request when request.Request.Data.Is<MagenticPlanReviewRequest>(out _):
                    planReviewRequest = request.Request;
                    Console.WriteLine($"Captured plan review request: {request.Request.RequestId}");
                    break;

                case SuperStepCompletedEvent step:
                    gateCheckpoint = step.CompletionInfo?.Checkpoint ?? gateCheckpoint;
                    break;

                case WorkflowErrorEvent error:
                    throw new InvalidOperationException(
                        $"Workflow failed before the gate: {error.Exception}", error.Exception);
            }
        }

        if (planReviewRequest is null)
        {
            Console.WriteLine("No plan review request emitted; nothing to resume.");
            return 1;
        }

        // The checkpoint written at the superstep that surfaced the request. This is the
        // checkpoint-before-gate point; it is what the second process resumes from.
        gateCheckpoint ??= await checkpointManager.GetLatestCheckpointAsync(run.SessionId)
            ?? throw new InvalidOperationException("No checkpoints persisted.");
    }

    store.Dispose();

    // A small JSON record, written with source-generated type info so the file-based app keeps
    // working with reflection-based serialization disabled.
    File.WriteAllText(gateFile, JsonSerializer.Serialize(
        new GateRecord(gateCheckpoint!.SessionId, gateCheckpoint.CheckpointId, planReviewRequest.RequestId),
        GateJsonContext.Default.GateRecord));

    Console.WriteLine($"Using checkpoint {gateCheckpoint.CheckpointId} at session {gateCheckpoint.SessionId}");

    // Show that the checkpoint file itself carries the pending plan-review request, the way the
    // Python sample prints list(snapshot["pending_request_info_events"]).
    FileInfo gateFileInfo = dir.EnumerateFiles()
        .Single(f => f.Name.Contains(gateCheckpoint.CheckpointId, StringComparison.Ordinal));
    using JsonDocument snapshot = JsonDocument.Parse(File.ReadAllText(gateFileInfo.FullName));
    List<string> pending = [.. snapshot.RootElement
        .GetProperty("runnerData").GetProperty("outstandingRequests").EnumerateArray()
        .Select(r => r.GetProperty("requestId").GetString() ?? "")];
    Console.WriteLine($"Pending plan-review requests persisted in checkpoint: [{string.Join(", ", pending.Select(p => $"'{p}'"))}]");
    Console.WriteLine($"Checkpoint id recorded in {gateFile}");
    Console.WriteLine();
    Console.WriteLine("=== Process exits at the gate; approval is supplied by resume.sh ===");

    foreach (FileInfo file in dir.EnumerateFiles().OrderBy(f => f.Name))
    {
        Console.WriteLine($"  checkpoint store: {file.Name} ({file.Length} bytes)");
    }

    return 0;
}

if (mode == "resume")
{
    if (!File.Exists(gateFile))
    {
        throw new InvalidOperationException($"No gate record at '{gateFile}'. Run run.sh first.");
    }

    GateRecord gate = JsonSerializer.Deserialize(File.ReadAllText(gateFile), GateJsonContext.Default.GateRecord)
        ?? throw new InvalidOperationException($"Could not read '{gateFile}'.");
    string sessionId = gate.SessionId;
    string checkpointId = gate.CheckpointId;
    string recordedRequestId = gate.RequestId;

    Console.WriteLine("=== Stage 2: resume from checkpoint and approve plan ===");
    Console.WriteLine($"Resuming from checkpoint {checkpointId} (session {sessionId})");

    // A fresh store, manager, environment and workflow: nothing is carried over in memory from
    // the process that ran to the gate.
    using FileSystemJsonCheckpointStore store = new(dir);
    CheckpointManager checkpointManager = CheckpointManager.CreateJson(store, JsonOptions);
    InProcessExecutionEnvironment environment =
        InProcessExecution.Default.WithCheckpointing(checkpointManager);

    Workflow workflow = BuildWorkflow();
    CheckpointInfo from = new(sessionId, checkpointId);

    await using StreamingRun run = await environment.ResumeStreamingAsync(workflow, from);

    // The pending request is re-emitted on resume, exactly as the Python sample re-reads it before
    // constructing the approval.
    ExternalRequest? reemitted = null;
    await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
    {
        LogEvent(evt);

        if (evt is RequestInfoEvent request && request.Request.Data.Is<MagenticPlanReviewRequest>(out _))
        {
            reemitted = request.Request;
            break;
        }
    }

    if (reemitted is null)
    {
        Console.WriteLine("No plan review request re-emitted on resume; cannot approve.");
        return 1;
    }

    Console.WriteLine(
        $"Resumed plan review request: {reemitted.RequestId}" +
        (reemitted.RequestId == recordedRequestId ? " (matches the recorded request)" : " (id differs from the recorded request)"));

    MagenticPlanReviewRequest plan = reemitted.Data.As<MagenticPlanReviewRequest>()
        ?? throw new InvalidOperationException("The re-emitted request did not carry a plan.");
    ExternalResponse approval = reemitted.CreateResponse(plan.Approve());
    Console.WriteLine("Approving plan and continuing to completion.");

    await run.SendResponseAsync(approval);

    WorkflowOutputEvent? finalEvent = null;
    CheckpointInfo? postApprovalCheckpoint = null;

    await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false))
    {
        LogEvent(evt);

        switch (evt)
        {
            case WorkflowOutputEvent output:
                finalEvent = output;
                break;

            // The checkpoint-after-approval point: the run keeps checkpointing past the gate and the
            // latest one is what a third process would restart from.
            case SuperStepCompletedEvent step:
                postApprovalCheckpoint = step.CompletionInfo?.Checkpoint ?? postApprovalCheckpoint;
                break;

            case WorkflowErrorEvent error:
                throw new InvalidOperationException(
                    $"Workflow failed after approval: {error.Exception}", error.Exception);
        }
    }

    if (finalEvent is null)
    {
        Console.WriteLine("Workflow did not complete after resume.");
        return 1;
    }

    List<ChatMessage> result = finalEvent.As<List<ChatMessage>>()
        ?? throw new InvalidOperationException("The workflow output did not carry a message list.");
    Console.WriteLine();
    Console.WriteLine("=== Final Answer ===");
    Console.WriteLine(result[^1].Text);

    postApprovalCheckpoint ??= await checkpointManager.GetLatestCheckpointAsync(sessionId);

    if (postApprovalCheckpoint is null)
    {
        Console.WriteLine("No checkpoint was written after approval.");
        return 1;
    }

    if (postApprovalCheckpoint.Equals(from))
    {
        Console.WriteLine($"No checkpoint was written after approval; latest is still {postApprovalCheckpoint.CheckpointId}.");
    }
    else
    {
        Console.WriteLine($"Checkpoint after approval: {postApprovalCheckpoint.CheckpointId} " +
                          $"(was {from.CheckpointId} at the gate)");
    }

    foreach (FileInfo file in dir.EnumerateFiles().OrderBy(f => f.Name))
    {
        Console.WriteLine($"  checkpoint store: {file.Name} ({file.Length} bytes)");
    }

    return 0;
}

Console.Error.WriteLine($"Unknown mode '{mode}'. Use 'run' or 'resume'.");
return 2;

// One line per event the workflow raises, so the evidence file shows where each process was.
static void LogEvent(WorkflowEvent evt)
{
    if (evt is RequestInfoEvent request)
    {
        Console.WriteLine($"  event: RequestInfoEvent {request.Request.RequestId}");
    }
    else if (evt is SuperStepCompletedEvent)
    {
        Console.WriteLine("  event: SuperStepCompletedEvent");
    }
    else if (evt is AgentResponseUpdateEvent)
    {
        // Streaming tokens are already printed as the final answer; skip them in the trace.
    }
    else if (evt is ExecutorInvokedEvent invoked)
    {
        Console.WriteLine($"  event: ExecutorInvokedEvent {invoked.ExecutorId}");
    }
    else
    {
        Console.WriteLine($"  event: {evt.GetType().Name}");
    }
}

internal sealed record GateRecord(string SessionId, string CheckpointId, string RequestId);

[System.Text.Json.Serialization.JsonSerializable(typeof(GateRecord))]
internal sealed partial class GateJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
