namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Native pure C# Model Context Protocol (MCP) client.
/// Connects to MCP servers over stream or memory transports with zero reflection (Native AOT ready).
/// </summary>
public sealed class McpClient : IAsyncDisposable, IDisposable
{
    private readonly IMcpTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private long _nextId;
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

    private async ValueTask<JsonElement> SendRequestAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
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
            if (writeParams != null)
            {
                writer.WritePropertyName("params");
                writeParams(writer);
            }
            writer.WriteEndObject();
        }

        string requestJson = Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(requestJson, ct).ConfigureAwait(false);

        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private async ValueTask SendNotificationAsync(string method, Action<Utf8JsonWriter>? writeParams, CancellationToken ct)
    {
        Start();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (writeParams != null)
            {
                writer.WritePropertyName("params");
                writeParams(writer);
            }
            writer.WriteEndObject();
        }

        string json = Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(json, ct).ConfigureAwait(false);
    }

    public async ValueTask<McpInitializeResult> InitializeAsync(
        string clientName = "glacier-agent-client",
        string clientVersion = "1.1.0",
        CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", "2024-11-05");
            writer.WriteStartObject("capabilities");
            writer.WriteEndObject();
            writer.WriteStartObject("clientInfo");
            writer.WriteString("name", clientName);
            writer.WriteString("version", clientVersion);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        await SendNotificationAsync("notifications/initialized", null, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize(result.GetRawText(), AgentMcpJsonContext.Default.McpInitializeResult)!;
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
        JsonElement result = await SendRequestAsync("tools/list", static w => { w.WriteStartObject(); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("tools", out JsonElement toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
        {
            var list = new List<McpTool>(toolsElem.GetArrayLength());
            foreach (JsonElement elem in toolsElem.EnumerateArray())
            {
                list.Add(McpTool.FromJsonElement(elem));
            }
            return list;
        }
        return Array.Empty<McpTool>();
    }

    public async ValueTask<McpToolCallResult> CallToolAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("tools/call", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WritePropertyName("arguments");
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                doc.RootElement.WriteTo(writer);
            }
            catch
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        return JsonSerializer.Deserialize(result.GetRawText(), AgentMcpJsonContext.Default.McpToolCallResult)!;
    }

    public async ValueTask<IReadOnlyList<McpResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("resources/list", static w => { w.WriteStartObject(); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("resources", out JsonElement resElem))
        {
            return JsonSerializer.Deserialize(resElem.GetRawText(), AgentMcpJsonContext.Default.ListMcpResource) ?? new List<McpResource>();
        }
        return Array.Empty<McpResource>();
    }

    public async ValueTask<McpResourceContent> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("resources/read", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        if (result.TryGetProperty("contents", out JsonElement contentsElem) && contentsElem.GetArrayLength() > 0)
        {
            return JsonSerializer.Deserialize(contentsElem[0].GetRawText(), AgentMcpJsonContext.Default.McpResourceContent)!;
        }
        throw new InvalidOperationException($"No content returned for resource '{uri}'.");
    }

    public async ValueTask<IReadOnlyList<McpPrompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("prompts/list", static w => { w.WriteStartObject(); w.WriteEndObject(); }, ct).ConfigureAwait(false);
        if (result.TryGetProperty("prompts", out JsonElement promptsElem))
        {
            return JsonSerializer.Deserialize(promptsElem.GetRawText(), AgentMcpJsonContext.Default.ListMcpPrompt) ?? new List<McpPrompt>();
        }
        return Array.Empty<McpPrompt>();
    }

    public async ValueTask<IReadOnlyList<McpPromptMessage>> GetPromptAsync(
        string name,
        Dictionary<string, string>? arguments = null,
        CancellationToken ct = default)
    {
        JsonElement result = await SendRequestAsync("prompts/get", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WritePropertyName("arguments");
            JsonSerializer.Serialize(writer, arguments ?? new Dictionary<string, string>(), AgentMcpJsonContext.Default.DictionaryStringString);
            writer.WriteEndObject();
        }, ct).ConfigureAwait(false);

        if (result.TryGetProperty("messages", out JsonElement msgElem))
        {
            return JsonSerializer.Deserialize(msgElem.GetRawText(), AgentMcpJsonContext.Default.ListMcpPromptMessage) ?? new List<McpPromptMessage>();
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
