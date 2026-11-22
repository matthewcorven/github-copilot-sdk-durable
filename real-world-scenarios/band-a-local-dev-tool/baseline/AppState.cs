using GitHub.Copilot;

namespace AgentOrchestrator;

public class AppState(PropertyDatabase propertyDatabase)
{
    private readonly CopilotClient copilotClient = new CopilotClient(new()
    {
        Mode = CopilotClientMode.Empty,
        BaseDirectory = Path.Combine(AppContext.BaseDirectory, ".copilot"),
    });

    public Dictionary<string, Agent> Agents { get; } = new();
    public event Action? UpdateUi;

    public async Task RunAgentAsync(string enquiry)
    {
        var agentId = Guid.NewGuid().ToString("N")[..8];
        var agent = new Agent(agentId, enquiry, propertyDatabase, NotifyChanged);
        Agents[agentId] = agent;
        NotifyChanged();
        Console.WriteLine($"Created agent {agentId}");

        try
        {
            await agent.RunAsync(copilotClient);
            agent.FinishedAt = DateTime.UtcNow;
            NotifyChanged();

            if (agent.Phase == Phase.Done)
            {
                // For demo, successful agents stay on screen forever
                return;
            }

            // Rejected agents linger for 15s then get removed
            await Task.Delay(15000);
            if (Agents.Remove(agentId, out _))
            {
                await agent.DisposeAsync();
            }
            NotifyChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running agent {agentId}: {ex}");
            if (Agents.Remove(agentId, out _))
            {
                await agent.DisposeAsync();
            }
            NotifyChanged();
        }
    }

    public void NotifyChanged()
        => UpdateUi?.Invoke();
}
