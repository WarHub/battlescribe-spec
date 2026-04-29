namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public sealed class SpecStructureTests
{
    private static IEnumerable<(string path, SpecFile spec)> AllSpecFiles()
    {
        var specsDir = SpecLoader.FindSpecsDirectory();
        if (specsDir is null || !Directory.Exists(specsDir))
        {
            yield break;
        }

        foreach (var (path, _, _) in SpecLoader.DiscoverSpecs(specsDir))
        {
            var spec = SpecLoader.Load(path);
            yield return (path, spec);
        }
    }

    public static IEnumerable<object[]> AllSpecs() =>
        AllSpecFiles().Select(x => new object[] { x.path, $"{x.spec.Category}/{x.spec.Id}" });

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void EverySpecHasSetup(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        Assert.True(spec.Setup is not null, $"{specName}: missing 'setup' section");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void LastStepIsExpectedState(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        Assert.True(spec.Steps is not null, $"{specName}: missing 'steps' section");
        Assert.True(spec.Steps.Count > 0, $"{specName}: 'steps' is empty");
        var lastStep = spec.Steps[^1];
        Assert.True(lastStep.ExpectedState is not null,
            $"{specName}: last step must be 'expectedState'");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void NoLegacyAssertSteps(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Steps is null)
        {
            return;
        }
        // StepDef no longer has Assert property, so this verifies
        // that no YAML has 'assert:' keys (they would be silently ignored).
        // We check the raw YAML text instead.
        var text = File.ReadAllText(specPath);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, @"^[ \t]*- assert:", System.Text.RegularExpressions.RegexOptions.Multiline),
            $"{specName}: contains legacy 'assert:' step (use 'expectedState:' instead)");
    }

    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void AllErrorAssertionsHaveFrom(string specPath, string specName)
    {
        var spec = SpecLoader.Load(specPath);
        if (spec.Steps is null)
        {
            return;
        }

        foreach (var step in spec.Steps)
        {
            if (step.ExpectedState?.Errors is not { } errors)
            {
                continue;
            }

            foreach (var err in errors)
            {
                Assert.False(string.IsNullOrEmpty(err.From),
                    $"Error assertion on='{err.On}' is missing 'from:' field in {specName}");
            }

            // Also check engine overrides (allow missing 'from:' since some engines
            // genuinely don't provide constraintId for certain error types)
            if (step.ExpectedState.Engines is { } engines)
            {
                foreach (var (engine, over) in engines)
                {
                    if (over.Errors is not { } overErrors)
                    {
                        continue;
                    }

                    // Engine overrides may omit 'from:' when the engine doesn't
                    // expose structured constraint data for that error type.
                    Assert.All(overErrors, err => Assert.NotNull(err.On));
                }
            }
        }
    }



    [Theory]
    [MemberData(nameof(AllSpecs))]
    public void NoLegacyErrorFields(string specPath, string specName)
    {
        var text = File.ReadAllText(specPath);
        var legacyFields = new[] { "validationErrors", "validationErrorCount", "hasValidationErrors", "noValidationErrors" };
        foreach (var field in legacyFields)
        {
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(text, $@"^[ \t]+{field}:", System.Text.RegularExpressions.RegexOptions.Multiline),
                $"{specName}: contains legacy field '{field}:' (use 'errors:' instead)");
        }
    }
}
