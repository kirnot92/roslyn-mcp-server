using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RoslynMcpServer.Lsp;

public static class LspFraming
{
    private static readonly byte[] Separator = "\r\n\r\n"u8.ToArray();

    public static async Task WriteAsync(Stream stream, object payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions.Default);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonDocument?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(128);
        while (!EndsWith(headerBytes, Separator))
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            headerBytes.Add(buffer[0]);
            if (headerBytes.Count > 16 * 1024)
            {
                throw new InvalidDataException("LSP header exceeded 16KB.");
            }
        }

        var headerText = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(headerBytes));
        var contentLength = ParseContentLength(headerText);
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of LSP message body.");
            }

            offset += read;
        }

        return JsonDocument.Parse(body);
    }

    public static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (string.Equals(line[..colon], "Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(colon + 1)..].Trim(), out var length) &&
                length >= 0)
            {
                return length;
            }
        }

        throw new InvalidDataException("LSP message is missing Content-Length.");
    }

    private static bool EndsWith(List<byte> bytes, byte[] suffix)
    {
        if (bytes.Count < suffix.Length)
        {
            return false;
        }

        for (var i = 0; i < suffix.Length; i++)
        {
            if (bytes[bytes.Count - suffix.Length + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }
}
