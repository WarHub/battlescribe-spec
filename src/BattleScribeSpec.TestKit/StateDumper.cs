using System.Text.Json;

namespace BattleScribeSpec;

/// <summary>
/// Options controlling state dump output format.
/// </summary>
public record DumpOptions(
    bool Json = false,
    IDumpEnricher? Enricher = null);

/// <summary>
/// Pretty-prints roster state as a human-readable tree or JSON.
/// Used by the spec debugger and available for ad-hoc debugging.
/// </summary>
public static class StateDumper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Dump roster state and validation errors to the given writer.
    /// </summary>
    public static void Dump(
        RosterState state,
        IReadOnlyList<ValidationErrorState> errors,
        TextWriter writer,
        DumpOptions? options = null)
    {
        options ??= new DumpOptions();

        if (options.Json)
        {
            DumpJson(state, errors, writer, options);
            return;
        }

        DumpTree(state, errors, writer, options);
    }

    private static void DumpJson(
        RosterState state,
        IReadOnlyList<ValidationErrorState> errors,
        TextWriter writer,
        DumpOptions options)
    {
        var dump = new Dictionary<string, object?>
        {
            ["roster"] = state,
            ["validationErrors"] = errors
        };

        if (options.Enricher is { } enricher)
        {
            var extra = enricher.EnrichDump(new DumpContext(state, errors));
            foreach (var (key, value) in extra)
                dump[key] = value;
        }

        writer.WriteLine(JsonSerializer.Serialize(dump, JsonOpts));
    }

    private static void DumpTree(
        RosterState state,
        IReadOnlyList<ValidationErrorState> errors,
        TextWriter writer,
        DumpOptions options)
    {
        writer.WriteLine($"Roster: {state.Name}  (gameSystemId: {state.GameSystemId})");

        // Costs
        if (state.Costs.Count > 0)
        {
            var costStr = string.Join(", ", state.Costs.Select(c => $"{c.Name}={c.Value}"));
            writer.WriteLine($"Costs: {costStr}");
        }

        // Forces
        writer.WriteLine($"Forces: {state.Forces.Count}");
        for (var fi = 0; fi < state.Forces.Count; fi++)
            DumpForce(state.Forces[fi], writer, indent: "  ", index: fi);

        // Errors
        writer.WriteLine();
        if (errors.Count == 0)
        {
            writer.WriteLine("Errors: (none)");
        }
        else
        {
            writer.WriteLine($"Errors: {errors.Count}");
            foreach (var err in errors)
            {
                var parts = new List<string> { err.Message };
                if (err.OwnerType is { } ot) parts.Add($"owner={ot}");
                if (err.OwnerId is { } oid) parts.Add($"ownerId={oid}");
                if (err.EntryId is { } eid) parts.Add($"entryId={eid}");
                if (err.ConstraintId is { } cid) parts.Add($"constraintId={cid}");
                writer.WriteLine($"  - {string.Join("  ", parts)}");
            }
        }

        // Engine enrichment
        if (options.Enricher is { } enricher)
        {
            var extra = enricher.EnrichDump(new DumpContext(state, errors));
            if (extra.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine("Engine-specific:");
                foreach (var (key, value) in extra)
                {
                    writer.WriteLine($"  [{key}]");
                    var valueStr = value?.ToString() ?? "(null)";
                    foreach (var line in valueStr.Split('\n'))
                        writer.WriteLine($"    {line.TrimEnd('\r')}");
                }
            }
        }
    }

    private static void DumpForce(ForceState force, TextWriter writer, string indent, int index)
    {
        writer.Write($"{indent}Force[{index}]: \"{force.Name}\"");
        if (force.CatalogueId is { } catId) writer.Write($"  catalogueId={catId}");
        if (force.PublicationId is { } pubId) writer.Write($"  pub={pubId}");
        if (force.Page is { } page) writer.Write($"  p.{page}");
        writer.WriteLine();

        // Force profiles
        if (force.Profiles.Count > 0)
        {
            writer.WriteLine($"{indent}  Profiles: {force.Profiles.Count}");
            foreach (var p in force.Profiles)
                DumpProfile(p, writer, indent + "    ");
        }

        // Force rules
        if (force.Rules.Count > 0)
        {
            writer.WriteLine($"{indent}  Rules: {force.Rules.Count}");
            foreach (var r in force.Rules)
                DumpRule(r, writer, indent + "    ");
        }

        // Selections
        writer.WriteLine($"{indent}  Selections: {force.Selections.Count}");
        for (var si = 0; si < force.Selections.Count; si++)
            DumpSelection(force.Selections[si], writer, indent + "    ", si);

        // Child forces
        if (force.ChildForces.Count > 0)
        {
            writer.WriteLine($"{indent}  ChildForces: {force.ChildForces.Count}");
            for (var ci = 0; ci < force.ChildForces.Count; ci++)
                DumpForce(force.ChildForces[ci], writer, indent + "    ", ci);
        }
    }

    private static void DumpSelection(SelectionState sel, TextWriter writer, string indent, int index)
    {
        writer.Write($"{indent}[{index}] \"{sel.Name}\"");
        if (sel.Type is { } type) writer.Write($" ({type})");
        writer.Write($" ×{sel.Number}");
        if (sel.Hidden) writer.Write(" [hidden]");
        if (sel.EntryId is { } eid) writer.Write($"  entryId={eid}");
        writer.WriteLine();

        // Costs
        if (sel.Costs.Count > 0)
        {
            var costStr = string.Join(", ", sel.Costs.Select(c => $"{c.Name}={c.Value}"));
            writer.WriteLine($"{indent}  Costs: {costStr}");
        }

        // Publication / page
        if (sel.PublicationId is not null || sel.Page is not null || sel.PublicationName is not null)
        {
            var parts = new List<string>();
            if (sel.PublicationName is { } pn) parts.Add($"pub=\"{pn}\"");
            if (sel.PublicationId is { } pid) parts.Add($"pubId={pid}");
            if (sel.Page is { } pg) parts.Add($"p.{pg}");
            writer.WriteLine($"{indent}  {string.Join("  ", parts)}");
        }

        // Categories
        if (sel.Categories.Count > 0)
        {
            var catStr = string.Join(", ",
                sel.Categories.Select(c => c.Primary ? $"*{c.Name}" : c.Name));
            writer.WriteLine($"{indent}  Categories: [{catStr}]");
        }

        // Profiles
        if (sel.Profiles.Count > 0)
        {
            writer.WriteLine($"{indent}  Profiles: {sel.Profiles.Count}");
            foreach (var p in sel.Profiles)
                DumpProfile(p, writer, indent + "    ");
        }

        // Rules
        if (sel.Rules.Count > 0)
        {
            writer.WriteLine($"{indent}  Rules: {sel.Rules.Count}");
            foreach (var r in sel.Rules)
                DumpRule(r, writer, indent + "    ");
        }

        // Children (recursive)
        if (sel.Children.Count > 0)
        {
            writer.WriteLine($"{indent}  Children: {sel.Children.Count}");
            for (var ci = 0; ci < sel.Children.Count; ci++)
                DumpSelection(sel.Children[ci], writer, indent + "    ", ci);
        }
    }

    private static void DumpProfile(ProfileState p, TextWriter writer, string indent)
    {
        writer.Write($"{indent}- \"{p.Name}\"");
        if (p.TypeName is { } tn) writer.Write($" [{tn}]");
        if (p.Hidden) writer.Write(" [hidden]");
        if (p.PublicationId is { } pid) writer.Write($"  pub={pid}");
        if (p.Page is { } pg) writer.Write($"  p.{pg}");
        writer.WriteLine();

        if (p.Characteristics.Count > 0)
        {
            var chars = string.Join(", ", p.Characteristics.Select(c => $"{c.Name}={c.Value}"));
            writer.WriteLine($"{indent}  {chars}");
        }
    }

    private static void DumpRule(RuleState r, TextWriter writer, string indent)
    {
        writer.Write($"{indent}- \"{r.Name}\"");
        if (r.Hidden) writer.Write(" [hidden]");
        if (r.PublicationId is { } pid) writer.Write($"  pub={pid}");
        if (r.Page is { } pg) writer.Write($"  p.{pg}");
        writer.WriteLine();

        if (!string.IsNullOrEmpty(r.Description))
            writer.WriteLine($"{indent}  {r.Description}");
    }
}
