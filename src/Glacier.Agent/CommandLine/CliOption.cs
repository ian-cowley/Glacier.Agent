namespace Glacier.Agent.CommandLine;

using System;

/// <summary>
/// CLI Option flag or key-value option definition.
/// Zero-reflection, Native AOT trim-safe.
/// </summary>
public sealed class CliOption
{
    public string Name { get; }
    public string? ShortName { get; }
    public string Description { get; }
    public bool HasValue { get; }
    public string? DefaultValue { get; }
    public bool IsRequired { get; }

    public CliOption(
        string name,
        string? shortName = null,
        string description = "",
        bool hasValue = true,
        string? defaultValue = null,
        bool isRequired = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name cannot be null or whitespace.", nameof(name));
        }

        Name = name.StartsWith('-') ? name : "--" + name;
        ShortName = shortName != null ? (shortName.StartsWith('-') ? shortName : "-" + shortName) : null;
        Description = description;
        HasValue = hasValue;
        DefaultValue = defaultValue;
        IsRequired = isRequired;
    }

    /// <summary>
    /// Creates a boolean toggle/flag option (e.g. -v / --verbose).
    /// </summary>
    public static CliOption Flag(string name, string? shortName = null, string description = "")
        => new(name, shortName, description, hasValue: false);

    /// <summary>
    /// Creates a value-bearing option (e.g. --port 8080 or -p 8080).
    /// </summary>
    public static CliOption Value(string name, string? shortName = null, string description = "", string? defaultValue = null, bool isRequired = false)
        => new(name, shortName, description, hasValue: true, defaultValue, isRequired);
}

/// <summary>
/// Positional argument definition.
/// </summary>
public sealed class CliArgument
{
    public string Name { get; }
    public string Description { get; }
    public int Index { get; }
    public bool IsRequired { get; }
    public string? DefaultValue { get; }

    public CliArgument(string name, int index, string description = "", bool isRequired = true, string? defaultValue = null)
    {
        Name = name;
        Index = index;
        Description = description;
        IsRequired = isRequired;
        DefaultValue = defaultValue;
    }
}
