namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Workflows;
using Glacier.Agent.Tools;
using Glacier.Agent.Llm;
using Glacier.Agent.Mcp;
using Glacier.Agent.Tests.Mocks;
using Microsoft.Extensions.Configuration;
using Xunit;

public partial class AdkRemediationTests
{
    [JsonSerializable(typeof(SearchOrdersArgs))]
    [JsonSerializable(typeof(SearchOrdersResult))]
    internal partial class SampleToolsJsonContext : JsonSerializerContext
    {
    }

    public sealed class SearchOrdersArgs
    {
        [Description("Customer identifier")]
        public required string CustomerId { get; set; }

        [Description("Maximum number of orders to return")]
        public int Limit { get; set; } = 10;

        public string? RegionFilter { get; set; }
    }

    public sealed class SearchOrdersResult
    {
        public int Count { get; set; }
        public string Summary { get; set; } = string.Empty;
    }

    [Fact]
    public async Task FunctionTool_GeneratesAccurateSchemaForTArgs_NeverHardcodesCity()
    {
        var tool = new FunctionTool<AdkToolTests.CalcArgs, AdkToolTests.CalcResult>(
            "add_numbers",
            "Adds two integers",
            args => new AdkToolTests.CalcResult { Sum = args.A + args.B });

        var schema1 = tool.GetParametersSchema().AsObject();
        var schema2 = tool.GetParametersSchema().AsObject();

        // Must be independent clones so attaching to OpenAI/Gemini payload objects never throws parent exceptions
        Assert.NotSame(schema1, schema2);
        Assert.Equal("object", schema1["type"]?.ToString());

        var props = schema1["properties"]!.AsObject();
        Assert.False(props.ContainsKey("city"), "FunctionTool schema must never hardcode 'city'.");
        Assert.True(props.ContainsKey("a"));
        Assert.True(props.ContainsKey("b"));
        Assert.Equal("integer", props["a"]?["type"]?.ToString());
        Assert.Equal("integer", props["b"]?["type"]?.ToString());
        var calcRequired = schema1["required"]!.AsArray();
        Assert.Equal(2, calcRequired.Count);

        var resultJson = await tool.ExecuteAsync("{\"a\": 19, \"b\": 23}");
        var parsed = JsonNode.Parse(resultJson)!;
        Assert.Equal(42, parsed["sum"]!.GetValue<int>());
    }

    [Fact]
    public async Task FunctionTool_AotTypeInfoOverload_IncludesDescriptionsAndRequiredFields()
    {
        var tool = new FunctionTool<SearchOrdersArgs, SearchOrdersResult>(
            "search_orders",
            "Searches customer orders",
            async args =>
            {
                await Task.Yield();
                return new SearchOrdersResult
                {
                    Count = args.Limit,
                    Summary = $"{args.CustomerId}:{args.RegionFilter ?? "ALL"}"
                };
            },
            SampleToolsJsonContext.Default.SearchOrdersArgs,
            SampleToolsJsonContext.Default.SearchOrdersResult);

        var schema = tool.GetParametersSchema().AsObject();
        Assert.Equal("object", schema["type"]?.ToString());

        var props = schema["properties"]!.AsObject();
        Assert.Equal("Customer identifier", props["CustomerId"]?["description"]?.ToString());
        Assert.Equal("Maximum number of orders to return", props["Limit"]?["description"]?.ToString());

        var required = schema["required"]!.AsArray();
        var requiredNames = new HashSet<string>();
        foreach (var item in required)
        {
            if (item is not null)
            {
                requiredNames.Add(item.ToString());
            }
        }

        Assert.Contains("CustomerId", requiredNames);
        Assert.DoesNotContain("Limit", requiredNames);
        Assert.DoesNotContain("RegionFilter", requiredNames);

        var output = await tool.ExecuteAsync("{\"CustomerId\":\"CUST-99\"}");
        var parsed = JsonSerializer.Deserialize(output, SampleToolsJsonContext.Default.SearchOrdersResult);
        Assert.NotNull(parsed);
        Assert.Equal(10, parsed!.Count);
        Assert.Equal("CUST-99:ALL", parsed.Summary);
    }

    [Fact]
    public async Task OpenAiService_StreamGenerateContentAsync_ReassemblesFragmentedToolCallsAndCorrelatesIds()
    {
        var capturedRequestBodies = new List<string>();
        int httpCallCount = 0;

        var handler = new DelegateHttpMessageHandler(async (req, _) =>
        {
            httpCallCount++;
            capturedRequestBodies.Add(await req.Content!.ReadAsStringAsync());

            if (httpCallCount == 1)
            {
                // Simulate OpenAI SSE stream splitting tool call name and JSON arguments across 4 frames for 2 parallel calls
                string sse = string.Join("\n\n", new[]
                {
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_alpha\",\"type\":\"function\",\"function\":{\"name\":\"add_numbers\",\"arguments\":\"\"}}]}}]}",
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"a\\\": 10,\"}}]}}]}",
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\" \\\"b\\\": 32}\"}},{\"index\":1,\"id\":\"call_beta\",\"type\":\"function\",\"function\":{\"name\":\"add_numbers\",\"arguments\":\"{\\\"a\\\": 3, \\\"b\\\": 4}\"}}]}}]}",
                    "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":8}}",
                    "data: [DONE]",
                    ""
                });

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            string finalSse = string.Join("\n\n", new[]
            {
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Sums are \"}}]}",
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"42 and 7.\"}}]}",
                "data: [DONE]",
                ""
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(finalSse, Encoding.UTF8, "text/event-stream")
            };
        });

        using var httpClient = new HttpClient(handler);
        var openAi = new OpenAiService("https://mock.openai.local/v1", apiKey: "test-token", httpClient: httpClient);

        var addTool = new FunctionTool<AdkToolTests.CalcArgs, AdkToolTests.CalcResult>(
            "add_numbers",
            "Adds two integers",
            args => new AdkToolTests.CalcResult { Sum = args.A + args.B });

        var agent = new LlmAgent("MathStreamer", "gpt-4o-mini", "Math helper", "Compute accurately.", new List<ITool> { addTool });

        var streamedChunks = new List<string>();
        await foreach (var chunk in agent.RunStreamingAsync("Add (10,32) and (3,4)", openAi))
        {
            streamedChunks.Add(chunk);
        }

        Assert.Equal("Sums are 42 and 7.", string.Concat(streamedChunks));
        Assert.Equal(2, httpCallCount);

        // Verify second request echoed exact tool_call_ids (call_alpha and call_beta), not duplicate tool names
        var secondPayload = JsonNode.Parse(capturedRequestBodies[1])!.AsObject();
        var messages = secondPayload["messages"]!.AsArray();

        var toolMessages = new List<JsonObject>();
        foreach (var msg in messages)
        {
            if (msg?["role"]?.ToString() == "tool")
            {
                toolMessages.Add(msg.AsObject());
            }
        }

        Assert.Equal(2, toolMessages.Count);
        Assert.Equal("call_alpha", toolMessages[0]["tool_call_id"]?.ToString());
        Assert.Equal("call_beta", toolMessages[1]["tool_call_id"]?.ToString());
    }

    [Fact]
    public async Task GeminiService_StreamGenerateContentAsync_StreamsIncrementalSseWithoutBufferingWholeStream()
    {
        Uri? capturedUri = null;
        string? capturedHeaderKey = null;

        using var serverPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientPipe = new AnonymousPipeClientStream(PipeDirection.In, serverPipe.ClientSafePipeHandle);

        var handler = new DelegateHttpMessageHandler((req, _) =>
        {
            capturedUri = req.RequestUri;
            if (req.Headers.TryGetValues("x-goog-api-key", out var values))
            {
                capturedHeaderKey = string.Join(",", values);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(clientPipe)
            };
            return Task.FromResult(response);
        });

        using var httpClient = new HttpClient(handler);
        var gemini = new GeminiService("fake-test-gemini-header", httpClient, "https://mock.gemini.local/v1beta");

        var request = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            SystemInstruction = LlmContent.Model("Be concise."),
            Contents = { LlmContent.User("Stream hello") }
        };

        using var writer = new StreamWriter(serverPipe, new UTF8Encoding(false)) { AutoFlush = true };
        var firstChunkReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerTask = Task.Run(async () =>
        {
            await writer.WriteLineAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Hello \"}]}}]}");
            await writer.WriteLineAsync();

            // Wait until consumer proves it received chunk 1 BEFORE chunk 2 is even written (proving true non-buffered SSE!)
            await releaseSecondChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await writer.WriteLineAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"World!\"}]}}],\"usageMetadata\":{\"promptTokenCount\":5,\"candidatesTokenCount\":2}}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("data: [DONE]");
            writer.Close();
        });

        var chunks = new List<string>();
        await foreach (var part in gemini.StreamGenerateContentAsync(request))
        {
            if (part.Content is not null)
            {
                chunks.Add(part.Content);
                if (chunks.Count == 1)
                {
                    firstChunkReceived.TrySetResult();
                    releaseSecondChunk.TrySetResult();
                }
            }
        }

        await writerTask;

        Assert.True(firstChunkReceived.Task.IsCompletedSuccessfully);
        Assert.Equal(new[] { "Hello ", "World!" }, chunks);
        Assert.NotNull(capturedUri);
        Assert.Contains("alt=sse", capturedUri!.Query);
        Assert.DoesNotContain("key=", capturedUri.Query);
        Assert.Equal("fake-test-gemini-header", capturedHeaderKey);
    }

    [Fact]
    public async Task SensitiveTool_WhenApprovalServiceMissing_FailsClosedAndNeverExecutesInnerTool()
    {
        bool innerExecuted = false;
        var destructiveTool = new FunctionTool<AdkToolTests.CalcArgs, string>(
            "drop_table",
            "Drops table",
            _ =>
            {
                innerExecuted = true;
                return "DROPPED";
            });

        int turn = 0;
        var mockLlm = new TestLlmService(_ =>
        {
            turn++;
            if (turn == 1)
            {
                return new LlmResponse
                {
                    FunctionCalls = new List<LlmFunctionCall>
                    {
                        new LlmFunctionCall { Id = "call_1", Name = "drop_table", Args = JsonNode.Parse("{\"a\":1,\"b\":2}") }
                    }
                };
            }
            return new LlmResponse { Content = "Operation was denied." };
        });

        // Intentionally leave agent.ApprovalService == null!
        var agent = new LlmAgent("UnconfiguredAgent", "test-model", "Desc", "Inst", new List<ITool> { new SensitiveTool(destructiveTool) });
        var reply = await agent.RunAsync("Drop it", mockLlm);

        Assert.False(innerExecuted, "SensitiveTool MUST NEVER execute when IApprovalService is null.");
        Assert.Equal("Operation was denied.", reply);

        var functionTurn = agent.History[2];
        Assert.Equal("function", functionTurn.Role);
        var responseJson = functionTurn.Parts[0].FunctionResponse!.Response!;
        Assert.Equal("error", responseJson["status"]?.ToString());
        Assert.Contains("IApprovalService", responseJson["message"]?.ToString() ?? string.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SensitiveTool_WithApprovalService_EnforcesHumanDecision(bool approve)
    {
        int executionCount = 0;
        var innerTool = new FunctionTool<AdkToolTests.CalcArgs, string>(
            "transfer_funds",
            "Transfers funds",
            args =>
            {
                executionCount++;
                return $"Transferred {args.A + args.B}";
            });

        int turn = 0;
        var mockLlm = new TestLlmService(_ =>
        {
            turn++;
            if (turn == 1)
            {
                return new LlmResponse
                {
                    FunctionCalls = new List<LlmFunctionCall>
                    {
                        new LlmFunctionCall { Id = "call_tx", Name = "transfer_funds", Args = JsonNode.Parse("{\"a\":100,\"b\":50}") }
                    }
                };
            }
            return new LlmResponse { Content = approve ? "Approved!" : "Rejected!" };
        });

        var agent = new LlmAgent("BankAgent", "test-model", "Desc", "Inst", new List<ITool> { new SensitiveTool(innerTool) })
        {
            ApprovalService = new StubApprovalService(approve)
        };

        var result = await agent.RunAsync("Send 150", mockLlm);

        Assert.Equal(approve ? 1 : 0, executionCount);
        Assert.Equal(approve ? "Approved!" : "Rejected!", result);
    }

    [Fact]
    public async Task BeforeToolCall_ExceptionWithQuotesAndNewlines_ProducesValidErrorJson()
    {
        var tool = new FunctionTool<AdkToolTests.CalcArgs, int>("calc", "Calc", a => a.A + a.B);
        int turn = 0;
        var mockLlm = new TestLlmService(_ =>
        {
            turn++;
            return turn == 1
                ? new LlmResponse
                {
                    FunctionCalls = new List<LlmFunctionCall>
                    {
                        new LlmFunctionCall { Name = "calc", Args = JsonNode.Parse("{\"a\":1,\"b\":2}") }
                    }
                }
                : new LlmResponse { Content = "Handled safely." };
        });

        var agent = new LlmAgent("GuardAgent", "test-model", "Desc", "Inst", new List<ITool> { tool })
        {
            BeforeToolCall = (_, _) => throw new InvalidOperationException("Blocked \"malicious\"\npayload\\path")
        };

        var response = await agent.RunAsync("Run", mockLlm);
        Assert.Equal("Handled safely.", response);

        var fnResp = agent.History[2].Parts[0].FunctionResponse!.Response!;
        Assert.Equal("error", fnResp["status"]?.ToString());
        Assert.Contains("Blocked \"malicious\"", fnResp["message"]?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task McpService_InitializeFromConfigAsync_EmptyConfig_ReturnsEmptyToolsWithoutReflection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        await using var mcp = new McpService();
        var tools = await mcp.InitializeFromConfigAsync(config);
        Assert.Empty(tools);
    }

    private sealed class StubApprovalService : IApprovalService
    {
        private readonly bool _approve;
        public StubApprovalService(bool approve) => _approve = approve;
        public Task<bool> ApproveAsync(string toolName, string arguments) => Task.FromResult(_approve);
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
