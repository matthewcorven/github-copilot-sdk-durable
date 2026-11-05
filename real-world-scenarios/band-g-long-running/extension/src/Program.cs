// The Band G middle column, driven by the harness instead of by the sample's own console loop.
//
//   extension-app <verb> [options]
//     start  --instance <id> [--stop <port>]   start the workflow and leave it at a port
//     wait   --instance <id> [--timeout N]     block until the instance reaches a terminal state
//     status --instance <id>                   print the instance's runtime status
//     answer --instance <id> --port <id> [--times N]
//                                              raise N answers for a port, all with the same payload
//     inspect --instance <id>                  print the runtime status and the path it is at
//
// The workflow is the vendored sample's, unchanged: the executors in `../../upstream/Executors.cs` and
// the graph `Program.cs` builds. What this file adds is a set of verbs, so a fault script can put the run
// in a state, kill a process, and observe from a different one — the same shape the two repository
// variants are driven in.
//
// **The answer is an event, and the event's name is the port id.** That is what makes the duplicate and
// late faults meaningful here rather than assertions about library internals: an answer is a message
// raised on an orchestration instance, so a second one is a second event, and the framework — not this
// driver — decides what an event that arrives after the workflow settled means.
//
// The instance id is caller-supplied on `start`, which is what lets a later process name the same run
// without a side channel. It also means a second `start` with a live id is refused by the scheduler
// (`OrchestrationAlreadyExistsException`) rather than silently forking the run.

using System.Diagnostics;
using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkflowHITL;

string connectionString = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
    ?? "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";

string verb = args.Length > 0 ? args[0] : string.Empty;
string? instance = null;
string? stopAt = null;
string? answerPort = null;
int times = 1;
int timeoutSeconds = 180;
var waitAfter = false;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--instance" when i + 1 < args.Length: instance = args[++i]; break;
        case "--stop" when i + 1 < args.Length: stopAt = args[++i]; break;
        case "--port" when i + 1 < args.Length: answerPort = args[++i]; break;
        case "--times" when i + 1 < args.Length: times = int.Parse(args[++i]); break;
        case "--timeout" when i + 1 < args.Length: timeoutSeconds = int.Parse(args[++i]); break;
        case "--wait": waitAfter = true; break;
        default:
            Console.Error.WriteLine($"extension-app: unknown argument '{args[i]}'");
            return 2;
    }
}

if (verb.Length == 0 || (instance is null && verb != "ready"))
{
    Console.Error.WriteLine("usage: extension-app start|wait|status|answer|inspect|terminate --instance <id> [...]; extension-app ready");
    return 2;
}

// The sample's graph, exactly as `upstream/Program.cs` builds it.
CreateApprovalRequest createRequest = new();
RequestPort<ApprovalRequest, ApprovalResponse> managerApproval = RequestPort.Create<ApprovalRequest, ApprovalResponse>("ManagerApproval");
PrepareFinanceReview prepareFinanceReview = new();
RequestPort<ApprovalRequest, ApprovalResponse> budgetApproval = RequestPort.Create<ApprovalRequest, ApprovalResponse>("BudgetApproval");
RequestPort<ApprovalRequest, ApprovalResponse> complianceApproval = RequestPort.Create<ApprovalRequest, ApprovalResponse>("ComplianceApproval");
ExpenseReimburse reimburse = new();

Workflow expenseApproval = new WorkflowBuilder(createRequest)
    .WithName("ExpenseReimbursement")
    .WithDescription("Expense reimbursement with manager and parallel finance approvals")
    .AddEdge(createRequest, managerApproval)
    .AddEdge(managerApproval, prepareFinanceReview)
    .AddFanOutEdge(prepareFinanceReview, [budgetApproval, complianceApproval])
    .AddFanInBarrierEdge([budgetApproval, complianceApproval], reimburse)
    .Build();

IHost host = Host.CreateDefaultBuilder()
    .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
    .ConfigureServices(services =>
        services.ConfigureDurableWorkflows(
            options => options.AddWorkflow(expenseApproval),
            workerBuilder: builder => builder.UseDurableTaskScheduler(connectionString),
            clientBuilder: builder => builder.UseDurableTaskScheduler(connectionString)))
    .Build();

await host.StartAsync();
try
{
    var workflows = host.Services.GetRequiredService<IWorkflowClient>();
    var scheduler = host.Services.GetRequiredService<DurableTaskClient>();
    var target = instance!;

    switch (verb)
    {
        case "start":
        {
            // A run id is a durable handle, so the caller names it. A second start on a live id is the
            // scheduler's refusal to have two runs under one name.
            IStreamingWorkflowRun run = await workflows.StreamAsync(expenseApproval, "EXP-BAND-G", target);
            Console.WriteLine($"instance:   {run.RunId}");
            Console.Out.Flush();

            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                if (evt is DurableWorkflowWaitingForInputEvent waiting)
                {
                    Console.WriteLine($"paused:     {waiting.RequestPort.Id}");
                    Console.Out.Flush();
                    if (stopAt is not null && waiting.RequestPort.Id == stopAt)
                    {
                        // Leave the process with the run still parked. Nothing is answered and nothing is
                        // cancelled: the scheduler holds the instance, which is the whole shape.
                        Console.WriteLine("leaving:    the process exits with the run parked at the port");
                        Console.Out.Flush();
                        return 0;
                    }

                    await run.SendResponseAsync(waiting, new ApprovalResponse(true, "answered by the harness"));
                }
                else if (evt is DurableWorkflowCompletedEvent completed)
                {
                    Console.WriteLine($"completed:  {completed.Result}");
                }
                else if (evt is DurableWorkflowFailedEvent failed)
                {
                    Console.WriteLine($"failed:     {failed.ErrorMessage}");
                }
            }

            return 0;
        }

        case "wait":
        {
            var finished = await scheduler.WaitForInstanceCompletionAsync(instance, getInputsAndOutputs: true, CancellationToken.None);
            Console.WriteLine($"instance:   {finished?.InstanceId}");
            Console.WriteLine($"status:     {finished?.RuntimeStatus}");
            if (finished?.SerializedOutput is { } output)
            {
                Console.WriteLine($"output:     {output}");
            }

            return 0;
        }

        case "ready":
        {
            // "Did the scheduler answer?" is a different question from "does this instance exist", and
            // conflating them made a readiness probe fail against a scheduler that was answering
            // perfectly well with `(absent)`. This verb returns 0 for any answer.
            try
            {
                await scheduler.GetInstanceAsync("__readiness__", CancellationToken.None);
                Console.WriteLine("ready");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"extension-app: {ex.Message}");
                return 1;
            }
        }

        case "status":
        {
            var metadata = await scheduler.GetInstanceAsync(target, CancellationToken.None);
            Console.WriteLine(metadata is null ? "status:     (absent)" : $"status:     {metadata.RuntimeStatus}");
            return metadata is null ? 3 : 0;
        }

        case "terminate":
        {
            // The abort. `TerminateInstanceAsync` is the scheduler's own stop; whether the instance then reads
            // Terminated is the scheduler's answer, and whether a later event can move it is the framework's.
            try
            {
                await scheduler.TerminateInstanceAsync(target, "terminated by the harness", CancellationToken.None);
                Console.WriteLine("terminated: requested");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"terminated: call failed — {ex.GetType().Name}: {ex.Message}");
                return 4;
            }
        }

        case "answer":
        {
            // The answer is an event on the instance, named for the port. `times` delivers the same answer
            // more than once, which is the duplicate: nothing here deduplicates, so what the framework does
            // with the second one is the observation.
            if (answerPort is null)
            {
                Console.Error.WriteLine("extension-app: answer needs --port <id>");
                return 2;
            }

            for (var n = 1; n <= times; n++)
            {
                // The port's answer crosses the orchestration boundary as JSON. The framework records the
                // one its own client sent and this harness replays that exact shape, so the value below is
                // read from `fixtures/port-answer.json` rather than guessed: a wrong encoding does not
                // fail loudly here, it fails inside the orchestration.
                var answer = Environment.GetEnvironmentVariable("BAND_G_PORT_ANSWER")
                    ?? System.Text.Json.JsonSerializer.Serialize(new ApprovalResponse(true, "answered by the harness"));
                await scheduler.RaiseEventAsync(target, answerPort, answer, CancellationToken.None);
                Console.WriteLine($"raised:     {answerPort} ({n} of {times})");
            }

            if (waitAfter)
            {
                // The answer is a message, and this process is the worker that carries it to the run. It
                // serves until the instance stops or until it pauses at another port, then says so and
                // exits — a process that sat on `WaitForInstanceCompletionAsync` would hang for every run
                // that pauses again, which is most of them.
                Console.WriteLine("serving:    a worker is running to deliver the answer");
                Console.Out.Flush();
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
                {
                    var metadata = await scheduler.GetInstanceAsync(target, CancellationToken.None);
                    var status = metadata?.RuntimeStatus.ToString();
                    if (status is "Completed" or "Failed" or "Terminated" or null)
                    {
                        Console.WriteLine($"status:     {status ?? "(absent)"}");
                        if (metadata?.SerializedOutput is { } output)
                        {
                            Console.WriteLine($"output:     {output}");
                        }

                        return 0;
                    }

                    var history = await scheduler.GetOrchestrationHistoryAsync(target);
                    if (history.Count(e => e.EventType.ToString() == "EventRaised") > 0)
                    {
                        // The answer was delivered: the run has moved past the port it named.
                        Console.WriteLine($"delivered:  {answerPort}");
                        return 0;
                    }

                    await Task.Delay(500);
                }

                Console.WriteLine("status:     (still waiting)");
                return 3;
            }

            return 0;
        }

        case "inspect":
        {
            var metadata = await scheduler.GetInstanceAsync(target, CancellationToken.None);
            if (metadata is null)
            {
                Console.WriteLine("status:     (absent)");
                return 3;
            }

            Console.WriteLine($"status:     {metadata.RuntimeStatus}");
            var history = await scheduler.GetOrchestrationHistoryAsync(target);
            var raised = new List<string>();
            var events = new List<string>();
            foreach (var entry in history)
            {
                events.Add(entry.EventType.ToString());
                if (entry.EventType.ToString() != "EventRaised")
                {
                    continue;
                }

                var json = System.Text.Json.JsonSerializer.Serialize(entry, entry.GetType());
                using var document = System.Text.Json.JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("Name", out var name))
                {
                    raised.Add(name.GetString() ?? "?");
                }
            }

            Console.WriteLine($"raised:     {(raised.Count == 0 ? "(none)" : string.Join(", ", raised))}");
            Console.WriteLine($"answered:   {history.Count(e => e.EventType.ToString() == "TaskCompleted")} task completion(s)");
            Console.WriteLine($"history:    {string.Join(" > ", events)}");
            return 0;
        }

        default:
            Console.Error.WriteLine($"extension-app: unknown verb '{verb}'");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"extension-app: {ex.Message}");
    return 1;
}
finally
{
    // The host is stopped on the way out of a *normal* verb. A process a fault script kills never reaches
    // here, which is the point: the run has to survive without this process tidying up after it.
    await host.StopAsync();
}

