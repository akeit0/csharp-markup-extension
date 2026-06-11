using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace Csmx.LanguageServer;

internal sealed partial class LspServer
{
    private static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length"u8;
    private static ReadOnlySpan<byte> LspHeaderSeparator => ": "u8;
    private static ReadOnlySpan<byte> LspNewLine => "\r\n"u8;
    private static ReadOnlySpan<byte> JsonContentChanges => "contentChanges"u8;
    private static ReadOnlySpan<byte> JsonId => "id"u8;
    private static ReadOnlySpan<byte> JsonLine => "line"u8;
    private static ReadOnlySpan<byte> JsonMethod => "method"u8;
    private static ReadOnlySpan<byte> JsonParams => "params"u8;
    private static ReadOnlySpan<byte> JsonPosition => "position"u8;
    private static ReadOnlySpan<byte> JsonCharacter => "character"u8;
    private static ReadOnlySpan<byte> JsonText => "text"u8;
    private static ReadOnlySpan<byte> JsonTextDocument => "textDocument"u8;
    private static ReadOnlySpan<byte> JsonUri => "uri"u8;
    private static ReadOnlySpan<byte> JsonVersion => "version"u8;
    private static ReadOnlySpan<byte> MethodInitialize => "initialize"u8;
    private static ReadOnlySpan<byte> MethodInitialized => "initialized"u8;
    private static ReadOnlySpan<byte> MethodShutdown => "shutdown"u8;
    private static ReadOnlySpan<byte> MethodExit => "exit"u8;
    private static ReadOnlySpan<byte> MethodDidOpen => "textDocument/didOpen"u8;
    private static ReadOnlySpan<byte> MethodDidChange => "textDocument/didChange"u8;
    private static ReadOnlySpan<byte> MethodDidClose => "textDocument/didClose"u8;
    private static ReadOnlySpan<byte> MethodCompletion => "textDocument/completion"u8;
    private static ReadOnlySpan<byte> MethodHover => "textDocument/hover"u8;
    private static ReadOnlySpan<byte> MethodDefinition => "textDocument/definition"u8;
    private static ReadOnlySpan<byte> MethodSemanticTokensFull =>
        "textDocument/semanticTokens/full"u8;
    private static ReadOnlySpan<byte> MethodGetGeneratedCSharp => "csmx/getGeneratedCSharp"u8;
    private static ReadOnlySpan<byte> MethodInspectProjectBinding =>
        "csmx/inspectProjectBinding"u8;
    private static ReadOnlySpan<byte> MethodReloadProjectContext => "csmx/reloadProjectContext"u8;
    private static ReadOnlySpan<byte> MethodSetProjectContextOptions =>
        "csmx/setProjectContextOptions"u8;

    private LspMessage? ReadMessage()
    {
        Span<byte> recent = stackalloc byte[4];
        Span<byte> headerBuffer = stackalloc byte[512];
        var headerLength = 0;
        byte[]? rentedHeader = null;

        while (true)
        {
            var b = _input.ReadByte();
            if (b == -1)
            {
                if (rentedHeader is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedHeader);
                }

                return null;
            }

            if (headerLength == headerBuffer.Length)
            {
                var next = ArrayPool<byte>.Shared.Rent(headerBuffer.Length * 2);
                headerBuffer.CopyTo(next);
                if (rentedHeader is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedHeader);
                }

                rentedHeader = next;
                headerBuffer = rentedHeader;
            }

            headerBuffer[headerLength++] = (byte)b;
            recent[0] = recent[1];
            recent[1] = recent[2];
            recent[2] = recent[3];
            recent[3] = (byte)b;

            if (recent.SequenceEqual(HeaderEnd))
            {
                break;
            }
        }

        var contentLength = ParseContentLength(headerBuffer[..headerLength]);
        if (rentedHeader is not null)
        {
            ArrayPool<byte>.Shared.Return(rentedHeader);
        }

        if (contentLength <= 0)
        {
            return null;
        }

        var rentedBody = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            var read = 0;
            while (read < contentLength)
            {
                var n = _input.Read(rentedBody, read, contentLength - read);
                if (n == 0)
                {
                    return null;
                }

                read += n;
            }

            var document = JsonDocument.Parse(rentedBody.AsMemory(0, contentLength));
            return new LspMessage(document, rentedBody);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rentedBody);
            throw;
        }
    }

    private async Task SendResponseAsync(JsonElement id, object? result)
    {
        await SendAsync(
            new
            {
                jsonrpc = "2.0",
                id = ConvertId(id),
                result,
            }
        );
    }

    private async Task SendNotificationAsync(string method, object parameters)
    {
        await SendAsync(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
            }
        );
    }

    private async Task SendAsync(object payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
            var length = content.Length;
            var lengthDigits = CountDecimalDigits(length);
            var headerLength =
                ContentLengthHeader.Length
                + LspHeaderSeparator.Length
                + lengthDigits
                + HeaderEnd.Length;
            var header = ArrayPool<byte>.Shared.Rent(headerLength);
            try
            {
                var written = WriteHeader(header.AsSpan(0, headerLength), length);

                await _output.WriteAsync(header.AsMemory(0, written));
                await _output.WriteAsync(content);
                await _output.FlushAsync();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        while (!header.IsEmpty)
        {
            var lineEnd = header.IndexOf(LspNewLine);
            var line = lineEnd < 0 ? header : header[..lineEnd];
            header =
                lineEnd < 0 ? ReadOnlySpan<byte>.Empty : header[(lineEnd + LspNewLine.Length)..];

            if (line.IsEmpty)
            {
                break;
            }

            var separator = line.IndexOf((byte)':');
            if (separator <= 0)
            {
                continue;
            }

            var name = TrimAscii(line[..separator]);
            if (!AsciiEqualsIgnoreCase(name, ContentLengthHeader))
            {
                continue;
            }

            var value = TrimAscii(line[(separator + 1)..]);
            return
                Utf8Parser.TryParse(value, out int contentLength, out var consumed)
                && consumed == value.Length
                ? contentLength
                : 0;
        }

        return 0;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && IsAsciiWhitespace(value[0]))
        {
            value = value[1..];
        }

        while (!value.IsEmpty && IsAsciiWhitespace(value[^1]))
        {
            value = value[..^1];
        }

        return value;
    }

    private static bool IsAsciiWhitespace(byte value) => value is (byte)' ' or (byte)'\t';

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (ToAsciiUpper(left[i]) != ToAsciiUpper(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte ToAsciiUpper(byte value) =>
        value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;

    private static int CountDecimalDigits(int value)
    {
        var digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    private static int WriteHeader(Span<byte> destination, int contentLength)
    {
        ContentLengthHeader.CopyTo(destination);
        var written = ContentLengthHeader.Length;
        LspHeaderSeparator.CopyTo(destination[written..]);
        written += LspHeaderSeparator.Length;

        if (
            !Utf8Formatter.TryFormat(
                contentLength,
                destination[written..],
                out var contentLengthBytes
            )
        )
        {
            throw new InvalidOperationException("Failed to write LSP content length.");
        }

        written += contentLengthBytes;
        HeaderEnd.CopyTo(destination[written..]);
        written += HeaderEnd.Length;
        return written;
    }

    private static object? ConvertId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var number) => number,
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    private sealed class LspMessage : IDisposable
    {
        private readonly JsonDocument _document;
        private readonly byte[] _buffer;

        public LspMessage(JsonDocument document, byte[] buffer)
        {
            _document = document;
            _buffer = buffer;
        }

        public JsonElement RootElement => _document.RootElement;

        public void Dispose()
        {
            _document.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
