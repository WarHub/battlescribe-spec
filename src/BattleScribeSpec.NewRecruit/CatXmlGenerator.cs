namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Generates BattleScribe .cat/.gst XML files from spec setup data.
/// Uses WarHub.ArmouryModel to build and serialize the data model.
/// This bridges the gap between synthetic YAML spec data and NR's file-based loading.
/// </summary>
public static class CatXmlGenerator
{
    /// <summary>
    /// Generate a .gst (game system) XML string from a GameSystemSpec.
    /// </summary>
    public static string GenerateGameSystemXml(GameSystemSpec gameSystem)
    {
        // TODO: Use WarHub.ArmouryModel to build a proper game system node
        // and serialize to BattleScribe XML format.
        // For now, return a minimal valid .gst XML.
        var costTypes = "";
        if (gameSystem.CostTypes is { Length: > 0 })
        {
            var costTypeEntries = string.Join("\n",
                gameSystem.CostTypes.Select(ct =>
                    $"      <costType id=\"{Esc(ct.Id)}\" name=\"{Esc(ct.Name)}\" defaultCostLimit=\"-1.0\" hidden=\"false\"/>"));
            costTypes = $"\n    <costTypes>\n{costTypeEntries}\n    </costTypes>";
        }

        var forceEntries = "";
        if (gameSystem.ForceEntries is { Length: > 0 })
        {
            var feEntries = string.Join("\n",
                gameSystem.ForceEntries.Select(fe =>
                    $"      <forceEntry id=\"{Esc(fe.Id)}\" name=\"{Esc(fe.Name)}\" hidden=\"false\"/>"));
            forceEntries = $"\n    <forceEntries>\n{feEntries}\n    </forceEntries>";
        }

        var categoryEntries = "";
        if (gameSystem.CategoryEntries is { Length: > 0 })
        {
            var catEntries = string.Join("\n",
                gameSystem.CategoryEntries.Select(ce =>
                    $"      <categoryEntry id=\"{Esc(ce.Id)}\" name=\"{Esc(ce.Name)}\" hidden=\"false\"/>"));
            categoryEntries = $"\n    <categoryEntries>\n{catEntries}\n    </categoryEntries>";
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <gameSystem id="{Esc(gameSystem.Id)}" name="{Esc(gameSystem.Name)}" revision="1" battleScribeVersion="2.03" xmlns="http://www.battlescribe.net/schema/gameSystemSchema">{costTypes}{forceEntries}{categoryEntries}
            </gameSystem>
            """;
    }

    /// <summary>
    /// Generate a .cat (catalogue) XML string from a CatalogueSpec.
    /// </summary>
    public static string GenerateCatalogueXml(CatalogueSpec catalogue)
    {
        // TODO: Full implementation with WarHub.ArmouryModel for complex catalogues.
        // For now, generate minimal valid .cat XML.
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <catalogue id="{Esc(catalogue.Id)}" name="{Esc(catalogue.Name)}" revision="1" battleScribeVersion="2.03" gameSystemId="{Esc(catalogue.GameSystemId)}" xmlns="http://www.battlescribe.net/schema/catalogueSchema">
            </catalogue>
            """;
    }

    private static string Esc(string value) =>
        System.Security.SecurityElement.Escape(value) ?? value;
}
