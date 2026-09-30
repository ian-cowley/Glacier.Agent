namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// In-memory transport for zero-IO testing and intra-process MCP client/server messaging.
/// </summary>
public sealed class InMemoryMcpTransport : IMcpTransport
{
    private readonly ChannelWriter<string> _outgoingWriter;
    private readonly ChannelReader<string> _incomingReader;
    private readonly Action _onClose;
    private bool _isDisposed = false;

    private InMemoryMcpTransport(
        ChannelWriter<string> outgoingWriter,
        ChannelReader<string> incomingReader,
        Action onClose)
    {
        _outgoingWriter = outgoingWriter;
        _incomingReader = incomingReader;
        _onClose = onClose;
    }

    /// <summary>
    /// Creates a connected pair of in-memory transports: one for the client, one for the server.
    /// </summary>
    public static (InMemoryMcpTransport Client, InMemoryMcpTransport Server) CreateConnectedPair()
    {
        var clientToServer = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        var serverToClient = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        void CloseClient()
        {
            clientToServer.Writer.TryComplete();
        }

        void CloseServer()
        {
            serverToClient.Writer.TryComplete();
        }

        var client = new InMemoryMcpTransport(clientToServer.Writer, serverToClient.Reader, CloseClient);
        var server = new InMemoryMcpTransport(serverToClient.Writer, clientToServer.Reader, CloseServer);

        return (client, server);
    }

    public async ValueTask SendMessageAsync(string messageJson, CancellationToken ct = default)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(InMemoryMcpTransport));
        await _outgoingWriter.WriteAsync(messageJson, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<string> ReadMessagesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!_isDisposed && await _incomingReader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_incomingReader.TryRead(out string? msg))
            {
                yield return msg;
            }
        }
    }

    public ValueTask CloseAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _onClose();
        }
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
