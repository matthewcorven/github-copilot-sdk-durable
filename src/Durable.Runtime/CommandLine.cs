namespace Durable.Runtime;

/// <summary>The command line could not be understood.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>One option a command accepts.</summary>
internal sealed record OptionSpec(string Name, bool TakesValue, string Help);

/// <summary>The options a command was given, in the order they appeared.</summary>
internal sealed class ParsedOptions
{
    private readonly List<(string Name, string? Value)> _values = [];

    public void Add(string name, string? value) => _values.Add((name, value));

    public bool Has(string name) => _values.Exists(entry => entry.Name == name);

    public string? Last(string name)
    {
        for (var i = _values.Count - 1; i >= 0; i--)
        {
            if (_values[i].Name == name)
            {
                return _values[i].Value;
            }
        }

        return null;
    }

    public IReadOnlyList<string> All(string name) =>
        _values.Where(entry => entry.Name == name && entry.Value is not null).Select(entry => entry.Value!).ToList();
}

internal static class CommandLine
{
    /// <summary>
    /// Parses <paramref name="args"/> from <paramref name="start"/> against
    /// <paramref name="specs"/>. Anything that is not an option is a positional.
    /// </summary>
    public static ParsedOptions Parse(
        IReadOnlyList<string> args,
        int start,
        IReadOnlyList<OptionSpec> specs,
        out List<string> positionals)
    {
        var known = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            known[spec.Name] = spec;
        }

        var parsed = new ParsedOptions();
        positionals = [];

        for (var i = start; i < args.Count; i++)
        {
            var argument = args[i];

            if (argument == "--")
            {
                for (var j = i + 1; j < args.Count; j++)
                {
                    positionals.Add(args[j]);
                }

                break;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            var name = argument[2..];
            string? inline = null;
            var separator = name.IndexOf('=');
            if (separator >= 0)
            {
                inline = name[(separator + 1)..];
                name = name[..separator];
            }

            if (!known.TryGetValue(name, out var spec))
            {
                throw new UsageException($"unknown option '--{name}'");
            }

            if (!spec.TakesValue)
            {
                if (inline is not null)
                {
                    throw new UsageException($"option '--{name}' does not take a value");
                }

                parsed.Add(name, null);
                continue;
            }

            if (inline is not null)
            {
                parsed.Add(name, inline);
                continue;
            }

            if (i + 1 >= args.Count)
            {
                throw new UsageException($"option '--{name}' needs a value");
            }

            parsed.Add(name, args[++i]);
        }

        return parsed;
    }

    public static string Required(this ParsedOptions options, string name)
    {
        var value = options.Last(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"option '--{name}' is required");
        }

        return value;
    }
}
