namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>Proof that a lint can fail, kept as a test.</b> A lint that passes the whole repository says nothing
/// about a rule that no longer matches what it looks for; a broken sample it lets through does. Each lint
/// that keeps samples feeds them through the same function it applies to the repository.
/// </summary>
internal static class LintSamples
{
    /// <summary>
    /// Fails unless <paramref name="lint"/> finds nothing in <paramref name="sample"/>, and, for each break —
    /// the text of the sample replaced, its replacement — reports a finding containing what the break
    /// <c>Says</c>. The clean sample passing is what makes each finding the break's own.
    /// </summary>
    /// <param name="sample">An input the lint accepts.</param>
    /// <param name="breaks">One thing broken per row, and what the lint must say about it.</param>
    /// <param name="lint">The lint's own check over one input, returning what it found.</param>
    public static void AssertEachBreakIsRejected(
        string sample, IEnumerable<(string Text, string BrokenBy, string Says)> breaks, Func<string, IReadOnlyCollection<string>> lint)
    {
        ArgumentNullException.ThrowIfNull(breaks);
        ArgumentNullException.ThrowIfNull(lint);
        var clean = lint(sample);
        Assert.True(clean.Count == 0, $"The clean sample does not pass the lint:\n  {string.Join("\n  ", clean)}");

        var silent = new List<string>();
        foreach (var (text, brokenBy, says) in breaks)
        {
            // Exactly once, so a sample edited out from under a row fails here rather than breaking nothing.
            Assert.True(sample.Split(text).Length == 2, $"The broken text must occur exactly once in the sample: {text}");
            var found = lint(sample.Replace(text, brokenBy, StringComparison.Ordinal));
            if (!found.Any(f => f.Contains(says, StringComparison.Ordinal)))
            {
                silent.Add($"expected \"{says}\", got: {(found.Count == 0 ? "nothing" : string.Join(" | ", found))}");
            }
        }

        Assert.True(silent.Count == 0, $"The lint let these broken samples through:\n  {string.Join("\n  ", silent)}");
    }
}
