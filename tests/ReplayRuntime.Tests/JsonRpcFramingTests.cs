using System.Text;
using System.Text.Json.Nodes;

namespace ReplayRuntime.Tests;

public sealed class JsonRpcFramingTests
{
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
