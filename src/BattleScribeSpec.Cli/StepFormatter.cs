using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Cli;

/// <summary>Human-readable rendering of spec steps and safe file names for artifacts.</summary>
internal static class StepFormatter
{
    public static string DescribeStep(StepDef step)
    {
        if (step.Action is { } action)
        {
            var parts = new List<string> { action };

            void Add(string label, object? value)
            {
                if (value is not null)
                {
                    parts.Add($"{label}={value}");
                }
            }

            if (step.Id is { Length: > 0 } sid)
            {
                parts.Add($"id={sid}");
            }

            Add("forceEntryId", step.ForceEntryId);
            Add("entryId", step.EntryId);
            Add("catalogueId", step.CatalogueId);
            Add("forceId", step.ForceId);
            Add("selectionId", step.SelectionId);
            Add("count", step.Count);
            Add("costTypeId", step.CostTypeId);
            Add("value", step.Value);
            // Roster XML payloads are far too long to dump inline — report the size instead.
            Add("content", step.Content is { } xml ? $"<{xml.Length} chars>" : null);

            return string.Join(" ", parts);
        }

        return step.ExpectedState is not null ? "expectedState (assertion)" : "(unknown)";
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
