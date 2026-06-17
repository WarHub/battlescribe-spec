using System.CommandLine;

namespace BattleScribeSpec.Cli;

/// <summary>Which product implements the engine.</summary>
internal enum EngineProduct
{
    Battlescribe,
    Newrecruit,
}

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

/// <summary>
/// The three orthogonal engine axes resolved into a concrete engine selection:
/// product (battlescribe/newrecruit), surface (API vs <c>--ui</c>), and domain
/// (roster/gamedata, inferred from the spec unless overridden).
/// </summary>
internal sealed record EngineSpec(EngineProduct Product, bool Ui, EngineDomain Domain)
{
    /// <summary>The concrete engine name used by the factories and runners.</summary>
    public string EngineName => (Product, Ui) switch
    {
        (EngineProduct.Battlescribe, false) => "battlescribe",
        (EngineProduct.Battlescribe, true) => "battlescribe-ui",
        (EngineProduct.Newrecruit, false) => "newrecruit",
        (EngineProduct.Newrecruit, true) => "newrecruit-ui",
        _ => throw new ArgumentOutOfRangeException(nameof(Product)),
    };

    /// <summary>
    /// Assertion-override engine: UI engines assert as their non-UI counterpart, since a
    /// UI engine drives the same underlying product (battlescribe-ui IS battlescribe).
    /// </summary>
    public string AssertionEngineName =>
        Product == EngineProduct.Battlescribe ? "battlescribe" : "newrecruit";

    public string Display => $"{(Domain == EngineDomain.Gamedata ? "gamedata" : "roster")}/{EngineName}";
}

/// <summary>
/// The shared engine-selection options (<c>--engine</c>, <c>--ui</c>,
/// <c>--gamedata</c>/<c>--roster</c>, <c>--headed</c>). One instance per command;
/// <see cref="Resolve"/> turns the parsed values plus the spec input into an
/// <see cref="EngineSpec"/>.
/// </summary>
internal sealed class EngineOptions
{
    public Option<EngineProduct> Engine { get; } = new("--engine")
    {
        Description = "Engine product: battlescribe or newrecruit.",
        DefaultValueFactory = _ => EngineProduct.Battlescribe,
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

    /// <summary>Resolve the parsed axes into a concrete <see cref="EngineSpec"/>.</summary>
    public EngineSpec Resolve(ParseResult parseResult, string specInput)
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
            _ => SpecLoading.InferEngineType(specInput) == "gamedata"
                ? EngineDomain.Gamedata
                : EngineDomain.Roster,
        };

        return new EngineSpec(parseResult.GetValue(Engine), parseResult.GetValue(Ui), domain);
    }
}

/// <summary>Raised for user-facing input errors; handlers translate it to a red message + exit 1.</summary>
internal sealed class CliInputException(string message) : Exception(message);
