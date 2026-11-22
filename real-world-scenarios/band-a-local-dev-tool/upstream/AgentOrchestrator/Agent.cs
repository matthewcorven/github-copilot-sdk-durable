using GitHub.Copilot;

namespace AgentOrchestrator;

public class Agent(string id, string enquiry, PropertyDatabase database, Action updateUi) : IAsyncDisposable
{
    public string Id => id;
    public string Enquiry => enquiry;

    public string? CurrentIntent { get; private set; }
    public Phase Phase { get; private set; } = Phase.Queued;
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public List<SessionEvent> SessionEvents { get; } = new();
    private CopilotSession Session { get; set; } = default!;

    public async Task RunAsync(CopilotClient client)
    {
        var systemMessageConfig = new SystemMessageConfig { Mode = SystemMessageMode.Customize };
        systemMessageConfig.Sections = new Dictionary<SystemMessageSection, SectionOverride>();
        systemMessageConfig.Sections[SystemMessageSection.Identity] = new()
        {
            Action = SectionOverrideAction.Replace,
            Content = """
            You are part of a real estate recommendation system. You will receive enquiries from customers,
            and you must carry out the following workflow. As you proceed, you will update your current phase
            and intent, which will be visible to the user. Do not stop until the phase reaches a final state.
            Start by setting phase to "validating".
        
            - Validation phase
              - Check the enquiry is genuine and not spam, garbage, or off-topic.
              - If it's not genuine, set phase to "rejected_garbage" and stop.
            - Search phase
              - Extract relevant search criteria and search our property listings.
              - To search our property listings, call the search tool.
                You may call it multiple times with different filters to refine results.
              - If the customer is looking for a neighbourhood with a particular feature (such as schools)
                always perform at least one web search to confirm locations.
              - At the end of this phase, if you don't find any relevant properties, set phase to
                "rejected_no_matches" and stop. We are very busy and do not want to talk to any customers
                that don't match our offerings. Don't write reports for customers that won't convert.
            - Report phase
              - Write up a report for our salesperson to use when calling the customer
              - Your report should include a summary of the customer's needs and the top 1-3 matching
                properties. For each property, include key selling points for this customer.
              - At the end of this phase, set phase to "done" and stop.
        
            As you go, always use set_current_phase each time you enter a new phase, and report your
            intent at each step.
            """,
        };

        Session = await client.CreateSessionAsync(new()
        {
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = systemMessageConfig,
            AvailableTools = new ToolSet().AddCustom("*").AddBuiltIn("web_fetch"),
            Tools = [
                CopilotTool.DefineTool(SetCurrentPhase),
                CopilotTool.DefineTool(ReportIntent, new() { OverridesBuiltInTool = true }),
                CopilotTool.DefineTool(database.SearchProperties),
            ],
        });

        Session.On<SessionEvent>(evt =>
        {
            SessionEvents.Add(evt);
            updateUi();
        });
        
        await Session.SendAndWaitAsync($"<enquiry>{Enquiry}</enquiry>");
    }

    [DisplayName("set_current_phase")]
    [Description("Sets the current phase of the agent. Use this to report progress.")]
    private void SetCurrentPhase(Phase phase)
    {
        Phase = phase;
        updateUi();
    }
    
    [DisplayName("report_intent")]
    [Description("Reports the current intent of the agent")]
    private void ReportIntent([Description("Intent in max 4 words")] string intent)
    {
        CurrentIntent = intent;
        updateUi();
    }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
