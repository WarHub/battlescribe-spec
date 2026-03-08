namespace BattleScribeSpec.Tests;

[Trait("Category", "Unit")]
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

        // Verify structural content rather than exact formatting
        Assert.Contains("# Engine Compatibility Matrix", markdown);
        Assert.Contains("BattleScribe", markdown);
        Assert.Contains("New Recruit", markdown);
        // BattleScribe: 4/4 conditions, 2/2 modifiers — all pass
        Assert.Contains("4/4", markdown);
        Assert.Contains("2/2", markdown);
        // New Recruit: 3/4 conditions pass, 0/2 modifiers pass
        Assert.Contains("3/4", markdown);
        Assert.Contains("0/2", markdown);
        // Totals: 6/6 (100%) and 3/6 (50%)
        Assert.Contains("6/6", markdown);
        Assert.Contains("3/6", markdown);
        Assert.Contains("100%", markdown);
        Assert.Contains("50%", markdown);
        // Emoji: ✅ for 100%, 🟡 for 75-99%, 🔴 for <75%
        Assert.Contains("✅", markdown);
        Assert.Contains("🟡", markdown);
        Assert.Contains("🔴", markdown);
    }

    [Fact]
    public void GenerateMarkdown_EmptyReports_ProducesMinimalOutput()
    {
        var markdown = CompatibilityMatrix.GenerateMarkdown();
        Assert.Contains("# Engine Compatibility Matrix", markdown);
        Assert.Contains("n/a", markdown);
    }

    [Fact]
    public void GenerateMarkdown_SingleEngine_ShowsAllCategories()
    {
        var report = new ConformanceReport(
            "TestEngine",
            new DateTime(2025, 6, 1),
            3, 2, 1, 0, 67,
            [
                new SpecResultSummary("s1", "cost", "cost test", "passed", []),
                new SpecResultSummary("s2", "cost", "cost test 2", "passed", []),
                new SpecResultSummary("s3", "force", "force test", "failed", ["err"]),
            ]);

        var markdown = CompatibilityMatrix.GenerateMarkdown(report);

        Assert.Contains("TestEngine", markdown);
        Assert.Contains("cost", markdown);
        Assert.Contains("force", markdown);
        Assert.Contains("2/2", markdown); // cost: 2 passed of 2
        Assert.Contains("0/1", markdown); // force: 0 passed of 1
    }
}
