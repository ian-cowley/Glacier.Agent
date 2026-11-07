using System;
using System.Threading.Tasks;

namespace Glacier.Agent.Workflows;

/// <summary>
/// Executes a task repeatedly until a condition is met or a turn limit is reached.
/// </summary>
public class LoopAgent : IAgent
{
    public string Name { get; }
    public string Description { get; }
    private readonly IAgent _loopMember;
    private readonly int _maxTurns;
    private readonly Func<string, bool>? _stopCondition;

    public LoopAgent(string name, string description, IAgent loopMember, int maxTurns = 5, Func<string, bool>? stopCondition = null)
    {
        Name = name;
        Description = description;
        _loopMember = loopMember;
        _maxTurns = maxTurns;
        _stopCondition = stopCondition;
    }

    public async Task<string> RunAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity($"Loop {Name}");
        string currentInput = prompt;

        for (int i = 1; i <= _maxTurns; i++)
        {
            currentInput = await _loopMember.RunAsync(currentInput, llmService, sessionId);

            if (_stopCondition != null && _stopCondition(currentInput))
            {
                break;
            }
        }

        return currentInput;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(string prompt, ILlmService llmService, string? sessionId = null)
    {
        string currentInput = prompt;

        for (int i = 1; i <= _maxTurns; i++)
        {
            // For now, loop streaming yields the full result of each iteration.
            currentInput = await _loopMember.RunAsync(currentInput, llmService, sessionId);
            yield return $"--- Turn {i} ---\n{currentInput}\n";

            if (_stopCondition != null && _stopCondition(currentInput))
            {
                break;
            }
        }
    }
}
