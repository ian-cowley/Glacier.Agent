namespace Glacier.Agent;

using System;
using System.Text.Json.Serialization;
using System.Threading;

/// <summary>
/// Immutable snapshot of <see cref="VerificationCircuitBreaker"/> state.
/// </summary>
public sealed record VerificationCircuitBreakerState(
    CircuitBreakerStatus Status,
    int ConsecutiveFailures,
    int MaxConsecutiveRetries,
    int TotalPasses,
    int TotalFailures,
    string? LastFailureReason,
    VerificationResult? LastResult)
{
    [JsonIgnore]
    public bool IsTripped => Status == CircuitBreakerStatus.Open;
}

/// <summary>
/// Exception thrown when <see cref="VerificationOptions.ThrowOnCircuitBreakerTripped"/> is enabled and
/// consecutive verification failures exceed <see cref="VerificationOptions.MaxConsecutiveVerificationRetries"/>.
/// </summary>
public sealed class VerificationCircuitBreakerException : InvalidOperationException
{
    public VerificationCircuitBreakerState State { get; }
    public VerificationResult LastResult { get; }

    public VerificationCircuitBreakerException(
        VerificationCircuitBreakerState state,
        VerificationResult lastResult)
        : base(BuildMessage(state, lastResult))
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        LastResult = lastResult ?? throw new ArgumentNullException(nameof(lastResult));
    }

    private static string BuildMessage(VerificationCircuitBreakerState state, VerificationResult lastResult)
    {
        string? reason = lastResult.FailureReason ?? state.LastFailureReason;
        return string.IsNullOrWhiteSpace(reason)
            ? $"Verification circuit breaker tripped after {state.ConsecutiveFailures} consecutive failures (max retries: {state.MaxConsecutiveRetries})."
            : $"Verification circuit breaker tripped after {state.ConsecutiveFailures} consecutive failures (max retries: {state.MaxConsecutiveRetries}): {reason}";
    }
}

/// <summary>
/// Thread-safe circuit breaker that tracks consecutive verification failures and resets on verification pass.
/// Trips when <c>ConsecutiveFailures &gt; MaxConsecutiveRetries</c>.
/// </summary>
public sealed class VerificationCircuitBreaker
{
    private readonly Lock _lock = new();
    private CircuitBreakerStatus _status = CircuitBreakerStatus.Closed;
    private int _consecutiveFailures;
    private int _totalPasses;
    private int _totalFailures;
    private string? _lastFailureReason;
    private VerificationResult? _lastResult;

    public int MaxConsecutiveRetries { get; }

    public VerificationCircuitBreaker(int maxConsecutiveRetries = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxConsecutiveRetries);
        MaxConsecutiveRetries = maxConsecutiveRetries;
    }

    public int ConsecutiveFailures
    {
        get { lock (_lock) { return _consecutiveFailures; } }
    }

    public int TotalPasses
    {
        get { lock (_lock) { return _totalPasses; } }
    }

    public int TotalFailures
    {
        get { lock (_lock) { return _totalFailures; } }
    }

    public bool IsTripped
    {
        get { lock (_lock) { return _status == CircuitBreakerStatus.Open; } }
    }

    public CircuitBreakerStatus Status
    {
        get { lock (_lock) { return _status; } }
    }

    public string? LastFailureReason
    {
        get { lock (_lock) { return _lastFailureReason; } }
    }

    public VerificationResult? LastResult
    {
        get { lock (_lock) { return _lastResult; } }
    }

    public VerificationCircuitBreakerState GetState()
    {
        lock (_lock)
        {
            return CreateStateUnsafe();
        }
    }

    public VerificationCircuitBreakerState RecordSuccess(VerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _status = CircuitBreakerStatus.Closed;
            _totalPasses++;
            _lastFailureReason = null;
            _lastResult = result with
            {
                Passed = true,
                Status = VerificationStatus.Passed,
                CircuitTripped = false,
                ConsecutiveFailures = 0
            };
            return CreateStateUnsafe();
        }
    }

    public VerificationCircuitBreakerState RecordFailure(VerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            _consecutiveFailures++;
            _totalFailures++;
            if (_consecutiveFailures > MaxConsecutiveRetries)
            {
                _status = CircuitBreakerStatus.Open;
            }

            bool tripped = _status == CircuitBreakerStatus.Open;
            string reason = !string.IsNullOrWhiteSpace(result.FailureReason)
                ? result.FailureReason!
                : (!string.IsNullOrWhiteSpace(result.Summary) ? result.Summary : "Verification failed.");

            _lastFailureReason = reason;
            _lastResult = result with
            {
                Passed = false,
                Status = tripped
                    ? VerificationStatus.CircuitBreakerTripped
                    : (result.Status == VerificationStatus.Error ? VerificationStatus.Error : VerificationStatus.Failed),
                FailureReason = reason,
                CircuitTripped = tripped,
                ConsecutiveFailures = _consecutiveFailures
            };
            return CreateStateUnsafe();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _status = CircuitBreakerStatus.Closed;
            _consecutiveFailures = 0;
            _totalPasses = 0;
            _totalFailures = 0;
            _lastFailureReason = null;
            _lastResult = null;
        }
    }

    private VerificationCircuitBreakerState CreateStateUnsafe()
        => new(
            _status,
            _consecutiveFailures,
            MaxConsecutiveRetries,
            _totalPasses,
            _totalFailures,
            _lastFailureReason,
            _lastResult);
}
