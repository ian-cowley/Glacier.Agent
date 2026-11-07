namespace Glacier.Agent.Tests.Mocks;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Glacier.Agent;
using Glacier.Agent.Llm;

public class TestLlmService : ILlmService
{
    private readonly Func<LlmRequest, LlmResponse> _responder;

    public List<LlmRequest> CapturedRequests { get; } = new();

    public TestLlmService(Func<LlmRequest, LlmResponse> responder)
    {
        _responder = responder;
    }

    public TestLlmService(string staticResponse)
    {
        _responder = _ => new LlmResponse { Content = staticResponse };
    }

    public Task<LlmResponse> GenerateContentAsync(LlmRequest request)
    {
        CapturedRequests.Add(request);
        return Task.FromResult(_responder(request));
    }

    public async IAsyncEnumerable<LlmResponse> StreamGenerateContentAsync(LlmRequest request)
    {
        var response = await GenerateContentAsync(request);
        yield return response;
    }
}
