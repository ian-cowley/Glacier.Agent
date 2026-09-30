namespace Glacier.Agent.CommandLine;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Execution context for a parsed CLI command invocation.
/// </summary>
public sealed class CliContext
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly List<string> _arguments = new();
    private readonly string[] _rawArgs;

    public IReadOnlyList<string> Arguments => _arguments;
    public IReadOnlyCollection<string> Flags => _flags;
    public IReadOnlyDictionary<string, string> Options => _options;
    public string[] RawArgs => _rawArgs;

    public CliContext(string[] rawArgs)
    {
        _rawArgs = rawArgs ?? Array.Empty<string>();
    }

    internal void AddFlag(string flag)
    {
        _flags.Add(flag);
    }

    internal void AddOption(string key, string value)
    {
        _options[key] = value;
    }

    internal void AddArgument(string arg)
    {
        _arguments.Add(arg);
    }

    /// <summary>
    /// Checks whether a flag is set (e.g. "-v" or "--verbose").
    /// </summary>
    public bool HasFlag(ReadOnlySpan<char> flag)
    {
        ReadOnlySpan<char> cleanFlag = flag.TrimStart('-');
        foreach (string f in _flags)
        {
            if (flag.Equals(f.AsSpan(), StringComparison.Ordinal) ||
                cleanFlag.Equals(f.AsSpan().TrimStart('-'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Retrieves the string value of an option.
    /// </summary>
    public string? GetOption(ReadOnlySpan<char> name, string? defaultValue = null)
    {
        ReadOnlySpan<char> cleanName = name.TrimStart('-');
        foreach (var (k, v) in _options)
        {
            if (name.Equals(k.AsSpan(), StringComparison.Ordinal) ||
                cleanName.Equals(k.AsSpan().TrimStart('-'), StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return defaultValue;
    }

    /// <summary>
    /// Parses an integer option value with branchless span conversion.
    /// </summary>
    public int GetOptionInt(ReadOnlySpan<char> name, int defaultValue = 0)
    {
        string? val = GetOption(name);
        if (val == null)
        {
            return defaultValue;
        }

        return int.TryParse(val.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Parses a long option value.
    /// </summary>
    public long GetOptionLong(ReadOnlySpan<char> name, long defaultValue = 0L)
    {
        string? val = GetOption(name);
        if (val == null)
        {
            return defaultValue;
        }

        return long.TryParse(val.AsSpan(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Parses a double option value.
    /// </summary>
    public double GetOptionDouble(ReadOnlySpan<char> name, double defaultValue = 0.0)
    {
        string? val = GetOption(name);
        if (val == null)
        {
            return defaultValue;
        }

        return double.TryParse(val.AsSpan(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : defaultValue;
    }

    /// <summary>
    /// Parses a boolean option value (or presence of flag).
    /// </summary>
    public bool GetOptionBool(ReadOnlySpan<char> name, bool defaultValue = false)
    {
        if (HasFlag(name))
        {
            return true;
        }

        string? val = GetOption(name);
        if (val == null)
        {
            return defaultValue;
        }

        return bool.TryParse(val.AsSpan(), out bool result) ? result : defaultValue;
    }

    /// <summary>
    /// Retrieves a positional argument by zero-based index.
    /// </summary>
    public string? GetArgument(int index, string? defaultValue = null)
    {
        if ((uint)index < (uint)_arguments.Count)
        {
            return _arguments[index];
        }
        return defaultValue;
    }
}
