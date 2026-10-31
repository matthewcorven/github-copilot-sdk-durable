using System.Text.Json;
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
        new("runtime-arg", true, "Pass an argument to the runtime process. Repeatable."),
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
                "serve" => await NotYetAsync(args, "serve", "#95"),
                "schedule" => await NotYetAsync(args, "schedule", "#97"),
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
        var input = options.Input!;

        using var store = OpenStore(options);
        ClaimOwnership(store, options.StorePath);

        var registry = BuildRegistry();
        var definition = await LoadDefinitionAsync(store, registry, options);

        var (containment, reason) = ContainmentClassifier.Classify(definition, options.PermissionHandler is not null);
        ReportContainment(definition.Name, containment, reason);

        var harnessOptions = new DurableHarnessOptions
        {
            PermissionHandler = options.PermissionHandler,
            RuntimeArguments = options.RuntimeArguments.ToList(),
        };
        await using var harness = await DurableHarness.OpenAsync(store, registry, harnessOptions);

        var conversation = await OpenConversationAsync(harness, store, definition, options);

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
            ContainmentReason = reason,
            Outcome = outcome,
            Error = error,
        };

        if (message is not null)
        {
            Console.Error.WriteLine($"answer: {message.Text()}");
        }

        Console.Error.WriteLine($"effective model: {report}");
        Console.Out.WriteLine(result.ToJson());

        return outcome switch
        {
            "succeeded" => ExitCodes.Succeeded,
            "turnUnsuccessful" => ExitCodes.TurnUnsuccessful,
            _ => ExitCodes.AgentFailed,
        };
    }

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
            RuntimeArguments: parsed.All("runtime-arg").ToList());
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
          serve     Serve an agent over HTTP. Delivered by #95.
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
          --runtime-arg <arg>         Pass an argument to the runtime process. Repeatable.
          --help                      Show this help.

        Exit codes:
          0  the turn succeeded
          1  the agent could not run, or the turn failed
          2  the command line was not understood
          3  the turn was admitted and checkpointed but produced no assistant message
          4  the command is not implemented in this build

        Standard output carries the run result JSON. Diagnostics, including the effective model
        the runtime reported, go to standard error.
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
        IReadOnlyList<string> RuntimeArguments);
}
