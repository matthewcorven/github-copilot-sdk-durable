using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace Durable.Runtime;

/// <summary>
/// The durable agent host: one process, one store, one agent occurrence per invocation.
/// </summary>
/// <remarks>
/// The host is deliberately thin. It owns the store, validates the agent definition, decides
/// how much of the agent's authority it can enforce, runs one turn, and prints the run result.
/// Everything durable — conversations, admissions, checkpoints, documents — is the library's,
/// so a second transport can serialize the same <see cref="RunResult"/> without re-deriving any
/// of it.
/// <para>
/// Standard output carries the run result and nothing else, so a caller can pipe it into a JSON
/// reader. Diagnostics, including the human-readable effective-model line, go to standard error.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>The document kind a conversation's checkpoints are stored under.</summary>
    private const string CheckpointDocumentKind = "durable.checkpoint";

    /// <summary>
    /// The definition the host writes and reads. It is rebuilt here rather than reached for
    /// through the runtime, because the runtime's copy is internal to a conversation and a
    /// definition outlives every conversation it creates.
    /// </summary>
    private static readonly DocumentDefinition<AgentDefinition> AgentDefinitions =
        new(AgentDefinition.DocumentKind, DocumentHistory.Rewindable, ForkPolicy.Initial);

    /// <summary>How long the host waits for the runtime to report a model.</summary>
    private static readonly TimeSpan ModelReportTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a shutdown waits for an in-flight turn to reach its checkpoint before the process
    /// leaves anyway.
    /// </summary>
    /// <remarks>
    /// The wait is bounded because a runtime that never settles would otherwise make the host
    /// unkillable. It is generous because the alternative — leaving early — abandons a turn the
    /// durable layer exists to finish.
    /// </remarks>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(60);

    private static readonly OptionSpec[] HostSpecs =
    [
        new("agent", true, "The agent definition to run. Required."),
        new("input", true, "The prompt for this occurrence. Required by run."),
        new("definition", true, "Import an agent definition from a JSON file before running."),
        new("store", true, "The store file. Defaults to the per-user durable store."),
        new("format", true, "The store format: auto, jsonl, legacy-json, or in-memory. Defaults to auto."),
        new("new-conversation", false, "Start a new conversation instead of continuing the agent's."),
        new("permission-handler", true, "approve-all, grants, or deny-all. Defaults to grants."),
        new("require-effective-model", false, "Fail when the runtime reports no model."),
        new("allow-uncontained", false, "Allow serve and schedule to start with containment None."),
        new("http", false, "Serve over HTTP. Required by serve."),
        new("port", true, "The port to listen on. Defaults to 0, which asks the operating system."),
        new("runtime-arg", true, "Pass an argument to the runtime process. Repeatable."),
        new("every", true, "The schedule interval, e.g. 30s, 5m, or 1h. Required by schedule."),
        new("missed-run", true, "skip, coalesce, or run-once. Defaults to skip."),
        new("remove", false, "Remove the agent's schedule instead of registering one."),
        new("now", true, "Treat this instant as the current time. For tests and for catch-up."),
        new("passes", true, "Run at most this many scheduler passes, then exit. Defaults to one."),
    ];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help" || args.Contains("--help"))
        {
            Console.Out.Write(HelpText);
            return ExitCodes.Succeeded;
        }

        try
        {
            return args[0] switch
            {
                "run" => await RunAsync(args),
                "serve" => await ServeAsync(args),
                "schedule" => await ScheduleAsync(args),
                _ => throw new UsageException(
                    $"unknown command '{args[0]}'. Commands are run, serve, and schedule."),
            };
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.Write(HelpText);
            return ExitCodes.UsageError;
        }
        catch (AgentDefinitionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.AgentFailed;
        }
        catch (DurableSandboxPolicyException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.AgentFailed;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"error: the agent definition is not valid JSON: {ex.Message}");
            return ExitCodes.AgentFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or IOException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.AgentFailed;
        }
    }

    /// <summary>Runs one durable turn for an agent and prints the run result.</summary>
    private static async Task<int> RunAsync(string[] args)
    {
        var options = ParseHostOptions(args, requireInput: true);

        using var store = OpenStore(options);
        ClaimOwnership(store, options.StorePath);

        var registry = BuildRegistry();
        var definition = await LoadDefinitionAsync(store, registry, options);

        var (containment, reason) = ContainmentClassifier.Classify(definition, options.PermissionHandler is not null);
        ReportContainment(definition.Name, containment, reason);

        await using var harness = await DurableHarness.OpenAsync(store, registry, HarnessOptions(options));

        var result = await RunTurnAsync(
            harness, store, definition, options, containment, reason, options.Input!);

        Console.Out.WriteLine(result.ToJson());

        return result.Outcome switch
        {
            "succeeded" => ExitCodes.Succeeded,
            "turnUnsuccessful" => ExitCodes.TurnUnsuccessful,
            _ => ExitCodes.AgentFailed,
        };
    }

    /// <summary>
    /// Registers, removes, or drives a durable schedule for an agent.
    /// </summary>
    /// <remarks>
    /// The command has three shapes, and which one runs is decided by the options rather than by
    /// a separate verb. <c>--every</c> registers a schedule and then drives it; <c>--remove</c>
    /// disables the agent's schedule and stops; neither means "drive whatever the store already
    /// holds", which is what a host that restarted does. The schedule itself is a document in the
    /// store, so the second and third shapes see the first shape's work across a process boundary.
    /// </remarks>
    private static async Task<int> ScheduleAsync(string[] args)
    {
        var options = ParseHostOptions(args, requireInput: false);

        if (options.Remove && options.Every is not null)
        {
            throw new UsageException("--remove and --every cannot be combined: a removal has no interval.");
        }

        using var store = OpenStore(options);
        ClaimOwnership(store, options.StorePath);

        var registry = BuildRegistry();
        var definition = await LoadDefinitionAsync(store, registry, options);

        var (containment, reason) = ContainmentClassifier.Classify(definition, options.PermissionHandler is not null);
        ReportContainment(definition.Name, containment, reason);

        if (containment == Containment.None && !options.AllowUncontained)
        {
            Console.Error.WriteLine(
                $"error: refusing to schedule agent '{definition.Name}': containment is None ({reason}). "
                + "Pass --allow-uncontained to start anyway.");
            return ExitCodes.AgentFailed;
        }

        var clock = ClockFor(options);

        if (options.Remove)
        {
            var removed = await RemoveScheduleAsync(store, definition.Name);
            Console.Out.WriteLine(new ScheduleResult
            {
                Agent = definition.Name,
                Schedule = removed is null ? null : ScheduleStateResult.From(removed),
                Occurrences = [],
            }.ToJson());
            return ExitCodes.Succeeded;
        }

        if (options.Every is { } interval)
        {
            await RegisterScheduleAsync(store, definition.Name, interval, options, clock());
        }

        var occurrences = await DriveAsync(store, registry, definition, options, containment, reason, clock);

        var current = ScheduleStore.Find(store, definition.Name);
        Console.Out.WriteLine(new ScheduleResult
        {
            Agent = definition.Name,
            Schedule = current is null ? null : ScheduleStateResult.From(current),
            Occurrences = occurrences,
        }.ToJson());

        return ExitCodeFor(occurrences);
    }

    /// <summary>
    /// Drives the persisted schedules for <c>--passes</c> passes, or until the process is
    /// interrupted when no bound was given.
    /// </summary>
    /// <remarks>
    /// The harness is opened once and reused across occurrences, because every occurrence is a
    /// turn in the same agent conversation. A pass commits the advanced schedule before it runs
    /// the turn, so an occurrence is claimed durably before it costs anything.
    /// </remarks>
    private static async Task<IReadOnlyList<RunResult>> DriveAsync(
        DurableStore store,
        ExtensionRegistry registry,
        AgentDefinition definition,
        HostOptions options,
        Containment containment,
        string reason,
        Func<DateTimeOffset> clock)
    {
        if (options.Passes == 0)
        {
            return [];
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            await using var harness = await DurableHarness.OpenAsync(store, registry, HarnessOptions(options));

            var scheduler = new Scheduler(
                store,
                clock,
                (schedule, _) => RunTurnAsync(
                    harness,
                    store,
                    definition,
                    options,
                    containment,
                    reason,
                    schedule.Input ?? options.Input ?? DefaultScheduleInput(definition.Name)));

            var occurrences = new List<RunResult>();
            for (var pass = 0; options.Passes is not { } limit || pass < limit; pass++)
            {
                try
                {
                    occurrences.AddRange(await scheduler.PassAsync(cancellation.Token));

                    if (options.Passes is null)
                    {
                        await scheduler.DelayUntilNextDueAsync(cancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // A stopped scheduler is a stopped scheduler, not a failed one.
                    break;
                }
            }

            return occurrences;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Writes the schedule for an agent, keeping the existing cadence when nothing about the
    /// schedule changed.
    /// </summary>
    /// <remarks>
    /// Keeping the cadence is what makes a restart harmless: a host that comes back with the same
    /// command must not push the next occurrence out by a whole interval, and must not forget the
    /// instants it slept through. Only a changed interval or policy starts a fresh cadence.
    /// </remarks>
    private static Task RegisterScheduleAsync(
        DurableStore store,
        string agent,
        TimeSpan interval,
        HostOptions options,
        DateTimeOffset now)
    {
        var existing = ScheduleStore.Find(store, agent);

        var schedule = existing is { Enabled: true }
            && existing.Interval == interval
            && existing.MissedRun == options.MissedRun
                ? existing with { Input = options.Input ?? existing.Input }
                : new DurableSchedule
                {
                    Agent = agent,
                    Every = FormatInterval(interval),
                    EveryMilliseconds = (long)interval.TotalMilliseconds,
                    MissedRun = options.MissedRun,
                    NextDue = now + interval,
                    Input = options.Input,
                };

        return ScheduleStore.SaveAsync(store, schedule);
    }

    /// <summary>
    /// Disables an agent's schedule, or returns <see langword="null"/> when the store holds none.
    /// </summary>
    /// <remarks>
    /// The store is append-only, so a removal commits a disabled version rather than deleting a
    /// record. A scheduler treats a disabled schedule exactly as it treats an absent one, which is
    /// why the runs stop.
    /// </remarks>
    private static async Task<DurableSchedule?> RemoveScheduleAsync(DurableStore store, string agent)
    {
        if (ScheduleStore.Find(store, agent) is not { } existing)
        {
            return null;
        }

        var removed = existing with { Enabled = false };
        await ScheduleStore.SaveAsync(store, removed);
        return removed;
    }

    /// <summary>Runs one durable turn and returns the run result without printing it.</summary>
    private static async Task<RunResult> RunTurnAsync(
        DurableHarness harness,
        DurableStore store,
        AgentDefinition definition,
        HostOptions options,
        Containment containment,
        string reason,
        string input)
    {
        var conversation = await OpenConversationAsync(harness, store, definition, options);

        var result = await ExecuteTurnAsync(
            store, conversation, definition, options, containment, reason, options.Input!);

        if (result.Answer is not null)
        {
            Console.Error.WriteLine($"answer: {result.Answer}");
        }

        Console.Error.WriteLine($"effective model: {result.Report}");
        return result.Result;
    }

    /// <summary>
    /// Runs one turn and describes it as a <see cref="RunResult"/>.
    /// </summary>
    /// <remarks>
    /// This is the whole of a turn, shared by <c>run</c> and <c>serve</c> so the two transports
    /// cannot drift: the same admission, the same checkpoint, the same effective-model wait, and
    /// the same outcome vocabulary. A transport decides only how the result is delivered.
    /// <para>
    /// The turn is never cancelled. A cancelled <see cref="DurableConversation.SubmitAsync"/> skips
    /// both the turn checkpoint and the admission's settle, which would leave the store describing
    /// a turn that neither finished nor failed. A shutdown therefore waits for this method rather
    /// than interrupting it.
    /// </para>
    /// </remarks>
    private static async Task<TurnExecution> ExecuteTurnAsync(
        DurableStore store,
        DurableConversation conversation,
        AgentDefinition definition,
        HostOptions options,
        Containment containment,
        string containmentReason,
        string input)
    {
        AssistantMessageEvent? message = null;
        string? error = null;
        try
        {
            message = await conversation.SubmitAsync(input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        var admissionId = conversation.Admission()?.AdmissionId;
        var checkpointId = store.Document(CheckpointDocumentKind, conversation.Id)?.Versions is { Count: > 0 } versions
            ? versions[^1].EntryId
            : null;

        var report = await conversation.WaitForEffectiveModelAsync(ModelReportTimeout);
        if (options.RequireEffectiveModel)
        {
            try
            {
                _ = report.RequiredModel;
            }
            catch (InvalidOperationException ex)
            {
                error ??= ex.Message;
            }
        }

        if (!report.HasModel)
        {
            Console.Error.WriteLine(
                $"warning: the runtime reported no model for agent '{definition.Name}' "
                + $"in conversation '{conversation.Id}'.");
        }

        var outcome = error is not null
            ? "agentFailed"
            : message is null ? "turnUnsuccessful" : "succeeded";

        if (message is not null)
        {
            Console.Error.WriteLine($"answer: {message.Text()}");
        }

        Console.Error.WriteLine($"effective model: {report}");

        var result = new RunResult
        {
            Agent = definition.Name,
            ConversationId = conversation.Id,
            AdmissionId = admissionId,
            CheckpointId = checkpointId,
            EffectiveModel = report.HasModel
                ? new EffectiveModelResult { Model = report.Model!, Source = report.Source }
                : null,
            ModelReported = report.HasModel,
            Containment = containment.ToString(),
            ContainmentReason = containmentReason,
            Outcome = outcome,
            Error = error,
        };

        return new TurnExecution(
            result,
            message?.Text(),
            report,
            outcome switch
            {
                "succeeded" => ExitCodes.Succeeded,
                "turnUnsuccessful" => ExitCodes.TurnUnsuccessful,
                _ => ExitCodes.AgentFailed,
            });
    }

    /// <summary>
    /// Serves one agent over HTTP: a JSON request runs one durable turn, and the response streams
    /// the turn's events before its result.
    /// </summary>
    /// <remarks>
    /// The server is a process contract like <c>run</c>, so it reuses the same store ownership
    /// probe, the same definition loading, the same containment gate, and the same
    /// <see cref="ExecuteTurnAsync"/>. What it adds is a lifetime: it accepts requests until it is
    /// asked to stop, and a stop waits for the turn in flight rather than interrupting it.
    /// </remarks>
    private static async Task<int> ServeAsync(string[] args)
    {
        var options = ParseHostOptions(args, requireInput: false);
        if (!options.Http)
        {
            throw new UsageException("serve requires --http; this build serves HTTP only.");
        }

        using var store = OpenStore(options);
        ClaimOwnership(store, options.StorePath);

        var registry = BuildRegistry();
        var definition = await LoadDefinitionAsync(store, registry, options);

        var (containment, reason) = ContainmentClassifier.Classify(definition, options.PermissionHandler is not null);
        ReportContainment(definition.Name, containment, reason);

        if (containment == Containment.None && !options.AllowUncontained)
        {
            Console.Error.WriteLine(
                $"error: refusing to serve agent '{definition.Name}': containment is None ({reason}). "
                + "Pass --allow-uncontained to start anyway.");
            return ExitCodes.AgentFailed;
        }

        var harnessOptions = new DurableHarnessOptions
        {
            PermissionHandler = options.PermissionHandler,
            RuntimeArguments = options.RuntimeArguments.ToList(),
        };
        await using var harness = await DurableHarness.OpenAsync(store, registry, harnessOptions);

        await using var transport = HttpTransport.Start(options.Port);

        // One turn at a time. A conversation admits one turn, so a second concurrent request
        // would be refused by the durable layer; queueing it here is the honest answer, and it
        // keeps the store's admission history readable.
        using var turnGate = new SemaphoreSlim(1, 1);
        using var stopping = new CancellationTokenSource();

        using var sigterm = RegisterShutdown(PosixSignal.SIGTERM, stopping);
        using var sigint = RegisterShutdown(PosixSignal.SIGINT, stopping);

        Console.Error.WriteLine($"listening on http://127.0.0.1:{transport.Port}");

        await transport.RunAsync(
            (request, response) => HandleAsync(
                request, response, harness, store, definition, options, containment, reason, turnGate),
            stopping.Token,
            ShutdownGrace);

        Console.Error.WriteLine("stopped accepting connections.");
        return ExitCodes.Succeeded;
    }

    private static DurableHarnessOptions HarnessOptions(HostOptions options) => new()
    {
        PermissionHandler = options.PermissionHandler,
        RuntimeArguments = options.RuntimeArguments.ToList(),
    };

    /// <summary>
    /// The clock the scheduler reads. <c>--now</c> freezes it, which is how a test stands at a
    /// chosen instant without waiting for one.
    /// </summary>
    private static Func<DateTimeOffset> ClockFor(HostOptions options) =>
        options.Now is { } now ? () => now : () => DateTimeOffset.UtcNow;

    /// <summary>The prompt an occurrence sends when neither the schedule nor the command gave one.</summary>
    private static string DefaultScheduleInput(string agent) =>
        $"Run the scheduled task for agent '{agent}'.";

    /// <summary>The exit code for a set of occurrences: the worst outcome wins.</summary>
    private static int ExitCodeFor(IReadOnlyList<RunResult> occurrences)
    {
        if (occurrences.Any(result => result.Outcome == "agentFailed"))
        {
            return ExitCodes.AgentFailed;
        }

        return occurrences.Any(result => result.Outcome == "turnUnsuccessful")
            ? ExitCodes.TurnUnsuccessful
            : ExitCodes.Succeeded;
    }

    /// <summary>
    /// Turns a signal into a request to stop accepting connections. The signal is cancelled so the
    /// process is not torn down underneath a turn that is still reaching its checkpoint.
    /// </summary>
    private static PosixSignalRegistration RegisterShutdown(PosixSignal signal, CancellationTokenSource stopping) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Console.Error.WriteLine(
                $"received {context.Signal}: finishing the turn in flight, then exiting.");
            stopping.Cancel();
        });

    /// <summary>
    /// Serves one request: validate it, run one turn, and stream the turn's events ahead of its
    /// result.
    /// </summary>
    /// <remarks>
    /// A refusal is a response, never an exception: a malformed body or an agent this host does
    /// not serve must leave the server running, so every check answers with a 4xx that names the
    /// problem and returns.
    /// </remarks>
    private static async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        DurableHarness harness,
        DurableStore store,
        AgentDefinition definition,
        HostOptions options,
        Containment containment,
        string containmentReason,
        SemaphoreSlim turnGate)
    {
        if (request.Method != "POST")
        {
            await response.WriteErrorAsync(
                405, "Method Not Allowed", $"this host accepts POST, not {request.Method}.");
            return;
        }

        if (request.Path is not ("/" or "/turns"))
        {
            await response.WriteErrorAsync(
                404, "Not Found", $"'{request.Path}' is not a route this host serves. POST / or POST /turns.");
            return;
        }

        string input;
        bool newConversation;
        try
        {
            var body = JsonNode.Parse(request.Body) as JsonObject
                ?? throw new JsonException("the body must be a JSON object.");

            if (body["agent"] is { } agentNode)
            {
                var requested = ReadString(agentNode, "agent");
                if (!string.Equals(requested, definition.Name, StringComparison.Ordinal))
                {
                    await response.WriteErrorAsync(
                        400, "Bad Request",
                        $"agent '{requested}' is not served by this host; this host serves '{definition.Name}'.");
                    return;
                }
            }

            input = body["input"] is { } inputNode
                ? ReadString(inputNode, "input")
                : throw new JsonException("the body has no 'input' property.");

            if (input.Length == 0)
            {
                throw new JsonException("'input' is empty.");
            }

            newConversation = body["newConversation"] is { } flag && flag.GetValue<bool>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            await response.WriteErrorAsync(
                400, "Bad Request", $"the request body is not a valid turn request: {ex.Message}");
            return;
        }

        await turnGate.WaitAsync();
        try
        {
            var conversation = await OpenConversationAsync(
                harness, store, definition, options with { NewConversation = newConversation });

            await response.StartAsync(200, "OK", "application/x-ndjson");

            // Events are queued rather than written from the subscription callback: the callback
            // runs on the runtime's event thread, and a slow client must not stall it.
            var events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            var pump = Task.Run(async () =>
            {
                await foreach (var line in events.Reader.ReadAllAsync())
                {
                    await response.WriteLineAsync(line);
                }
            });

            TurnExecution execution;
            var subscription = conversation.Session.On<SessionEvent>(
                evt => events.Writer.TryWrite(EventLine(evt)));
            try
            {
                execution = await ExecuteTurnAsync(
                    store, conversation, definition, options, containment, containmentReason, input);
            }
            finally
            {
                subscription.Dispose();
            }

            // The result is written only after every event has been flushed, so a client that
            // reads to the end sees the turn in the order it happened.
            events.Writer.TryComplete();
            await pump;

            await response.WriteLineAsync(ResultLine(execution.Result));
            await response.CompleteAsync();
        }
        finally
        {
            turnGate.Release();
        }
    }

    private static string ReadString(JsonNode node, string name) =>
        node.GetValueKind() == JsonValueKind.String
            ? node.GetValue<string>()
            : throw new JsonException($"'{name}' must be a string.");

    /// <summary>One streamed event, as a single newline-delimited JSON line.</summary>
    private static string EventLine(SessionEvent evt) =>
        new JsonObject
        {
            ["type"] = "event",
            ["event"] = JsonNode.Parse(evt.ToJson()),
        }.ToJsonString();

    /// <summary>
    /// The turn's result, as the last newline-delimited JSON line.
    /// </summary>
    /// <remarks>
    /// The result is serialized compactly. <see cref="RunResult.Json"/> is indented for a human
    /// reading standard output, and an indented object cannot be one line of a stream.
    /// </remarks>
    private static string ResultLine(RunResult result) =>
        new JsonObject
        {
            ["type"] = "result",
            ["result"] = JsonNode.Parse(JsonSerializer.Serialize(result, CompactResultJson)),
        }.ToJsonString();

    private static readonly JsonSerializerOptions CompactResultJson =
        new(RunResult.Json) { WriteIndented = false };

    /// <summary>
    /// Parses and validates a command whose implementation is delivered by another issue, then
    /// reports that it is not in this build. The containment gate runs here too, so a host that
    /// would serve an uncontained agent is refused before the transport exists rather than after.
    /// </summary>
    private static async Task<int> NotYetAsync(string[] args, string command, string issue)
    {
        var options = ParseHostOptions(args, requireInput: false);

        using var store = OpenStore(options);
        ClaimOwnership(store, options.StorePath);

        var registry = BuildRegistry();
        var definition = await LoadDefinitionAsync(store, registry, options);

        var (containment, reason) = ContainmentClassifier.Classify(definition, options.PermissionHandler is not null);
        ReportContainment(definition.Name, containment, reason);

        if (containment == Containment.None && !options.AllowUncontained)
        {
            Console.Error.WriteLine(
                $"error: refusing to {command} agent '{definition.Name}': containment is None ({reason}). "
                + "Pass --allow-uncontained to start anyway.");
            return ExitCodes.AgentFailed;
        }

        Console.Error.WriteLine($"{command} is not implemented in this build; it is delivered by {issue}.");
        return ExitCodes.NotAvailable;
    }

    private static HostOptions ParseHostOptions(string[] args, bool requireInput)
    {
        var parsed = CommandLine.Parse(args, 1, HostSpecs, out var positionals);
        if (positionals.Count > 0)
        {
            throw new UsageException($"unexpected argument '{positionals[0]}'");
        }

        var format = ParseFormat(parsed.Last("format"));
        var storePath = parsed.Last("store");
        if (format == DurableStoreFormat.InMemory)
        {
            storePath = null;
        }
        else
        {
            storePath ??= DurableStore.DefaultStorePath();
        }

        return new HostOptions(
            Agent: parsed.Required("agent"),
            Input: requireInput ? parsed.Required("input") : parsed.Last("input"),
            DefinitionPath: parsed.Last("definition"),
            StorePath: storePath,
            Format: format,
            NewConversation: parsed.Has("new-conversation"),
            PermissionHandler: ParsePermissionHandler(parsed.Last("permission-handler")),
            RequireEffectiveModel: parsed.Has("require-effective-model"),
            AllowUncontained: parsed.Has("allow-uncontained"),
            Http: parsed.Has("http"),
            Port: ParsePort(parsed.Last("port")),
            RuntimeArguments: parsed.All("runtime-arg").ToList(),
            Every: parsed.Last("every") is { } every ? ParseInterval(every) : null,
            MissedRun: MissedRunPolicies.Parse(parsed.Last("missed-run")),
            Remove: parsed.Has("remove"),
            Now: ParseNow(parsed.Last("now")),
            Passes: ParsePasses(parsed.Last("passes")));
    }

    /// <summary>
    /// Parses an interval such as <c>30s</c>, <c>5m</c>, or <c>1h</c>.
    /// </summary>
    /// <remarks>
    /// A bare number is seconds, because that is the unit a person writes when they are in a
    /// hurry and the unit the smallest useful schedule is expressed in. The suffix set stops at
    /// hours on purpose: a daily or weekly cadence is calendar-aware scheduling, which this
    /// command does not do.
    /// </remarks>
    private static TimeSpan ParseInterval(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new UsageException("option '--every' needs an interval, e.g. 30s, 5m, or 1h.");
        }

        var (digits, unit) = char.IsLetter(trimmed[^1])
            ? (trimmed[..^1], char.ToLowerInvariant(trimmed[^1]))
            : (trimmed, 's');

        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new UsageException(
                $"'{text}' is not an interval. Write a positive number with an optional unit: "
                + "30s, 5m, or 1h. A bare number is seconds.");
        }

        var milliseconds = unit switch
        {
            's' => value * 1000L,
            'm' => value * 60_000L,
            'h' => value * 3_600_000L,
            _ => throw new UsageException(
                $"'{text}' uses an unknown unit '{unit}'. The units are s, m, and h."),
        };

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Renders an interval the way <see cref="ParseInterval"/> reads it.</summary>
    private static string FormatInterval(TimeSpan interval) =>
        interval.TotalMilliseconds % 3_600_000 == 0 ? $"{interval.TotalHours:0}h"
        : interval.TotalMilliseconds % 60_000 == 0 ? $"{interval.TotalMinutes:0}m"
        : interval.TotalSeconds % 1 == 0 ? $"{interval.TotalSeconds:0}s"
        : $"{interval.TotalMilliseconds:0}ms";

    /// <summary>
    /// The instant to treat as now, or <see langword="null"/> to read the wall clock.
    /// </summary>
    /// <remarks>
    /// A schedule is a wall-clock thing, so the production path reads the clock. A test cannot
    /// wait five minutes, so it names the instant instead and the scheduler believes it.
    /// </remarks>
    private static DateTimeOffset? ParseNow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var now))
        {
            throw new UsageException(
                $"'{text}' is not an instant. Write an ISO 8601 timestamp, e.g. 2026-01-01T00:00:00Z.");
        }

        return now;
    }

    /// <summary>How many scheduler passes to run, or <see langword="null"/> to run until stopped.</summary>
    private static int? ParsePasses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 1;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var passes) || passes < 0)
        {
            throw new UsageException($"'{text}' is not a pass count. Write a whole number, e.g. 1 or 3.");
        }

        return passes;
    }

    /// <summary>
    /// Reads the port to bind. Zero is the default and means "ask the operating system", which is
    /// what makes two hosts runnable at once and a test runnable twice.
    /// </summary>
    private static int ParsePort(string? text)
    {
        if (text is null or "")
        {
            return 0;
        }

        if (!int.TryParse(text, out var port) || port is < 0 or > 65535)
        {
            throw new UsageException($"--port must be a number between 0 and 65535, got '{text}'.");
        }

        return port;
    }

    private static DurableStoreFormat ParseFormat(string? text) => text switch
    {
        null or "" or "auto" => DurableStoreFormat.Auto,
        "jsonl" => DurableStoreFormat.Jsonl,
        "legacy-json" => DurableStoreFormat.LegacyJson,
        "in-memory" => DurableStoreFormat.InMemory,
        _ => throw new UsageException(
            $"unknown store format '{text}'. Formats are auto, jsonl, legacy-json, and in-memory."),
    };

    private static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? ParsePermissionHandler(
        string? text) => text switch
    {
        // The durable fail-closed policy is the default: the host supplies no handler, so every
        // request is decided against the agent's stored grants.
        null or "" or "grants" => null,
        "approve-all" => PermissionHandler.ApproveAll,
        "deny-all" => DenyAll,
        _ => throw new UsageException(
            $"unknown permission handler '{text}'. Handlers are approve-all, grants, and deny-all."),
    };

    private static Task<PermissionDecision> DenyAll(PermissionRequest request, PermissionInvocation invocation) =>
        Task.FromResult<PermissionDecision>(new PermissionDecisionReject
        {
            Feedback = "denied by the host: --permission-handler deny-all rejects every request.",
        });

    private static DurableStore OpenStore(HostOptions options) =>
        DurableStore.Create(new DurableStoreOptions { Path = options.StorePath, Format = options.Format });

    /// <summary>
    /// Validates that this host owns the store, before anything is started.
    /// </summary>
    /// <remarks>
    /// The ordering matters. <see cref="DurableHarness.OpenAsync"/> starts the runtime process
    /// before it constructs the harness that claims the store's task-runner slot, so a host that
    /// discovered the conflict afterwards would leave an orphaned runtime behind. The claim is
    /// probed and released here, and the real claim happens when the harness attaches.
    /// <para>
    /// The cross-process half of ownership is already settled by the time this runs: opening a
    /// persistent store takes the store file's exclusive lock. This probe covers the in-process
    /// half, where two stores over one file cannot both exist but a store can still be driven by
    /// a runtime this process already built.
    /// </para>
    /// </remarks>
    private static void ClaimOwnership(DurableStore store, string? storePath)
    {
        var probe = new object();
        try
        {
            store.ClaimTaskScheduler(probe, static (_, _) => { });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"the store at '{storePath ?? "(in memory)"}' is already driven by another durable runtime. "
                + ex.Message,
                ex);
        }
        finally
        {
            store.ReleaseTaskScheduler(probe);
        }
    }

    /// <summary>
    /// The extensions the host installs. They are the vocabulary an agent definition may name;
    /// a definition that names anything else is refused rather than silently narrowed.
    /// </summary>
    private static ExtensionRegistry BuildRegistry()
    {
        var registry = new ExtensionRegistry();
        registry.Install(new Extension("durable", sections:
        [
            new PromptSection("durable", _ => Task.FromResult<string?>(
                "Durable state is available through the conversation's documents and entries.")),
        ]));
        registry.Install(new Extension("git", sections:
        [
            new PromptSection("git", _ => Task.FromResult<string?>(
                "Use git for version control, and never rewrite history that has been shared.")),
        ]));
        registry.Install(new Extension("tests", sections:
        [
            new PromptSection("tests", _ => Task.FromResult<string?>(
                "Run the project's tests before reporting that a change works.")),
        ]));
        return registry;
    }

    /// <summary>
    /// Loads the agent definition, importing it first when the invocation names a file.
    /// </summary>
    /// <remarks>
    /// A file is an import surface only: the store is the source of truth, so an imported
    /// definition is validated and committed before it is used, and every later invocation reads
    /// it back from the store. Validation runs on both paths, which is what makes an extension
    /// that was uninstalled after import a refusal rather than a silent narrowing.
    /// </remarks>
    private static async Task<AgentDefinition> LoadDefinitionAsync(
        DurableStore store,
        ExtensionRegistry registry,
        HostOptions options)
    {
        if (options.DefinitionPath is { } path)
        {
            var imported = ReadDefinition(path);
            AgentDefinitionValidator.Validate(imported, registry);

            if (!string.Equals(imported.Name, options.Agent, StringComparison.Ordinal))
            {
                throw new UsageException(
                    $"the definition in '{path}' is for agent '{imported.Name}', not '{options.Agent}'.");
            }

            await SaveDefinitionAsync(store, imported);
            return imported;
        }

        var ownerId = AgentDefinition.OwnerId(options.Agent);
        var state = store.Document(AgentDefinition.DocumentKind, ownerId);
        if (state?.Latest is not { } latest)
        {
            throw new AgentDefinitionException(UnknownAgentMessage(store, options.Agent));
        }

        var stored = DurableJson.FromElement<AgentDefinition>(latest)
            ?? throw new AgentDefinitionException(
                $"the stored definition for agent '{options.Agent}' could not be read.");

        AgentDefinitionValidator.Validate(stored, registry);
        return stored;
    }

    private static AgentDefinition ReadDefinition(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"the agent definition file does not exist: '{path}'.", path);
        }

        var definition = JsonSerializer.Deserialize<AgentDefinition>(File.ReadAllText(path), DurableJson.Options)
            ?? throw new AgentDefinitionException($"the agent definition file '{path}' is empty.");

        return definition;
    }

    /// <summary>
    /// Commits an imported definition. The entry is anchored to the definition's own owner id
    /// rather than to a conversation, because the definition outlives every conversation it is
    /// used to create.
    /// </summary>
    private static Task SaveDefinitionAsync(DurableStore store, AgentDefinition definition)
    {
        var ownerId = AgentDefinition.OwnerId(definition.Name);
        return store.CommitAsync(async tx =>
        {
            var draft = tx.Doc(AgentDefinitions, ownerId);
            draft.Value = definition;
            draft.Save(tx.AppendEntry(ownerId, "agent-definition"));
            await Task.CompletedTask;
        });
    }

    private static string UnknownAgentMessage(DurableStore store, string name)
    {
        var available = store.Documents
            .Where(document => document.Kind == AgentDefinition.DocumentKind)
            .Select(document => document.OwnerId.StartsWith(AgentDefinition.OwnerPrefix, StringComparison.Ordinal)
                ? document.OwnerId[AgentDefinition.OwnerPrefix.Length..]
                : document.OwnerId)
            .Order(StringComparer.Ordinal)
            .ToList();

        var listing = available.Count == 0 ? "(none)" : string.Join(", ", available);
        return $"agent '{name}' is not defined in this store.\n"
            + $"Available agents: {listing}.\n"
            + "Import one with --definition <file>.";
    }

    /// <summary>
    /// Finds the agent's conversation, or creates it. One conversation per agent: each
    /// occurrence is an admission and a checkpoint inside it, and <c>--new-conversation</c> is
    /// the opt-out.
    /// </summary>
    private static async Task<DurableConversation> OpenConversationAsync(
        DurableHarness harness,
        DurableStore store,
        AgentDefinition definition,
        HostOptions options)
    {
        var ownerId = AgentDefinition.OwnerId(definition.Name);

        if (!options.NewConversation && FindAgentConversation(store, ownerId) is { } existing)
        {
            var resumed = await harness.ResumeConversationAsync(existing.Id);

            // The store is the source of truth for the definition, so a resumed conversation
            // follows the definition as it stands now rather than as it stood when the
            // conversation was created.
            await resumed.ConfigureAsync(definition.ToChoices());
            return resumed;
        }

        return await harness.CreateConversationAsync(
            definition.ToChoices(),
            ownerConversationId: ownerId);
    }

    private static ConversationRecord? FindAgentConversation(DurableStore store, string ownerId) =>
        store.Conversations
            .Where(record => record.OwnerConversationId == ownerId)
            .OrderByDescending(record => ConversationOrdinal(record.Id))
            .FirstOrDefault();

    private static long ConversationOrdinal(string id) =>
        id.Length > 1 && id[0] == 'c' && long.TryParse(id[1..], out var value) ? value : -1;

    private static void ReportContainment(string agent, Containment containment, string reason)
    {
        var line = $"containment: {containment} for agent '{agent}': {reason}";
        Console.Error.WriteLine(containment == Containment.None ? $"warning: {line}" : line);
    }

    private const string HelpText = """
        durable-runtime: the durable agent host

        Usage:
          Durable.Runtime run      --agent <name> --input <text> [options]
          Durable.Runtime serve    --agent <name> [options]
          Durable.Runtime schedule --agent <name> [options]
          Durable.Runtime --help

        Commands:
          run       Run one durable turn for an agent and print the run result as JSON.
          serve     Serve an agent over HTTP: one turn per request, events streamed ahead of the
                    result. Requires --http.
          schedule  Run an agent on a schedule. Delivered by #97.

        Options:
          --agent <name>              The agent definition to run. Required.
          --input <text>              The prompt for this occurrence. Required by run.
          --definition <file>         Import an agent definition from a JSON file before running.
          --store <path>              The store file. Defaults to the per-user durable store.
          --format <format>           auto, jsonl, legacy-json, or in-memory. Defaults to auto.
          --new-conversation          Start a new conversation instead of continuing the agent's.
          --permission-handler <h>    approve-all, grants, or deny-all. Defaults to grants, which
                                      leaves every request to the durable fail-closed policy.
          --require-effective-model   Fail when the runtime reports no model.
          --allow-uncontained         Allow serve and schedule to start with containment None.
          --http                      Serve over HTTP. Required by serve.
          --port <port>               The port to listen on. Defaults to 0, which asks the operating
                                      system for an ephemeral port.
          --runtime-arg <arg>         Pass an argument to the runtime process. Repeatable.
          --every <interval>          Register a schedule at this interval: 30s, 5m, or 1h.
                                      A bare number is seconds. Required to register.
          --missed-run <policy>       skip, coalesce, or run-once. Defaults to skip. Applies to
                                      the occurrences a stopped host slept through.
          --remove                    Disable the agent's schedule instead of registering one.
          --now <instant>             Treat this ISO 8601 instant as the current time.
          --passes <n>                Run at most this many scheduler passes, then exit.
                                      Defaults to one. Pass 0 to register without running.
          --help                      Show this help.

        Exit codes:
          0  the turn succeeded
          1  the agent could not run, or the turn failed
          2  the command line was not understood
          3  the turn was admitted and checkpointed but produced no assistant message
          4  the command is not implemented in this build

        Standard output carries the run result JSON, or the schedule result JSON for schedule.
        Diagnostics, including the effective model the runtime reported, go to standard error.
        """;

    private sealed record HostOptions(
        string Agent,
        string? Input,
        string? DefinitionPath,
        string? StorePath,
        DurableStoreFormat Format,
        bool NewConversation,
        Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>>? PermissionHandler,
        bool RequireEffectiveModel,
        bool AllowUncontained,
        bool Http,
        int Port,
        IReadOnlyList<string> RuntimeArguments,
        TimeSpan? Every,
        MissedRunPolicy MissedRun,
        bool Remove,
        DateTimeOffset? Now,
        int? Passes);

    /// <summary>
    /// One turn, as the transport that ran it needs to see it: the serialized result, the answer
    /// for a human reading stderr, the model report, and the exit code the turn maps to.
    /// </summary>
    private sealed record TurnExecution(
        RunResult Result,
        string? Answer,
        DurableModelReport Report,
        int ExitCode);
}
