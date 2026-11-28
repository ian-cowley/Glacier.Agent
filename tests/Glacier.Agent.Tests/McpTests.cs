namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Mcp;
using Glacier.Agent.Runtime;
using Xunit;

public sealed class McpTests
{
    [Fact]
    public async Task McpClientAndServer_CompleteHandshakeAndToolInvocation()
    {
        var (clientTransport, serverTransport) = InMemoryMcpTransport.CreateConnectedPair();

        await using var server = new McpServer(serverTransport, "test-mcp-server", "1.0.0");
        await using var client = new McpClient(clientTransport);

        // Register tool on server
        var weatherTool = new McpTool("get_weather", "Fetches weather for location", new { type = "object" });
        server.RegisterTool(weatherTool, (args, _) => ValueTask.FromResult(McpToolCallResult.Success("Sunny in Seattle")));

        // Register resource on server
        var configResource = new McpResource("config://app", "App Config", "Application configuration", "text/plain");
        server.RegisterResource(configResource, (uri, _) => ValueTask.FromResult(new McpResourceContent(uri, "text/plain", "mode=production")));

        // Register prompt on server
        var prompt = new McpPrompt("greeting", "Greets user");
        server.RegisterPrompt(prompt, (args, _) =>
        {
            var msgs = new List<McpPromptMessage>
            {
                new("user", new McpContent("text", "Hello " + (args.TryGetValue("name", out var n) ? n : "friend")))
            };
            return ValueTask.FromResult(msgs);
        });

        server.Start();
        client.Start();

        // 1. Initialize Handshake
        var initResult = await client.InitializeAsync("test-client", "1.0.0");
        Assert.Equal("2024-11-05", initResult.ProtocolVersion);
        Assert.Equal("test-mcp-server", initResult.ServerInfo.Name);

        // 2. Ping
        bool pong = await client.PingAsync();
        Assert.True(pong);

        // 3. Tools
        var tools = await client.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("get_weather", tools[0].Name);

        var toolResult = await client.CallToolAsync("get_weather", "{\"city\":\"Seattle\"}");
        Assert.False(toolResult.IsError);
        Assert.Equal("Sunny in Seattle", toolResult.Content[0].Text);

        // 4. Resources
        var resources = await client.ListResourcesAsync();
        Assert.Single(resources);
        Assert.Equal("config://app", resources[0].Uri);

        var resContent = await client.ReadResourceAsync("config://app");
        Assert.Equal("mode=production", resContent.Text);

        // 5. Prompts
        var prompts = await client.ListPromptsAsync();
        Assert.Single(prompts);
        Assert.Equal("greeting", prompts[0].Name);

        var promptMsgs = await client.GetPromptAsync("greeting", new Dictionary<string, string> { ["name"] = "Alice" });
        Assert.Single(promptMsgs);
        Assert.Equal("Hello Alice", promptMsgs[0].Content.Text);

        // 6. Test McpAgentToolAdapter plugged into AgentExecutionEngine
        IAgentTool agentTool = client.AsAgentTool(tools[0]);
        using var engine = new AgentExecutionEngine("mcp-agent");
        engine.RegisterTool(agentTool);

        var response = await engine.ExecuteStepAsync(new AgentContext("sess-1", "CALL get_weather: {}", 1));
        Assert.Equal("Sunny in Seattle", response.Output);
    }

    [Fact]
    public async Task McpClient_CallToolAsync_RejectsMalformedJsonAndCleansPendingOnSendFailure()
    {
        var (clientTransport, serverTransport) = InMemoryMcpTransport.CreateConnectedPair();
        await using var server = new McpServer(serverTransport);
        await using var client = new McpClient(clientTransport);

        server.RegisterTool(new McpTool("echo", "Echo"), (args, _) => ValueTask.FromResult(McpToolCallResult.Success(args)));
        server.Start();
        client.Start();

        await Assert.ThrowsAsync<ArgumentException>(() => client.CallToolAsync("echo", "{malformed json").AsTask());

        // Verify client still operates after invalid argument rejection
        var validResult = await client.CallToolAsync("echo", "{\"k\":1}");
        Assert.False(validResult.IsError);
        Assert.Contains("\"k\":1", validResult.Content[0].Text, StringComparison.Ordinal);

        await using var failingClient = new McpClient(new FailingSendTransport());
        await Assert.ThrowsAsync<IOException>(() => failingClient.CallToolAsync("echo", "{}").AsTask());
    }

    [Fact]
    public async Task McpServer_RegisterAgentTool_UsesJsonElementDefaultAndRejectsInvalidSchema()
    {
        var (clientTransport, serverTransport) = InMemoryMcpTransport.CreateConnectedPair();
        await using var server = new McpServer(serverTransport);
        await using var client = new McpClient(clientTransport);

        server.RegisterTool(new StubAgentTool("empty_schema_tool", ""));
        Assert.Throws<ArgumentException>(() => server.RegisterTool(new StubAgentTool("bad_schema_tool", "{not valid json")));

        server.Start();
        client.Start();

        var tools = await client.ListToolsAsync();
        McpTool listed = Assert.Single(tools);
        Assert.IsType<JsonElement>(listed.InputSchema);
        Assert.Equal("""{"type":"object"}""", listed.GetInputSchemaJson());
    }

    [Fact]
    public async Task McpServer_HandlesConcurrentBurstThroughBoundedChannel()
    {
        var (clientTransport, serverTransport) = InMemoryMcpTransport.CreateConnectedPair();
        await using var server = new McpServer(serverTransport);
        await using var client = new McpClient(clientTransport);

        server.RegisterTool(new McpTool("add_one", "Echo"), (args, _) => ValueTask.FromResult(McpToolCallResult.Success(args)));
        server.Start();
        client.Start();

        var tasks = new List<Task<McpToolCallResult>>(30);
        for (int i = 0; i < 30; i++)
        {
            tasks.Add(client.CallToolAsync("add_one", $"{{\"i\":{i}}}").AsTask());
        }

        McpToolCallResult[] results = await Task.WhenAll(tasks);
        Assert.Equal(30, results.Length);
        Assert.All(results, r => Assert.False(r.IsError));
    }

    private sealed class StubAgentTool(string name, string schema) : IAgentTool
    {
        public string Name => name;
        public string Description => "Stub";
        public string ParameterJsonSchema => schema;
        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
            => ValueTask.FromResult(argumentsJson);
    }

    private sealed class FailingSendTransport : IMcpTransport
    {
        public ValueTask SendMessageAsync(string messageJson, CancellationToken ct = default)
            => ValueTask.FromException(new IOException("Simulated transport failure"));

        public async IAsyncEnumerable<string> ReadMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }

        public ValueTask CloseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}
