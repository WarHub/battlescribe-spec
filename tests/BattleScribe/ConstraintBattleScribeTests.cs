using BattleScribeSpec.Protocol;

namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public class ConstraintBattleScribeTests
{
    private static (ProtocolGameSystem gs, ProtocolCatalogue[] cats) MakeUncategorisedScenario(ProtocolSelectionEntry[] entries)
    {
        return (
            new ProtocolGameSystem
            {
                Id = "test-gs",
                Name = "Test Game System",
                ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
            },
            [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "test-gs", SelectionEntries = [.. entries] }]);
    }

    private static (ProtocolGameSystem gs, ProtocolCatalogue[] cats) MakeCategorisedScenario(ProtocolSelectionEntry[] entries)
    {
        const string categoryId = "cat-troops";
        foreach (var e in entries)
        {
            if (e.CategoryLinks is not { Count: > 0 })
            {
                e.CategoryLinks = [new ProtocolCategoryLink { Id = $"cl-{e.Id}-troops", TargetId = categoryId, Name = "Troops", Primary = true }];
            }
        }

        return (
            new ProtocolGameSystem
            {
                Id = "test-gs",
                Name = "Test Game System",
                ForceEntries =
                [
                    new ProtocolForceEntry
                    {
                        Id = "fe-1",
                        Name = "Patrol",
                        CategoryLinks = [new ProtocolCategoryLink { Id = "cl-fe-troops", TargetId = categoryId, Name = "Troops", Primary = false }],
                    },
                ],
                CategoryEntries = [new ProtocolCategoryEntry { Id = categoryId, Name = "Troops" }],
            },
            [new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "test-gs", SelectionEntries = [.. entries] }]);
    }

    [Fact]
    public void MinConstraint_ViolatedWhenNotEnoughSelections()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeCategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [new ProtocolConstraint { Id = "c-min", Type = "min", Value = 1, Field = "selections", Scope = "parent" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        // Auto-select satisfies min=1 — no error yet
        Assert.False(engine.HasValidationErrors(), "Min=1 constraint should be satisfied by auto-selection.");

        // Remove the auto-selected entry to trigger violation
        engine.DeselectFirstSelection();
        var errors = engine.GetValidationErrors();
        Assert.True(engine.HasValidationErrors(), "Expected a min-constraint validation error after deselecting.");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void MaxConstraint_ViolatedWhenTooManySelections()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeCategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [new ProtocolConstraint { Id = "c-max", Type = "max", Value = 1, Field = "selections", Scope = "parent" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        engine.SelectFirstAvailableEntry();
        var errorsAfter1 = engine.GetValidationErrors();
        Assert.False(engine.HasValidationErrors());
        Assert.Empty(errorsAfter1);

        engine.SelectFirstAvailableEntry();
        var errorsAfter2 = engine.GetValidationErrors();
        Assert.True(engine.HasValidationErrors(), "Expected a max-constraint validation error after selecting twice.");
        Assert.NotEmpty(errorsAfter2);
        Assert.True(errorsAfter2.Count >= errorsAfter1.Count,
            $"Expected error count to stay the same or increase after exceeding max (before={errorsAfter1.Count}, after={errorsAfter2.Count}).");
    }

    [Fact]
    public void MinAndMax_ConstraintsSatisfied()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeCategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [
                    new ProtocolConstraint { Id = "c-min", Type = "min", Value = 1, Field = "selections", Scope = "parent" },
                    new ProtocolConstraint { Id = "c-max", Type = "max", Value = 3, Field = "selections", Scope = "parent" },
                ] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        engine.SelectFirstAvailableEntry();
        var errors = engine.GetValidationErrors();
        Assert.False(engine.HasValidationErrors());
        Assert.Empty(errors);
    }

    [Fact]
    public void MaxUnlimited_NoViolation()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeCategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [new ProtocolConstraint { Id = "c-max", Type = "max", Value = -1, Field = "selections", Scope = "parent" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        for (var i = 0; i < 5; i++)
        {
            engine.SelectFirstAvailableEntry();
        }

        var errors = engine.GetValidationErrors();
        Assert.False(engine.HasValidationErrors());
        Assert.Empty(errors);
    }

    [Fact]
    public void MinConstraint_UncategorisedParentScope_IsSkipped()
    {
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeUncategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [new ProtocolConstraint { Id = "c-min", Type = "min", Value = 1, Field = "selections", Scope = "parent" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        var errors = engine.GetValidationErrors();
        Assert.False(engine.HasValidationErrors());
        Assert.Empty(errors);
    }

    [Fact]
    public void MaxConstraint_TripleViolation_ErrorMultiplicity()
    {
        // Diagnostic: 3 selections of max=1 — verify exact error count and placement
        using var engine = new BattleScribeEngine();
        var (gs, cats) = MakeCategorisedScenario([
            new ProtocolSelectionEntry { Id = "se-1", Name = "Marine Squad",
                Constraints = [new ProtocolConstraint { Id = "c-max", Type = "max", Value = 1, Field = "selections", Scope = "parent" }] }
        ]);

        engine.SetupFromProtocol(gs, cats);
        engine.AddForceByIndex(0);

        // 1st selection — at limit
        engine.SelectFirstAvailableEntry();
        var errorsAt1 = engine.GetValidationErrors();

        // 2nd selection — 1 over limit
        engine.SelectFirstAvailableEntry();
        var errorsAt2 = engine.GetValidationErrors();

        // 3rd selection — 2 over limit
        engine.SelectFirstAvailableEntry();
        var errorsAt3 = engine.GetValidationErrors();

        Assert.Empty(errorsAt1);
        Assert.NotEmpty(errorsAt2);
        Assert.NotEmpty(errorsAt3);

        // BS produces exactly 1 error regardless of how many selections exceed max.
        // The error message changes ("1 too many" → "2 too many") but count stays 1.
        Assert.Single(errorsAt2);
        Assert.Single(errorsAt3);

        // And the one error is raised on the CATEGORY that counted them, not on any of the three
        // selections. That is BattleScribe's own answer: a collective over-limit violation is about
        // a set, and the container is what noticed. A shared pass used to move it onto "the
        // selection responsible" to match NewRecruit; the corpus records the divergence now (#426).
        foreach (var e in errorsAt3)
        {
            Assert.Equal("category", e.RaisedOnType);
        }
    }

    [Fact]
    public void ForceEntryMaxConstraint_ResolvesEntryAndConstraintIds()
    {
        // Dump actual errors for force-field constraints on ForceEntry (max=2, 3 forces)
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test GS",
            ForceEntries = [new ProtocolForceEntry
            {
                Id = "fe-patrol",
                Name = "Patrol",
                Constraints = [new ProtocolConstraint { Id = "con-max-forces", Type = "max", Value = 2, Field = "forces", Scope = "roster" }]
            }],
        };
        var cat = new ProtocolCatalogue { Id = "cat-1", Name = "Cat", GameSystemId = "test-gs" };
        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);
        engine.AddForceByIndex(0);
        engine.AddForceByIndex(0); // 3 forces, max=2

        var errors = engine.GetValidationErrors();
        // Each error should have entryId=fe-patrol, constraintId=con-max-forces
        Assert.NotEmpty(errors);
        foreach (var e in errors)
        {
            Assert.Equal("roster", e.RaisedOnType);
            Assert.Equal("fe-patrol", e.EntryId);
            Assert.Equal("con-max-forces", e.ConstraintId);
        }
    }

    [Fact]
    public void SharedEntryConstraint_ResolvesEntryAndConstraintIds()
    {
        // Dump actual errors for shared entry with scope=roster, shared=true
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test GS",
            CategoryEntries = [new ProtocolCategoryEntry { Id = "cat-troops", Name = "Troops" }],
            ForceEntries = [new ProtocolForceEntry
            {
                Id = "fe-1",
                Name = "Patrol",
                CategoryLinks = [new ProtocolCategoryLink { Id = "cl-troops", TargetId = "cat-troops" }]
            }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "test-gs",
            SharedSelectionEntries = [new ProtocolSelectionEntry
            {
                Id = "shared-unit",
                Name = "Elite Guard",
                Type = "unit",
                CategoryLinks = [new ProtocolCategoryLink { Id = "cl-eg", TargetId = "cat-troops", Primary = true }],
                Constraints = [new ProtocolConstraint { Id = "con-max", Type = "max", Value = 2, Field = "selections", Scope = "roster", Shared = true }],
            }],
            EntryLinks = [new ProtocolEntryLink
            {
                Id = "link-1",
                Name = "Elite Guard",
                TargetId = "shared-unit",
                Type = "selectionEntry",
            }],
        };
        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);
        engine.SelectFirstAvailableEntry();
        engine.SelectFirstAvailableEntry();
        engine.SelectFirstAvailableEntry(); // 3 selections, max=2

        var errors = engine.GetValidationErrors();
        Assert.NotEmpty(errors);
        // Check that error has resolved entry/constraint IDs
        foreach (var e in errors)
        {
            Assert.NotNull(e.EntryId);
            Assert.NotNull(e.ConstraintId);
        }
    }

    [Fact]
    public void SelectionEntryFieldForces_ResolvesEntryAndConstraintIds()
    {
        // field=forces on a SelectionEntry — always violated (force count is always 0)
        using var engine = new BattleScribeEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test GS",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-patrol", Name = "Patrol" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "test-gs",
            SelectionEntries = [new ProtocolSelectionEntry
            {
                Id = "se-unit",
                Name = "Unit A",
                Type = "unit",
                Constraints = [new ProtocolConstraint { Id = "con-force-field", Type = "min", Value = 1, Field = "forces", Scope = "roster" }],
            }],
        };
        engine.SetupFromProtocol(gs, [cat]);
        engine.AddForceByIndex(0);

        var errors = engine.GetValidationErrors();
        Assert.NotEmpty(errors);
        foreach (var e in errors)
        {
            Assert.Equal("roster", e.RaisedOnType);
            Assert.Equal("se-unit", e.EntryId);
            Assert.Equal("con-force-field", e.ConstraintId);
        }
    }

    [Fact]
    public void EntryLinkOwnConstraint_ResolvesFromLinkConstraint()
    {
        var engine = new BattleScribeRosterEngine();
        var gs = new ProtocolGameSystem
        {
            Id = "test-gs",
            Name = "Test GS",
            ForceEntries = [new ProtocolForceEntry { Id = "fe-1", Name = "Patrol" }],
        };
        var cat = new ProtocolCatalogue
        {
            Id = "cat-1",
            Name = "Cat",
            GameSystemId = "test-gs",
            SharedSelectionEntries = [new ProtocolSelectionEntry
            {
                Id = "shared-unit",
                Name = "Strike Team",
                Type = "unit",
            }],
            EntryLinks = [new ProtocolEntryLink
            {
                Id = "link-1",
                Name = "Strike Team",
                TargetId = "shared-unit",
                Type = "selectionEntry",
                Constraints = [new ProtocolConstraint { Id = "con-link-max", Type = "max", Value = 2, Field = "selections", Scope = "force" }],
            }],
        };
        engine.Setup(gs, [cat]);
        var addForceOut = engine.AddForce("fe-1", "cat-1");
        var fId = addForceOut.ForceId!;
        engine.SelectEntry(fId, "shared-unit");
        engine.SelectEntry(fId, "shared-unit");
        engine.SelectEntry(fId, "shared-unit");

        var errors = engine.GetValidationErrors();
        Assert.Single(errors);
        var e = errors[0];

        // `from` is the LINK, because the link is what declares the constraint — that is the claim
        // this test was written for and it is unchanged.
        Assert.Equal("link-1", e.EntryId);
        Assert.Equal("con-link-max", e.ConstraintId);

        // The raising node is the FORCE the constraint is scoped to, named by the id `addForce`
        // handed back. It used to be reported as `selection shared-unit`: a shared pass re-homed the
        // error onto the link's target entry, which is neither the node BattleScribe raised it on
        // nor a node at all.
        Assert.Equal("force", e.RaisedOnType);
        Assert.Equal(fId, e.RaisedOnId);
        Assert.Equal("fe-1", e.RaisedOnEntryId);
    }
}
