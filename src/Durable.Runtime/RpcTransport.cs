using System.Text.Json;
using System.Text.Json.Nodes;

namespace Durable.Runtime;

/// <summary>
/// One request line, with the provenance an error response needs to name it.
/// </summary>
/// <remarks>
/// <see cref="Line"/> and <see cref="Text"/> are carried rather than re-derived so a refusal can
/// say which line of the stream was wrong and quote it, even when the line was too malformed to
/// yield an id.
/// </remarks>
internal sealed record RpcRequest(
    int Line,
    string Text,
    string Id,
    string? Agent,
    string Input,
    bool NewConversation);

/// <summary>
/// A line that is not a request, with whatever id it did carry.
/// </summary>
/// <remarks>
/// The id is salvaged rather than dropped: a client that pipelined requests needs to know which
/// of them was refused, and a line can be wrong in its input and still be right in its id.
/// </remarks>
internal sealed class RpcParseException(string message, string? id) : Exception(message)
{
    public string? Id { get; } = id;
}

/// <summary>
/// The JSON-lines transport: one request per line on standard input, and the lines a request
/// produces on standard output.
/// </summary>
/// <remarks>
/// The transport owns framing and nothing else. It reads a line, parses it into an
/// <see cref="RpcRequest"/>, hands it to a handler, and writes whatever the handler writes. What a
/// turn's lines contain is the host's business, so the same turn executor that serves HTTP serves
/// this transport too.
/// <para>
/// A malformed line is a response, never a crash: the refusal names the line and the problem and
/// the loop reads the next line. End of input ends the loop, which is how a client says it is
/// done. A stop signal ends the loop too, but only after the turn in flight has reached its
/// checkpoint — cancelling a turn would skip both the turn checkpoint and the admission settle.
/// </para>
/// </remarks>
internal sealed class RpcTransport
{
    private readonly TextReader _input;
    private readonly TextWriter _output;

    /// <summary>The turn the read loop is currently awaiting, so a stop can wait for it.</summary>
    private Task _inFlight = Task.CompletedTask;

    public RpcTransport(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>The process's own standard input and output, which is the wire.</summary>
    public static RpcTransport Standard() => new(Console.In, Console.Out);

    /// <summary>
    /// Reads requests until standard input ends or <paramref name="stopping"/> is cancelled, then
    /// waits up to <paramref name="grace"/> for the turn in flight.
    /// </summary>
    public async Task RunAsync(Func<RpcRequest, Task> handler, CancellationToken stopping, TimeSpan grace)
    {
        var loop = ReadAsync(handler, stopping);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stopping.Register(() => stopped.TrySetResult());

        if (await Task.WhenAny(loop, stopped.Task) == loop)
        {
            await loop;
            return;
        }

        // A stop arrived. The read loop is either blocked on standard input, which the process
        // leaving will end, or inside a turn, which has to reach its checkpoint first.
        try
        {
            await Volatile.Read(ref _inFlight).WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"warning: a turn was still running after {grace.TotalSeconds:0}s; leaving without it.");
        }
    }

    /// <summary>Writes one line and flushes it, so a client sees it while the turn is still running.</summary>
    public async Task WriteLineAsync(string line)
    {
        await _output.WriteLineAsync(line);
        await _output.FlushAsync();
    }

    /// <summary>Writes one JSON object as a line.</summary>
    public Task WriteObjectAsync(JsonObject line) => WriteLineAsync(line.ToJsonString());

    /// <summary>
    /// Writes a refusal: the line it is about, the text of that line, and the problem.
    /// </summary>
    /// <remarks>
    /// <paramref name="id"/> is <see langword="null"/> when the line was too malformed to carry
    /// one, which is exactly the case a client most needs the line number for.
    /// </remarks>
    public Task WriteErrorAsync(string? id, int line, string text, string problem) =>
        WriteObjectAsync(new JsonObject
        {
            ["type"] = "error",
            ["requestId"] = id,
            ["line"] = line,
            ["lineText"] = text,
            ["error"] = problem,
        });

    private async Task ReadAsync(Func<RpcRequest, Task> handler, CancellationToken stopping)
    {
        var number = 0;
        while (!stopping.IsCancellationRequested)
        {
            string? text;
            try
            {
                text = await _input.ReadLineAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (text is null)
            {
                return;
            }

            number++;

            // A blank line is not a request. Skipping it keeps a client that ends its stream with
            // a newline from being told it sent a malformed request.
            if (text.Trim().Length == 0)
            {
                continue;
            }

            RpcRequest request;
            try
            {
                request = Parse(number, text);
            }
            catch (RpcParseException ex)
            {
                await WriteErrorAsync(ex.Id, number, text, ex.Message);
                continue;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                await WriteErrorAsync(null, number, text, ex.Message);
                continue;
            }

            try
            {
                _inFlight = handler(request);
                await _inFlight;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A turn's own failure is already a result line. This is the rest: a store that
                // could not be read, a conversation that could not be opened. The host keeps
                // serving either way.
                await WriteErrorAsync(request.Id, number, text, ex.Message);
            }
        }
    }

    private static RpcRequest Parse(int number, string text)
    {
        if (JsonNode.Parse(text) is not JsonObject body)
        {
            throw new RpcParseException("the line must be a JSON object.", null);
        }

        var id = body["id"] is { } idNode && idNode.GetValueKind() == JsonValueKind.String
            ? idNode.GetValue<string>()
            : null;

        if (string.IsNullOrEmpty(id))
        {
            throw new RpcParseException(
                body.ContainsKey("id") ? "'id' must be a non-empty string." : "the request has no 'id' property.",
                null);
        }

        if (body["input"] is not { } inputNode || inputNode.GetValueKind() != JsonValueKind.String)
        {
            throw new RpcParseException(
                body.ContainsKey("input") ? "'input' must be a string." : "the request has no 'input' property.",
                id);
        }

        var input = inputNode.GetValue<string>();
        if (input.Length == 0)
        {
            throw new RpcParseException("'input' is empty.", id);
        }

        string? agent = null;
        if (body["agent"] is { } agentNode)
        {
            if (agentNode.GetValueKind() != JsonValueKind.String)
            {
                throw new RpcParseException("'agent' must be a string.", id);
            }

            agent = agentNode.GetValue<string>();
        }

        var newConversation = false;
        if (body["newConversation"] is { } flag)
        {
            if (flag.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new RpcParseException("'newConversation' must be true or false.", id);
            }

            newConversation = flag.GetValue<bool>();
        }

        return new RpcRequest(number, text, id, agent, input, newConversation);
    }
}
