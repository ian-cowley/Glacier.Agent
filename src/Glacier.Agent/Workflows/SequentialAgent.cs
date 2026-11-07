using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Glacier.Agent.Workflows;

/// <summary>
/// Executes a series of agents or tools in fixed sequence.
/// Each step receives the output of the previous step as its input.
/// </summary>
public class SequentialAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    private readonly IEnumerable<IAgent> _steps;

    public SequentialAgent(string name, string description, IEnumerable<IAgent> steps)
    {
        Name = name;
        Description = description;
        _steps = steps;
    }

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"Sequential {Name}");
        string currentInput = prompt;
        int stepIndex = 1;
        var stepsList = _steps.ToList();

        foreach (var agent in stepsList)
        {
            currentInput = await agent.RunAsync(currentInput, llmService, sessionId);
            stepIndex++;
        }

        return currentInput;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        string currentInput = prompt;
        var stepsList = _steps.ToList();

        for (int i = 0; i < stepsList.Count - 1; i++)
        {
            currentInput = await stepsList[i].RunAsync(currentInput, llmService, sessionId);
        }

        await foreach (var chunk in stepsList.Last().RunStreamingAsync(currentInput, llmService, sessionId))
        {
            yield return chunk;
        }
    }
}
