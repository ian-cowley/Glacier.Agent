namespace Glacier.Agent.Benchmarks;

using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using Glacier.Agent.Tokenizer;

[MemoryDiagnoser]
public class TokenizerBenchmark
{
    private SimdRadixTrieTokenizer _tokenizer = null!;
    private byte[] _inputBytes = null!;
    private int[] _outputTokens = null!;
    private byte[] _outputBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tokenizer = SimdRadixTrieTokenizer.CreateDefault();
        string sampleText = """
        public sealed class AgentExecutionEngine : IAgentRuntime
        {
            public async ValueTask<AgentResponse> ExecuteStepAsync(AgentContext context, CancellationToken ct)
            {
                var response = await ProcessAgentStepAsync(context, ct);
                return response;
            }
        }
        """;
        _inputBytes = Encoding.UTF8.GetBytes(sampleText);
        _outputTokens = new int[512];
        _outputBytes = new byte[1024];

        // Pre-encode for decode benchmark
        int count = _tokenizer.Encode(_inputBytes, _outputTokens);
    }

    [Benchmark(Baseline = true)]
    public int Encode_ZeroAllocation()
    {
        return _tokenizer.Encode(_inputBytes, _outputTokens);
    }

    [Benchmark]
    public int CountTokens_ZeroAllocation()
    {
        return _tokenizer.CountTokens(_inputBytes);
    }

    [Benchmark]
    public int Decode_ZeroAllocation()
    {
        return _tokenizer.Decode(_outputTokens.AsSpan(0, 30), _outputBytes);
    }
}
