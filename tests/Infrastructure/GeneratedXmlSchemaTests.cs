using BattleScribeSpec.XmlGen;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Every roster spec's generated game system and catalogues must validate against BattleScribe's
/// own XSD.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this did not exist, and what it cost.</b> <c>SchemaValidator</c> has been in the repo,
/// wrapping this exact schema, with zero callers — so nothing checked that the XML we generate is
/// XML BattleScribe will accept. Two engines never noticed: the in-process adapter builds Java
/// model objects directly through <c>JavaModelFactory</c> and never parses a file, and NewRecruit's
/// parser is more forgiving than the schema.
/// </para>
/// <para>
/// The desktop app is not. Six specs emitted a <c>condition</c> with no <c>childId</c> — an
/// attribute <c>QueryFilteredBase</c> marks <c>use="required"</c> — and BattleScribe answered by
/// deleting the staged file ("File was corrupted and has been deleted") and refusing to build the
/// roster. That was diagnosed through a 29-minute UI lane. This test answers the same question
/// offline, in seconds, and fails at the spec that introduced it rather than at whichever lane
/// trips over it later.
/// </para>
/// </remarks>
public sealed class GeneratedXmlSchemaTests
{
    public static TheoryData<string, string> RosterSpecs()
    {
        var data = new TheoryData<string, string>();
        var specsDir = SpecLoader.FindRosterSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            return data;
        }

        foreach (var spec in SpecLoader.DiscoverSpecs(specsDir))
        {
            data.Add(spec.Path, $"{spec.Category}/{spec.Id}");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RosterSpecs))]
    public void GeneratedXml_IsValidAgainstBattleScribeSchema(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Setup.DataSource is { Length: > 0 })
        {
            // File-based setup ships its own data; there is nothing generated to validate.
            return;
        }

        var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);

        AssertValid($"{specName} (game system)", CatXmlGenerator.GenerateGameSystemXml(gameSystem));
        foreach (var (fileName, xml) in CatXmlGenerator.GenerateAllCatalogueXml(gameSystem, catalogues))
        {
            AssertValid($"{specName} ({fileName})", xml);
        }
    }

    private static void AssertValid(string what, string xml)
    {
        var errors = SchemaValidator.ValidateXml(xml);
        Assert.True(
            errors.Count == 0,
            $"{what} does not validate against BattleScribe's XSD — the desktop app will refuse "
            + $"to load it:\n  {string.Join("\n  ", errors)}");
    }
}
