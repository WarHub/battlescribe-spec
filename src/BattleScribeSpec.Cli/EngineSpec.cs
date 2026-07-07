using System.CommandLine;
using BattleScribeSpec.Engines;

namespace BattleScribeSpec.Cli;

/// <summary>Which kind of spec the engine is editing.</summary>
internal enum EngineDomain
{
    Roster,
    Gamedata,
}

/// <summary>State dump rendering format.</summary>
internal enum OutputFormat
{
    Tree,
    Json,
}

/// <summary>A resolved engine selection: registry entry + domain + launch shaping.</summary>
internal sealed record EngineSelection(EngineEntry Entry, EngineDomain Domain, bool Headed, bool KeepAlive)
{
    /// <summary>Identity for applicability/assertions/labels; null for anonymous ad-hoc adapters.</summary>
    public string? EngineName => Entry.Name;

    /// <summary>Assertion engine: strip a trailing "-ui" from the identity.</summary>
    public string? AssertionEngineName =>
        EngineName is { } n ? (n.EndsWith("-ui", StringComparison.Ordinal) ? n[..^3] : n) : null;

    public string Display => $"{(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")}/{EngineName ?? "adapter"}";

    /// <summary>Start the adapter process for this selection.</summary>
    public Protocol.AdapterProcess StartProcess()
    {
        var launch = Engines.EngineHostLocator.Resolve(Entry, Headed, KeepAlive);
        return Protocol.AdapterProcess.Start(launch.Executable, launch.Arguments);
    }
}

/// <summary>
/// The shared engine-selection options (<c>--engine</c>, <c>--ui</c>,
/// <c>--gamedata</c>/<c>--roster</c>, <c>--headed</c>). One instance per command;
/// <see cref="Resolve"/> turns the parsed values plus the spec input into an
/// <see cref="EngineSelection"/>.
/// </summary>
internal sealed class EngineOptions
{
    public Option<string> Engine { get; } = new("--engine")
    {
        Description = "Engine to use: a built-in name (battlescribe, battlescribe-ui, newrecruit, " +
            "newrecruit-ui), a connectable (exec:<command>, dotnet:<dll-path>, <name>=<connectable>), " +
            "or a name from engines.json.",
        DefaultValueFactory = _ => "battlescribe",
    };

    public Option<bool> Ui { get; } = new("--ui")
    {
        Description = "Drive the real desktop/browser app instead of the in-process/API engine.",
    };

    public Option<bool> Gamedata { get; } = new("--gamedata")
    {
        Description = "Force the gamedata domain (otherwise inferred from the spec path).",
    };

    public Option<bool> Roster { get; } = new("--roster")
    {
        Description = "Force the roster domain (otherwise inferred from the spec path).",
    };

    public Option<bool> Headed { get; } = new("--headed")
    {
        Description = "Show the browser/app window (UI engines; default is headless).",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Engine);
        command.Options.Add(Ui);
        command.Options.Add(Gamedata);
        command.Options.Add(Roster);
        command.Options.Add(Headed);
    }

    /// <summary>Resolve the parsed axes into a concrete <see cref="EngineSelection"/>.</summary>
    public EngineSelection Resolve(ParseResult parseResult, string? specInput)
    {
        var gamedata = parseResult.GetValue(Gamedata);
        var roster = parseResult.GetValue(Roster);
        if (gamedata && roster)
        {
            throw new CliInputException("--gamedata and --roster are mutually exclusive.");
        }

        var domain = (gamedata, roster) switch
        {
            (true, _) => EngineDomain.Gamedata,
            (_, true) => EngineDomain.Roster,
            _ => SpecLoading.InferEngineType(specInput) == "gamedata" ? EngineDomain.Gamedata : EngineDomain.Roster,
        };

        EngineConnectable connectable;
        try
        {
            connectable = EngineConnectable.Parse(parseResult.GetValue(Engine)!);
        }
        catch (FormatException ex)
        {
            throw new CliInputException(ex.Message);
        }

        var ui = parseResult.GetValue(Ui);
        if (ui && connectable.IsLaunchable)
        {
            throw new CliInputException(
                "--ui cannot be combined with an exec:/dotnet: connectable; name the engine variant directly.");
        }

        // --ui sugar: append -ui to a plain registry name (idempotent).
        if (ui && connectable is { IsLaunchable: false, Name: { } plain }
            && !plain.EndsWith("-ui", StringComparison.Ordinal))
        {
            connectable = connectable with { Name = plain + "-ui" };
        }

        EngineEntry entry;
        try
        {
            entry = EngineRegistry.LoadDefault().Resolve(connectable);
        }
        catch (KeyNotFoundException ex)
        {
            throw new CliInputException(ex.Message);
        }

        return new EngineSelection(entry, domain, parseResult.GetValue(Headed), KeepAlive: false);
    }
}

/// <summary>Raised for user-facing input errors; handlers translate it to a red message + exit 1.</summary>
internal sealed class CliInputException(string message) : Exception(message);
