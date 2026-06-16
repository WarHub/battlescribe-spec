using System.CommandLine;

namespace BattleScribeSpec.Cli;

/// <summary>
/// <c>bs-spec format [&lt;dir&gt;] [--check]</c> — format roster spec YAML in place, or
/// report files needing formatting (exit 1) with <c>--check</c>.
/// </summary>
internal static class FormatCommand
{
    public static Command Create()
    {
        var dir = new Argument<string?>("dir")
        {
            Description = "Directory to format (default: specs/roster).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var check = new Option<bool>("--check")
        {
            Description = "Report files needing formatting without fixing them (exit 1 if any).",
        };

        var command = new Command("format", "Format roster spec YAML files (or check formatting with --check).");
        command.Arguments.Add(dir);
        command.Options.Add(check);

        command.SetAction(parseResult => Execute(parseResult.GetValue(dir), parseResult.GetValue(check)));
        return command;
    }

    private static int Execute(string? dir, bool check)
    {
        var targetDir = dir
            ?? SpecLoader.FindRosterSpecsDirectory()
            ?? throw new InvalidOperationException(
                "Could not locate specs/roster directory. Pass a directory as argument.");

        if (check)
        {
            Ui.Info($"Checking formatting in: {targetDir}");
            var issues = SpecFormatter.FormatDirectory(targetDir, checkOnly: true, log: Console.Error);
            if (issues > 0)
            {
                Ui.Warn($"{issues} file(s) need formatting. Run `bs-spec format` to fix.");
                return 1;
            }

            Ui.Pass("All files are correctly formatted.");
            return 0;
        }

        Ui.Info($"Formatting specs in: {targetDir}");
        var fixedCount = SpecFormatter.FormatDirectory(targetDir, checkOnly: false, log: Console.Error);
        Ui.Info(fixedCount > 0 ? $"Fixed {fixedCount} file(s)." : "All files are already correctly formatted.");
        return 0;
    }
}
