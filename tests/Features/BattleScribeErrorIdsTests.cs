using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Pins the parse of BattleScribe's <c>ownerId::entryId::constraintId</c> validation-error ids.
/// </summary>
/// <remarks>
/// The rule had three copies and no direct test — only transitive exercise through the spec corpus,
/// which covers the three-segment shape and nothing else. These cases are also the ones the Java
/// agent's hand-kept mirror (<c>EngineAccessor.parseValidationErrorIds</c>) is meant to answer
/// identically; it cannot call this code, so this file is the only written statement of what the
/// two share.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class BattleScribeErrorIdsTests
{
    [Fact]
    public void ThreeSegments_KeysByEntry_ValuesByConstraint()
    {
        var map = BattleScribeErrorIds.Parse(["force-1::shared-unit::con-max-shared"]);

        Assert.Equal(["shared-unit"], map.Keys);
        Assert.Equal(["con-max-shared"], map["shared-unit"]);
    }

    [Fact]
    public void FourSegments_MiddleBelongsToTheEntryId_NotTheConstraint()
    {
        // The entry id is itself composite (docs/entry-id-construction.md): the link traversed to
        // reach the entry is a segment OF THE ENTRY ID. Only the last segment is the constraint.
        var map = BattleScribeErrorIds.Parse(["force-1::el-weapon::sse-weapon::con-max"]);

        Assert.Equal(["el-weapon::sse-weapon"], map.Keys);
        Assert.Equal(["con-max"], map["el-weapon::sse-weapon"]);
    }

    [Fact]
    public void FiveSegments_EveryMiddleSegmentIsRejoined()
    {
        var map = BattleScribeErrorIds.Parse(["force-1::el-grp::el-power::sse-power::con-max"]);

        Assert.Equal(["el-grp::el-power::sse-power"], map.Keys);
        Assert.Equal(["con-max"], map["el-grp::el-power::sse-power"]);
    }

    [Fact]
    public void RepeatedId_IsDeduped()
    {
        // BattleScribe lists an id once per error the element carries, so repeats are the norm.
        var map = BattleScribeErrorIds.Parse(
        [
            "force-1::shared-unit::con-max-shared",
            "force-1::shared-unit::con-max-shared",
        ]);

        Assert.Equal(["con-max-shared"], map["shared-unit"]);
    }

    [Fact]
    public void Dedupe_PreservesFirstSeenOrder()
    {
        // The surviving order is the answer, not an incidental: callers walk the candidates in
        // order and take the first when the message quotes no value to decide on.
        var map = BattleScribeErrorIds.Parse(
        [
            "force-1::shared-unit::con-b",
            "force-1::shared-unit::con-a",
            "force-1::shared-unit::con-b",
        ]);

        Assert.Equal(["con-b", "con-a"], map["shared-unit"]);
    }

    [Fact]
    public void MultipleConstraintsOnOneEntry_AllSurviveInListedOrder()
    {
        var map = BattleScribeErrorIds.Parse(
        [
            "force-1::shared-unit::con-max-per-link",
            "force-1::shared-unit::con-max-shared",
        ]);

        Assert.Equal(["shared-unit"], map.Keys);
        Assert.Equal(["con-max-per-link", "con-max-shared"], map["shared-unit"]);
    }

    [Fact]
    public void DistinctEntries_GetSeparateKeys()
    {
        var map = BattleScribeErrorIds.Parse(
        [
            "force-1::unit-a::con-max",
            "force-1::unit-b::con-max",
        ]);

        Assert.Equal(["unit-a", "unit-b"], map.Keys);
        Assert.Equal(["con-max"], map["unit-a"]);
        Assert.Equal(["con-max"], map["unit-b"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("shared-unit")]
    [InlineData("force-1::shared-unit")]
    public void FewerThanThreeSegments_IsDropped(string errorId)
    {
        // Neither half can be named without guessing which one is missing, and a guess produces a
        // well-formed wrong `from` — worse than leaving the caller on its message-text fallback.
        Assert.Empty(BattleScribeErrorIds.Parse([errorId]));
    }

    [Fact]
    public void NullEntries_AreDropped_WithoutLosingTheRest()
    {
        var map = BattleScribeErrorIds.Parse([null, "force-1::shared-unit::con-max", null]);

        Assert.Equal(["shared-unit"], map.Keys);
        Assert.Equal(["con-max"], map["shared-unit"]);
    }

    [Fact]
    public void NullList_IsEmpty()
    {
        Assert.Empty(BattleScribeErrorIds.Parse(null));
    }

    [Fact]
    public void EmptySegments_AreKeptRatherThanRemoved()
    {
        // Removing them would re-index the segments and change the three-segment behaviour every
        // observed sample depends on. An id whose middle is blank still puts its constraint last.
        var map = BattleScribeErrorIds.Parse(["force-1::::con-max"]);

        Assert.Equal([""], map.Keys);
        Assert.Equal(["con-max"], map[""]);
    }

    // ParseOne: the single-id form the patched engine now hangs on each error (bsspecErrorId). The
    // Java agent's parseOneErrorId is the hand-kept mirror of these cases.

    [Fact]
    public void ParseOne_ThreeSegments_SplitsEntryAndConstraint()
    {
        Assert.Equal(("shared-unit", "con-max"), BattleScribeErrorIds.ParseOne("force-1::shared-unit::con-max"));
    }

    [Fact]
    public void ParseOne_FourSegments_MiddleRejoinsIntoEntry()
    {
        Assert.Equal(("link-1::sse-weapon", "con-max"), BattleScribeErrorIds.ParseOne("force-1::link-1::sse-weapon::con-max"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shared-unit")]
    [InlineData("force-1::shared-unit")]
    public void ParseOne_FewerThanThreeSegments_IsNull(string? errorId)
    {
        Assert.Equal((null, null), BattleScribeErrorIds.ParseOne(errorId));
    }
}
