using Durable;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AgentOrchestrator.Durable;

/// <summary>
/// The tools the model uses to report progress, and the system prompt that tells it the pipeline.
/// </summary>
/// <remarks>
/// The tool surface is the baseline's: <c>set_current_phase</c>, <c>report_intent</c>, and
/// <c>search_properties</c>. What changed is where their writes go. In the baseline the phase tool
/// assigned a field on an in-process object; here it commits a guarded transition to the store, so
/// the phase the UI shows is the phase a restart resumes at.
/// </remarks>
public static class EnquiryTools
{
    /// <summary>
    /// The system prompt. It is the baseline's identity override, unchanged in intent: the model
    /// is told the pipeline, told to report each phase, and told not to stop before a terminal
    /// one.
    /// </summary>
    /// <remarks>
    /// The durable layer builds the system message with <see cref="SystemMessageMode.Append"/>
    /// rather than <c>Customize</c>, so the instructions arrive as an appended section instead of
    /// replacing the runtime's identity. The prompt is written to stand on its own for the same
    /// reason: nothing in the runtime's own identity is being relied on to fill a gap.
    /// </remarks>
    public const string SystemPrompt = """
        You are part of a real estate recommendation system. You receive enquiries from customers
        and carry out the following workflow. As you proceed, update your current phase and intent,
        which are visible to the user. Do not stop until the phase reaches a final state.

        Always call set_current_phase when you enter a phase, and call report_intent at each step.

        - Validation phase
          - Check the enquiry is genuine and not spam, garbage, or off-topic.
          - If it is not genuine, set phase to "rejected_garbage" and stop.
        - Search phase
          - Extract relevant search criteria and search the property listings with search_properties.
            You may call it multiple times with different filters to refine results.
          - If the customer is looking for a neighbourhood with a particular feature (such as
            schools), perform at least one web search to confirm locations.
          - At the end of this phase, if you find no relevant properties, set phase to
            "rejected_no_matches" and stop. Do not write reports for customers who will not convert.
        - Report phase
          - Write a report for our salesperson to use when calling the customer.
          - Include a summary of the customer's needs and the top one to three matching properties,
            with the key selling points for this customer.
          - At the end of this phase, set phase to "done" and answer with the report.
        """;

    /// <summary>Builds the extension that carries the enquiry task, the tools, and the prompt.</summary>
    /// <param name="store">The store the tools write through.</param>
    /// <param name="database">The property listings the search tool reads.</param>
    /// <param name="resolveConversation">
    /// Maps a tool invocation's session id to the conversation that owns it, so a tool writes the
    /// document belonging to the session that called it. Two concurrent enquiries therefore write
    /// two documents, and neither tool call can reach the other's.
    /// </param>
    /// <param name="task">The enquiry task, which is registered with the extension.</param>
    public static Extension Build(
        DurableStore store,
        PropertyDatabase database,
        Func<string, string?> resolveConversation,
        DurableTaskDefinition task) =>
        new(
            EnquiryDurable.ExtensionName,
            tools:
            [
                CopilotTool.DefineTool(
                    ([Description("The phase the agent has just entered: validating, searching, writing_report, rejected_garbage, rejected_no_matches, or done.")] string phase,
                     ToolInvocation invocation) =>
                        SetPhase(store, resolveConversation(invocation.SessionId), phase),
                    factoryOptions: new AIFunctionFactoryOptions
                    {
                        Name = "set_current_phase",
                        Description = "Sets the current phase of the agent. Use this to report progress.",
                    }),

                CopilotTool.DefineTool(
                    ([Description("Intent in at most four words.")] string intent,
                     ToolInvocation invocation) =>
                        ReportIntent(store, resolveConversation(invocation.SessionId), intent),
                    factoryOptions: new AIFunctionFactoryOptions
                    {
                        Name = "report_intent",
                        Description = "Reports the current intent of the agent.",
                    },
                    toolOptions: new CopilotToolOptions { OverridesBuiltInTool = true }),

                CopilotTool.DefineTool(
                    ([Description("Property type substring, e.g. Detached. Pass an empty string for no filter.")] string type,
                     [Description("Listing status substring, e.g. For Sale. Pass an empty string for no filter.")] string status,
                     [Description("City substring, e.g. Toronto. Pass an empty string for no filter.")] string city,
                     [Description("Province substring, e.g. Ontario. Pass an empty string for no filter.")] string province,
                     [Description("Minimum price, inclusive. Pass 0 for no filter.")] long minPrice,
                     [Description("Maximum price, inclusive. Pass 0 for no filter.")] long maxPrice,
                     [Description("Minimum bedrooms, inclusive. Pass 0 for no filter.")] int minBedrooms,
                     [Description("Maximum bedrooms, inclusive. Pass 0 for no filter.")] int maxBedrooms,
                     [Description("Minimum bathrooms, inclusive. Pass 0 for no filter.")] int minBathrooms,
                     [Description("Free-text substring matched against descriptions, features, street, and city. Pass an empty string for no filter.")] string textContains,
                     [Description("Maximum results, 1-100. Pass 25 for the default.")] int limit,
                     ToolInvocation invocation) =>
                        Search(store, database, resolveConversation(invocation.SessionId), new PropertyFilter
                        {
                            Type = type,
                            Status = status,
                            City = city,
                            Province = province,
                            MinPrice = minPrice,
                            MaxPrice = maxPrice,
                            MinBedrooms = minBedrooms,
                            MaxBedrooms = maxBedrooms,
                            MinBathrooms = minBathrooms,
                            TextContains = textContains,
                            Limit = limit,
                        }),
                    factoryOptions: new AIFunctionFactoryOptions
                    {
                        Name = "search_properties",
                        Description = "Searches the real estate listings database. All filters are optional and " +
                                      "inclusive; string filters are case-insensitive substring matches.",
                    }),
            ],
            sections: [new PromptSection("instructions", _ => Task.FromResult<string?>(SystemPrompt), tag: false)],
            tasks: [task]);

    /// <summary>
    /// Calls one of the enquiry's tools the way the runtime calls it, for the late-wakeup probe.
    /// </summary>
    /// <remarks>
    /// The phase writers are not the only writes an enquiry has. <c>search_properties</c> commits a
    /// tool entry and <c>report_intent</c> commits an intent entry, and neither touches the phase —
    /// so a probe that only raced phase writers would report a terminal document as safe while
    /// leaving two of its write paths unexercised. The evidence uses this to fire the real tool
    /// bodies at a rejected document.
    /// </remarks>
    /// <param name="store">The store the tools write through.</param>
    /// <param name="database">The listings the search tool reads.</param>
    /// <param name="conversationId">The document the call is attributed to.</param>
    /// <param name="toolName">The tool to call: <c>search_properties</c> or <c>report_intent</c>.</param>
    /// <param name="argument">The intent text, for <c>report_intent</c>; ignored otherwise.</param>
    /// <returns>The tool's answer, as the model would receive it.</returns>
    public static string CallForProbe(
        DurableStore store,
        PropertyDatabase database,
        string conversationId,
        string toolName,
        string argument = "a late wakeup") =>
        toolName switch
        {
            "search_properties" => Search(store, database, conversationId, new PropertyFilter
            {
                City = "Toronto",
                MinBedrooms = 3,
                Limit = 3,
            }, reportWrite: true),
            "report_intent" => ReportIntent(store, conversationId, argument, reportWrite: true),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName), toolName,
                "the probe knows only the two entry-writing tools"),
        };

    private static string SetPhase(DurableStore store, string? conversationId, string phase)
    {
        if (conversationId is null)
        {
            return "no conversation is bound to this session";
        }

        var name = EnquiryPhases.From(phase);
        var write = EnquiryWrites.MoveToAndMaybeExitAsync(
                store, conversationId, expected: null, EnquiryPhases.Name(name))
            .GetAwaiter().GetResult();

        // The guard's answer is returned to the model, not swallowed: a phase report that was
        // dropped because the enquiry already ended is something the model should see, so it
        // stops instead of continuing a workflow whose result cannot be recorded.
        return write.Skipped
            ? $"ignored: this enquiry is already {write.Phase}"
            : $"phase is now {write.Phase}";
    }

    private static string ReportIntent(
        DurableStore store,
        string? conversationId,
        string intent,
        bool reportWrite = false)
    {
        if (conversationId is null)
        {
            return "no conversation is bound to this session";
        }

        // The terminal check is inside the commit, so this call is safe to make unconditionally:
        // a report that loses the race with a rejection is refused by the same transaction that
        // would have written it, rather than by a read that a concurrent commit could invalidate.
        var written = EnquiryWrites.ReportIntentAsync(store, conversationId, intent)
            .GetAwaiter().GetResult();
        if (!written)
        {
            var state = EnquiryDurable.Read(store, conversationId);
            return reportWrite
                ? $"refused by the terminal guard: this enquiry is already {EnquiryPhases.Name(state.Phase)}"
                : $"ignored: this enquiry is already {EnquiryPhases.Name(state.Phase)}";
        }

        return reportWrite ? "the terminal guard would have refused it, but it was written" : $"intent is now {intent}";
    }

    /// <summary>The filters one search tool call supplies.</summary>
    private sealed class PropertyFilter
    {
        public string Type { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Province { get; init; } = string.Empty;
        public long MinPrice { get; init; }
        public long MaxPrice { get; init; }
        public int MinBedrooms { get; init; }
        public int MaxBedrooms { get; init; }
        public int MinBathrooms { get; init; }
        public string TextContains { get; init; } = string.Empty;
        public int Limit { get; init; } = 25;
    }

    private static string? OrNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Search(
        DurableStore store,
        PropertyDatabase database,
        string? conversationId,
        PropertyFilter filter,
        bool reportWrite = false)
    {
        // The tool's sentinel values are translated here, so the query only carries the filters
        // the model actually supplied. Passing an empty string through would be a filter that
        // matches everything, which is the same result but a much less honest one.
        var results = database.SearchProperties(
            type: OrNull(filter.Type),
            status: OrNull(filter.Status),
            city: OrNull(filter.City),
            province: OrNull(filter.Province),
            minPrice: filter.MinPrice == 0 ? null : filter.MinPrice,
            maxPrice: filter.MaxPrice == 0 ? null : filter.MaxPrice,
            minBedrooms: filter.MinBedrooms == 0 ? null : filter.MinBedrooms,
            maxBedrooms: filter.MaxBedrooms == 0 ? null : filter.MaxBedrooms,
            minBathrooms: filter.MinBathrooms == 0 ? null : filter.MinBathrooms,
            textContains: OrNull(filter.TextContains),
            limit: filter.Limit <= 0 ? 25 : filter.Limit);

        if (conversationId is not null)
        {
            // The tool call is part of the transcript, so a restart shows the same lines the
            // browser showed before it died.
            var matches = results.ToList();
            var summary = $"{matches.Count} match(es)";
            var written = EnquiryWrites
                .AppendAsync(store, conversationId, "tool", summary, toolName: "search_properties", success: true)
                .GetAwaiter().GetResult();

            if (reportWrite)
            {
                var state = EnquiryDurable.Read(store, conversationId);
                return written
                    ? $"the terminal guard would have refused it, but it was written into {EnquiryPhases.Name(state.Phase)}"
                    : $"refused by the terminal guard: this enquiry is already {EnquiryPhases.Name(state.Phase)}";
            }
        }

        return JsonSerializer.Serialize(results.ToList(), DurableJson.Options);
    }
}
