using System.Globalization;
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

    /// <summary>
    /// How long a session may sit in the awaiting-tool-result state before the runtime gives
    /// up. This is an explicit budget, never a framework default: a fixture that records a
    /// tool call the client never answers must fail loudly instead of hanging.
    /// </summary>
    public static readonly TimeSpan DefaultToolResultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The phrase the runtime writes when it serves a session the fixture does not record. A
    /// caller that treats a deterministic pass as evidence has to be able to tell a recorded
    /// transcript from a synthesised one, so the marker is a stable, greppable string rather
    /// than prose that drifts.
    /// </summary>
    public const string SynthesisedSessionMarker = "synthesised session";

    /// <summary>
    /// The environment variable that names a file the runtime appends its session markers to.
    /// </summary>
    /// <remarks>
    /// The SDK captures the runtime's stderr and only surfaces it when a request fails, so a
    /// successful run would otherwise lose the marker. A caller that needs to know whether a
    /// pass was recorded sets this variable to a path it owns and reads the file afterwards.
    /// The marker is still written to stderr as well, for a runtime driven by hand.
    /// </remarks>
    public const string MarkerPathEnvironmentVariable = "REPLAY_MARKER_PATH";

    private readonly Fixture _fixture;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TimeSpan _toolResultTimeout;
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// The recorded session that supplies each live session's turns. A session the client
    /// created is served by the recording it was created from, so a later request resolves
    /// without re-deriving the answer.
    /// </summary>
    private readonly Dictionary<string, FixtureSession> _sources = new(StringComparer.Ordinal);

    /// <summary>Session ids already announced as synthesised, so the marker appears once each.</summary>
    private readonly HashSet<string> _announcedSynthesised = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _toolTimeout = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _toolTimeoutSource;
    private string? _toolTimeoutDiagnostic;
    private int _createCursor;
    private int _eventSequence;
    private bool _shutdownRequested;

    public ReplayHost(Fixture fixture, TextWriter stdout, TextWriter stderr)
        : this(fixture, stdout, stderr, DefaultToolResultTimeout)
    {
    }

    public ReplayHost(Fixture fixture, TextWriter stdout, TextWriter stderr, TimeSpan toolResultTimeout)
    {
        _fixture = fixture;
        _stdout = stdout;
        _stderr = stderr;
        _toolResultTimeout = toolResultTimeout;
    }

    /// <summary>Methods this runtime answers. Anything else is an unrecorded request.</summary>
    public static IReadOnlyList<string> SupportedMethods { get; } =
    [
        "connect",
        "ping",
        "models.list",
        "session.create",
        "session.send",
        "session.sendMessages",
        "session.sendAndWait",
        "session.abort",
        "session.detach",
        "session.delete",
        "session.getMessages",
        "session.getMetadata",
        "session.resume",
        "session.model.list",
        "session.model.switchTo",
        "session.tools.set",
        "session.tools.handlePendingToolCall",
        "session.tools.listPendingRequests",
        "runtime.shutdown",
    ];

    public static async Task<int> RunAsync(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string fixturePath;
        TimeSpan toolResultTimeout;
        try
        {
            fixturePath = ResolveFixturePath(args);
            toolResultTimeout = ResolveToolResultTimeout(args);
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

        var host = new ReplayHost(fixture, stdout, stderr, toolResultTimeout);
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

    private static TimeSpan ResolveToolResultTimeout(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string? value = null;
            if (args[i] is "--tool-timeout" or "-t")
            {
                if (i + 1 >= args.Length)
                {
                    throw new FixtureException("--tool-timeout requires a value in seconds");
                }

                value = args[i + 1];
            }
            else if (args[i].StartsWith("--tool-timeout=", StringComparison.Ordinal))
            {
                value = args[i]["--tool-timeout=".Length..];
            }

            if (value is null)
            {
                continue;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                throw new FixtureException($"--tool-timeout must be a positive number of seconds, got '{value}'");
            }

            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultToolResultTimeout;
    }

    private async Task<int> ServeAsync(TextReader stdin)
    {
        // The read loop blocks on stdin, so it runs on its own thread and the timeout
        // watchdog races it. Whichever finishes first decides the exit code.
        var loop = Task.Run(() => ServeLoop(stdin));
        var completed = await Task.WhenAny(loop, _toolTimeout.Task);
        if (completed == loop)
        {
            return await loop;
        }

        await _stderr.WriteLineAsync($"replay-runtime: {_toolTimeoutDiagnostic}");
        return 1;
    }

    private readonly List<JsonObject> _deferred = new();

    private int ServeLoop(TextReader stdin)
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
                _stderr.WriteLine($"replay-runtime: {ex.Message}");
                return 1;
            }

            if (frame is null)
            {
                var stalled = _sessions.Values.FirstOrDefault(s => s.AwaitingToolResult);
                if (stalled is not null)
                {
                    _stderr.WriteLine(
                        $"replay-runtime: {DescribeToolWait(stalled)}; the client closed the stream instead");
                    return 1;
                }

                break;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(frame) as JsonObject;
            }
            catch (JsonException ex)
            {
                _stderr.WriteLine($"replay-runtime: failed to parse incoming JSON-RPC message: {ex.Message}");
                return 1;
            }

            if (request is null)
            {
                _stderr.WriteLine("replay-runtime: failed to parse incoming JSON-RPC message: not a JSON object");
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
                WriteError(id, -32600, "Invalid Request: missing method");
                continue;
            }

            if (!SupportedMethods.Contains(method, StringComparer.Ordinal))
            {
                _stderr.WriteLine(
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
                _stderr.WriteLine($"replay-runtime: {method}: {ex.Message}");
                return 1;
            }

            if (result is null)
            {
                WriteError(id, -32601, $"Method not found: {method}");
                continue;
            }

            WriteResult(id, result);

            foreach (var deferred in _deferred)
            {
                WriteNotification("session.event", deferred);
            }

            _deferred.Clear();
        }

        return 0;
    }

    private JsonNode? Dispatch(string method, JsonObject? parameters) => method switch
    {
        "connect" => Connect(),
        "ping" => Ping(parameters),
        "models.list" => ModelsList(),
        "session.create" => SessionCreate(parameters),
        "session.send" => SessionSend(parameters, singleMessageId: true),
        "session.sendMessages" => SessionSend(parameters, singleMessageId: false),
        "session.sendAndWait" => SessionSend(parameters, singleMessageId: false),
        "session.abort" => new JsonObject { ["aborted"] = true },
        "session.detach" => new JsonObject { ["success"] = true },
        "session.delete" => SessionDelete(parameters),
        "session.resume" => SessionResume(parameters),
        "session.getMessages" => SessionGetMessages(parameters),
        "session.getMetadata" => SessionGetMetadata(parameters),
        "session.model.list" => ModelsList(),
        "session.model.switchTo" => new JsonObject { ["modelId"] = parameters?["modelId"]?.DeepClone() },
        "session.tools.set" => new JsonObject { ["ok"] = true },
        "session.tools.handlePendingToolCall" => HandlePendingToolCall(parameters),
        "session.tools.listPendingRequests" => ListPendingRequests(parameters),
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
        FixtureSession session;
        if (requestedId is not null)
        {
            // A requested id that the fixture does not record is still served: the client asked
            // for a session by name, and the recorded turns are what the runtime answers with.
            // The first recorded session supplies them, so a client that names its own session
            // does not have to know the recorded id. That is a synthesised session, not a
            // recorded one, and it is announced as such.
            var recorded = _fixture.FindSession(requestedId);
            session = recorded
                ?? (_fixture.Sessions.Count > 0
                    ? _fixture.Sessions[0]
                    : throw new FixtureException($"fixture records no session '{requestedId}' and no sessions at all"));

            if (recorded is null)
            {
                AnnounceSynthesised(requestedId, session);
            }
        }
        else
        {
            // Allocate from the fixture's session list in order, so repeated creates
            // walk the recorded sessions deterministically.
            if (_createCursor >= _fixture.Sessions.Count)
            {
                throw new FixtureException(
                    $"fixture records no session at index {_createCursor}; the fixture is exhausted");
            }

            session = _fixture.Sessions[_createCursor];
            _createCursor++;
        }

        // The state is keyed by the id the client asked for, so its later requests resolve.
        var id = requestedId ?? session.Id;
        _sessions[id] = new SessionState(id);
        _sources[id] = session;

        // A real runtime announces the session before any turn runs, and that announcement is
        // where it names the model it selected. The recording carries the model, so the runtime
        // reports it rather than leaving the client to guess from its own request.
        EmitSessionStart(_sessions[id], session);

        return new JsonObject { ["sessionId"] = id };
    }

    /// <summary>
    /// Emits <c>session.start</c> for a session the runtime has just created or resumed.
    /// </summary>
    /// <remarks>
    /// <c>selectedModel</c> comes from the fixture session the runtime serves, so a recording
    /// that names a model is a runtime that reports one. A recording that names none still gets
    /// the event, with no model in it: "the recording carries no model" and "the runtime said
    /// nothing at all" are then visibly different, and neither is invented.
    /// </remarks>
    private void EmitSessionStart(SessionState state, FixtureSession source)
    {
        var data = new JsonObject
        {
            ["sessionId"] = state.Id,
            ["copilotVersion"] = _fixture.RuntimeVersion,
            ["producer"] = "replay",
            ["startTime"] = Timestamp(),
            ["version"] = 1,
        };

        if (!string.IsNullOrWhiteSpace(source.Model))
        {
            data["selectedModel"] = source.Model;
        }

        EmitEvent(state, "session.start", data, defer: true);
    }

    /// <summary>
    /// Records that a session is being served from a recording that does not name it, and says
    /// so on stderr. The line is the only difference between a recorded pass and a synthesised
    /// one, so it is written once per session and never suppressed.
    /// </summary>
    private void AnnounceSynthesised(string sessionId, FixtureSession source)
    {
        if (!_announcedSynthesised.Add(sessionId))
        {
            return;
        }

        var line =
            $"replay-runtime: {SynthesisedSessionMarker} '{sessionId}' from recorded session '{source.Id}'; " +
            "the fixture does not record this session, so this is not a recorded transcript";

        _stderr.WriteLine(line);
        AppendMarker(line);
    }

    /// <summary>
    /// Appends a marker to the file named by <see cref="MarkerPathEnvironmentVariable"/>, when set.
    /// </summary>
    /// <remarks>
    /// The SDK captures the runtime's stderr and discards it on success, so a caller that wants to
    /// know whether a pass was recorded needs a sink it controls. Failing to write the file is not
    /// fatal: the marker has already reached stderr, and a run should not fail because a
    /// diagnostic could not be recorded.
    /// </remarks>
    private static void AppendMarker(string line)
    {
        var path = Environment.GetEnvironmentVariable(MarkerPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker is already on stderr; a diagnostic sink that cannot be written is not
            // a reason to fail the run.
        }
    }

    private JsonNode SessionSend(JsonObject? parameters, bool singleMessageId)
    {
        var session = ResolveSession(parameters);
        var state = GetState(parameters?["sessionId"]?.GetValue<string>() ?? session.Id);

        if (state.AwaitingToolResult)
        {
            throw new FixtureException(
                $"session '{session.Id}' turn {state.AwaitingTurnIndex} is still waiting for the result of tool call " +
                $"'{state.PendingToolCalls[0].ToolCallId}'; the client sent another message instead of " +
                "session.tools.handlePendingToolCall");
        }

        var turn = TakeTurn(session, state);
        EmitAssistantMessage(state, turn);
        EnterToolWaitIfNeeded(state, turn);

        // The idle notification is written after the response, so a client that awaits the send
        // has already subscribed by the time the turn's completion is announced.
        _ = Task.Run(async () =>
        {
            await Task.Delay(IdleDelay);
            EmitIdleIfSettled(state);
        });

        // session.send answers with a single message id; the legacy batch methods answer
        // with the list shape they were defined with.
        return singleMessageId
            ? new JsonObject { ["messageId"] = turn.MessageId }
            : new JsonObject { ["messageIds"] = new JsonArray(turn.MessageId) };
    }

    private JsonNode HandlePendingToolCall(JsonObject? parameters)
    {
        var session = ResolveSession(parameters);
        var requestId = parameters?["requestId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing requestId");
        var state = GetState(parameters?["sessionId"]?.GetValue<string>() ?? session.Id);

        if (!state.AwaitingToolResult)
        {
            throw new FixtureException(
                $"session '{session.Id}' received a tool result for request '{requestId}' but no tool call is pending");
        }

        var index = state.PendingToolCalls.FindIndex(
            p => string.Equals(p.RequestId, requestId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new FixtureException(
                $"session '{session.Id}' turn {state.AwaitingTurnIndex} has no pending tool call with request id '{requestId}'");
        }

        var pending = state.PendingToolCalls[index];
        state.PendingToolCalls.RemoveAt(index);

        // The client's own result is recorded verbatim; the runtime never invents one.
        EmitToolExecutionComplete(state, pending, parameters?["result"], parameters?["error"]?.GetValue<string>());

        if (state.PendingToolCalls.Count == 0)
        {
            state.AwaitingToolResult = false;
            DisarmToolTimeout();

            var turn = TakeTurn(session, state);
            EmitAssistantMessage(state, turn);
            EnterToolWaitIfNeeded(state, turn);
            _ = Task.Run(async () =>
            {
                await Task.Delay(IdleDelay);
                EmitIdleIfSettled(state);
            });
        }

        return new JsonObject { ["success"] = true };
    }

    private JsonNode ListPendingRequests(JsonObject? parameters)
    {
        var session = ResolveSession(parameters);
        var state = GetState(parameters?["sessionId"]?.GetValue<string>() ?? session.Id);

        var items = new JsonArray();
        foreach (var pending in state.PendingToolCalls)
        {
            items.Add(new JsonObject
            {
                ["requestId"] = pending.RequestId,
                ["toolCallId"] = pending.ToolCallId,
                ["toolName"] = pending.ToolName,
                ["arguments"] = pending.Arguments?.DeepClone(),
            });
        }

        return new JsonObject { ["items"] = items };
    }

    private static FixtureTurn TakeTurn(FixtureSession session, SessionState state)
    {
        if (state.TurnCursor >= session.Turns.Count)
        {
            throw new FixtureException(
                $"session '{session.Id}' has no recorded turn at index {state.TurnCursor}; the fixture is exhausted");
        }

        var turn = session.Turns[state.TurnCursor];
        state.TurnCursor++;
        return turn;
    }

    /// <summary>
    /// Moves the session into the awaiting-tool-result state when the recorded turn carries
    /// tool requests, emitting one <c>external_tool.requested</c> event per call.
    /// </summary>
    private void EnterToolWaitIfNeeded(SessionState state, FixtureTurn turn)
    {
        if (turn.ToolRequests is not { Count: > 0 })
        {
            return;
        }

        var turnIndex = state.TurnCursor - 1;
        state.AwaitingToolResult = true;
        state.AwaitingTurnIndex = turnIndex;
        state.PendingToolCalls.Clear();

        for (var i = 0; i < turn.ToolRequests.Count; i++)
        {
            var request = turn.ToolRequests[i];
            var toolName = request.GetProperty("name").GetString()!;
            var toolCallId = request.GetProperty("toolCallId").GetString()!;
            var arguments = request.TryGetProperty("arguments", out var recorded)
                ? JsonNode.Parse(recorded.GetRawText())
                : null;
            var requestId = $"req-{state.Id}-{turnIndex}-{i}";

            state.PendingToolCalls.Add(new PendingToolCall(requestId, toolCallId, toolName, arguments));

            EmitEvent(state, "external_tool.requested", new JsonObject
            {
                ["requestId"] = requestId,
                ["sessionId"] = state.Id,
                ["toolCallId"] = toolCallId,
                ["toolName"] = toolName,
                ["arguments"] = arguments?.DeepClone(),
            });
        }

        ArmToolTimeout(state);
    }

    /// <summary>
    /// How long the runtime waits before announcing that a turn has settled, so the notification
    /// arrives after the response to the request that started the turn.
    /// </summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Emits <c>session.idle</c> once a turn has nothing left in flight, which is what a client
    /// waiting for the turn to finish observes.
    /// </summary>
    private void EmitIdleIfSettled(SessionState state)
    {
        if (state.AwaitingToolResult)
        {
            return;
        }

        EmitEvent(state, "session.idle", new JsonObject { ["aborted"] = false });
    }

    private void EmitAssistantMessage(SessionState state, FixtureTurn turn)
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

        EmitEvent(state, "assistant.message", data);
    }

    private void EmitToolExecutionComplete(SessionState state, PendingToolCall pending, JsonNode? result, string? error)
    {
        var data = new JsonObject { ["toolCallId"] = pending.ToolCallId };

        if (error is not null)
        {
            data["success"] = false;
            data["error"] = new JsonObject { ["message"] = error };
        }
        else
        {
            var resultType = (result as JsonObject)?["resultType"]?.GetValue<string>();
            data["success"] = resultType is null or "success";
            data["result"] = new JsonObject
            {
                ["content"] = (result as JsonObject)?["textResultForLlm"]?.GetValue<string>()
                    ?? result?.ToJsonString()
                    ?? string.Empty,
            };
        }

        EmitEvent(state, "tool.execution_complete", data);
    }

    private void EmitEvent(SessionState state, string type, JsonObject data, bool defer = false)
    {
        var sequence = _eventSequence;
        var envelope = new JsonObject
        {
            ["sessionId"] = state.Id,
            ["event"] = new JsonObject
            {
                ["id"] = EventId(sequence),
                ["timestamp"] = Timestamp(),
                ["parentId"] = sequence == 0 ? null : EventId(sequence - 1),
                ["type"] = type,
                ["data"] = data,
            },
        };

        _eventSequence++;

        // Record what we emitted so getMessages replays the same transcript.
        state.EmittedEvents.Add(envelope["event"]!.DeepClone());

        if (defer)
        {
            _deferred.Add(envelope);
            return;
        }

        WriteNotification("session.event", envelope);
    }

    private JsonNode SessionGetMessages(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing sessionId");

        var events = _sessions.TryGetValue(sessionId, out var state)
            ? new JsonArray(state.EmittedEvents.Select(e => e!.DeepClone()).ToArray())
            : new JsonArray();

        return new JsonObject { ["events"] = events };
    }

    /// <summary>
    /// Answers the metadata lookup a client makes before resuming. A session the runtime has
    /// seen, or one the fixture records, has metadata; anything else is reported as absent,
    /// which is what a deleted native transcript looks like.
    /// </summary>
    private JsonNode SessionGetMetadata(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing sessionId");

        var known = _sessions.ContainsKey(sessionId) || _fixture.FindSession(sessionId) is not null;
        if (!known)
        {
            return new JsonObject();
        }

        return new JsonObject
        {
            ["session"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["startTime"] = Timestamp(),
                ["modifiedTime"] = Timestamp(),
                ["isRemote"] = false,
            },
        };
    }

    /// <summary>
    /// Resumes a session. A session this runtime created, or one the fixture records, resumes
    /// from its own recording. Anything else is served from the first recording and announced as
    /// synthesised, because resuming is exactly the case where the caller believes a transcript
    /// exists and the runtime has to be honest about whether it does.
    /// </summary>
    private JsonNode SessionResume(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing sessionId");

        var session = _sources.TryGetValue(sessionId, out var known)
            ? known
            : _fixture.FindSession(sessionId);

        if (session is null)
        {
            session = _fixture.Sessions.FirstOrDefault()
                ?? throw new FixtureException($"fixture records no session '{sessionId}' to resume");

            AnnounceSynthesised(sessionId, session);
        }

        _sources[sessionId] = session;
        _sessions[sessionId] = new SessionState(sessionId);
        EmitSessionStart(_sessions[sessionId], session);
        return new JsonObject { ["sessionId"] = sessionId };
    }

    private JsonNode SessionDelete(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>();
        if (sessionId is not null)
        {
            _sessions.Remove(sessionId);
            _sources.Remove(sessionId);
        }

        return new JsonObject { ["success"] = true };
    }

    private JsonNode Shutdown()
    {
        _shutdownRequested = true;
        return new JsonObject { ["ok"] = true };
    }

    private FixtureSession ResolveSession(JsonObject? parameters)
    {
        var sessionId = parameters?["sessionId"]?.GetValue<string>()
            ?? throw new FixtureException("request is missing sessionId");

        // A session the client created under its own id is served by the recording that supplied
        // its turns, so the id the client chose does not have to be the recorded one.
        if (_sources.TryGetValue(sessionId, out var source))
        {
            return source;
        }

        if (_fixture.FindSession(sessionId) is { } recorded)
        {
            return recorded;
        }

        // A request for a session this runtime never created and the fixture never recorded is
        // still answered from the first recording, but it is announced as synthesised so the
        // caller cannot mistake it for a recorded transcript.
        var fallback = _fixture.Sessions.FirstOrDefault()
            ?? throw new FixtureException($"fixture records no session '{sessionId}'");

        AnnounceSynthesised(sessionId, fallback);
        _sources[sessionId] = fallback;
        return fallback;
    }

    private SessionState GetState(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            state = new SessionState(sessionId);
            _sessions[sessionId] = state;
        }

        return state;
    }

    private void ArmToolTimeout(SessionState state)
    {
        DisarmToolTimeout();

        var source = new CancellationTokenSource();
        _toolTimeoutSource = source;
        source.CancelAfter(_toolResultTimeout);
        source.Token.Register(() =>
        {
            _toolTimeoutDiagnostic =
                $"{DescribeToolWait(state)}; timed out after " +
                $"{_toolResultTimeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";
            _toolTimeout.TrySetResult();
        });
    }

    private void DisarmToolTimeout()
    {
        var source = _toolTimeoutSource;
        _toolTimeoutSource = null;
        source?.Dispose();
    }

    private static string DescribeToolWait(SessionState state)
    {
        var callId = state.PendingToolCalls.Count > 0 ? state.PendingToolCalls[0].ToolCallId : "<none>";
        return $"session '{state.Id}' turn {state.AwaitingTurnIndex} is waiting for the result of tool call '{callId}' " +
               "but the client never sent session.tools.handlePendingToolCall";
    }

    /// <summary>
    /// Deterministic, GUID-shaped event id. The SDK deserializes <c>SessionEvent.Id</c> as a
    /// <see cref="Guid"/>, so the sequence number is packed into the first field.
    /// </summary>
    private static string EventId(int sequence) =>
        new Guid(sequence, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString();

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

    private void WriteResult(JsonNode id, JsonNode result)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        };

        JsonRpcFraming.WriteFrame(_stdout, response.ToJsonString());
    }

    private void WriteError(JsonNode id, int code, string message)
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
    }

    private sealed record PendingToolCall(string RequestId, string ToolCallId, string ToolName, JsonNode? Arguments);

    /// <summary>
    /// Per-session turn cursor plus the tool-dispatch state machine. A session is either
    /// ready (the next recorded turn may be emitted) or awaiting a tool result (the recorded
    /// turn carried tool requests and the client owes a result).
    /// </summary>
    private sealed class SessionState(string id)
    {
        public string Id { get; } = id;

        public int TurnCursor { get; set; }

        public bool AwaitingToolResult { get; set; }

        public int AwaitingTurnIndex { get; set; }

        public List<PendingToolCall> PendingToolCalls { get; } = [];

        public JsonArray EmittedEvents { get; } = new();
    }
}
