using System.Text.RegularExpressions;
using BattleScribeSpec;
using BattleScribeSpec.Protocol;
using SpecMigrator;

// ─── CLI args ────────────────────────────────────────────────────────────
var dryRun = args.Contains("--dry-run");
var filterIdx = Array.IndexOf(args, "--filter");
var filter = filterIdx >= 0 && filterIdx + 1 < args.Length ? args[filterIdx + 1] : null;

// ─── Find specs directory ────────────────────────────────────────────────
var specsDir = SpecLoader.FindSpecsDirectory()
    ?? throw new InvalidOperationException("Cannot find specs directory");

Console.WriteLine($"Migrating specs in: {specsDir}");
Console.WriteLine($"Mode: {(dryRun ? "DRY RUN" : "LIVE")}");
Console.WriteLine();

var changed = 0;
var skipped = 0;
var errors = new List<string>();

foreach (var (path, id, category) in SpecLoader.DiscoverSpecs(specsDir))
{
    if (filter is not null && !id.Contains(filter, StringComparison.OrdinalIgnoreCase))
        continue;

    try
    {
        var originalText = File.ReadAllText(path);

        // Skip specs without old fields
        if (!MigrationHelper.HasOldFields(originalText))
        {
            skipped++;
            continue;
        }

        // Skip dataSource specs (real-world data, no inline setup to parse)
        if (originalText.Contains("dataSource:"))
        {
            skipped++;
            continue;
        }

        var spec = SpecLoader.Load(path);
        var migrated = MigrationHelper.MigrateSpec(originalText, spec);

        if (migrated != originalText)
        {
            var rel = Path.GetRelativePath(specsDir, path).Replace('\\', '/');
            if (dryRun)
            {
                Console.WriteLine($"WOULD CHANGE: {rel}");
            }
            else
            {
                File.WriteAllText(path, migrated);
                Console.WriteLine($"MIGRATED: {rel}");
            }
            changed++;
        }
    }
    catch (Exception ex)
    {
        var rel = Path.GetRelativePath(specsDir, path).Replace('\\', '/');
        errors.Add($"{rel}: {ex.Message}");
        Console.Error.WriteLine($"ERROR: {rel}: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"Changed: {changed}, Skipped: {skipped}, Errors: {errors.Count}");
if (errors.Count > 0)
{
    Console.WriteLine("Errors:");
    foreach (var e in errors)
        Console.WriteLine($"  {e}");
}

return errors.Count > 0 ? 1 : 0;
