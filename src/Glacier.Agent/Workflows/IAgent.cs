namespace Glacier.Agent.Workflows;

using System.Collections.Generic;
using System.Threading.Tasks;
using Glacier.Agent.Llm;

/// <summary>
/// High-level autonomous workflow agent execution contract.
/// </summary>
public interface IAgent
{
    string Name { get; }
    string Description { get; }
    Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null);
    IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null);
}
