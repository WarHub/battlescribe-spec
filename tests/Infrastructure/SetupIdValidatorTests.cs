namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
public sealed class SetupIdValidatorTests
{
    [Fact]
    public void ThrowsOnDuplicateIds()
    {
        var yaml = @"
id: dup-test
category: test
description: Test duplicate detection

setup:
  gameSystem:
    id: gs1
    name: Test
    categoryEntries:
      - id: shared-id
        name: Cat A
      - id: shared-id
        name: Cat B
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
      selectionEntries:
        - id: se1
          name: Entry
";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SpecLoader.LoadFromYaml(yaml, "dup-test"));
        Assert.Contains("shared-id", ex.Message);
        Assert.Contains("duplicate IDs", ex.Message);
    }

    [Fact]
    public void ThrowsOnDuplicateAcrossGameSystemAndCatalogue()
    {
        var yaml = @"
id: cross-dup-test
category: test
description: Duplicate across game system and catalogue

setup:
  gameSystem:
    id: gs1
    name: Test
    forceEntries:
      - id: fe1
        name: Force
        categoryLinks:
          - id: cross-id
            targetId: cat-1
            name: CL
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
      selectionEntries:
        - id: cross-id
          name: Entry
";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SpecLoader.LoadFromYaml(yaml, "cross-dup-test"));
        Assert.Contains("cross-id", ex.Message);
    }

    [Fact]
    public void ThrowsOnDuplicateNestedConstraintIds()
    {
        var yaml = @"
id: nested-dup-test
category: test
description: Duplicate constraint IDs

setup:
  gameSystem:
    id: gs1
    name: Test
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
      selectionEntries:
        - id: se1
          name: Entry A
          constraints:
            - id: con-dup
              type: min
              value: 1
        - id: se2
          name: Entry B
          constraints:
            - id: con-dup
              type: max
              value: 3
";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SpecLoader.LoadFromYaml(yaml, "nested-dup-test"));
        Assert.Contains("con-dup", ex.Message);
    }

    [Fact]
    public void AcceptsDuplicateIdsWhenTagged()
    {
        var yaml = @"
id: tagged-dup-test
category: test
description: Tagged spec with duplicate IDs
tags:
  - duplicate-ids

setup:
  gameSystem:
    id: gs1
    name: Test
    categoryEntries:
      - id: shared-id
        name: Cat A
      - id: shared-id
        name: Cat B
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
";
        // Should not throw
        var spec = SpecLoader.LoadFromYaml(yaml, "tagged-dup-test");
        Assert.Equal("tagged-dup-test", spec.Id);
    }

    [Fact]
    public void AcceptsUniqueIds()
    {
        var yaml = @"
id: unique-test
category: test
description: All unique IDs

setup:
  gameSystem:
    id: gs1
    name: Test
    costTypes:
      - id: ct1
        name: Points
    categoryEntries:
      - id: cat-a
        name: Cat A
      - id: cat-b
        name: Cat B
    forceEntries:
      - id: fe1
        name: Force
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
      selectionEntries:
        - id: se1
          name: Entry A
          constraints:
            - id: con1
              type: min
              value: 1
        - id: se2
          name: Entry B
";
        // Should not throw
        var spec = SpecLoader.LoadFromYaml(yaml, "unique-test");
        Assert.Equal("unique-test", spec.Id);
    }

    [Fact]
    public void SkipsEmptyIds()
    {
        var yaml = @"
id: empty-id-test
category: test
description: Empty IDs are not checked

setup:
  gameSystem:
    id: gs1
    name: Test
    categoryEntries:
      - id: ''
        name: Cat A
      - id: ''
        name: Cat B
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
";
        // Should not throw — empty IDs are skipped
        var spec = SpecLoader.LoadFromYaml(yaml, "empty-id-test");
        Assert.Equal("empty-id-test", spec.Id);
    }

    [Fact]
    public void ErrorMessageIncludesLocations()
    {
        var yaml = @"
id: loc-test
category: test
description: Error message has locations

setup:
  gameSystem:
    id: gs1
    name: Test
  catalogues:
    - id: cat1
      name: Catalogue
      gameSystemId: gs1
      selectionEntries:
        - id: dup-se
          name: Entry A
        - id: dup-se
          name: Entry B
";
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SpecLoader.LoadFromYaml(yaml, "loc-test"));
        Assert.Contains("selectionEntries[0]", ex.Message);
        Assert.Contains("selectionEntries[1]", ex.Message);
    }
}
