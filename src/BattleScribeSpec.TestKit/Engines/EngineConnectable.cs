using System.Text.RegularExpressions;

namespace BattleScribeSpec.Engines;

/// <summary>
/// A parsed engine selector: a registry name (<c>battlescribe-ui</c>, <c>wham</c>),
/// an ad-hoc launchable (<c>exec:node adapter.js</c>, <c>dotnet:adapter.dll</c>),
/// or both (<c>battlescribe=dotnet:adapter.dll</c> — run THIS adapter AS that identity).
/// Inspired by bowtie connectables and the eshost host registry.
/// </summary>
public sealed partial record EngineConnectable(string? Name, string? Executable, string? Arguments)
{
    /// <summary>True when this connectable carries its own launch command.</summary>
    public bool IsLaunchable => Executable is not null;

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]*$")]
    private static partial Regex NamePattern();

    public static EngineConnectable Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("Engine connectable must not be empty.");
        }

        // name=<scheme:...> — identity + launch.
        var eq = input.IndexOf('=');
        if (eq > 0 && NamePattern().IsMatch(input[..eq]))
        {
            if (ParseLaunch(input[(eq + 1)..]) is not { } launch)
            {
                throw new FormatException(
                    $"Invalid engine connectable '{input}': expected <name>=exec:<command> or <name>=dotnet:<dll>.");
            }

            return launch with { Name = input[..eq] };
        }

        if (ParseLaunch(input) is { } anonymous)
        {
            return anonymous;
        }

        if (NamePattern().IsMatch(input))
        {
            return new EngineConnectable(input, null, null);
        }

        throw new FormatException(
            $"Invalid engine connectable '{input}': expected an engine name, exec:<command>, dotnet:<dll>, or <name>=<connectable>.");
    }

    private static EngineConnectable? ParseLaunch(string input)
    {
        if (input.StartsWith("exec:", StringComparison.Ordinal))
        {
            var command = input[5..].Trim();
            if (command.Length == 0)
            {
                throw new FormatException("exec: connectable requires a command.");
            }

            var space = command.IndexOf(' ');
            return space < 0
                ? new EngineConnectable(null, command, null)
                : new EngineConnectable(null, command[..space], command[(space + 1)..].Trim());
        }

        if (input.StartsWith("dotnet:", StringComparison.Ordinal))
        {
            var dll = input[7..].Trim();
            if (dll.Length == 0)
            {
                throw new FormatException("dotnet: connectable requires a dll path.");
            }

            return new EngineConnectable(null, "dotnet", dll);
        }

        return null;
    }
}
