namespace Glacier.Agent.Benchmarks;

using System.IO;
using BenchmarkDotNet.Attributes;
using Glacier.Agent.CommandLine;

[MemoryDiagnoser]
public class CommandLineBenchmark
{
    private string[] _args = null!;
    private CliApp _app = null!;

    [GlobalSetup]
    public void Setup()
    {
        _args = ["serve", "--model", "gpt-4", "-p", "8080", "-v", "config.json"];
        _app = new CliApp("glacier", "Glacier CLI");
        _app.Output = TextWriter.Null;
        _app.Error = TextWriter.Null;

        _app.AddCommand(new CliCommand("serve")
            .AddOption("model", "m", "Model identifier", hasValue: true)
            .AddOption("port", "p", "Port", hasValue: true)
            .AddFlag("verbose", "v", "Verbose")
            .AddArgument("config", "Config path")
            .SetHandler(ctx => 0));
    }

    [Benchmark]
    public int ParseAndDispatchCommandLine()
    {
        return _app.Run(_args);
    }
}
