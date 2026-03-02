namespace BattleScribeSpec.Tests;

public class CompatibilityMatrixTests
{
    [Fact]
    public void GenerateMarkdown_IncludesHeadersRowsAndEmoji()
    {
        var battleScribe = new ConformanceReport(
            "BattleScribe",
            new DateTime(2025, 1, 15),
            6,
            6,
            0,
            0,
            100,
            [
                new SpecResultSummary("c1", "condition", "cond", "passed", []),
                new SpecResultSummary("c2", "condition", "cond", "passed", []),
                new SpecResultSummary("c3", "condition", "cond", "passed", []),
                new SpecResultSummary("c4", "condition", "cond", "passed", []),
                new SpecResultSummary("m1", "modifier", "mod", "passed", []),
                new SpecResultSummary("m2", "modifier", "mod", "passed", [])
            ]);

        var newRecruit = new ConformanceReport(
            "New Recruit",
            new DateTime(2025, 1, 15),
            6,
            3,
            3,
            0,
            50,
            [
                new SpecResultSummary("c1", "condition", "cond", "passed", []),
                new SpecResultSummary("c2", "condition", "cond", "passed", []),
                new SpecResultSummary("c3", "condition", "cond", "passed", []),
                new SpecResultSummary("c4", "condition", "cond", "failed", ["x"]),
                new SpecResultSummary("m1", "modifier", "mod", "failed", ["x"]),
                new SpecResultSummary("m2", "modifier", "mod", "failed", ["x"])
            ]);

        var markdown = CompatibilityMatrix.GenerateMarkdown(battleScribe, newRecruit);

        Assert.Contains("# Engine Compatibility Matrix", markdown);
        Assert.Contains("| Category | BattleScribe | New Recruit |", markdown);
        Assert.Contains("| condition | 4/4 ✅ | 3/4 🟡 |", markdown);
        Assert.Contains("| modifier | 2/2 ✅ | 0/2 🔴 |", markdown);
        Assert.Contains("| **Total** | **6/6 (100%)** | **3/6 (50%)** |", markdown);
    }
}
