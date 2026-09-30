namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Native pure C# Model Context Protocol (MCP) client.
/// Connects to MCP servers over stream or memory transports, invoking tools, resources, and prompts.
/// </summary>
public sealed class McpClient : IAsyncDisposable, IDisposable
{
    private readonly IMcpTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private long _nextId = 0;
    private Task? _listenerTask;

    public McpClient(IMcpTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public void Start()
    {
        if (_listenerTask == null)
        {
            _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string line in _transport.ReadMessagesAsync(ct).ConfigureAwait(false))
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement.Clone();

                if (root.TryGetProperty("id", out JsonElement idElem) && idElem.TryGetInt64(out long id))
                {
                    if (_pendingRequests.TryRemove(id, out var tcs))
                    {
                        if (root.TryGetProperty("error", out JsonElement errorElem))
                        {
                            int code = errorElem.GetProperty("code").GetInt32();
                            string msg = errorElem.GetProperty("message").GetString() ?? "Unknown error";
                            tcs.TrySetException(new McpRpcException(code, msg));
                        }
                        else if (root.TryGetProperty("result", out JsonElement resultElem))
                        {
                            tcs.TrySetResult(resultElem);
                        }
                        else
                        {
                            tcs.TrySetResult(root);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            foreach (var (_, tcs) in _pendingRequests)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }

    private async ValueTask<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        Start();

        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (parameters != null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
            }
            writer.WriteEndObject();
        }

        string requestJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(requestJson, ct).ConfigureAwait(false);

        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async ValueTask SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        Start();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters != null)
            {
                writer.WritePropertyName("params");
                JsonSerializer.Serialize(writer, parameters);
            }
            writer.WriteEndObject();
        }

        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(json, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpInitializeResult> InitializeAsync(
        string clientName = "glacier-agent-client",
        string clientVersion = "1.0.0",
        CancellationToken ct = default)
    {
        var parameters = new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = clientName, version = clientVersion }
        };

        JsonElement result = await SendRequestAsync("initialize", parameters, ct).ConfigureAwait(false);
        await SendNotificationAsync("notifications/initialized", null, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize<McpInitializeResult>(result.GetRawText())!;
    }

    public async ValueTask<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await SendRequestAsync("ping", null, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("tools/list", new { }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("tools", out JsonElement toolsElem))
        {
            return JsonSerializer.Deserialize<List<McpTool>>(toolsElem.GetRawText()) ?? new List<McpTool>();
        }
        return Array.Empty<McpTool>();
    }

    public async ValueTask<McpToolCallResult> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        object parsedArgs;
        try
        {
            parsedArgs = JsonSerializer.Deserialize<JsonElement>(argumentsJson);
        }
        catch
        {
            parsedArgs = new { };
        }

        var parameters = new
        {
            name,
            arguments = parsedArgs
        };

        JsonElement result = await SendRequestAsync("tools/call", parameters, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<McpToolCallResult>(result.GetRawText())!;
    }

    public async ValueTask<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("resources/list", new { }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("resources", out JsonElement resElem))
        {
            return JsonSerializer.Deserialize<List<McpResource>>(resElem.GetRawText()) ?? new List<McpResource>();
        }
        return Array.Empty<McpResource>();
    }

    public async ValueTask<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        var parameters = new { uri };
        JsonElement result = await SendRequestAsync("resources/read", parameters, ct).ConfigureAwait(false);
        if (result.TryGetProperty("contents", out JsonElement contentsElem) && contentsElem.GetArrayLength() > 0)
        {
            return JsonSerializer.Deserialize<McpResourceContent>(contentsElem[0].GetRawText())!;
        }
        throw new InvalidOperationException($"No content returned for resource '{uri}'.");
    }

    public async ValueTask<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("prompts/list", new { }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("prompts", out JsonElement promptsElem))
        {
            return JsonSerializer.Deserialize<List<McpPrompt>>(promptsElem.GetRawText()) ?? new List<McpPrompt>();
        }
        return Array.Empty<McpPrompt>();
    }

    public async ValueTask<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
        string name,
        Dictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        var parameters = new
        {
            name,
            arguments = arguments ?? new Dictionary<string, string>()
        };

        JsonElement result = await SendRequestAsync("prompts/get", parameters, ct).ConfigureAwait(false);
        if (result.TryGetProperty("messages", out JsonElement msgElem))
        {
            return JsonSerializer.Deserialize<List<McpPromptMessage>>(msgElem.GetRawText()) ?? new List<McpPromptMessage>();
        }
        return Array.Empty<McpPromptMessage>();
    }

    /// <summary>
    /// Adapts a remote MCP tool to an <see cref="IAgentTool"/> for autonomous agent loops.
    /// </summary>
    public IAgentTool AsAgentTool(McpTool tool)
    {
        return new McpAgentToolAdapter(this, tool);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _transport.CloseAsync().ConfigureAwait(false);
        if (_listenerTask != null)
        {
            try { await _listenerTask.ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }
}
