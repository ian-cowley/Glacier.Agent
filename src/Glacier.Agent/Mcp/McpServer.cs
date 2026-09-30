namespace Glacier.Agent.Mcp;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Native pure C# Model Context Protocol (MCP) server.
/// Streams JSON-RPC 2.0 messages for tools, resources, and prompt templates.
/// </summary>
public sealed class McpServer : IAsyncDisposable, IDisposable
{
    private readonly IMcpTransport _transport;
    private readonly string _name;
    private readonly string _version;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenerTask;
    private bool _isInitialized = false;

    private readonly ConcurrentDictionary<string, (McpTool Tool, Func<string, CancellationToken, ValueTask<McpToolCallResult>> Handler)> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (McpResource Resource, Func<string, CancellationToken, ValueTask<McpResourceContent>> Reader)> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (McpPrompt Prompt, Func<Dictionary<string, string>, CancellationToken, ValueTask<List<McpPromptMessage>>> Handler)> _prompts = new(StringComparer.Ordinal);

    public string Name => _name;
    public string Version => _version;
    public bool IsInitialized => _isInitialized;

    public McpServer(IMcpTransport transport, string name = "glacier-agent-mcp", string version = "1.0.0")
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _name = name;
        _version = version;
    }

    public void RegisterTool(McpTool tool, Func<string, CancellationToken, ValueTask<McpToolCallResult>> handler)
    {
        _tools[tool.Name] = (tool, handler);
    }

    public void RegisterTool(IAgentTool agentTool)
    {
        object schemaObj;
        try
        {
            schemaObj = JsonSerializer.Deserialize<JsonElement>(agentTool.ParameterJsonSchema);
        }
        catch
        {
            schemaObj = new { type = "object" };
        }

        var mcpTool = new McpTool(agentTool.Name, agentTool.Description, schemaObj);
        RegisterTool(mcpTool, async (args, ct) =>
        {
            try
            {
                string result = await agentTool.ExecuteAsync(args, ct).ConfigureAwait(false);
                return McpToolCallResult.Success(result);
            }
            catch (Exception ex)
            {
                return McpToolCallResult.Error(ex.Message);
            }
        });
    }

    public void RegisterResource(McpResource resource, Func<string, CancellationToken, ValueTask<McpResourceContent>> reader)
    {
        _resources[resource.Uri] = (resource, reader);
    }

    public void RegisterPrompt(McpPrompt prompt, Func<Dictionary<string, string>, CancellationToken, ValueTask<List<McpPromptMessage>>> handler)
    {
        _prompts[prompt.Name] = (prompt, handler);
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessMessageAsync(line, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[McpServer] Error processing message: {ex.Message}");
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async ValueTask ProcessMessageAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        // Extract ID (may be number, string, or absent for notifications)
        JsonElement idElement = default;
        bool hasId = root.TryGetProperty("id", out idElement);

        if (!root.TryGetProperty("method", out JsonElement methodElem))
        {
            // Response message received by server, ignore
            return;
        }

        string method = methodElem.GetString() ?? "";
        JsonElement paramsElem = default;
        root.TryGetProperty("params", out paramsElem);

        if (!hasId)
        {
            // Notification
            HandleNotification(method, paramsElem);
            return;
        }

        // Request expecting a response
        try
        {
            object result = await HandleRequestAsync(method, paramsElem, ct).ConfigureAwait(false);
            await SendSuccessResponseAsync(idElement, result, ct).ConfigureAwait(false);
        }
        catch (McpRpcException rpcEx)
        {
            await SendErrorResponseAsync(idElement, rpcEx.Code, rpcEx.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(idElement, -32603, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method == "notifications/initialized")
        {
            _isInitialized = true;
        }
    }

    private async ValueTask<object> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                _isInitialized = true;
                return new McpInitializeResult(
                    ProtocolVersion: "2024-11-05",
                    Capabilities: new McpServerCapabilities(
                        Tools: new { },
                        Resources: new { },
                        Prompts: new { },
                        Logging: new { }),
                    ServerInfo: new McpImplementation(_name, _version));

            case "ping":
                return new { };

            case "tools/list":
            {
                var toolList = new List<McpTool>(_tools.Count);
                foreach (var (_, (tool, _)) in _tools)
                {
                    toolList.Add(tool);
                }
                return new { tools = toolList };
            }

            case "tools/call":
            {
                if (!parameters.TryGetProperty("name", out JsonElement nameElem))
                {
                    throw new McpRpcException(-32602, "Missing 'name' in tools/call parameters.");
                }

                string toolName = nameElem.GetString() ?? "";
                if (!_tools.TryGetValue(toolName, out var entry))
                {
                    throw new McpRpcException(-32601, $"Tool '{toolName}' not found.");
                }

                string argumentsJson = "{}";
                if (parameters.TryGetProperty("arguments", out JsonElement argsElem))
                {
                    argumentsJson = argsElem.GetRawText();
                }

                McpToolCallResult callResult = await entry.Handler(argumentsJson, ct).ConfigureAwait(false);
                return callResult;
            }

            case "resources/list":
            {
                var resourceList = new List<McpResource>(_resources.Count);
                foreach (var (_, (res, _)) in _resources)
                {
                    resourceList.Add(res);
                }
                return new { resources = resourceList };
            }

            case "resources/read":
            {
                if (!parameters.TryGetProperty("uri", out JsonElement uriElem))
                {
                    throw new McpRpcException(-32602, "Missing 'uri' in resources/read parameters.");
                }

                string uri = uriElem.GetString() ?? "";
                if (!_resources.TryGetValue(uri, out var entry))
                {
                    throw new McpRpcException(-32601, $"Resource '{uri}' not found.");
                }

                McpResourceContent content = await entry.Reader(uri, ct).ConfigureAwait(false);
                return new { contents = new[] { content } };
            }

            case "prompts/list":
            {
                var promptList = new List<McpPrompt>(_prompts.Count);
                foreach (var (_, (prompt, _)) in _prompts)
                {
                    promptList.Add(prompt);
                }
                return new { prompts = promptList };
            }

            case "prompts/get":
            {
                if (!parameters.TryGetProperty("name", out JsonElement nameElem))
                {
                    throw new McpRpcException(-32602, "Missing 'name' in prompts/get parameters.");
                }

                string promptName = nameElem.GetString() ?? "";
                if (!_prompts.TryGetValue(promptName, out var entry))
                {
                    throw new McpRpcException(-32601, $"Prompt '{promptName}' not found.");
                }

                var argsDict = new Dictionary<string, string>(StringComparer.Ordinal);
                if (parameters.TryGetProperty("arguments", out JsonElement argsElem) && argsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in argsElem.EnumerateObject())
                    {
                        argsDict[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                List<McpPromptMessage> messages = await entry.Handler(argsDict, ct).ConfigureAwait(false);
                return new { messages };
            }

            default:
                throw new McpRpcException(-32601, $"Unknown method: {method}");
        }
    }

    private async ValueTask SendSuccessResponseAsync(JsonElement id, object result, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result);
            writer.WriteEndObject();
        }

        string responseJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(responseJson, ct).ConfigureAwait(false);
    }

    private async ValueTask SendErrorResponseAsync(JsonElement id, int code, string message, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        string responseJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        await _transport.SendMessageAsync(responseJson, ct).ConfigureAwait(false);
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

public sealed class McpRpcException : Exception
{
    public int Code { get; }

    public McpRpcException(int code, string message) : base(message)
    {
        Code = code;
    }
}
