namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Stream-backed MCP transport reading and writing newline-delimited JSON-RPC messages.
/// Suitable for standard I/O (stdio) pipes, NetworkStreams, or named pipes.
/// </summary>
public sealed class StreamMcpTransport : IMcpTransport
{
    private readonly Stream _inputStream;
    private readonly Stream _outputStream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Lock _writeLock = new();
    private bool _isDisposed = false;

    public StreamMcpTransport(Stream stream) : this(stream, stream)
    {
    }

    public StreamMcpTransport(Stream inputStream, Stream outputStream)
    {
        _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
        _outputStream = outputStream ?? throw new ArgumentNullException(nameof(outputStream));
        _reader = new StreamReader(_inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(_outputStream, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public async ValueTask SendMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(StreamMcpTransport));

        byte[] bytes = Encoding.UTF8.GetBytes(messageJson + "\n");
        await _outputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _outputStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadMessagesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!_isDisposed && !ct.IsCancellationRequested)
        {
            string? line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line == null)
            {
                break; // End of stream
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
        }
    }

    public ValueTask CloseAsync()
    {
        _isDisposed = true;
        _reader.Dispose();
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        CloseAsync().GetAwaiter().GetResult();
    }
}
