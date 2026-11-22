namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Xunit;

public sealed class OpenAiServiceAndChatModelTests
{
    private sealed class StubAgentHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> _responder;
        public List<(Uri Uri, HttpMethod Method, string Body, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];

        public StubAgentHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;

            Requests.Add((request.RequestUri!, request.Method, body, request.Headers.Authorization));
            return await _responder(request, body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage ChatCompletionJson(string content)
    {
        string escaped = JsonSerializer.Serialize(content);
        string payload = $$"""
            {
              "id": "chatcmpl-1",
              "object": "chat.completion",
              "choices": [
                {
                  "index": 0,
                  "message": { "role": "assistant", "content": {{escaped}} }
                }
              ],
              "usage": { "prompt_tokens": 12, "completion_tokens": 5 }
            }
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage ChatToolCallJson(string toolName, string argumentsJson)
    {
        string escapedName = JsonSerializer.Serialize(toolName);
        string escapedArgs = JsonSerializer.Serialize(argumentsJson);
        string payload = $$"""
            {
              "id": "chatcmpl-2",
              "object": "chat.completion",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": {{escapedName}},
                          "arguments": {{escapedArgs}}
                        }
                      }
                    ]
                  }
                }
              ]
            }
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    [Theory]
    [InlineData("http://127.0.0.1:52625", "http://127.0.0.1:52625/v1", "http://127.0.0.1:52625/v1/chat/completions")]
    [InlineData("http://127.0.0.1:52625/", "http://127.0.0.1:52625/v1", "http://127.0.0.1:52625/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://127.0.0.1:52625/v1/", "http://127.0.0.1:52625/v1", "http://127.0.0.1:52625/v1/chat/completions")]
    [InlineData("http://127.0.0.1:52625/V1/", "http://127.0.0.1:52625/v1", "http://127.0.0.1:52625/v1/chat/completions")]
    [InlineData("https://proxy.local/openai/v1", "https://proxy.local/openai/v1", "https://proxy.local/openai/v1/chat/completions")]
    [InlineData("https://proxy.local/api/v1/", "https://proxy.local/api/v1", "https://proxy.local/api/v1/chat/completions")]
    [InlineData("http://127.0.0.1:52625/v1beta", "http://127.0.0.1:52625/v1beta/v1", "http://127.0.0.1:52625/v1beta/v1/chat/completions")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "https://generativelanguage.googleapis.com/v1beta/openai", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions")]
    public async Task OpenAiService_NormalizesBaseUrl_ForGenerateAndStream(
        string inputBaseUrl,
        string expectedBaseUrl,
        string expectedCompletionsUrl)
    {
        using var handler = new StubAgentHandler((_, body, _) =>
        {
            using var doc = JsonDocument.Parse(body);
            bool stream = doc.RootElement.GetProperty("stream").GetBoolean();
            if (!stream)
            {
                return Task.FromResult(ChatCompletionJson("ok"));
            }

            string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        });
        using var client = new HttpClient(handler);

        var service = new OpenAiService(inputBaseUrl, httpClient: client);
        Assert.Equal(expectedBaseUrl, service.BaseUrl);

        var req = new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("ping") }
        };

        LlmResponse res = await service.GenerateContentAsync(req);
        Assert.Equal("ok", res.Content);

        var chunks = new List<string>();
        await foreach (var chunk in service.StreamGenerateContentAsync(req))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
                chunks.Add(chunk.Content);
        }
        Assert.Equal(["hello"], chunks);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(expectedCompletionsUrl, handler.Requests[0].Uri.ToString());
        Assert.Equal(expectedCompletionsUrl, handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public async Task OpenAiService_EmitsTemperatureAndMaxTokens_OnlyWhenSpecified()
    {
        using var handler = new StubAgentHandler((_, _, _) =>
            Task.FromResult(ChatCompletionJson("ok")));
        using var client = new HttpClient(handler);

        var service = new OpenAiService("http://127.0.0.1:52625", httpClient: client);

        await service.GenerateContentAsync(new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("default params") }
        });

        await service.GenerateContentAsync(new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("custom params") },
            Temperature = 0.25f,
            MaxTokens = 512
        });

        using var doc1 = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.False(doc1.RootElement.TryGetProperty("temperature", out _));
        Assert.False(doc1.RootElement.TryGetProperty("max_tokens", out _));

        using var doc2 = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal(0.25, doc2.RootElement.GetProperty("temperature").GetDouble(), precision: 2);
        Assert.Equal(512, doc2.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task OpenAiService_DiscoverNpuAsync_PreservesPriorityAndRejectsInvalidPayloads()
    {
        using var handler = new StubAgentHandler(async (req, _, ct) =>
        {
            string uri = req.RequestUri!.ToString();
            if (uri.StartsWith("http://127.0.0.1:59201", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>Not OpenAI</html>", Encoding.UTF8, "text/html")
                };
            }
            if (uri.StartsWith("http://127.0.0.1:59202", StringComparison.Ordinal))
            {
                await Task.Delay(35, ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"qwen3-8b\"},{\"id\":\"embeddinggemma\"}]}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"fallback-model\"}]}", Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);

        OpenAiNpuDiscoveryResult? discovered = await OpenAiService.DiscoverNpuAsync(
            httpClient: client,
            candidateEndpoints:
            [
                "http://127.0.0.1:59201",
                "http://127.0.0.1:59202/",
                "http://127.0.0.1:59203/v1"
            ],
            perEndpointTimeout: TimeSpan.FromSeconds(2));

        Assert.NotNull(discovered);
        Assert.Equal("http://127.0.0.1:59202/v1", discovered.BaseUrl);
        Assert.Equal(["qwen3-8b", "embeddinggemma"], discovered.Models);
        Assert.Equal("qwen3-8b", discovered.PrimaryModel);
    }

    [Fact]
    public async Task OpenAiService_DiscoverNpuAsync_AllOffline_ReturnsNull()
    {
        using var handler = new StubAgentHandler((_, _, _) =>
            throw new HttpRequestException("Connection refused"));
        using var client = new HttpClient(handler);

        OpenAiNpuDiscoveryResult? discovered = await OpenAiService.DiscoverNpuAsync(
            httpClient: client,
            candidateEndpoints: ["http://127.0.0.1:59211", "http://127.0.0.1:59212"],
            perEndpointTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Null(discovered);
    }

    [Fact]
    public async Task OpenAiService_CreateNpuServiceAsync_ConstructsServiceWiredToNormalizedBaseUrl()
    {
        using var handler = new StubAgentHandler((req, _, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"phi4-npu\"}]}", Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(ChatCompletionJson("hello from npu"));
        });
        using var client = new HttpClient(handler);

        var created = await OpenAiService.CreateNpuServiceAsync(
            httpClient: client,
            candidateEndpoints: ["http://127.0.0.1:59221"]);

        Assert.NotNull(created);
        Assert.Equal("phi4-npu", created.Value.PrimaryModel);
        Assert.Equal("http://127.0.0.1:59221/v1", created.Value.Service.BaseUrl);

        LlmResponse reply = await created.Value.Service.GenerateContentAsync(new LlmRequest
        {
            Model = created.Value.PrimaryModel!,
            Contents = { LlmContent.User("hi") }
        });
        Assert.Equal("hello from npu", reply.Content);
        Assert.Equal("http://127.0.0.1:59221/v1/chat/completions", handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public void LlmServiceChatModel_ValidatesConstructorArguments()
    {
        var svc = new OpenAiService("http://127.0.0.1:52625");

        Assert.Throws<ArgumentNullException>(() => new LlmServiceChatModel(null!, "model"));
        Assert.ThrowsAny<ArgumentException>(() => new LlmServiceChatModel(svc, "  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmServiceChatModel(svc, "model", temperature: -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmServiceChatModel(svc, "model", temperature: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmServiceChatModel(svc, "model", maxTokens: 0));
    }

    [Fact]
    public async Task LlmServiceChatModel_CompleteAsync_ForwardsSystemUserTemperatureAndMaxTokens()
    {
        using var handler = new StubAgentHandler((_, _, _) =>
            Task.FromResult(ChatCompletionJson("answer")));
        using var client = new HttpClient(handler);

        var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: client);
        var chatModel = new LlmServiceChatModel(openAi, "qwen3-8b", temperature: 0.2f, maxTokens: 256);

        string result = await chatModel.CompleteAsync("Be concise.", "What is 2+2?");
        Assert.Equal("answer", result);

        Assert.Single(handler.Requests);
        using var doc = JsonDocument.Parse(handler.Requests[0].Body);
        var root = doc.RootElement;
        Assert.Equal("qwen3-8b", root.GetProperty("model").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble(), precision: 2);
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Be concise.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("What is 2+2?", messages[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LlmServiceChatModel_WithAgentExecutionEngine_ExecutesMultiStepToolLoop(bool useNativeToolCall)
    {
        int turn = 0;
        using var handler = new StubAgentHandler((_, body, _) =>
        {
            turn++;
            if (turn == 1)
            {
                if (useNativeToolCall)
                {
                    return Task.FromResult(ChatToolCallJson("clock", "{\"tz\":\"UTC\"}"));
                }
                return Task.FromResult(ChatCompletionJson("{\"tool\":\"clock\",\"arguments\":{\"tz\":\"UTC\"}}"));
            }

            Assert.Contains("12:00Z", body, StringComparison.Ordinal);
            return Task.FromResult(ChatCompletionJson("The UTC time is 12:00Z."));
        });
        using var client = new HttpClient(handler);

        var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: client);
        var chatModel = new LlmServiceChatModel(openAi, "qwen3-8b", temperature: 0.0f, maxTokens: 128);

        using var engine = new AgentExecutionEngine("npu-agent", chatModel);
        bool toolInvoked = false;
        engine.RegisterTool(DelegateAgentTool.Create(
            "clock",
            "Returns the current time.",
            "{\"type\":\"object\",\"properties\":{\"tz\":{\"type\":\"string\"}}}",
            args =>
            {
                toolInvoked = true;
                Assert.Contains("UTC", args, StringComparison.Ordinal);
                return "12:00Z";
            }));

        AgentResponse response = await engine.ExecuteLoopAsync("session-1", "Check the clock.", maxSteps: 4);

        Assert.True(toolInvoked);
        Assert.True(response.IsCompleted);
        Assert.Equal("The UTC time is 12:00Z.", response.Output);
        Assert.Equal(2, turn);
    }

    [Fact]
    public async Task LlmServiceChatModel_HandlesEmptyContentMalformedJsonAndCancellation()
    {
        int call = 0;
        using var handler = new StubAgentHandler((_, _, _) =>
        {
            call++;
            if (call == 1)
            {
                return Task.FromResult(ChatCompletionJson(string.Empty));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);

        var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: client);
        var chatModel = new LlmServiceChatModel(openAi, "qwen3-8b");

        string empty = await chatModel.CompleteAsync("sys", "usr");
        Assert.Equal(string.Empty, empty);

        string malformedError = await chatModel.CompleteAsync("sys", "usr");
        Assert.Contains("malformed JSON", malformedError, StringComparison.OrdinalIgnoreCase);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await chatModel.CompleteAsync("sys", "usr", cts.Token));
    }

    [Fact]
    public async Task OpenAiService_DiscoverNpuAsync_RealHttpClient_SkipsNonHttpSchemesAndFallsBackCleanly()
    {
        string[] invalidOnly =
        [
            "ftp://127.0.0.1:52625",
            "localhost:52625",
            "htt://127.0.0.1:52625",
            "file:///C:/nonexistent"
        ];

        OpenAiNpuDiscoveryResult? nullResult = await OpenAiService.DiscoverNpuAsync(
            httpClient: null,
            candidateEndpoints: invalidOnly,
            perEndpointTimeout: TimeSpan.FromMilliseconds(300));
        Assert.Null(nullResult);

        using var explicitRealClient = new HttpClient();
        OpenAiNpuDiscoveryResult? explicitResult = await OpenAiService.DiscoverNpuAsync(
            httpClient: explicitRealClient,
            candidateEndpoints: invalidOnly,
            perEndpointTimeout: TimeSpan.FromMilliseconds(300));
        Assert.Null(explicitResult);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = ServeSingleModelsResponseAsync(listener, "{\"data\":[{\"id\":\"qwen3-fallback\"}]}", serverCts.Token);

        OpenAiNpuDiscoveryResult? fallbackResult = await OpenAiService.DiscoverNpuAsync(
            httpClient: null,
            candidateEndpoints:
            [
                "ftp://127.0.0.1:52625",
                "localhost:52625",
                "htt://127.0.0.1:52625",
                "file:///C:/nonexistent",
                $"http://127.0.0.1:{port}"
            ],
            perEndpointTimeout: TimeSpan.FromSeconds(2));

        await serverTask;

        Assert.NotNull(fallbackResult);
        Assert.Equal($"http://127.0.0.1:{port}/v1", fallbackResult.BaseUrl);
        Assert.Equal("qwen3-fallback", fallbackResult.PrimaryModel);
    }

    [Theory]
    [InlineData("Let me check the clock tool.")]
    [InlineData("\n")]
    public async Task LlmServiceChatModel_WithContentAndToolCalls_PrioritizesToolCallAndCompletesLoop(string assistantContent)
    {
        int turn = 0;
        using var handler = new StubAgentHandler((_, body, _) =>
        {
            turn++;
            if (turn == 1)
            {
                string escapedContent = JsonSerializer.Serialize(assistantContent);
                string payload = $$"""
                    {
                      "id": "chatcmpl-both",
                      "object": "chat.completion",
                      "choices": [
                        {
                          "index": 0,
                          "message": {
                            "role": "assistant",
                            "content": {{escapedContent}},
                            "tool_calls": [
                              {
                                "id": "call_both_1",
                                "type": "function",
                                "function": {
                                  "name": "clock",
                                  "arguments": "{\"tz\":\"UTC\"}"
                                }
                              }
                            ]
                          }
                        }
                      ]
                    }
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                });
            }

            Assert.Contains("12:34Z", body, StringComparison.Ordinal);
            return Task.FromResult(ChatCompletionJson("The UTC time is 12:34Z."));
        });
        using var client = new HttpClient(handler);

        var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: client);
        var chatModel = new LlmServiceChatModel(openAi, "qwen3-8b");

        using var engine = new AgentExecutionEngine("npu-agent", chatModel);
        bool toolInvoked = false;
        engine.RegisterTool(DelegateAgentTool.Create(
            "clock",
            "Returns the current time.",
            "{\"type\":\"object\",\"properties\":{\"tz\":{\"type\":\"string\"}}}",
            args =>
            {
                toolInvoked = true;
                Assert.Contains("UTC", args, StringComparison.Ordinal);
                return "12:34Z";
            }));

        AgentResponse response = await engine.ExecuteLoopAsync("session-both", "Check the clock.", maxSteps: 4);

        Assert.True(toolInvoked);
        Assert.True(response.IsCompleted);
        Assert.Equal("The UTC time is 12:34Z.", response.Output);
        Assert.Equal(2, turn);
    }

    [Fact]
    public async Task OpenAiService_GenerateContentAsync_HandlesEmptyChoicesAndNonArrayToolCalls()
    {
        int call = 0;
        using var handler = new StubAgentHandler((_, _, _) =>
        {
            call++;
            string payload = call == 1
                ? """{"id":"chatcmpl-empty","choices":[]}"""
                : """{"id":"chatcmpl-nontool","choices":[{"index":0,"message":{"role":"assistant","content":"ok","tool_calls":{}}}]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        var service = new OpenAiService("http://127.0.0.1:52625", httpClient: client);

        LlmResponse emptyRes = await service.GenerateContentAsync(new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("hello") }
        });
        Assert.Null(emptyRes.Content);
        Assert.Null(emptyRes.FunctionCalls);

        LlmResponse nonArrayRes = await service.GenerateContentAsync(new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("hello") }
        });
        Assert.Equal("ok", nonArrayRes.Content);
        Assert.Null(nonArrayRes.FunctionCalls);
    }

    [Fact]
    public async Task OpenAiService_SharedHttpClient_SendsPerInstanceAuthWithoutMutatingDefaultHeaders()
    {
        using var handler = new StubAgentHandler((_, _, _) =>
            Task.FromResult(ChatCompletionJson("ok")));
        using var sharedClient = new HttpClient(handler);

        var svc1 = new OpenAiService("http://127.0.0.1:52625", apiKey: "secret-1", httpClient: sharedClient);
        var svc2 = new OpenAiService("http://127.0.0.1:52625", apiKey: "secret-2", httpClient: sharedClient);
        var svc3 = new OpenAiService("http://127.0.0.1:52625", apiKey: null, httpClient: sharedClient);

        Assert.Null(sharedClient.DefaultRequestHeaders.Authorization);

        var req = new LlmRequest
        {
            Model = "qwen3-8b",
            Contents = { LlmContent.User("ping") }
        };

        await svc1.GenerateContentAsync(req);
        await svc2.GenerateContentAsync(req);
        await svc3.GenerateContentAsync(req);

        Assert.Null(sharedClient.DefaultRequestHeaders.Authorization);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("Bearer", handler.Requests[0].Authorization?.Scheme);
        Assert.Equal("secret-1", handler.Requests[0].Authorization?.Parameter);
        Assert.Equal("Bearer", handler.Requests[1].Authorization?.Scheme);
        Assert.Equal("secret-2", handler.Requests[1].Authorization?.Parameter);
        Assert.Null(handler.Requests[2].Authorization);
    }

    private static async Task ServeSingleModelsResponseAsync(TcpListener listener, string jsonBody, CancellationToken ct)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        var buffer = new byte[2048];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
            if (Encoding.ASCII.GetString(buffer, 0, totalRead).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }

        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        string headers = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
