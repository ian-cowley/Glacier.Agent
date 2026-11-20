namespace Glacier.Agent;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Asynchronous verification hook executed on candidate agent outputs.
/// </summary>
public interface IVerificationHook
{
    string Name { get; }
    ValueTask<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken ct = default);
}

/// <summary>
/// Marks an <see cref="IAgentTool"/> as a verification tool whose outcomes feed the verification circuit breaker.
/// </summary>
public interface IVerificationTool : IAgentTool
{
}

/// <summary>
/// Delegate-backed implementation of <see cref="IVerificationHook"/>.
/// </summary>
public sealed class DelegateVerificationHook : IVerificationHook
{
    private readonly Func<VerificationContext, CancellationToken, ValueTask<VerificationResult>> _handler;

    public string Name { get; }

    public DelegateVerificationHook(
        string name,
        Func<VerificationContext, CancellationToken, ValueTask<VerificationResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public ValueTask<VerificationResult> VerifyAsync(VerificationContext context, CancellationToken ct = default)
        => _handler(context, ct);

    public static DelegateVerificationHook Create(string name, Func<VerificationContext, VerificationResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new DelegateVerificationHook(name, (ctx, _) => ValueTask.FromResult(handler(ctx)));
    }

    public static DelegateVerificationHook CreateAsync(
        string name,
        Func<VerificationContext, CancellationToken, ValueTask<VerificationResult>> handler)
        => new(name, handler);
}
