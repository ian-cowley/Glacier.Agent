namespace Glacier.Agent;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Autonomous agent runtime execution contract.
/// </summary>
public interface IAgentRuntime : IDisposable
{
    /// <summary>
    /// Unique identifier for this agent.
    /// </summary>
    string AgentId { get; }

    /// <summary>
    /// Executes a single discrete reasoning or action step in the autonomous loop.
    /// </summary>
    ValueTask<AgentResponse> ExecuteStepAsync(AgentContext context, CancellationToken ct = default);

    /// <summary>
    /// Registers an executable tool into the runtime.
    /// </summary>
    void RegisterTool(IAgentTool tool);

    /// <summary>
    /// Event stream published during agent execution steps.
    /// </summary>
    event Action<AgentEvent>? EventPublished;
}

/// <summary>
/// High-performance vector indexing contract for dense embeddings.
/// </summary>
public interface IVectorIndex : IDisposable
{
    /// <summary>
    /// Inserts a vector with a corresponding identifier.
    /// </summary>
    void Add(long id, ReadOnlySpan<float> vector);

    /// <summary>
    /// Performs nearest-neighbor similarity search.
    /// Returns the number of results found (up to topK).
    /// </summary>
    int Search(ReadOnlySpan<float> query, Span<long> outIds, Span<float> outDistances, int topK);

    /// <summary>
    /// Serializes the index to a stream.
    /// </summary>
    void Save(Stream stream);

    /// <summary>
    /// Deserializes the index from a stream.
    /// </summary>
    void Load(Stream stream);
}

/// <summary>
/// Executable tool contract for autonomous agent consumption.
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// Tool identifier name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Tool description for model planning.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON schema describing the expected tool arguments.
    /// </summary>
    string ParameterJsonSchema { get; }

    /// <summary>
    /// Executes the tool with the given JSON arguments.
    /// </summary>
    ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}

/// <summary>
/// Immutable context passed into an agent execution step.
/// </summary>
public readonly record struct AgentContext(string SessionId, string Prompt, int StepCount);

/// <summary>
/// Result of an agent execution step.
/// </summary>
public readonly record struct AgentResponse(string Output, bool IsCompleted, string? ToolCallName = null);

/// <summary>
/// Reactive event emitted by an agent.
/// </summary>
public readonly record struct AgentEvent(string SourceAgent, string EventType, string Payload);
