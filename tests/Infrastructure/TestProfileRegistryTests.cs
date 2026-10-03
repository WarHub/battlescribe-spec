using System.Reflection;
using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;
using Xunit.Sdk;
using Xunit.v3;

namespace BattleScribeSpec.Tests;

/// <summary>
/// Holds the test-profile registry (<c>tests/TestProfiles/</c>) to the suite it describes: every
/// <c>Engine</c> value and every engine-tagged class is accounted for, every filter clause names
/// something the assembly carries, every environment switch is classified, and the pre-push gate
/// keeps its promise.
/// </summary>
/// <remarks>
/// <para>
/// <b>The values are read by reflection, not from source.</b> The pre-push decision table this
/// replaces found <c>Engine</c> values with a regex over every <c>.cs</c> file, and had to exclude its
/// own file because the prose explaining it spelled the attribute out and invented a thirteenth lane.
/// Asking xunit for the traits it assigns (<see cref="ExtensibilityPointFactory"/>, as discovery does)
/// has neither problem: a comment is not an attribute. Assembly, class and method traits are read
/// that way; a data source or a row can carry traits too, and
/// <see cref="NoDataSourceOrRow_CarriesAnEngineCategoryOrModeTrait"/> keeps the three properties
/// profiles select by off them, so the method-level values are the whole story for those three.
/// </para>
/// <para>
/// Mutation-checked when written; each of these turns the named test red: a
/// <c>[Trait("Engine", "Bogus")]</c> on a test class (<see cref="EveryEngineTraitInTheAssembly_IsDeclared"/>
/// and <see cref="EveryLaneClass_IsDeclared"/>); <c>BsRosterUi</c> set <c>InPrePush: true</c>
/// (<see cref="PrePushHonoursItsPromise"/>); an engine misspelt in a profile's selection
/// (<see cref="EveryClause_IsWellFormed_AndNamesValuesTheAssemblyCarries"/>); and an unclassified
/// <c>NR_FOO</c> string literal in a test file (<see cref="EveryKnobLiteral_IsClassified"/>). The
/// further mutations each test was checked with are listed on it.
/// </para>
/// </remarks>
[Trait("Category", "Lint")]
public sealed class TestProfileRegistryTests
{
    /// <summary>
    /// The one operator-explicit clause shape a profile filter may use. A bare word is a valid VSTest
    /// filter too — it means <c>FullyQualifiedName~word</c> — so a typo does not fail; it selects
    /// something else.
    /// </summary>
    internal static readonly Regex ClauseShape = new(@"^(Engine|Category|Mode|Tag|DisplayName|FullyQualifiedName)(=|!=|~|!~)[^&|()]+$");

    private static readonly Regex KebabCase = new("^[a-z0-9]+(?:-[a-z0-9]+)*$");

    /// <summary>Properties whose values are trait values this assembly carries, checked exactly.</summary>
    private static readonly string[] ReflectedProperties = ["Engine", "Category", "Mode"];

    [Fact]
    public void EveryProfileName_IsUniqueKebabCase()
    {
        Assert.NotEmpty(TestProfiles.All);

        var problems = TestProfiles.All
            .Where(static p => !KebabCase.IsMatch(p.Name))
            .Select(static p => $"  '{p.Name}' is not lower-case kebab-case")
            .Concat(TestProfiles.All
                .GroupBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Where(static g => g.Count() > 1)
                .Select(static g => $"  '{g.Key}' is declared {g.Count()} times"))
            .Concat(TestProfiles.All
                .Where(static p => string.IsNullOrWhiteSpace(p.Purpose))
                .Select(static p => $"  '{p.Name}' has no Purpose"))
            .ToList();

        Assert.True(problems.Count == 0,
            "Profile names are what -p:TestProfile= takes and what CI and the docs refer to, so each must be unique "
            + "(case-insensitively: a file system may not tell two apart) and lower-case kebab-case, and each says "
            + "what it is for:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Every clause of every profile's filter is <c>Property Operator Value</c> with an explicit
    /// operator, and an <c>Engine</c>, <c>Category</c> or <c>Mode</c> clause compares exactly against
    /// a value some test in this assembly carries.
    /// </summary>
    [Fact]
    public void EveryClause_IsWellFormed_AndNamesValuesTheAssemblyCarries()
    {
        var carried = ReflectedProperties.ToDictionary(static p => p, SuiteTraits.Values, StringComparer.Ordinal);
        Assert.All(carried, static kv => Assert.True(kv.Value.Count > 0,
            $"Reflection found no '{kv.Key}' trait on any test in {EngineLanes.Assembly}, so no clause on it can be checked."));

        var problems = new List<string>();
        var clauses = 0;
        foreach (var profile in TestProfiles.All)
        {
            if (profile.Selection.Filter is not { } filter)
            {
                continue;
            }

            foreach (var clause in Selection.ClausesOf(filter))
            {
                clauses++;
                var match = ClauseShape.Match(clause);
                if (!match.Success)
                {
                    problems.Add($"  {profile.Name}: '{clause}' is not Property(=|!=|~|!~)Value over "
                        + "Engine, Category, Mode, Tag, DisplayName or FullyQualifiedName");
                    continue;
                }

                var (property, op, value) = (match.Groups[1].Value, match.Groups[2].Value, clause[(match.Groups[2].Index + match.Groups[2].Length)..]);
                if (!carried.TryGetValue(property, out var values))
                {
                    continue;
                }

                if (op is not ("=" or "!="))
                {
                    problems.Add($"  {profile.Name}: '{clause}' compares {property} by substring; a misspelt value still "
                        + "matches whatever contains it. Use = or != against the exact trait value.");
                }
                else if (!values.Contains(value))
                {
                    problems.Add($"  {profile.Name}: '{clause}' names {property} '{value}', which no test in "
                        + $"{EngineLanes.Assembly} carries (it carries: {string.Join(", ", values.Order(StringComparer.Ordinal))})");
                }
            }
        }

        Assert.True(clauses > 0, "No profile has a filter clause, so this check checked nothing.");
        Assert.True(problems.Count == 0,
            "A profile filter that does not mean what it says selects something else and still passes:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// Every environment key a profile sets is a switch <see cref="Knobs"/> classifies, and one a
    /// profile may set: not an internal one, and not a retired one.
    /// </summary>
    [Fact]
    public void EveryProfileEnvironmentKey_IsAClassifiedKnob()
    {
        var problems = (
            from profile in TestProfiles.All
            from key in profile.Env.Keys
            let knob = Knobs.Find(key)
            where knob is null || knob.Kind is KnobKind.Internal or KnobKind.Retired
            select knob is null
                ? $"  {profile.Name} sets {key}, which Knobs does not classify"
                : $"  {profile.Name} sets {key}, a {knob.Kind} switch ({knob.Why})").ToList();

        Assert.True(TestProfiles.All.Any(static p => p.Env.Count > 0), "No profile sets any environment, so this check checked nothing.");
        Assert.True(problems.Count == 0,
            "A profile's environment is part of what the lane IS, so every key must be a classified switch, and one a "
            + "caller could meaningfully set:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>A profile that claims a lane sets every switch the lane cannot run without</b>
    /// (<see cref="EngineLane.RequiredEnv"/>): a live lane's endpoint URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every live fixture skips whole when its URL is unset, so a live profile without one passes
    /// having run nothing. Two were like that: <c>nr-editor-ui-live</c> left <c>NR_EDITOR_URL</c> to
    /// the caller for its whole life, and <c>nr-live-sequential</c> needed the URL added when it was
    /// written. A <c>MaySkip</c> engine is exempt (it may skip whole by definition), and so is a lane a
    /// <c>Raw</c> filter reaches only incidentally: <c>non-conformance</c> reaches
    /// <c>LiveNrRosterSmokeTests</c> and must not drive the live site from the <c>checks</c> job, which
    /// is why it declares the lane incidental rather than claiming it.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>NR_ENGINE_URL</c> removed from <c>nr-live-sequential</c>, and
    /// <c>RequiredEnv</c> naming an unclassified switch, each turn this red.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryProfile_SuppliesItsEnginesRequiredEnv()
    {
        var problems = EngineLanes.All
            .SelectMany(static l => l.RequiredEnv.Select(key => (Lane: l, Key: key, Knob: Knobs.Find(key))))
            .Where(static r => r.Knob is null || r.Knob.Kind is KnobKind.Internal or KnobKind.Retired)
            .Select(static r => r.Knob is null
                ? $"  {r.Lane.Trait} requires {r.Key}, which Knobs does not classify"
                : $"  {r.Lane.Trait} requires {r.Key}, a {r.Knob.Kind} switch no profile may set")
            .ToList();

        var checkedPairs = 0;
        foreach (var profile in TestProfiles.All)
        {
            foreach (var lane in profile.Selection.Claims.Select(EngineLanes.Find).OfType<EngineLane>())
            {
                if (profile.MaySkip.Any(m => m.Engine == lane.Trait))
                {
                    continue;
                }

                foreach (var key in lane.RequiredEnv)
                {
                    checkedPairs++;
                    if (profile.Env.GetValueOrDefault(key) is not { Length: > 0 })
                    {
                        problems.Add($"  {profile.Name} claims {lane.Trait} but does not set {key}; every {lane.Trait} test skips without it");
                    }
                }
            }
        }

        Assert.True(checkedPairs > 0, "No profile claims a lane with RequiredEnv, so this check checked nothing.");
        Assert.True(problems.Count == 0,
            "A profile that names a lane it cannot reach passes having run none of it. Set the switch in the profile's Env, "
            + "and classify it in Knobs:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// Every assembly a profile covers is a test project in <c>BattleScribeSpec.slnx</c>, a profile
    /// that claims an engine covers the assembly the engine lanes live in, and <c>pre-push</c>
    /// covers every test project.
    /// </summary>
    [Fact]
    public void EveryProfileAssembly_IsASolutionTestProject()
    {
        var testAssemblies = CiTestInvocations.TestProjects.Select(static p => p.AssemblyName).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(testAssemblies);
        Assert.Contains(EngineLanes.Assembly, testAssemblies);

        var problems = new List<string>();
        foreach (var profile in TestProfiles.All)
        {
            if (profile.Assemblies.Count == 0)
            {
                problems.Add($"  {profile.Name} covers no assembly");
            }

            problems.AddRange(profile.Assemblies
                .Where(a => !testAssemblies.Contains(a))
                .Select(a => $"  {profile.Name} covers '{a}', which is not a test project in {RepoRoot.MarkerFileName}"));
            problems.AddRange(profile.Assemblies
                .GroupBy(static a => a, StringComparer.Ordinal)
                .Where(static g => g.Count() > 1)
                .Select(g => $"  {profile.Name} lists '{g.Key}' twice"));

            if (profile.Selection.Claims.Count > 0 && !profile.Assemblies.Contains(EngineLanes.Assembly, StringComparer.Ordinal))
            {
                problems.Add($"  {profile.Name} claims {string.Join(", ", profile.Selection.Claims)} but does not cover "
                    + $"{EngineLanes.Assembly}, where every engine lane lives");
            }
        }

        var prePush = TestProfiles.Find("pre-push");
        Assert.NotNull(prePush);
        problems.AddRange(testAssemblies
            .Where(a => !prePush.Assemblies.Contains(a, StringComparer.Ordinal))
            .Select(static a => $"  pre-push does not cover {a}; the gate runs every test project"));

        Assert.True(problems.Count == 0, "Profile assemblies disagree with the solution:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <c>MaySkip</c> names only an engine that needs the desktop app, that the profile claims, and
    /// says why.
    /// </summary>
    [Fact]
    public void MaySkip_NamesOnlyDesktopAppEnginesTheProfileClaims_WithAReason()
    {
        var problems = new List<string>();
        foreach (var profile in TestProfiles.All)
        {
            foreach (var (engine, why) in profile.MaySkip)
            {
                var lane = EngineLanes.Find(engine);
                if (lane is null)
                {
                    problems.Add($"  {profile.Name}: MaySkip names '{engine}', which is not an engine lane");
                    continue;
                }

                if (!lane.Needs.HasFlag(Needs.DesktopApp))
                {
                    problems.Add($"  {profile.Name}: MaySkip names {engine}, which needs {lane.Needs}, not the desktop app. "
                        + "A browser or a snapshot is setup.ps1's to provision, and a lane that skips without one is the "
                        + "silent hole this list must not reopen.");
                }

                if (!profile.Selection.Claims.Contains(engine, StringComparer.Ordinal))
                {
                    problems.Add($"  {profile.Name}: MaySkip names {engine}, which the profile does not claim");
                }

                if (string.IsNullOrWhiteSpace(why))
                {
                    problems.Add($"  {profile.Name}: MaySkip names {engine} without a reason");
                }
            }
        }

        Assert.True(TestProfiles.All.Any(static p => p.MaySkip.Count > 0), "No profile declares MaySkip, so this check checked nothing.");
        Assert.True(problems.Count == 0, "An engine allowed to skip whole is an engine a lane may not have run:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>What a selection claims is exactly the set of engine lanes its filter reaches</b>, apart from
    /// lanes a <see cref="Selection.Raw"/> filter declares <see cref="Selection.Incidental"/> with a
    /// reason. Every claim and every incidental entry is an engine lane, named once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Evaluated, not read off the <c>Engine</c> clauses.</b> Each filter is run against every test
    /// method of every lane's own classes (<see cref="EngineLane.LaneTests"/>), with the traits discovery
    /// gives it and the names VSTest matches (<see cref="FilterReach"/>). A lane is reached when some test
    /// of it can match. What a method alone cannot settle — a theory row's label under
    /// <c>DisplayName</c>, a row-level <c>Tag</c> — counts as "can match", so the check
    /// over-approximates in one direction only, and both of these are hard errors:
    /// </para>
    /// <list type="bullet">
    /// <item><description>a lane reached through any clause at all that the profile does not account
    /// for: <c>Category=Conformance</c> reaches every lane, and claiming none of them would leave a
    /// lane-composition check nothing to check;</description></item>
    /// <item><description>a claimed lane the filter provably cannot reach: an aggregate <c>[Fact]</c>
    /// narrowed by <c>DisplayName~kitchen-sink</c>, the shape that once selected nothing and stayed
    /// green.</description></item>
    /// </list>
    /// <para>
    /// Mutation-checked when written: <c>non-conformance</c> as <c>Raw("Category=Conformance", claims: [])</c>
    /// (every lane reached, none claimed); <c>lint</c> as <c>Raw("Engine=BsRoster", claims: [])</c>;
    /// <c>smoke-nr-ui</c> narrowed by <c>.Where("DisplayName~kitchen-sink")</c> (claimed, unreachable);
    /// <c>non-conformance</c>'s incidental <c>LiveNrRoster</c> entry dropped; and an incidental entry for a
    /// lane the filter cannot reach.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySelection_ClaimsTheLanesItsFilterReaches()
    {
        var testsOf = SuiteTraits.TestMethods.ToLookup(static t => t.TestClass.FullName!, StringComparer.Ordinal);
        var problems = new List<string>();
        var evaluated = 0;
        foreach (var profile in TestProfiles.All)
        {
            var selection = profile.Selection;
            var claims = selection.Claims;
            var incidental = selection.Incidental.Select(static i => i.Engine).ToList();

            problems.AddRange(claims.Concat(incidental).Where(static c => EngineLanes.Find(c) is null)
                .Select(c => $"  {profile.Name} names '{c}' in its claims or incidental lanes, which is not an engine lane"));
            problems.AddRange(claims.Concat(incidental).GroupBy(static c => c, StringComparer.Ordinal).Where(static g => g.Count() > 1)
                .Select(g => $"  {profile.Name} names {g.Key} more than once across its claims and incidental lanes"));
            problems.AddRange(selection.Incidental.Where(static i => string.IsNullOrWhiteSpace(i.Why))
                .Select(i => $"  {profile.Name} declares {i.Engine} incidental without a reason"));

            // A profile that does not cover the lanes' assembly claims nothing (EveryProfileAssembly_IsASolutionTestProject).
            if (!profile.Assemblies.Contains(EngineLanes.Assembly, StringComparer.Ordinal))
            {
                continue;
            }

            evaluated++;
            var filter = FilterReach.Parse(selection.Filter);
            foreach (var lane in EngineLanes.All)
            {
                var laneTests = lane.LaneTests.SelectMany(c => testsOf[c]).ToList();
                var reachedBy = laneTests.FirstOrDefault(t => filter.Evaluate(t) != Reach.No);
                var claimed = claims.Contains(lane.Trait, StringComparer.Ordinal);
                var declared = incidental.Contains(lane.Trait, StringComparer.Ordinal);

                if (reachedBy is null && (claimed || declared))
                {
                    problems.Add($"  {profile.Name} {(claimed ? "claims" : "declares incidental")} {lane.Trait}, but its filter "
                        + $"'{selection.Filter}' can select none of that lane's {laneTests.Count} test methods "
                        + $"({string.Join(", ", lane.LaneTests.Select(static c => c[(c.LastIndexOf('.') + 1)..]))})");
                }
                else if (reachedBy is not null && !claimed && !declared)
                {
                    problems.Add($"  {profile.Name}'s filter '{selection.Filter ?? "(none)"}' can select {lane.Trait}'s own tests "
                        + $"(e.g. {reachedBy.FullyQualifiedName}), but the profile does not claim {lane.Trait}");
                }
            }
        }

        Assert.True(evaluated > 0, $"No profile covers {EngineLanes.Assembly}, so no filter was evaluated.");
        Assert.True(problems.Count == 0,
            "A profile's claims are the engine lanes it exists to run, and a lane-composition check can only hold a profile "
            + "to what it claims. So the claims must be exactly the lanes the filter can reach: claim a lane it reaches, "
            + "or — for a Raw filter that reaches one only by the way — declare it incidental with the reason; and do not "
            + "claim a lane the filter cannot select a single test of:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>Every <c>Engine</c> value in the assembly has a lane in the registry, and every lane in the
    /// registry is a value the assembly carries.</b> A lane with no row would join <c>pre-push</c>'s
    /// deny-list by silence — the whole of #405.
    /// </summary>
    [Fact]
    public void EveryEngineTraitInTheAssembly_IsDeclared()
    {
        var carried = SuiteTraits.Values("Engine");
        Assert.NotEmpty(carried);

        var declared = EngineLanes.All.Select(static l => l.Trait).ToList();
        var duplicates = declared.GroupBy(static t => t, StringComparer.Ordinal).Where(static g => g.Count() > 1).Select(static g => g.Key).ToList();
        var undeclared = carried.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var phantom = declared.Except(carried, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(duplicates.Count == 0 && undeclared.Count == 0 && phantom.Count == 0,
            "EngineLanes and the Engine traits in " + EngineLanes.Assembly + " disagree.\n"
            + (undeclared.Count > 0 ? $"  Carried by a test, with no lane: {string.Join(", ", undeclared)}\n" : "")
            + (phantom.Count > 0 ? $"  A lane no test carries: {string.Join(", ", phantom)}\n" : "")
            + (duplicates.Count > 0 ? $"  Declared twice: {string.Join(", ", duplicates)}\n" : "")
            + "\npre-push is a deny-list: a lane with no row is a lane that joined the gate every contributor runs "
            + "before every push without anyone choosing that. It is how BsRosterUi came to spend 688.8s of a 689.2s run "
            + "launching the desktop app in a profile documented as offline and fast (#405). Add a row to EngineLanes.All "
            + "saying what the lane needs and whether pre-push runs it, with the measured cost that justifies the answer.");
    }

    /// <summary>
    /// <b>Every class that carries an <c>Engine</c> trait is either one of its lane's own classes or
    /// declared not to be, with a reason</b> — and every name in either list is a class in this
    /// assembly carrying that engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "The lane ran" has to mean its spec suite ran. Six <c>Engine=FrozenNrUiRoster</c> regression facts
    /// drive a blank page and pass on a machine with no HAR, while the lane they share a trait with
    /// skips whole; counted together, the lane would look executed. So the lane's own classes are listed
    /// (<see cref="EngineLane.LaneTests"/>), and so is everything else that carries the trait
    /// (<see cref="EngineLanes.NotLaneTests"/>): a new engine-tagged class has to land on one side.
    /// </para>
    /// <para>
    /// Every engine-tagged class is checked, not only those carrying <c>Category=Conformance</c>:
    /// scoped to that category, this would put the regression facts on the lane's side, because they
    /// carry <c>Category=Conformance</c> too (it is what keeps browser tests out of CI's offline step).
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryLaneClass_IsDeclared()
    {
        var assembly = typeof(TestProfileRegistryTests).Assembly;
        var tagged = SuiteTraits.EngineTaggedClasses();
        Assert.NotEmpty(tagged);

        var laneOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var lane in EngineLanes.All)
        {
            foreach (var type in lane.LaneTests)
            {
                (laneOf.TryGetValue(type, out var lanes) ? lanes : laneOf[type] = []).Add(lane.Trait);
            }
        }

        var notLane = EngineLanes.NotLaneTests.ToLookup(static n => n.Type, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (type, engines) in tagged.OrderBy(static t => t.Key.FullName, StringComparer.Ordinal))
        {
            var name = type.FullName!;
            var asLane = laneOf.GetValueOrDefault(name) ?? [];
            if (asLane.Count == 0 && !notLane.Contains(name))
            {
                problems.Add($"  {name} carries Engine={string.Join(",", engines)} and is in neither a lane's LaneTests nor "
                    + "EngineLanes.NotLaneTests");
            }
            else if (asLane.Count > 0 && notLane.Contains(name))
            {
                problems.Add($"  {name} is listed both as a lane test ({string.Join(", ", asLane)}) and in NotLaneTests");
            }
        }

        foreach (var (name, lanes) in laneOf)
        {
            var type = assembly.GetType(name);
            if (type is null)
            {
                problems.Add($"  LaneTests of {string.Join(", ", lanes)} names {name}, which is not a class in {EngineLanes.Assembly}");
                continue;
            }

            if (lanes.Count > 1)
            {
                problems.Add($"  {name} is a lane test of {string.Join(" and ", lanes)}; a class belongs to one lane");
            }

            var classEngines = SuiteTraits.ClassTraits(type).GetValueOrDefault("Engine") ?? [];
            problems.AddRange(lanes
                .Where(l => !classEngines.Contains(l))
                .Select(l => $"  {name} is a lane test of {l} but does not carry [Trait(\"Engine\", \"{l}\")] on the class"));
        }

        foreach (var (name, why) in EngineLanes.NotLaneTests)
        {
            var type = assembly.GetType(name);
            if (type is null || !tagged.ContainsKey(type))
            {
                problems.Add($"  NotLaneTests names {name}, which is {(type is null ? "not a class in " + EngineLanes.Assembly : "not engine-tagged")}");
            }

            if (string.IsNullOrWhiteSpace(why))
            {
                problems.Add($"  NotLaneTests lists {name} without a reason");
            }
        }

        problems.AddRange(EngineLanes.All.Where(static l => l.LaneTests.Count == 0).Select(static l => $"  {l.Trait} has no lane test"));

        Assert.True(problems.Count == 0,
            "Every class that carries an Engine trait must be classified: one of its lane's own classes (EngineLane.LaneTests, "
            + "the classes that run the spec suite through that engine) or EngineLanes.NotLaneTests with a reason (a contract, "
            + "a failure-message shape, a metric). \"The lane executed tests\" can only be proved over the first kind.\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>No lane that needs the desktop app or a third party's site runs in <c>pre-push</c>,</b> and
    /// <c>pre-push</c>'s selection is <see cref="Selection.PrePush"/> itself: a deny-list of the lanes not
    /// marked <see cref="EngineLane.InPrePush"/>, plus <c>Mode=Sequential</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AGENTS.md calls <c>pre-push</c> the offline gate: no network, no desktop app. That is a promise
    /// about every lane in it, so it is checked against what each lane needs rather than against a
    /// list of names.
    /// </para>
    /// <para>
    /// The filter is pinned, not only the claims. Two hand-written selections would claim exactly the
    /// <c>InPrePush</c> lanes and still break the gate: an allow-list of those lanes, which drops every
    /// test that carries no <c>Engine</c> trait (unit, lint, protocol — most of the suite); and a
    /// <c>Raw</c> deny-list that forgets a newly added lane, which joins the gate by silence — #405's shape.
    /// </para>
    /// <para>
    /// Mutation-checked when written: <c>BsRosterUi</c> set <c>InPrePush: true</c>; <c>pre-push</c> as
    /// <c>AllExcept("BsRosterUi")</c>; as <c>Engines(…the six InPrePush lanes…).Where("Mode!=Sequential")</c>;
    /// and <see cref="Selection.PrePush"/> with its <c>Mode!=Sequential</c> dropped.
    /// </para>
    /// </remarks>
    [Fact]
    public void PrePushHonoursItsPromise()
    {
        var broken = EngineLanes.All
            .Where(static l => l.InPrePush && (l.Needs & (Needs.DesktopApp | Needs.ThirdPartySite)) != 0)
            .Select(static l => $"  {l.Trait} needs {l.Needs} but is marked InPrePush ({l.Why})")
            .ToList();

        var prePush = TestProfiles.Find("pre-push");
        Assert.NotNull(prePush);

        var derived = Selection.PrePush();
        if (prePush.Selection.Filter != derived.Filter)
        {
            broken.Add($"  pre-push's filter is '{prePush.Selection.Filter}', but Selection.PrePush() derives '{derived.Filter}' from "
                + "the InPrePush column: its selection must be Selection.PrePush() itself");
        }

        var expected = EngineLanes.All.Where(static l => l.InPrePush).Select(static l => l.Trait).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(derived.Claims))
        {
            broken.Add($"  Selection.PrePush() claims [{string.Join(", ", derived.Claims)}], but the lanes marked InPrePush are "
                + $"[{string.Join(", ", expected)}]: it must be derived from that column");
        }

        if (derived.Filter is not { } filter)
        {
            broken.Add("  Selection.PrePush() has no filter, so it runs every lane");
        }
        else
        {
            if (filter.Contains('|', StringComparison.Ordinal)
                || Selection.ClausesOf(filter).FirstOrDefault(static c => ClauseShape.Match(c) is not { Success: true } m || !m.Groups[2].Value.StartsWith('!')) is not null)
            {
                broken.Add($"  Selection.PrePush() renders '{filter}', which is not a deny-list (every clause a negation, no '|'): "
                    + "the tests that carry no Engine trait — unit, lint, protocol — are only in the gate because nothing excludes them");
            }

            if (!Selection.ClausesOf(filter).Contains("Mode!=Sequential", StringComparer.Ordinal))
            {
                broken.Add("  pre-push does not exclude Mode=Sequential (manual-only, gated behind NR_SEQUENTIAL)");
            }
        }

        Assert.True(broken.Count == 0,
            "pre-push is the gate AGENTS.md tells every contributor to run before every push, and calls offline: no network, "
            + "no desktop app. It runs on every push by every contributor, which is the last traffic profile a third party's "
            + "site should see, and a desktop-app lane is what made it cost 11m29s instead of seconds (#405):\n"
            + string.Join("\n", broken));
    }

    /// <summary>
    /// <b>Every string literal under <c>src/</c> and <c>tests/</c> that looks like one of this repo's
    /// environment switches is classified in <see cref="Knobs"/>, and every classified switch is still
    /// named by code other than the registry and the lints.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Literals, not call sites: a switch read through a constant (<c>FullVariable = "NR_UI_ROSTER_FULL"</c>)
    /// or set on a child process (<c>psi.Environment["BSSPEC_WORKER_INDEX"]</c>) is still one, and
    /// <c>GetEnvironmentVariable(</c> sees neither. C#, the Java agent's sources and PowerShell are
    /// scanned; build output is not.
    /// </para>
    /// <para>
    /// <b>The two directions scan different things.</b> A literal anywhere but <c>Knobs.cs</c> must be
    /// classified — a comment or a lint naming a switch included. But a row stays only while some
    /// <i>other</i> code names its switch: a hit inside <c>tests/TestProfiles/</c>, in a file that
    /// declares a <c>Category=Lint</c> test, or on a comment line does not count, because each of those
    /// keeps naming a switch after the code that read it has gone. The profile table names
    /// <c>NR_FROZEN_SMOKE</c> as an environment key, this test's own remarks quote two switches, and
    /// <c>CiProfileLaneTests</c> asserts the <c>nr-ui-frozen</c> profile sets <c>NR_UI_ROSTER_FULL</c>;
    /// counted, any of them would keep a row green forever once its reader was renamed.
    /// </para>
    /// <para>
    /// Mutation-checked when written: an unclassified <c>NR_FOO</c> literal; a row no file names; and
    /// <c>FrozenNrUiRosterConformanceTests</c>' <c>FullVariable</c> renamed, which leaves
    /// <c>NR_UI_ROSTER_FULL</c> named only by the profile table and a drift test, and turns it red.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryKnobLiteral_IsClassified()
    {
        var repoRoot = ConcurrencyConfigurationDriftTests.RepoRoot;
        var knobsFile = Path.Combine(repoRoot, "tests", "TestProfiles", "Knobs.cs");
        Assert.True(File.Exists(knobsFile), $"Expected the knob table at {knobsFile}.");
        var registry = Path.Combine(repoRoot, "tests", "TestProfiles") + Path.DirectorySeparatorChar;

        var sources = SourceFiles(repoRoot, "src", "tests")
            .Where(f => !string.Equals(f, knobsFile, StringComparison.OrdinalIgnoreCase))
            .Select(static f => (Path: f, Lines: File.ReadAllLines(f)))
            .ToList();
        var lintFiles = SuiteTraits.LintTests.Select(static t => t.TestClass).Distinct()
            .SelectMany(c => DeclaringFiles(c, sources))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(lintFiles);

        var literal = new Regex("\"((?:NR|BS|BSSPEC|BSUI)_[A-Z0-9_]+)\"");
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, lines) in sources)
        {
            var counts = !file.StartsWith(registry, StringComparison.OrdinalIgnoreCase) && !lintFiles.Contains(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in literal.Matches(lines[i]))
                {
                    var where = $"{Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}:{i + 1}";
                    (found.TryGetValue(m.Groups[1].Value, out var sites) ? sites : found[m.Groups[1].Value] = []).Add(where);
                    if (counts && !IsCommentLine(file, lines[i]))
                    {
                        named.Add(m.Groups[1].Value);
                    }
                }
            }
        }

        Assert.True(sources.Count > 0 && found.Count > 0 && named.Count > 0,
            $"Scanned {sources.Count} source files under src/ and tests/ and found {found.Count} switch literals, {named.Count} of them "
            + "outside the registry, the lints and comments, so this check checked nothing.");

        var problems = found
            .Where(static kv => Knobs.Find(kv.Key) is null)
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .Select(static kv => $"  {kv.Key} is unclassified — {string.Join(", ", kv.Value.Take(3))}{(kv.Value.Count > 3 ? ", …" : "")}")
            .ToList();
        problems.AddRange(Knobs.All
            .Where(k => !named.Contains(k.Name))
            .Select(k => $"  {k.Name} is classified ({k.Kind}) but no code outside tests/TestProfiles/, the lint tests and comments names "
                + "it any more; delete the row" + (found.TryGetValue(k.Name, out var sites) ? $" (still named at: {string.Join(", ", sites.Take(3))})" : "")));
        problems.AddRange(Knobs.All.GroupBy(static k => k.Name, StringComparer.Ordinal).Where(static g => g.Count() > 1)
            .Select(static g => $"  {g.Key} is classified {g.Count()} times"));
        problems.AddRange(Knobs.All.Where(static k => string.IsNullOrWhiteSpace(k.Why)).Select(static k => $"  {k.Name} has no Why"));
        problems.AddRange(Knobs.All.SelectMany(static k => k.Engines.Where(static e => EngineLanes.Find(e) is null)
            .Select(e => $"  {k.Name} names '{e}', which is not an engine lane")));

        Assert.True(problems.Count == 0,
            "Every NR_*/BS_*/BSSPEC_*/BSUI_* switch must be classified in tests/TestProfiles/Knobs.cs as LaneDefining (it "
            + "changes which tests run, or whether a lane runs at all), Default (it tunes how a lane runs), Internal (the "
            + "harness sets it) or Retired. A lane-defining switch exported in someone's shell silently shrinks or skips a "
            + "profiled run that shows nothing on success, which is why each one has to be a decision:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>No data source and no theory row carries an <c>Engine</c>, <c>Category</c> or <c>Mode</c>
    /// trait.</b> Those are what profiles select by and what lanes are classified by, and both are read
    /// at the method level (<see cref="SuiteTraits"/>); a row-level one would be selected by a filter
    /// while every check here looked straight past it.
    /// </summary>
    /// <remarks>
    /// Row-level traits are legitimate for anything else — every spec row carries its <c>Tag</c>s that
    /// way. The rows are asked for as discovery asks for them, inline data included. Mutation-checked
    /// when written: a <c>Category</c> trait added to the spec rows of <see cref="ConformanceTestBase"/>,
    /// and a <c>Traits = ["Engine", …]</c> on an <c>[InlineData]</c>, each turn this red.
    /// </remarks>
    [Fact]
    public async Task NoDataSourceOrRow_CarriesAnEngineCategoryOrModeTrait()
    {
        var problems = new List<string>();
        var rows = 0;
        await using var tracker = new DisposalTracker();
        foreach (var test in SuiteTraits.TestMethods)
        {
            foreach (var source in test.Data)
            {
                var where = $"{test.TestClass.Name}.{test.Method.Name} [{source.GetType().Name}]";
                var pairs = source.Traits ?? [];
                problems.AddRange(Enumerable.Range(0, pairs.Length / 2).Select(i => pairs[2 * i])
                    .Where(static k => ReflectedProperties.Contains(k, StringComparer.Ordinal))
                    .Select(k => $"  {where}: the data source sets trait {k}"));

                foreach (var row in await source.GetData(test.Method, tracker))
                {
                    rows++;
                    problems.AddRange((row.Traits?.Keys ?? Enumerable.Empty<string>())
                        .Where(static k => ReflectedProperties.Contains(k, StringComparer.Ordinal))
                        .Select(k => $"  {where} row {row.Label ?? "(unlabelled)"}: sets trait {k}"));
                }
            }
        }

        Assert.True(rows > 0, "Asked every data source in the assembly for its rows and got none, so this check checked nothing.");
        Assert.True(problems.Count == 0,
            "Engine, Category and Mode belong on the class or the method, where every registry check reads them. Set on a "
            + "data source or a row, the trait still selects the test under a profile's filter, but lane classification, "
            + "the clause-value check and the claims check never see it:\n"
            + string.Join("\n", problems.Take(40)) + (problems.Count > 40 ? $"\n  … and {problems.Count - 40} more" : ""));
    }

    /// <summary>The C#, Java and PowerShell sources under the given top-level directories, build output left out.</summary>
    internal static IEnumerable<string> SourceFiles(string repoRoot, params string[] directories)
    {
        string[] extensions = [".cs", ".java", ".ps1"];
        string[] generated = ["bin", "obj", "out", "TestResults", "node_modules"];
        return directories
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(repoRoot, d), "*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(repoRoot, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(s => generated.Contains(s, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>The files among <paramref name="sources"/> that declare a class or record named like <paramref name="type"/>.</summary>
    internal static IEnumerable<string> DeclaringFiles(Type type, IEnumerable<(string Path, string[] Lines)> sources)
    {
        var declaration = new Regex($@"\b(?:class|record)\s+{Regex.Escape(type.Name)}\b");
        return sources.Where(s => s.Lines.Any(declaration.IsMatch)).Select(static s => s.Path);
    }

    /// <summary>
    /// Whether a source line is a comment as a whole: <c>//</c> (doc comments included), a block
    /// comment's <c>/*</c> or <c>*</c> line, or a PowerShell <c>#</c> line.
    /// </summary>
    internal static bool IsCommentLine(string file, string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith("/*", StringComparison.Ordinal)
            || (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && text.StartsWith('#'));
    }
}

/// <summary>
/// One test method xunit would run, with its fact and data attributes and the traits discovery merges
/// onto it from the assembly, its class and itself.
/// </summary>
internal sealed record SuiteTest(
    Type TestClass,
    MethodInfo Method,
    IReadOnlyCollection<IFactAttribute> Facts,
    IReadOnlyCollection<IDataAttribute> Data,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> Traits)
{
    /// <summary>Whether it is a theory: its rows add a label (or arguments) to its display name, and may add traits.</summary>
    public bool IsTheory => Facts.Any(static f => f is ITheoryAttribute);

    /// <summary>
    /// <c>Namespace.Class.Method</c>: VSTest's <c>FullyQualifiedName</c> for every row of it, and the
    /// display name of a fact (the method display is <c>ClassAndMethod</c>, and nothing overrides it —
    /// <c>TheoryRowIdentityTests.EveryTestName_KeepsItsClass</c>).
    /// </summary>
    public string FullyQualifiedName => $"{TestClass.FullName}.{Method.Name}";

    /// <summary>The values of trait <paramref name="name"/> at the method level.</summary>
    public IReadOnlyCollection<string> Trait(string name) => Traits.GetValueOrDefault(name) ?? [];
}

/// <summary>
/// The traits xunit assigns in this assembly, read the way discovery reads them — through
/// <see cref="ExtensibilityPointFactory"/> — so a trait in a comment is not one. This reads the
/// assembly, class and method attributes. Traits a data source or a theory row adds are not read
/// here: for <c>Engine</c>, <c>Category</c> and <c>Mode</c>,
/// <c>TestProfileRegistryTests.NoDataSourceOrRow_CarriesAnEngineCategoryOrModeTrait</c> keeps there
/// from being any, so the method-level values are exact for those three.
/// </summary>
internal static class SuiteTraits
{
    private static readonly Assembly Suite = typeof(SuiteTraits).Assembly;

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> AssemblyLevel = ExtensibilityPointFactory.GetAssemblyTraits(Suite);

    private static readonly Lazy<IReadOnlyList<SuiteTest>> Methods = new(LoadMethods);

    /// <summary>Every test method in this assembly with the traits discovery gives it (assembly, class and method merged).</summary>
    public static IReadOnlyList<SuiteTest> TestMethods => Methods.Value;

    /// <summary>Every test method carrying <c>Category=Lint</c>.</summary>
    public static IEnumerable<SuiteTest> LintTests => TestMethods.Where(static t => t.Trait("Category").Contains("Lint"));

    /// <summary>The traits declared on a class itself (and the assembly), not on its methods.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> ClassTraits(Type type) =>
        ExtensibilityPointFactory.GetClassTraits(type, AssemblyLevel);

    /// <summary>
    /// Every value of trait <paramref name="name"/> in the assembly: on any test method as discovery
    /// merges it, and on any class at all — a test class, or one xunit reads traits from, such as a
    /// collection definition.
    /// </summary>
    public static IReadOnlySet<string> Values(string name) =>
        TestMethods.SelectMany(m => m.Trait(name))
            .Concat(Suite.GetTypes().Where(static t => t.IsClass).SelectMany(t => ClassTraits(t).GetValueOrDefault(name) ?? []))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every class whose tests carry an <c>Engine</c> trait, on the class or on any of its test methods, with the values.</summary>
    public static IReadOnlyDictionary<Type, IReadOnlySet<string>> EngineTaggedClasses() =>
        TestMethods
            .Select(static m => (m.TestClass, Engines: m.Trait("Engine")))
            .Where(static m => m.Engines.Count > 0)
            .GroupBy(static m => m.TestClass)
            .ToDictionary(static g => g.Key, static g => (IReadOnlySet<string>)g.SelectMany(static m => m.Engines).ToHashSet(StringComparer.Ordinal));

    private static IReadOnlyList<SuiteTest> LoadMethods() =>
        [.. TheoryRowIdentityTests.TestMethods()
            .Select(static m => new SuiteTest(m.TestClass, m.Method, m.Facts, m.Data,
                ExtensibilityPointFactory.GetMethodTraits(m.Method, ClassTraits(m.TestClass))))];
}

/// <summary>What a filter can say about one test method before its rows exist.</summary>
internal enum Reach
{
    /// <summary>No row of the method can match.</summary>
    No,

    /// <summary>It depends on the row: a theory row's label under <c>DisplayName</c>, or a row-level <c>Tag</c>.</summary>
    Maybe,

    /// <summary>Every row of the method matches.</summary>
    Yes,
}

/// <summary>
/// A test-case filter parsed as VSTest parses one — <c>&amp;</c> binds tighter than <c>|</c>,
/// parentheses group, comparisons ignore case — and evaluated against one test method with
/// three-valued logic (<see cref="Reach"/>).
/// </summary>
/// <remarks>
/// <c>Engine</c>, <c>Category</c> and <c>Mode</c> are settled by the method-level traits, which are the
/// whole story for those three (no data source or row may carry them). <c>FullyQualifiedName</c> is
/// settled by the method's name. <c>DisplayName</c> is settled for a fact, whose display name is its
/// fully qualified name, and for a theory only where that prefix already decides it. <c>Tag</c> is
/// settled for a fact; a theory's rows carry their own. A clause outside the grammar evaluates to
/// <see cref="Reach.Maybe"/>: <c>EveryClause_IsWellFormed_AndNamesValuesTheAssemblyCarries</c> fails it.
/// </remarks>
internal sealed class FilterReach
{
    private readonly Func<SuiteTest, Reach> evaluate;

    private FilterReach(Func<SuiteTest, Reach> evaluate) => this.evaluate = evaluate;

    /// <summary>What the filter says about <paramref name="test"/>.</summary>
    public Reach Evaluate(SuiteTest test) => evaluate(test);

    /// <summary>Parses <paramref name="filter"/>; <see langword="null"/> or empty is the whole assembly.</summary>
    public static FilterReach Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return new(static _ => Reach.Yes);
        }

        var parser = new Parser(Tokenize(filter), filter);
        var root = parser.Or();
        parser.ExpectEnd();
        return new(root);
    }

    private static List<string> Tokenize(string filter)
    {
        var tokens = new List<string>();
        var clause = new System.Text.StringBuilder();
        foreach (var c in filter)
        {
            if (c is '(' or ')' or '&' or '|')
            {
                Flush();
                tokens.Add(c.ToString());
            }
            else
            {
                clause.Append(c);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (clause.ToString().Trim() is { Length: > 0 } text)
            {
                tokens.Add(text);
            }

            clause.Clear();
        }
    }

    private static Func<SuiteTest, Reach> Clause(string text)
    {
        var match = TestProfileRegistryTests.ClauseShape.Match(text);
        if (!match.Success)
        {
            return static _ => Reach.Maybe;
        }

        var property = match.Groups[1].Value;
        var op = match.Groups[2].Value;
        var value = text[(match.Groups[2].Index + match.Groups[2].Length)..];
        var contains = op.EndsWith('~');
        bool Matches(string candidate) => contains
            ? candidate.Contains(value, StringComparison.OrdinalIgnoreCase)
            : string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase);

        Func<SuiteTest, Reach> positive = property switch
        {
            "FullyQualifiedName" => t => Matches(t.FullyQualifiedName) ? Reach.Yes : Reach.No,
            "DisplayName" => t => !t.IsTheory
                ? (Matches(t.FullyQualifiedName) ? Reach.Yes : Reach.No)
                : (contains && Matches(t.FullyQualifiedName) ? Reach.Yes : Reach.Maybe),
            "Tag" => t => t.Trait(property).Any(Matches) ? Reach.Yes : t.IsTheory ? Reach.Maybe : Reach.No,
            _ => t => t.Trait(property).Any(Matches) ? Reach.Yes : Reach.No,
        };

        // VSTest: != and !~ hold when NO value of the property matches — the negation of = and ~.
        return op.StartsWith('!') ? t => Not(positive(t)) : positive;
    }

    private static Reach Not(Reach r) => r switch { Reach.Yes => Reach.No, Reach.No => Reach.Yes, _ => Reach.Maybe };

    private sealed class Parser(List<string> tokens, string filter)
    {
        private int position;

        public Func<SuiteTest, Reach> Or()
        {
            var parts = new List<Func<SuiteTest, Reach>> { And() };
            while (Accept("|"))
            {
                parts.Add(And());
            }

            return parts.Count == 1 ? parts[0] : t =>
            {
                var results = parts.Select(p => p(t)).ToList();
                return results.Contains(Reach.Yes) ? Reach.Yes : results.Contains(Reach.Maybe) ? Reach.Maybe : Reach.No;
            };
        }

        public void ExpectEnd()
        {
            if (position != tokens.Count)
            {
                throw new FormatException($"Unexpected '{tokens[position]}' at token {position} of filter '{filter}'.");
            }
        }

        private Func<SuiteTest, Reach> And()
        {
            var parts = new List<Func<SuiteTest, Reach>> { Primary() };
            while (Accept("&"))
            {
                parts.Add(Primary());
            }

            return parts.Count == 1 ? parts[0] : t =>
            {
                var results = parts.Select(p => p(t)).ToList();
                return results.Contains(Reach.No) ? Reach.No : results.Contains(Reach.Maybe) ? Reach.Maybe : Reach.Yes;
            };
        }

        private Func<SuiteTest, Reach> Primary()
        {
            if (Accept("("))
            {
                var inner = Or();
                if (!Accept(")"))
                {
                    throw new FormatException($"Unbalanced '(' in filter '{filter}'.");
                }

                return inner;
            }

            if (position >= tokens.Count || tokens[position] is "&" or "|" or ")")
            {
                throw new FormatException($"Expected a clause at token {position} of filter '{filter}'.");
            }

            return Clause(tokens[position++]);
        }

        private bool Accept(string token)
        {
            if (position < tokens.Count && tokens[position] == token)
            {
                position++;
                return true;
            }

            return false;
        }
    }
}
