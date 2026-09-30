namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Generic;
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
    /// Asynchronously enumerates incoming JSON-RPC message strings.
    /// </summary>
    IAsyncEnumerable<string> ReadMessagesAsync(CancellationToken ct = default);

    /// <summary>
    /// Closes the transport.
    /// </summary>
    ValueTask CloseAsync();
}
