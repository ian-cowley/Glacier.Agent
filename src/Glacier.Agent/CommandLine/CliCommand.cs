namespace Glacier.Agent.CommandLine;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// CLI Command representation with strongly-typed options and arguments.
/// Zero reflection, compile-time safe, Native AOT trim-friendly.
/// </summary>
public sealed class CliCommand
{
    private readonly List<CliOption> _options = new();
    private readonly List<CliArgument> _arguments = new();
    private readonly Dictionary<string, CliCommand> _subcommands = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }
    public string Description { get; set; }
    public IReadOnlyList<CliOption> Options => _options;
    public IReadOnlyList<CliArgument> Arguments => _arguments;
    public IReadOnlyDictionary<string, CliCommand> Subcommands => _subcommands;

    public Func<CliContext, ValueTask<int>>? AsyncHandler { get; set; }

    public CliCommand(string name, string description = "")
    {
        Name = name;
        Description = description;
    }

    public CliCommand AddOption(CliOption option)
    {
        _options.Add(option);
        return this;
    }

    public CliCommand AddOption(string name, string? shortName = null, string description = "", bool hasValue = true, string? defaultValue = null, bool isRequired = false)
    {
        _options.Add(new CliOption(name, shortName, description, hasValue, defaultValue, isRequired));
        return this;
    }

    public CliCommand AddFlag(string name, string? shortName = null, string description = "")
    {
        _options.Add(CliOption.Flag(name, shortName, description));
        return this;
    }

    public CliCommand AddArgument(string name, string description = "", bool isRequired = true, string? defaultValue = null)
    {
        _arguments.Add(new CliArgument(name, _arguments.Count, description, isRequired, defaultValue));
        return this;
    }

    public CliCommand AddSubcommand(CliCommand command)
    {
        _subcommands[command.Name] = command;
        return this;
    }

    public CliCommand SetHandler(Func<CliContext, int> handler)
    {
        AsyncHandler = ctx => ValueTask.FromResult(handler(ctx));
        return this;
    }

    public CliCommand SetHandler(Func<CliContext, ValueTask<int>> handler)
    {
        AsyncHandler = handler;
        return this;
    }
}
