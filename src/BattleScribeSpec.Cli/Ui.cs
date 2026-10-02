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

    /// <summary>
    /// <see cref="Error"/> as exactly one line, however long. Spectre word-wraps markup at the console
    /// width, and a redirected stderr — every CI log, every script — has no width, so Spectre uses 80
    /// columns: a long message comes out as several lines and a log tail shows only its end. Use this
    /// for a message a reader or a script is meant to find as the last line on stderr; a terminal
    /// soft-wraps it for display anyway.
    /// </summary>
    public static void ErrorLine(string message)
    {
        Err.Markup("[red]error:[/] ");

        // The writer Spectre itself writes to, not Console.Error, so the two halves cannot part.
        Err.Profile.Out.Writer.WriteLine(message);
    }

    public static void Pass(string message) =>
        Err.MarkupLine($"[green]✓ {Markup.Escape(message)}[/]");

    public static void Fail(string message) =>
        Err.MarkupLine($"[red]✗ {Markup.Escape(message)}[/]");

    public static void FailItem(string message) =>
        Err.MarkupLine($"  [red]{Markup.Escape(message)}[/]");

    public static void Blank() => Err.WriteLine();
}
