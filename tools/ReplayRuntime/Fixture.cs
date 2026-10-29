using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReplayRuntime;

/// <summary>
/// A recorded transcript of one Copilot runtime conversation. The runtime answers
/// every client request from this document and never invents a response.
/// </summary>
public sealed class Fixture
{
    /// <summary>Protocol version reported on the <c>connect</c> handshake.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 3;

    /// <summary>Package version reported on the <c>connect</c> handshake.</summary>
    [JsonPropertyName("runtimeVersion")]
    public string RuntimeVersion { get; set; } = "0.0.0-replay";

    /// <summary>Models returned by <c>models.list</c>.</summary>
    [JsonPropertyName("models")]
    public List<FixtureModel> Models { get; set; } = [];

    /// <summary>Sessions the runtime will accept on <c>session.create</c>.</summary>
    [JsonPropertyName("sessions")]
    public List<FixtureSession> Sessions { get; set; } = [];

    /// <summary>
    /// Recorded responses keyed by JSON-RPC method name. Used for methods that are
    /// not modelled explicitly above.
    /// </summary>
    [JsonPropertyName("responses")]
    public Dictionary<string, JsonElement> Responses { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Fixture Parse(string json)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(json, Options)
            ?? throw new FixtureException("fixture document is empty");
        fixture.Validate();
        return fixture;
    }

    public static Fixture Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FixtureException($"fixture file not found: {path}");
        }

        return Parse(File.ReadAllText(path));
    }

    public void Validate()
    {
        if (ProtocolVersion <= 0)
        {
            throw new FixtureException("protocolVersion must be a positive integer");
        }

        var duplicateSessions = Sessions
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateSessions.Count > 0)
        {
            throw new FixtureException($"duplicate session id(s): {string.Join(", ", duplicateSessions)}");
        }

        foreach (var session in Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id))
            {
                throw new FixtureException("every session requires a non-empty id");
            }

            foreach (var turn in session.Turns)
            {
                if (string.IsNullOrWhiteSpace(turn.MessageId))
                {
                    throw new FixtureException($"session '{session.Id}' has a turn without a messageId");
                }
            }
        }
    }

    public FixtureSession? FindSession(string? id) =>
        id is null ? null : Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

public sealed class FixtureModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "replay";

    [JsonPropertyName("capabilities")]
    public Dictionary<string, JsonElement>? Capabilities { get; set; }
}

public sealed class FixtureSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Recorded turns, consumed in order by <c>session.sendMessages</c>.</summary>
    [JsonPropertyName("turns")]
    public List<FixtureTurn> Turns { get; set; } = [];
}

public sealed class FixtureTurn
{
    /// <summary>Identifier echoed back in <c>SendMessagesResult.messageIds</c>.</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Assistant text emitted as an <c>assistant.message</c> notification.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    /// <summary>Optional tool requests emitted alongside the assistant message.</summary>
    [JsonPropertyName("toolRequests")]
    public List<JsonElement>? ToolRequests { get; set; }
}

public sealed class FixtureException(string message) : Exception(message);
