namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Transport layer for JSON-RPC 2.0 streaming messages.
/// </summary>
public interface IMcpTransport : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Sends a JSON-RPC message string across the transport.
    /// </summary>
    ValueTask SendMessageAsync(string messageJson, CancellationToken ct = default);

    /// <summary>
    /// Sends a UTF-8 encoded JSON-RPC message payload across the transport.
    /// </summary>
    ValueTask SendMessageUtf8Async(ReadOnlyMemory<byte> utf8Json, CancellationToken ct = default)
    {
        return SendMessageAsync(Encoding.UTF8.GetString(utf8Json.Span), ct);
    }

    /// <summary>
    /// Asynchronously enumerates incoming JSON-RPC message strings.
    /// </summary>
    IAsyncEnumerable<string> ReadMessagesAsync(CancellationToken ct = default);

    /// <summary>
    /// Closes the transport.
    /// </summary>
    ValueTask CloseAsync();
}
