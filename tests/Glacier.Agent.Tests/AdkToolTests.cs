namespace Glacier.Agent.Tests;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Workflows;
using Glacier.Agent.Tools;
using Glacier.Agent.Llm;
using Glacier.Agent.Mcp;
using Glacier.Agent.Tests.Mocks;
using Xunit;

public class AdkToolTests
{
    [Fact]
    public async Task FunctionTool_ExecutesTypedLambdaAndSerializesResult()
    {
        var tool = new FunctionTool<CalcArgs, CalcResult>(
            "add_numbers",
            "Adds two integers",
            args => new CalcResult { Sum = args.A + args.B });

        Assert.Equal("add_numbers", tool.Name);
        Assert.Equal("Adds two integers", tool.Description);
        Assert.NotNull(tool.GetParametersSchema());

        string jsonArgs = JsonSerializer.Serialize(new CalcArgs { A = 14, B = 28 });
        string jsonResult = await tool.ExecuteAsync(jsonArgs);

        var result = JsonSerializer.Deserialize<CalcResult>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal(42, result.Sum);
    }

    [Fact]
    public async Task SensitiveTool_WrapsInnerToolCorrectly()
    {
        var inner = new FunctionTool<CalcArgs, CalcResult>(
            "secure_action",
            "Sensitive calculation",
            args => new CalcResult { Sum = args.A * args.B });

        var sensitive = new SensitiveTool(inner);

        Assert.Equal("secure_action", sensitive.Name);
        Assert.Equal("Sensitive calculation", sensitive.Description);

        string jsonArgs = JsonSerializer.Serialize(new CalcArgs { A = 6, B = 7 });
        string jsonResult = await sensitive.ExecuteAsync(jsonArgs);

        var result = JsonSerializer.Deserialize<CalcResult>(jsonResult, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(result);
        Assert.Equal(42, result.Sum);
    }

    [Fact]
    public async Task DelegationTool_DelegatesPromptToTargetAgent()
    {
        var mock = new TestLlmService("Delegated Result from Specialist");
        var worker = new LlmAgent("Specialist", "specialist-model", "Expert agent", "Solve expert tasks", new());

        var delegationTool = new DelegationTool(worker, mock);
        Assert.Equal("Specialist", delegationTool.Name);

        var response = await delegationTool.ExecuteAsync("{\"prompt\": \"Analyze market trends\"}");

        Assert.Equal("Delegated Result from Specialist", response);
    }

    [Fact]
    public async Task McpServiceAndTool_WorkWithGlacierAgentMcp()
    {
        var (clientTransport, serverTransport) = Glacier.Agent.Mcp.InMemoryMcpTransport.CreateConnectedPair();
        await using var server = new Glacier.Agent.Mcp.McpServer(serverTransport, "test-server", "1.0.0");
        server.RegisterTool(
            new Glacier.Agent.Mcp.McpTool("greet", "Says hello", new { type = "object" }),
            (args, ct) => ValueTask.FromResult(Glacier.Agent.Mcp.McpToolCallResult.Success($"Hello, {args}!")));
        server.Start();

        var client = new Glacier.Agent.Mcp.McpClient(clientTransport);
        await client.InitializeAsync("test-client", "1.0.0");

        await using var service = new McpService();
        var tools = await service.RegisterClientAsync(client);

        Assert.Single(tools);
        var tool = tools[0];
        Assert.Equal("greet", tool.Name);
        Assert.Equal("Says hello", tool.Description);

        var result = await tool.ExecuteAsync("{\"name\": \"Glacier\"}");
        Assert.Contains("Hello", result);
    }

    public class CalcArgs
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    public class CalcResult
    {
        public int Sum { get; set; }
    }
}
