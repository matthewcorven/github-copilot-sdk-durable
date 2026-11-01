using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReplayRuntime.Tests;

public sealed class JsonRpcFramingTests
{
    [Fact]
    public async Task WriteFrame_ConcurrentResponsesAndNotifications_PreserveEveryUtf8Frame()
    {
        const int writers = 8;
        const int framesPerWriter = 32;
        using var buffer = new MemoryStream();
        using var streamWriter = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true);
        using var output = new FragmentYieldingWriter(streamWriter);
        using var start = new ManualResetEventSlim();
        var expected = Enumerable.Range(0, writers * framesPerWriter).Select(sequence =>
            sequence % 2 == 0
                ? $$$"""{"jsonrpc":"2.0","id":{{{sequence}}},"result":{"text":"héllo→😀","sequence":{{{sequence}}}}}"""
                : $$$"""{"jsonrpc":"2.0","method":"session.event","params":{"text":"héllo→😀","sequence":{{{sequence}}}}}"""
        ).ToArray();

        var tasks = Enumerable.Range(0, writers).Select(worker =>
            Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var i = 0; i < framesPerWriter; i++)
                {
                    JsonRpcFraming.WriteFrame(output, expected[worker * framesPerWriter + i]);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        ).ToArray();

        start.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        var bytes = buffer.ToArray();
        var offset = 0;
        var seen = new HashSet<int>();
        var responses = 0;
        var notifications = 0;
        while (offset < bytes.Length)
        {
            var end = bytes.AsSpan(offset).IndexOf("\r\n\r\n"u8);
            Assert.True(end >= 0, $"Missing header terminator at byte {offset}");
            var header = Encoding.ASCII.GetString(bytes, offset, end);
            Assert.StartsWith("Content-Length: ", header);
            Assert.True(int.TryParse(header["Content-Length: ".Length..],
                NumberStyles.None, CultureInfo.InvariantCulture, out var length),
                $"Invalid frame header at byte {offset}: {header}");
            offset += end + 4;
            Assert.InRange(length, 1, bytes.Length - offset);
            using var payload = JsonDocument.Parse(bytes.AsMemory(offset, length));
            var root = payload.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            var isResponse = root.TryGetProperty("id", out var id);
            var data = root.GetProperty(isResponse ? "result" : "params");
            var sequence = data.GetProperty("sequence").GetInt32();
            Assert.True(seen.Add(sequence), $"Duplicate frame {sequence}");
            Assert.Equal(expected[sequence], Encoding.UTF8.GetString(bytes, offset, length));
            if (isResponse)
            {
                Assert.Equal(sequence, id.GetInt32());
                responses++;
            }
            else
            {
                Assert.Equal("session.event", root.GetProperty("method").GetString());
                notifications++;
            }

            offset += length;
        }

        Assert.Equal(expected.Length, seen.Count);
        Assert.Equal(expected.Length / 2, responses);
        Assert.Equal(expected.Length / 2, notifications);
        Assert.Equal(expected.Length, output.FlushCount);
    }

    private sealed class FragmentYieldingWriter(TextWriter inner) : TextWriter
    {
        private readonly object _gate = new();
        public override Encoding Encoding => inner.Encoding;
        public int FlushCount { get; private set; }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                inner.Write(value);
            }

            Thread.Sleep(1);
        }

        public override void Write(char value) => Write(value.ToString());

        public override void Flush()
        {
            lock (_gate)
            {
                inner.Flush();
                FlushCount++;
            }

            Thread.Yield();
        }
    }

    [Fact]
    public void WriteFrame_ThenReadFrame_RoundTrips()
    {
        const string payload = """{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""";
        var buffer = new StringWriter();

        JsonRpcFraming.WriteFrame(buffer, payload);
        var read = JsonRpcFraming.ReadFrame(new StringReader(buffer.ToString()));

        Assert.Equal(payload, read);
    }

    [Fact]
    public void WriteFrame_UsesUtf8ByteCountNotCharCount()
    {
        const string payload = """{"text":"héllo→"}""";
        var buffer = new StringWriter();

        JsonRpcFraming.WriteFrame(buffer, payload);

        var expected = Encoding.UTF8.GetByteCount(payload);
        Assert.Contains($"Content-Length: {expected}", buffer.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(payload.Length, expected);
    }

    [Fact]
    public void ReadFrame_SkipsAdditionalHeaders()
    {
        const string payload = """{"id":7}""";
        var bytes = Encoding.UTF8.GetByteCount(payload);
        var input = $"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: {bytes}\r\n\r\n{payload}";

        Assert.Equal(payload, JsonRpcFraming.ReadFrame(new StringReader(input)));
    }

    [Fact]
    public void ReadFrame_ReturnsNullAtEndOfStream()
    {
        Assert.Null(JsonRpcFraming.ReadFrame(new StringReader(string.Empty)));
    }

    [Fact]
    public void ReadFrame_ThrowsWhenContentLengthIsMissing()
    {
        var ex = Assert.Throws<FixtureException>(
            () => JsonRpcFraming.ReadFrame(new StringReader("Content-Type: text/plain\r\n\r\n{}")));

        Assert.Contains("Content-Length", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFrame_ThrowsWhenBodyIsTruncated()
    {
        var ex = Assert.Throws<FixtureException>(
            () => JsonRpcFraming.ReadFrame(new StringReader("Content-Length: 40\r\n\r\n{}")));

        Assert.Contains("stream ended", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
