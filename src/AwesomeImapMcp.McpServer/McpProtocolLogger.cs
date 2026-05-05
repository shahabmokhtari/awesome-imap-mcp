using Microsoft.Extensions.Logging;

namespace AwesomeImapMcp.McpServer;

/// <summary>
/// Wrapping stream that tees all read/write bytes to a logger.
/// Used to capture raw MCP JSON-RPC protocol traffic when verbose logging is enabled.
/// </summary>
public sealed class McpProtocolLogger(Stream inner, ILogger logger, string direction) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = inner.Read(buffer, offset, count);
        if (bytesRead > 0)
            LogBytes(buffer.AsSpan(offset, bytesRead));
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = await inner.ReadAsync(buffer, offset, count, cancellationToken)
            .ConfigureAwait(false);
        if (bytesRead > 0)
            LogBytes(buffer.AsSpan(offset, bytesRead));
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead > 0)
            LogBytes(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        LogBytes(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        LogBytes(buffer.AsSpan(offset, count));
        await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        LogBytes(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    private void LogBytes(ReadOnlySpan<byte> data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data).TrimEnd();
        if (!string.IsNullOrWhiteSpace(text))
            logger.LogDebug("[MCP.Protocol] {Direction}: {Data}", direction, text);
    }
}
