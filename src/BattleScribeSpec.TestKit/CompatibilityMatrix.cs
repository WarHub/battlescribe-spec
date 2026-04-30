using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BattleScribeSpec;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ConformanceReport))]
internal partial class ConformanceReportJsonContext : JsonSerializerContext;

public static class CompatibilityMatrix
{
    /// <summary>
    /// Load multiple conformance reports and generate a markdown compatibility matrix.
    /// </summary>
    public static string GenerateMarkdown(params ConformanceReport[] reports)
    {
        if (reports.Length == 0)
        {
            return "# Engine Compatibility Matrix\n\nGenerated: n/a\n";
        }

        var engines = reports.Select(r => r.Engine).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var categories = reports
            .SelectMany(r => r.Results)
            .Select(r => r.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var perEngine = new Dictionary<string, Dictionary<string, (int Passed, int Total)>>(StringComparer.OrdinalIgnoreCase);
        var engineTotals = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);

        foreach (var engine in engines)
        {
            perEngine[engine] = new Dictionary<string, (int Passed, int Total)>(StringComparer.OrdinalIgnoreCase);
            engineTotals[engine] = (0, 0);
        }

        foreach (var report in reports)
        {
            var byCategory = report.Results
                .Where(r => !string.Equals(r.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Category = g.Key,
                    Passed = g.Count(r => string.Equals(r.Status, "passed", StringComparison.OrdinalIgnoreCase)),
                    Total = g.Count(),
                });

            foreach (var item in byCategory)
            {
                perEngine[report.Engine].TryGetValue(item.Category, out var existing);
                perEngine[report.Engine][item.Category] = (existing.Passed + item.Passed, existing.Total + item.Total);

                var totals = engineTotals[report.Engine];
                engineTotals[report.Engine] = (totals.Passed + item.Passed, totals.Total + item.Total);
            }
        }

        var generated = reports.Max(r => r.GeneratedAt).ToString("yyyy-MM-dd");
        var sb = new StringBuilder();
        sb.AppendLine("# Engine Compatibility Matrix");
        sb.AppendLine();
        sb.AppendLine($"Generated: {generated}");
        sb.AppendLine();
        sb.Append("| Category |");
        foreach (var engine in engines)
        {
            sb.Append($" {engine} |");
        }

        sb.AppendLine();
        sb.Append("|----------|");
        foreach (var _ in engines)
        {
            sb.Append(":----------:|");
        }

        sb.AppendLine();

        foreach (var category in categories)
        {
            sb.Append($"| {category} |");
            foreach (var engine in engines)
            {
                if (!perEngine[engine].TryGetValue(category, out var counts) || counts.Total == 0)
                {
                    sb.Append(" — |");
                    continue;
                }

                var rate = (double)counts.Passed / counts.Total * 100.0;
                sb.Append($" {counts.Passed}/{counts.Total} {GetEmoji(rate)} |");
            }
            sb.AppendLine();
        }

        sb.Append("| **Total** |");
        foreach (var engine in engines)
        {
            var totals = engineTotals[engine];
            if (totals.Total == 0)
            {
                sb.Append(" **—** |");
            }
            else
            {
                sb.Append($" **{totals.Passed}/{totals.Total} ({(double)totals.Passed / totals.Total * 100:0}%)** |");
            }
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Legend");
        sb.AppendLine("- ✅ 100% pass rate");
        sb.AppendLine("- 🟡 75-99% pass rate");
        sb.AppendLine("- 🔴 <75% pass rate");
        sb.AppendLine("- — Not tested");
        return sb.ToString();
    }

    /// <summary>
    /// Load conformance reports from JSON files.
    /// </summary>
    public static ConformanceReport LoadReport(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var report = JsonSerializer.Deserialize(json, ConformanceReportJsonContext.Default.ConformanceReport);

        return report ?? throw new InvalidDataException($"Failed to deserialize conformance report: {jsonPath}");
    }

    private static string GetEmoji(double passRate)
    {
        if (passRate >= 100.0)
        {
            return "✅";
        }

        if (passRate >= 75.0)
        {
            return "🟡";
        }

        return "🔴";
    }
}
