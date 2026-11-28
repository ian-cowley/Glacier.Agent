using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Glacier.Agent.Llm;
using Glacier.Agent.Sandbox;

namespace Glacier.Agent.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(List<LlmContent>))]
[JsonSerializable(typeof(LlmContent))]
[JsonSerializable(typeof(LlmPart))]
[JsonSerializable(typeof(LlmFunctionCall))]
[JsonSerializable(typeof(LlmFunctionResponse))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(VerificationStatus))]
[JsonSerializable(typeof(CircuitBreakerStatus))]
[JsonSerializable(typeof(VerificationIssue))]
[JsonSerializable(typeof(List<VerificationIssue>))]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(List<VerificationResult>))]
[JsonSerializable(typeof(VerificationContext))]
[JsonSerializable(typeof(VerificationCircuitBreakerState))]
[JsonSerializable(typeof(VerificationEventPayload))]
[JsonSerializable(typeof(ToolErrorPayload))]
[JsonSerializable(typeof(VerificationFeedbackPayload))]
[JsonSerializable(typeof(SelfVerificationToolArgs))]
[JsonSerializable(typeof(SandboxOptions))]
[JsonSerializable(typeof(SandboxExecutionResult))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(OpenAiModelsListResponse))]
[JsonSerializable(typeof(GeminiGenerateResponse))]
[JsonSerializable(typeof(List<GeminiGenerateResponse>))]
public partial class AdkJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(List<LlmContent>))]
[JsonSerializable(typeof(VerificationResult))]
[JsonSerializable(typeof(SandboxExecutionResult))]
public partial class AdkIndentedJsonContext : JsonSerializerContext
{
}

internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;

    public PooledByteBufferWriter(int initialCapacity = 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
    }

    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            byte[] buf = _buffer ?? throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
            return buf.AsMemory(0, _written);
        }
    }

    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            byte[] buf = _buffer ?? throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
            return buf.AsSpan(0, _written);
        }
    }

    public void Reset() => _written = 0;

    public void Advance(int count)
    {
        byte[] buf = _buffer ?? throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
        if (count < 0 || _written + count > buf.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        byte[] buf = EnsureCapacity(sizeHint);
        return buf.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        byte[] buf = EnsureCapacity(sizeHint);
        return buf.AsSpan(_written);
    }

    private byte[] EnsureCapacity(int sizeHint)
    {
        byte[] buf = _buffer ?? throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
        int needed = Math.Max(sizeHint, 1);
        if (buf.Length - _written >= needed)
        {
            return buf;
        }

        int newSize = Math.Max(buf.Length * 2, _written + needed);
        byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
        buf.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(buf);
        _buffer = next;
        return next;
    }

    public void Dispose()
    {
        byte[]? buf = Interlocked.Exchange(ref _buffer, null);
        if (buf != null)
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
