using Spectre.Console;

namespace BattleScribeSpec.Cli;

/// <summary>
/// Console "chrome" — section rules, status lines, and pass/fail rendering.
/// Everything here is written to <b>stderr</b> so that stdout stays clean for piped
/// state dumps (tree or JSON). Dynamic content is markup-escaped.
/// </summary>
internal static class Ui
{
    private static readonly IAnsiConsole Err = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Out = new AnsiConsoleOutput(Console.Error),
    });

    public static void Rule(string title) =>
        Err.Write(new Rule($"[grey]{Markup.Escape(title)}[/]").LeftJustified());

    public static void Info(string message) =>
        Err.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public static void Warn(string message) =>
        Err.MarkupLine($"[yellow]warning:[/] {Markup.Escape(message)}");

    public static void Error(string message) =>
        Err.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");

    public static void Pass(string message) =>
        Err.MarkupLine($"[green]✓ {Markup.Escape(message)}[/]");

    public static void Fail(string message) =>
        Err.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");

    public static void FailItem(string message) =>
        Err.MarkupLine($"  [red]{Markup.Escape(message)}[/]");

    public static void Blank() => Err.WriteLine();
}
