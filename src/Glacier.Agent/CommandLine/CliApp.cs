namespace Glacier.Agent.CommandLine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// High-performance compile-time CLI application runner.
/// Pure C#, zero-reflection, Native AOT trim-safe, achieving sub-5ms cold startup.
/// </summary>
public sealed class CliApp
{
    private readonly Dictionary<string, CliCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly CliCommand _rootCommand;

    public string Name { get; }
    public string Description { get; }
    public string Version { get; }
    public TextWriter Output { get; set; } = Console.Out;
    public TextWriter Error { get; set; } = Console.Error;

    public CliApp(string name, string description = "", string version = "1.0.0")
    {
        Name = name;
        Description = description;
        Version = version;
        _rootCommand = new CliCommand(name, description);
    }

    public CliCommand RootCommand => _rootCommand;

    public CliApp AddCommand(CliCommand command)
    {
        _commands[command.Name] = command;
        return this;
    }

    /// <summary>
    /// Executes the CLI application synchronously.
    /// </summary>
    public int Run(string[] args)
    {
        return RunAsync(args).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the CLI application asynchronously.
    /// </summary>
    public async ValueTask<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        args ??= Array.Empty<string>();

        // Fast path: cold help or version flags
        if (args.Length == 0)
        {
            PrintHelp(_rootCommand);
            return 0;
        }

        ReadOnlySpan<char> firstArg = args[0].AsSpan();
        if (firstArg.Equals("-h", StringComparison.Ordinal) ||
            firstArg.Equals("--help", StringComparison.Ordinal) ||
            firstArg.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length > 1 && _commands.TryGetValue(args[1], out CliCommand? subCmd))
            {
                PrintHelp(subCmd);
            }
            else
            {
                PrintHelp(_rootCommand);
            }
            return 0;
        }

        if (firstArg.Equals("-v", StringComparison.Ordinal) ||
            firstArg.Equals("--version", StringComparison.Ordinal) ||
            firstArg.Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            Output.WriteLine($"{Name} v{Version}");
            return 0;
        }

        // Check if first argument is a registered subcommand
        CliCommand activeCommand;
        int argOffset;

        if (_commands.TryGetValue(args[0], out CliCommand? matchedCommand))
        {
            activeCommand = matchedCommand;
            argOffset = 1;
        }
        else
        {
            activeCommand = _rootCommand;
            argOffset = 0;
        }

        // Check for --help on subcommand
        if (argOffset < args.Length)
        {
            ReadOnlySpan<char> secondArg = args[argOffset].AsSpan();
            if (secondArg.Equals("-h", StringComparison.Ordinal) ||
                secondArg.Equals("--help", StringComparison.Ordinal))
            {
                PrintHelp(activeCommand);
                return 0;
            }
        }

        // Parse options, flags, and positional arguments
        var context = new CliContext(args);
        int posArgIndex = 0;

        for (int i = argOffset; i < args.Length; i++)
        {
            string current = args[i];
            ReadOnlySpan<char> span = current.AsSpan();

            if (span.StartsWith("--".AsSpan(), StringComparison.Ordinal))
            {
                // Option or Flag
                int eqIdx = span.IndexOf('=');
                if (eqIdx > 0)
                {
                    string key = current.Substring(0, eqIdx);
                    string val = current.Substring(eqIdx + 1);
                    context.AddOption(key, val);
                }
                else
                {
                    // Check if option expects a value
                    CliOption? opt = FindOption(activeCommand, span);
                    if (opt != null && opt.HasValue)
                    {
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            context.AddOption(current, args[++i]);
                        }
                        else if (opt.DefaultValue != null)
                        {
                            context.AddOption(current, opt.DefaultValue);
                        }
                        else
                        {
                            Error.WriteLine($"Error: Option '{current}' expects a value.");
                            return 1;
                        }
                    }
                    else
                    {
                        // Boolean flag
                        context.AddFlag(current);
                    }
                }
            }
            else if (span.StartsWith('-') && span.Length > 1 && !char.IsDigit(span[1]))
            {
                // Short option or short flag (e.g. -p or -v)
                CliOption? opt = FindOption(activeCommand, span);
                if (opt != null && opt.HasValue)
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    {
                        context.AddOption(opt.Name, args[++i]);
                    }
                    else if (opt.DefaultValue != null)
                    {
                        context.AddOption(opt.Name, opt.DefaultValue);
                    }
                    else
                    {
                        Error.WriteLine($"Error: Option '{current}' expects a value.");
                        return 1;
                    }
                }
                else
                {
                    // Short flag: could be combined flags e.g. -xyz
                    if (span.Length > 2 && opt == null)
                    {
                        for (int c = 1; c < span.Length; c++)
                        {
                            context.AddFlag("-" + span[c]);
                        }
                    }
                    else
                    {
                        context.AddFlag(current);
                    }
                }
            }
            else
            {
                // Positional argument
                context.AddArgument(current);
                posArgIndex++;
            }
        }

        // Populate default values for unsupplied options
        foreach (CliOption opt in activeCommand.Options)
        {
            if (opt.DefaultValue != null && context.GetOption(opt.Name) == null)
            {
                context.AddOption(opt.Name, opt.DefaultValue);
            }
        }

        // Validate required options and arguments
        foreach (CliOption opt in activeCommand.Options)
        {
            if (opt.IsRequired && !context.Options.ContainsKey(opt.Name))
            {
                Error.WriteLine($"Error: Missing required option '{opt.Name}'.");
                return 1;
            }
        }

        for (int i = 0; i < activeCommand.Arguments.Count; i++)
        {
            CliArgument arg = activeCommand.Arguments[i];
            if (arg.IsRequired && i >= context.Arguments.Count)
            {
                Error.WriteLine($"Error: Missing required argument '{arg.Name}'.");
                return 1;
            }
        }

        if (activeCommand.AsyncHandler != null)
        {
            return await activeCommand.AsyncHandler(context).ConfigureAwait(false);
        }

        PrintHelp(activeCommand);
        return 0;
    }

    private static CliOption? FindOption(CliCommand command, ReadOnlySpan<char> span)
    {
        foreach (CliOption opt in command.Options)
        {
            if (span.Equals(opt.Name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return opt;
            }
            if (opt.ShortName != null && span.Equals(opt.ShortName.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return opt;
            }
        }
        return null;
    }

    public void PrintHelp(CliCommand command)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name} - {Description}");
        sb.AppendLine($"Usage: {Name} [command] [options] [arguments]");
        sb.AppendLine();

        if (_commands.Count > 0)
        {
            sb.AppendLine("Commands:");
            foreach (var (cmdName, cmd) in _commands)
            {
                sb.AppendLine($"  {cmdName,-16} {cmd.Description}");
            }
            sb.AppendLine();
        }

        if (command.Arguments.Count > 0)
        {
            sb.AppendLine("Arguments:");
            foreach (CliArgument arg in command.Arguments)
            {
                string req = arg.IsRequired ? "(required)" : "(optional)";
                sb.AppendLine($"  <{arg.Name,-14}> {arg.Description} {req}");
            }
            sb.AppendLine();
        }

        if (command.Options.Count > 0)
        {
            sb.AppendLine("Options:");
            foreach (CliOption opt in command.Options)
            {
                string shortPart = opt.ShortName != null ? $"{opt.ShortName}, " : "    ";
                string valHint = opt.HasValue ? " <value>" : "";
                string defHint = opt.DefaultValue != null ? $" [default: {opt.DefaultValue}]" : "";
                sb.AppendLine($"  {shortPart}{opt.Name + valHint,-20} {opt.Description}{defHint}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Global Options:");
        sb.AppendLine("  -h, --help           Show help information");
        sb.AppendLine("  -v, --version        Show version information");

        Output.WriteLine(sb.ToString().TrimEnd());
    }
}
