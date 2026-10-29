using System.Text;

namespace ReplayRuntime;

/// <summary>
/// Reads and writes JSON-RPC 2.0 frames using the LSP-style
/// <c>Content-Length</c> header framing the Copilot runtime transport expects.
/// </summary>
public static class JsonRpcFraming
{
    private const string ContentLengthHeader = "Content-Length:";

    /// <summary>
    /// Reads one frame. Returns <see langword="null"/> at end of stream.
    /// </summary>
    public static string? ReadFrame(TextReader reader)
    {
        int? contentLength = null;

        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                if (contentLength is not null)
                {
                    throw new FixtureException("stream ended while reading JSON-RPC headers");
                }

                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith(ContentLengthHeader, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[ContentLengthHeader.Length..].Trim();
                if (!int.TryParse(value, out var parsed) || parsed < 0)
                {
                    throw new FixtureException($"invalid Content-Length header: '{value}'");
                }

                contentLength = parsed;
            }
        }

        if (contentLength is null)
        {
            throw new FixtureException("JSON-RPC frame is missing the Content-Length header");
        }

        var buffer = new char[contentLength.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = reader.Read(buffer, read, buffer.Length - read);
            if (chunk == 0)
            {
                throw new FixtureException("stream ended while reading JSON-RPC body");
            }

            read += chunk;
        }

        return new string(buffer);
    }

    /// <summary>
    /// Writes one frame. The byte count is computed from UTF-8 so the header always
    /// matches the payload the client decodes.
    /// </summary>
    public static void WriteFrame(TextWriter writer, string json)
    {
        var byteCount = Encoding.UTF8.GetByteCount(json);
        writer.Write(ContentLengthHeader);
        writer.Write(' ');
        writer.Write(byteCount);
        writer.Write("\r\n\r\n");
        writer.Write(json);
        writer.Flush();
    }
}
