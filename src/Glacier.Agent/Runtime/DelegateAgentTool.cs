namespace Glacier.Agent.Runtime;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Delegate-backed implementation of <see cref="IAgentTool"/>.
/// Zero reflection, compile-time typed dispatch.
/// </summary>
public sealed class DelegateAgentTool : IAgentTool
{
    private readonly Func<string, CancellationToken, ValueTask<string>> _handler;

    public string Name { get; }
    public string Description { get; }
    public string ParameterJsonSchema { get; }

    public DelegateAgentTool(
        string name,
        string description,
        string parameterJsonSchema,
        Func<string, CancellationToken, ValueTask<string>> handler)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? "";
        ParameterJsonSchema = parameterJsonSchema ?? "{}";
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public static DelegateAgentTool Create(
        string name,
        string description,
        string parameterJsonSchema,
        Func<string, string> handler)
    {
        return new DelegateAgentTool(
            name,
            description,
            parameterJsonSchema,
            (args, _) => ValueTask.FromResult(handler(args)));
    }

    public static DelegateAgentTool CreateAsync(
        string name,
        string description,
        string parameterJsonSchema,
        Func<string, CancellationToken, ValueTask<string>> handler)
    {
        return new DelegateAgentTool(name, description, parameterJsonSchema, handler);
    }

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        return _handler(argumentsJson, ct);
    }
}
