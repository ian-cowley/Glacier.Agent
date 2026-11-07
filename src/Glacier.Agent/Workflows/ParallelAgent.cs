using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Glacier.Agent.Workflows;

/// <summary>
/// Executes multiple agents concurrently on the same prompt.
/// Results are collected and can optionally be processed by a consolidator agent.
/// </summary>
public class ParallelAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    private readonly IEnumerable<IAgent> _members;
    private readonly IAgent? _consolidator;

    public ParallelAgent(string name, string description, IEnumerable<IAgent> members, IAgent? consolidator = null)
    {
        Name = name;
        Description = description;
        _members = members;
        _consolidator = consolidator;
    }

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"Parallel {Name}");
        var tasks = _members.Select(agent =>
        {
            return agent.RunAsync(prompt, llmService, sessionId);
        });

        string[] results = await Task.WhenAll(tasks);

        var combinedResults = string.Join("\n\n---\n\n", results.Select((res, i) => $"### Result from { _members.ElementAt(i).Name }:\n{res}"));

        if (_consolidator != null)
        {
            var consolidationPrompt = $"Please consolidate and summarize the following parallel execution results into a single cohesive report:\n\n{combinedResults}";
            return await _consolidator.RunAsync(consolidationPrompt, llmService, sessionId);
        }

        return combinedResults;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        // Parallel streaming is complex as multiple agents yield tokens.
        // For simplicity, we run them in parallel and then stream the consolidated result.
        var result = await RunAsync(prompt, llmService, sessionId);
        yield return result;
    }
}
