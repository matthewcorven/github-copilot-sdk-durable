using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReplayRuntime;

/// <summary>
/// Serves the Copilot runtime JSON-RPC surface from a recorded fixture.
/// Every response is derived from the fixture alone: no wall clock, no randomness,
/// no network. An unrecorded request terminates the process with a diagnostic.
/// </summary>
public sealed class ReplayHost
{
    /// <summary>Fixed epoch so transcripts are byte-identical across runs.</summary>
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly Fixture _fixture;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Dictionary<string, int> _turnCursor = new(StringComparer.Ordinal);
    private readonly HashSet<string> _createdSessions = new(StringComparer.Ordinal);
    private int _eventSequence;
    private bool _shutdownRequested;

    public ReplayHost(Fixture fixture, TextWriter stdout, TextWriter stderr)
    {
        _fixture = fixture;
        _stdout = stdout;
        _stderr = stderr;
    }

    /// <summary>Methods this runtime answers. Anything else is an unrecorded request.</summary>
    public static IReadOnlyList<string> SupportedMethods { get; } =
    [
        "connect",
        "ping",
        "models.list",
        "session.create",
        "session.sendMessages",
        "session.sendAndWait",
        "session.abort",
        "session.detach",
        "session.delete",
        "session.getMessages",
        "session.model.list",
        "session.model.switchTo",
        "session.tools.set",
        "runtime.shutdown",
    ];

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string? fixturePath;
        try
        {
            fixturePath = ResolveFixturePath(args);
        }
        catch (FixtureException ex)
        {
            await stderr.WriteLineAsync($"replay-runtime: {ex.Message}");
            return 2;
        }

        Fixture fixture;
        try
        {
            fixture = Fixture.Load(fixturePath);
        }
        catch (Exception ex) when (ex is FixtureException or JsonException or IOException)
        {
            await stderr.WriteLineAsync($"replay-runtime: failed to load fixture '{fixturePath}': {ex.Message}");
            return 2;
        }

        var host = new ReplayHost(fixture, stdout, stderr);
        return await host.ServeAsync(stdin);
    }

    private static string ResolveFixturePath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fixture" or "-f")
            {
                if (i + 1 >= args.Length)
                {
                    throw new FixtureException("--fixture requires a path argument");
                }

                return args[i + 1];
            }

            if (args[i].StartsWith("--fixture=", StringComparison.Ordinal))
            {
                return args[i]["--fixture=".Length..];
            }
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "basic.json");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        throw new FixtureException("no fixture supplied; pass --fixture <path>");
    }

    private async Task<int> ServeAsync(TextReader stdin)
    {
        while (!_shutdownRequested)
        {
            string? frame;
            try
            {
                frame = JsonRpcFraming.ReadFrame(stdin);
            }
            catch (FixtureException ex)
            {
                await _stderr.WriteLineAsync($"replay-runtime: {ex.Message}");
                return 1;
            }

            if (frame is null)
            {
                break;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(frame) as JsonObject;
            }
            catch (JsonException ex)
            {
                await _stderr.WriteLineAsync($"replay-runtime: failed to parse incoming JSON-RPC message: {ex.Message}");
                return 1;
            }

            if (request is null)
            {
                await _stderr.WriteLineAsync("replay-runtime: failed to parse incoming JSON-RPC message: not a JSON object");
                return 1;
            }

            var method = request["method"]?.GetValue<string>();
            var id = request["id"];

            // Notifications (no id) carry no response obligation.
            if (id is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(method))
            {
                await WriteErrorAsync(id, -32600, "Invalid Request: missing method");
                continue;
            }

            if (!SupportedMethods.Contains(method, StringComparer.Ordinal))
            {
                await _stderr.WriteLineAsync(
                    $"replay-runtime: unrecorded request '{method}' has no fixture entry; refusing to invent a response");
                return 1;
            }

            var parameters = request["params"] as JsonObject;
            JsonNode? result;
            try
            {
                result = Dispatch(method, parameters);
            }
            catch (FixtureException ex)
            {
                await _stderr.WriteLineAsync($"replay-runtime: {method}: {ex.Message}");
                return 1;
            }

            if (result is null)
            {
                await WriteErrorAsync(id, -32601, $"Method not found: {method}");
                continue;
            }

            await WriteResultAsync(id, result);
        }

        return 0;
    }

    private JsonNode? Dispatch(string method, JsonObject? parameters) => method switch
    {
        "connect" => Connect(),
        "ping" => Ping(parameters),
        "models.list" => ModelsList(),
        "session.create" => SessionCreate(parameters),
        "session.sendMessages" => SessionSend(parameters, wait: false),
        "session.sendAndWait" => SessionSend(parameters, wait: true),
        "session.abort" => new JsonObject { ["aborted"] = true },
        "session.detach" => new JsonObject { ["detached"] = true },
        "session.delete" => SessionDelete(parameters),
        "session.getMessages" => new JsonObject { ["events"] = new JsonArray() },
        "session.model.list" => ModelsList(),
        "session.model.switchTo" => new JsonObject { ["modelId"] = parameters?["modelId"]?.DeepClone() },
        "session.tools.set" => new JsonObject { ["ok"] = true },
        "runtime.shutdown" => Shutdown(),
        _ => null,
    };

    private JsonNode Connect() => new JsonObject
    {
        ["ok"] = true,
        ["protocolVersion"] = _fixture.ProtocolVersion,
        ["version"] = _fixture.RuntimeVersion,
        ["taskKinds"] = new JsonArray("agent", "shell"),
    };

    private JsonNode Ping(JsonObject? parameters) => new JsonObject
    {
        ["message"] = parameters?["message"]?.GetValue<string>() ?? "pong",
        ["protocolVersion"] = _fixture.ProtocolVersion,
        ["timestamp"] = Timestamp(),
    };

    private JsonNode ModelsList()
    {
        var list = new JsonArray();
        foreach (var model in _fixture.Models)
        {
            var entry = new JsonObject
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["vendor"] = model.Vendor,
            };

            if (model.Capabilities is { Count: > 0 })
            {
                var capabilities = new JsonObject();
                foreach (var (key, value) in model.Capabilities)
                {
                    capabilities[key] = JsonNode.Parse(value.GetRawText());
                }

                entry["capabilities"] = capabilities;
            }

            list.Add(entry);
        }

        return new JsonObject { ["list"] = list };
    }

    private JsonNode SessionCreate(JsonObject? parameters)
    {
        var requestedId = parameters?["sessionId"]?.GetValue<string>();
        var session = _fixture.FindSession(requestedId)
            ?? (requestedId is null ? _fixture.Sessions.FirstOrDefault() : null)
            ?? throw new FixtureException(
                requestedId is null
                    ? "fixture records no sessions to create"
                    : $"fixture records no session '{requestedId}'");

        _createdSessions.Add(session.Id);
        _turnCursor.TryAdd(session.Id, 0);

        return new JsonObject { ["sessionId"] = session.Id };
    }

    private JsonNode SessionSend(JsonObject? parameters, bool wait)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing sessionId");
        var session = _fixture.FindSession(sessionId)
            ?? throw new FixtureException($"fixture records no session '{sessionId}'");

        var cursor = _turnCursor.TryGetValue(session.Id, out var current) ? current : 0;
        if (cursor >= session.Turns.Count)
        {
            throw new FixtureException(
                $"session '{session.Id}' has no recorded turn at index {cursor}; the fixture is exhausted");
        }

        var turn = session.Turns[cursor];
        _turnCursor[session.Id] = cursor + 1;

        EmitAssistantMessage(session.Id, turn);

        return new JsonObject
        {
            ["messageIds"] = new JsonArray(turn.MessageId),
        };
    }

    private void EmitAssistantMessage(string sessionId, FixtureTurn turn)
    {
        var data = new JsonObject
        {
            ["content"] = turn.Content,
            ["messageId"] = turn.MessageId,
        };

        if (turn.ToolRequests is { Count: > 0 })
        {
            var requests = new JsonArray();
            foreach (var request in turn.ToolRequests)
            {
                requests.Add(JsonNode.Parse(request.GetRawText()));
            }

            data["toolRequests"] = requests;
        }

        var envelope = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["event"] = new JsonObject
            {
                ["id"] = $"evt-{_eventSequence:D4}",
                ["timestamp"] = Timestamp(),
                ["parentId"] = _eventSequence == 0 ? null : $"evt-{_eventSequence - 1:D4}",
                ["type"] = "assistant.message",
                ["data"] = data,
            },
        };

        _eventSequence++;
        WriteNotification("session.event", envelope);
    }

    private JsonNode SessionDelete(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>();
        if (sessionId is not null)
        {
            _createdSessions.Remove(sessionId);
            _turnCursor.Remove(sessionId);
        }

        return new JsonObject { ["deleted"] = true };
    }

    private JsonNode Shutdown()
    {
        _shutdownRequested = true;
        return new JsonObject { ["ok"] = true };
    }

    private string Timestamp() => Epoch.AddSeconds(_eventSequence).ToString("O");

    private void WriteNotification(string method, JsonNode parameters)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        };

        JsonRpcFraming.WriteFrame(_stdout, notification.ToJsonString());
    }

    private Task WriteResultAsync(JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };

        JsonRpcFraming.WriteFrame(_stdout, response.ToJsonString());
        return Task.CompletedTask;
    }

    private Task WriteErrorAsync(JsonNode id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        JsonRpcFraming.WriteFrame(_stdout, response.ToJsonString());
        return Task.CompletedTask;
    }
}
