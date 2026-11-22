namespace Glacier.Agent.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Runtime;
using Glacier.MemEx;
using Xunit;

public sealed class Tier5M7Phase2HardeningTests
{
    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
        public List<Uri?> CapturedUris { get; } = new();
        public bool Disposed { get; private set; }

        public TrackingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (CapturedUris) CapturedUris.Add(request.RequestUri);
            return _responder(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonDisposingClient : HttpClient
    {
        public bool WasDisposed { get; private set; }
        public NonDisposingClient(HttpMessageHandler handler) : base(handler, disposeHandler: false) { }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class SingleThreadSyncContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public void RunUntil(Task t)
        {
            t.ContinueWith(_ => _queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (cb, st) in _queue.GetConsumingEnumerable()) cb(st);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("http://127.0.0.1:52625/v1?api-version=1", "http://127.0.0.1:52625/v1/chat/completions?api-version=1", "http://127.0.0.1:52625/v1/models?api-version=1")]
    [InlineData("http://127.0.0.1:52625?api-version=1", "http://127.0.0.1:52625/v1/chat/completions?api-version=1", "http://127.0.0.1:52625/v1/models?api-version=1")]
    [InlineData("HTTP://127.0.0.1:52625/V1/", "http://127.0.0.1:52625/v1/chat/completions", "http://127.0.0.1:52625/v1/models")]
    public async Task OpenAiService_QueryStringAndFragmentEndpoints_PreservesQueryAfterV1Normalization(
        string baseUrl,
        string expectedChatUrl,
        string expectedModelsUrl)
    {
        using var handler = new TrackingHandler((req, _) =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"qwen3-4b"}]}"""));
            return Task.FromResult(Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"}}]}"""));
        });
        using var http = new HttpClient(handler);
        using var svc = new OpenAiService(baseUrl, httpClient: http);

        var resp = await svc.GenerateContentAsync(new LlmRequest
        {
            Model = "qwen3-4b",
            Contents = { LlmContent.User("hello") }
        });
        Assert.Equal("ok", resp.Content);

        var discovered = await OpenAiService.DiscoverNpuAsync(
            httpClient: http,
            candidateEndpoints: [baseUrl]);
        Assert.NotNull(discovered);
        Assert.Contains("qwen3-4b", discovered!.Models);

        Assert.Equal(expectedChatUrl, handler.CapturedUris[0]?.ToString());
        Assert.Equal(expectedModelsUrl, handler.CapturedUris[1]?.ToString());
    }

    [Fact]
    public async Task OpenAiService_NonObjectJsonAndMalformedUsage_FailsGracefullyWithoutInvalidOperationCrash()
    {
        string[] oddPayloads =
        [
            "[1,2,3]",
            "\"scalar\"",
            "null",
            """{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":"ten","completion_tokens":null,"total_tokens":{}}}"""
        ];

        int idx = 0;
        using var handler = new TrackingHandler((req, _) =>
        {
            int cur = Interlocked.Increment(ref idx) - 1;
            string payload = oddPayloads[cur % oddPayloads.Length];
            return Task.FromResult(Json(HttpStatusCode.OK, payload));
        });
        using var http = new HttpClient(handler);
        using var svc = new OpenAiService("http://127.0.0.1:52625", httpClient: http);

        for (int i = 0; i < 4; i++)
        {
            var r = await svc.GenerateContentAsync(new LlmRequest
            {
                Model = "qwen3-4b",
                Contents = { LlmContent.User("test") }
            });
            Assert.NotNull(r);
        }

        // SSE stream containing non-object JSON event "data: [1,2,3]" before valid content + [DONE]
        using var sseHandler = new TrackingHandler((_, _) =>
        {
            string sse =
                "data: [1,2,3]\n\n" +
                "data: \"scalar\"\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"safe\"}}]}\n\n" +
                "data: [DONE]\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            });
        });
        using var sseHttp = new HttpClient(sseHandler);
        using var sseSvc = new OpenAiService("http://127.0.0.1:52625", httpClient: sseHttp);

        var chunks = new List<string>();
        await foreach (var c in sseSvc.StreamGenerateContentAsync(new LlmRequest { Model = "m", Contents = { LlmContent.User("s") } }))
        {
            if (!string.IsNullOrEmpty(c.Content)) chunks.Add(c.Content);
        }
        Assert.Equal("safe", string.Concat(chunks));
    }

    [Fact]
    public void OpenAiService_OwnedHttpClient_ImplementsDisposableAndReleasesClient()
    {
        var ownedSvc = new OpenAiService("http://127.0.0.1:52625");
        ownedSvc.Dispose();
        ownedSvc.Dispose(); // idempotent

        using var handler = new TrackingHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var externalClient = new NonDisposingClient(handler);
        var borrowedSvc = new OpenAiService("http://127.0.0.1:52625", httpClient: externalClient);
        borrowedSvc.Dispose();
        Assert.False(externalClient.WasDisposed);
    }

    [Fact]
    public async Task LlmServiceChatModel_MultipleToolCallsAndErrorTool_PreservesAllCallsAndSurfacesError()
    {
        int mode = 0;
        using var handler = new TrackingHandler((_, _) =>
        {
            if (Volatile.Read(ref mode) == 0)
            {
                string multiToolJson = """
                    {
                      "choices": [{
                        "message": {
                          "role": "assistant",
                          "content": null,
                          "tool_calls": [
                            {"id": "c1", "type": "function", "function": {"name": "alpha", "arguments": "{\"x\":1}"}},
                            {"id": "c2", "type": "function", "function": {"name": "beta", "arguments": "{\"y\":2}"}}
                          ]
                        }
                      }]
                    }
                    """;
                return Task.FromResult(Json(HttpStatusCode.OK, multiToolJson));
            }

            string errorToolJson = """
                {
                  "choices": [{
                    "message": {
                      "role": "assistant",
                      "content": "",
                      "tool_calls": [
                        {"id": "c_err", "type": "function", "function": {"name": "error", "arguments": "{}"}}
                      ]
                    }
                  }]
                }
                """;
            return Task.FromResult(Json(HttpStatusCode.OK, errorToolJson));
        });
        using var http = new HttpClient(handler);
        using var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: http);
        var model = new LlmServiceChatModel(openAi, "qwen3-4b");

        string multiOut = await model.CompleteAsync("sys", "call both");
        using var multiDoc = JsonDocument.Parse(multiOut);
        Assert.Equal("alpha", multiDoc.RootElement.GetProperty("tool").GetString());
        Assert.Equal(2, multiDoc.RootElement.GetProperty("tool_calls").GetArrayLength());
        Assert.Equal("beta", multiDoc.RootElement.GetProperty("tool_calls")[1].GetProperty("tool").GetString());

        Volatile.Write(ref mode, 1);
        string errOut = await model.CompleteAsync("sys", "trigger err");
        Assert.DoesNotContain("\"tool\":\"error\"", errOut, StringComparison.Ordinal);
        Assert.Contains("error", errOut, StringComparison.OrdinalIgnoreCase);

        // Non-object JSON arguments on "error" tool (e.g. "[1,2,3]") must not throw InvalidOperationException
        using var arrHandler = new TrackingHandler((_, _) =>
        {
            string arrErrorToolJson = """
                {
                  "choices": [{
                    "message": {
                      "role": "assistant",
                      "content": "",
                      "tool_calls": [
                        {"id": "c_arr", "type": "function", "function": {"name": "error", "arguments": "[1,2,3]"}}
                      ]
                    }
                  }]
                }
                """;
            return Task.FromResult(Json(HttpStatusCode.OK, arrErrorToolJson));
        });
        using var arrHttp = new HttpClient(arrHandler);
        using var arrOpenAi = new OpenAiService("http://127.0.0.1:52625", httpClient: arrHttp);
        var arrModel = new LlmServiceChatModel(arrOpenAi, "qwen3-4b");
        string arrOut = await arrModel.CompleteAsync("sys", "trigger array err");
        Assert.Equal("[1,2,3]", arrOut);
    }

    [Fact]
    public async Task AgentExecutionEngine_MarkdownFencedJsonToolCall_ExecutesTool()
    {
        int step = 0;
        using var handler = new TrackingHandler((_, _) =>
        {
            int s = Interlocked.Increment(ref step);
            string reply = s == 1
                ? "```json\n{\"tool\":\"ping\",\"arguments\":{\"msg\":\"hi\"}}\n```"
                : "FINAL: tool executed";
            return Task.FromResult(Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply } } } })));
        });
        using var http = new HttpClient(handler);
        using var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: http);
        var chat = new LlmServiceChatModel(openAi, "qwen3-4b");

        using var engine = new AgentExecutionEngine("fence-agent", chat);
        bool invoked = false;
        engine.RegisterTool(DelegateAgentTool.Create("ping", "Ping", "{\"type\":\"object\"}", args =>
        {
            invoked = true;
            return "pong:" + args;
        }));

        var res = await engine.ExecuteLoopAsync("fence-sess", "run ping", maxSteps: 3);
        Assert.True(invoked);
        Assert.True(res.IsCompleted);
        Assert.Equal("FINAL: tool executed", res.Output);
    }

    [Fact]
    public void AgentExecutionEngine_ConcurrentLoopsSameSessionId_AndAsyncMemory_NoRaceOrDeadlock()
    {
        using var handler = new TrackingHandler(async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"FINAL: concurrent ok"}}]}""");
        });
        using var http = new HttpClient(handler);
        using var openAi = new OpenAiService("http://127.0.0.1:52625", httpClient: http);
        var chat = new LlmServiceChatModel(openAi, "qwen3-4b");
        using var memEx = new MemExEngine();
        using var engine = new AgentExecutionEngine("race-agent", chat);
        engine.AttachEpisodicMemory(memEx);

        var syncCtx = new SingleThreadSyncContext();
        Exception? failure = null;
        var driver = Task.Factory.StartNew(() =>
        {
            SynchronizationContext.SetSynchronizationContext(syncCtx);
            var runnerTask = Task.Run(async () =>
            {
                var loops = Enumerable.Range(0, 8)
                    .Select(i => engine.ExecuteLoopAsync("shared-session", $"task {i}", maxSteps: 2).AsTask())
                    .ToArray();
                var results = await Task.WhenAll(loops);
                Assert.All(results, r => Assert.True(r.IsCompleted));
            });
            try
            {
                syncCtx.RunUntil(runnerTask);
                runnerTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        driver.Wait();
        Assert.Null(failure);
    }
}
