namespace Glacier.Agent.Tests;

using System;
using System.Diagnostics;
using System.Text;
using Glacier.Agent.Tokenizer;
using Xunit;

public sealed class TokenizerTests
{
    [Fact]
    public void DefaultTokenizer_EncodesAndDecodes_EnglishText()
    {
        var tokenizer = SimdRadixTrieTokenizer.CreateDefault();
        string input = "public class AgentExecutionEngine { public void Run() { } }";
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);

        Span<int> tokens = stackalloc int[128];
        int tokenCount = tokenizer.Encode(inputBytes, tokens);

        Assert.True(tokenCount > 0);
        Assert.True(tokenCount < inputBytes.Length); // Multi-byte tokens matched

        int count = tokenizer.CountTokens(inputBytes);
        Assert.Equal(tokenCount, count);

        Span<byte> decodedBytes = stackalloc byte[inputBytes.Length * 2];
        int written = tokenizer.Decode(tokens.Slice(0, tokenCount), decodedBytes);

        string roundtrip = Encoding.UTF8.GetString(decodedBytes.Slice(0, written));
        Assert.Equal(input, roundtrip);
    }

    [Fact]
    public void CustomBuilder_MatchesLongestPrefix_AndHandlesOver32ChildEdges()
    {
        var builder = new SimdRadixTrieBuilder();
        builder.AddToken("cat");
        builder.AddToken("category");
        builder.AddToken("dog");

        // Add 45 distinct words prefixed with space to exercise both simdCount == 32 and overflow keys (> 32 edges)
        for (int i = 0; i < 45; i++)
        {
            char first = (char)(33 + i);
            builder.AddToken($" {first}longtoken_{i}");
        }

        var tokenizer = builder.Build();

        string text = "category dog cat !longtoken_0 @longtoken_31 Mlongtoken_44";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);

        Span<int> tokens = stackalloc int[64];
        int count = tokenizer.Encode(textBytes, tokens);

        // 3 words + 2 spaces + 3 space-prefixed long tokens = 8 tokens total
        Assert.Equal(8, count);

        string decoded = tokenizer.DecodeToString(tokens.Slice(0, count));
        Assert.Equal(text, decoded);
    }

    [Fact]
    public void ByteFallback_PreservesArbitraryUtf8AndBinary()
    {
        var builder = new SimdRadixTrieBuilder();
        // No custom words added, pure byte fallbacks
        var tokenizer = builder.Build();

        byte[] arbitrary = [0x00, 0xFF, 0x41, 0xC3, 0x28, 0x7E];
        Span<int> tokens = stackalloc int[arbitrary.Length];
        int count = tokenizer.Encode(arbitrary, tokens);

        Assert.Equal(arbitrary.Length, count);
        for (int i = 0; i < arbitrary.Length; i++)
        {
            Assert.Equal(arbitrary[i], tokens[i]);
        }

        Span<byte> decoded = stackalloc byte[arbitrary.Length];
        int written = tokenizer.Decode(tokens.Slice(0, count), decoded);
        Assert.Equal(arbitrary.Length, written);
        Assert.True(arbitrary.AsSpan().SequenceEqual(decoded.Slice(0, written)));
    }

    [Fact]
    public void Tokenizer_Throughput_SmokeCheck()
    {
        var tokenizer = SimdRadixTrieTokenizer.CreateDefault();
        string sampleText = "public async Task<ValueTask<AgentResponse>> ExecuteStepAsync(AgentContext context, CancellationToken ct) { return default; } ";
        byte[] sampleBytes = Encoding.UTF8.GetBytes(sampleText);

        Span<int> buffer = stackalloc int[1024];
        for (int i = 0; i < 100; i++)
        {
            tokenizer.Encode(sampleBytes, buffer);
        }

        int tokensPerIteration = tokenizer.Encode(sampleBytes, buffer);
        Assert.True(tokensPerIteration > 0);

        const int iterations = 10_000;
        long totalTokensProcessed = (long)tokensPerIteration * iterations;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            tokenizer.Encode(sampleBytes, buffer);
        }
        sw.Stop();

        double seconds = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
        double tokensPerSec = totalTokensProcessed / seconds;

        Assert.True(tokensPerSec > 100_000, $"Expected throughput >100k tokens/s on shared CI runner, got {tokensPerSec:N0} tokens/s");
    }
}
