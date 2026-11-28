namespace Glacier.Agent.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Agent.Llm;
using Glacier.Agent.Tools;
using Xunit;

public sealed class GeminiServiceTests
{
    private sealed class StubGeminiHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;
        public bool WasDisposed { get; private set; }
        public List<(Uri Uri, string Body, string? ApiKeyHeader)> Requests { get; } = new();

        public StubGeminiHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : string.Empty;
            string? keyHeader = request.Headers.TryGetValues("x-goog-api-key", out var vals)
                ? string.Join(",", vals)
                : null;
            Requests.Add((request.RequestUri!, body, keyHeader));
            return _responder(request, body);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task GenerateContentAsync_And_StreamGenerateContentAsync_RoundtripWithSourceGenDtos()
    {
        using var handler = new StubGeminiHandler((req, _) =>
        {
            if (req.RequestUri!.AbsoluteUri.Contains("streamGenerateContent", StringComparison.Ordinal))
            {
                string sse =
                    "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Part1 \"},{\"functionCall\":{\"name\":\"ping\",\"args\":{\"n\":7}}}]}}],\"usageMetadata\":{\"promptTokenCount\":4,\"candidatesTokenCount\":3}}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }

            string restJson = """
                {
                  "candidates": [
                    { "content": { "parts": [ { "text": "Hello from Gemini" } ] } }
                  ],
                  "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 5 }
                }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(restJson, Encoding.UTF8, "application/json")
            };
        });

        using var sharedClient = new HttpClient(handler, disposeHandler: false);
        var service = new GeminiService("test-gemini-key", sharedClient, "https://example.invalid/v1beta");

        var request = new LlmRequest
        {
            Model = "gemini-2.5-flash",
            SystemInstruction = LlmContent.Model("Be concise."),
            Contents = [LlmContent.User("Hi")],
            Tools = [new DelegateTool("ping", "Ping", new JsonObject { ["type"] = "object" }, _ => Task.FromResult("ok"))]
        };

        LlmResponse restResp = await service.GenerateContentAsync(request);
        Assert.Equal("Hello from Gemini", restResp.Content);
        Assert.NotNull(restResp.Usage);
        Assert.Equal(15, restResp.Usage!.TotalTokens);
        Assert.Equal("test-gemini-key", handler.Requests[0].ApiKeyHeader);
        Assert.Contains("\"system_instruction\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"function_declarations\"", handler.Requests[0].Body, StringComparison.Ordinal);

        var streamChunks = new List<LlmResponse>();
        await foreach (LlmResponse chunk in service.StreamGenerateContentAsync(request))
        {
            streamChunks.Add(chunk);
        }

        LlmResponse sseResp = Assert.Single(streamChunks);
        Assert.Equal("Part1 ", sseResp.Content);
        LlmFunctionCall fc = Assert.Single(sseResp.FunctionCalls!);
        Assert.Equal("ping", fc.Name);
        Assert.Equal(7, fc.Args!["n"]!.GetValue<int>());

        // Caller-provided HttpClient is NOT disposed when GeminiService is disposed
        service.Dispose();
        Assert.False(handler.WasDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GenerateContentAsync(request));
    }

    [Fact]
    public async Task Dispose_DisposesOwnedHttpClientAndGuardsSubsequentCalls()
    {
        var owned = new GeminiService("test-key", httpClient: null, "http://127.0.0.1:1/v1beta");
        owned.Dispose();
        owned.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            owned.GenerateContentAsync(new LlmRequest { Model = "gemini-2.5-flash" }));
    }

    private sealed class DelegateTool(string name, string desc, JsonNode schema, Func<string, Task<string>> exec) : ITool
    {
        public string Name => name;
        public string Description => desc;
        public JsonNode GetParametersSchema() => schema;
        public Task<string> ExecuteAsync(string arguments) => exec(arguments);
    }
}
