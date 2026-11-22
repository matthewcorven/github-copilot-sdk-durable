using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Durable.Runtime;

/// <summary>An HTTP/1.1 request as the host reads it: the method, the path, and the body.</summary>
internal sealed record HttpRequest(string Method, string Path, string Body);

/// <summary>
/// The response half of one request, written incrementally.
/// </summary>
/// <remarks>
/// The body is chunked so a line reaches the client the moment it is written. A response whose
/// length is only known at the end would have to be buffered, and a client would then see a turn's
/// events and its result arrive together — which is the one thing event streaming exists to avoid.
/// </remarks>
internal sealed class HttpResponse
{
    private static readonly byte[] LastChunk = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly byte[] ChunkTerminator = Encoding.ASCII.GetBytes("\r\n");

    private readonly NetworkStream _stream;

    public HttpResponse(NetworkStream stream) => _stream = stream;

    public async Task StartAsync(int status, string reason, string contentType)
    {
        var head = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + "Transfer-Encoding: chunked\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n";

        await _stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await _stream.FlushAsync();
    }

    /// <summary>Writes one newline-delimited JSON line and flushes it to the client.</summary>
    public async Task WriteLineAsync(string line)
    {
        var payload = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes($"{payload.Length:X}\r\n"));
        await _stream.WriteAsync(payload);
        await _stream.WriteAsync(ChunkTerminator);
        await _stream.FlushAsync();
    }

    public Task CompleteAsync() => _stream.WriteAsync(LastChunk).AsTask();

    /// <summary>Writes a refusal: a status, and a body that names the problem.</summary>
    public async Task WriteErrorAsync(int status, string reason, string message)
    {
        await StartAsync(status, reason, "application/json");
        await WriteLineAsync(new JsonObject { ["error"] = message }.ToJsonString());
        await CompleteAsync();
    }
}

/// <summary>
/// A minimal HTTP/1.1 server over a loopback socket, with a response a handler can write
/// incrementally.
/// </summary>
/// <remarks>
/// The host needs two things the BCL's <see cref="HttpListener"/> cannot give it: a real ephemeral
/// port, and a response that reaches the client while the turn that produced it is still running.
/// <see cref="HttpListener"/> refuses port 0 and buffers a response until it is closed, so the
/// transport is a <see cref="TcpListener"/> bound to port 0 with a chunked body.
/// <para>
/// The surface is deliberately small — one request per connection, no keep-alive, no TLS, no
/// authentication — because those are the parts of HTTP this host does not claim to implement.
/// It binds the loopback address only, so a host with no authentication cannot be reached from
/// another machine by accident.
/// </para>
/// </remarks>
internal sealed class HttpTransport : IAsyncDisposable
{
    private const int MaxRequestBytes = 1024 * 1024;

    /// <summary>How long a client may take to send its request before the connection is dropped.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _connections = [];

    private HttpTransport(TcpListener listener) => _listener = listener;

    /// <summary>The port the operating system assigned, which is what a caller connects to.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Binds the loopback address. A port of 0 asks the operating system for one.</summary>
    public static HttpTransport Start(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new HttpTransport(listener);
    }

    /// <summary>
    /// Accepts connections until <paramref name="stopping"/> is cancelled, then waits up to
    /// <paramref name="grace"/> for the connections already accepted to finish.
    /// </summary>
    /// <remarks>
    /// Waiting for the accepted connections is what makes shutdown graceful: a turn already in
    /// flight is being served by one of them, and the process does not leave until that turn has
    /// reached its checkpoint. The wait is bounded so a runtime that never settles cannot make the
    /// host unkillable.
    /// </remarks>
    public async Task RunAsync(
        Func<HttpRequest, HttpResponse, Task> handler,
        CancellationToken stopping,
        TimeSpan grace)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _stopping.Token);

        try
        {
            while (!linked.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException) when (linked.IsCancellationRequested)
                {
                    break;
                }

                lock (_connections)
                {
                    _connections.RemoveAll(task => task.IsCompleted);

                    // The connection is deliberately not tied to the accept loop's token: a turn
                    // already in flight has to be allowed to reach its checkpoint, so only
                    // disposal cancels it.
                    _connections.Add(ServeAsync(client, handler, _stopping.Token));
                }
            }
        }
        finally
        {
            _listener.Stop();
        }

        Task[] pending;
        lock (_connections)
        {
            pending = [.. _connections];
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"warning: {pending.Length} connection(s) were still serving a turn after {grace.TotalSeconds:0}s; leaving without them.");
        }
    }

    private static async Task ServeAsync(
        TcpClient client,
        Func<HttpRequest, HttpResponse, Task> handler,
        CancellationToken stopping)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, stopping);
                if (request is null)
                {
                    return;
                }

                await handler(request, new HttpResponse(stream));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that disconnects mid-request is not a server failure.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: a request failed: {ex.Message}");
            }
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(RequestTimeout);

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRequestBytes)
            {
                throw new IOException("the request headers are larger than this host accepts.");
            }

            headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        var head = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            throw new IOException("the request line is not 'METHOD PATH HTTP/1.1'.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var bodyLength = headers.TryGetValue("Content-Length", out var text) && int.TryParse(text, out var parsed)
            ? parsed
            : 0;

        if (bodyLength < 0 || bodyLength > MaxRequestBytes)
        {
            throw new IOException("the request body is larger than this host accepts.");
        }

        var body = new byte[bodyLength];
        var bodyStart = headerEnd + 4;
        var alreadyRead = Math.Min((int)buffer.Length - bodyStart, bodyLength);
        if (alreadyRead > 0)
        {
            Array.Copy(buffer.GetBuffer(), bodyStart, body, 0, alreadyRead);
        }

        var offset = alreadyRead;
        while (offset < bodyLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), timeout.Token);
            if (read == 0)
            {
                throw new IOException("the request body ended before Content-Length bytes arrived.");
            }

            offset += read;
        }

        var path = requestLine[1].Split('?')[0];
        return new HttpRequest(requestLine[0], path, Encoding.UTF8.GetString(body));
    }

    /// <summary>Finds the index of the blank line that ends the request headers.</summary>
    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i] == (byte)'\n'
                && buffer[i - 1] == (byte)'\r'
                && buffer[i - 2] == (byte)'\n'
                && buffer[i - 3] == (byte)'\r')
            {
                return i - 3;
            }
        }

        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        _stopping.Dispose();
    }
}
