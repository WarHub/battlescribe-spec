using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BattleScribeSpec.Tests.Profiles;

namespace BattleScribeSpec.Tests;

/// <summary>
/// <b>What each test profile really selects, asked of the test app itself</b>: every profile that covers this
/// assembly is listed by a child of this very executable (<c>--test-profile &lt;p&gt; --list-tests json</c>), and
/// the listings are held to what the registry claims.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why spawn the app.</b> <c>TestProfileRegistryTests</c> reasons about filters over method-level traits:
/// fast, but a model of what the platform does. This asks the platform. A profile whose filter parses but
/// selects nothing, a claimed lane the real filter misses (a misspelt engine is a valid filter that matches
/// no test), a smoke lane that stopped being part of its thorough lane, an engine no CI profile reaches — each
/// shows up here as the listing the host actually produced, under the environment a profile actually gets.
/// </para>
/// <para>
/// Each child runs with every switch the registry classifies (<see cref="Knobs"/>) and every <c>GITHUB_*</c>
/// variable removed, so a variable exported in the parent cannot change a listing and a child never writes to
/// the parent step's summary. <c>cli</c>, the one profile that does not cover this assembly, runs its whole
/// other assembly (<see cref="Selection.Whole"/>) and claims nothing; strict's exit 8 holds it to being
/// non-empty on every CI run.
/// </para>
/// <para>
/// <b>Not in <c>pre-push</c>.</b> Twenty-five children each discover the whole assembly: about 25 seconds on
/// an eight-thread box with nothing else running, 47 seconds held to four threads (a CI runner's count) — over the
/// 20-second line the plan drew for <c>pre-push</c>. So these carry <c>Category=SelectionAudit</c>, which
/// <see cref="Selection.PrePush"/> excludes with its reason, and run once per push in CI's <c>checks</c> job
/// (<c>non-conformance</c>); <c>core</c>, which the thorough job runs beside it, leaves them out too.
/// </para>
/// <para>
/// <b>Alone, after everything else</b> (<see cref="ProfileSelectionAuditCollection"/>). The children keep every
/// core busy while they discover; run beside the rest of a suite, they slowed it twofold and timed out a test
/// with a seven-second bound (<c>AdapterHandlerTests.ServerSpan_RecordsErrorStatus_WhenTheCommandHandlerThrows</c>,
/// in the first local <c>non-conformance</c> run). A collection that disables parallelization runs after every
/// parallel one has finished, so the audit adds its own time to the run and takes none from anyone else's.
/// </para>
/// <para>
/// Mutation-checked when written: a profile's engine misspelt in the registry (<c>bs</c> as
/// <c>Engines("BsRostr")</c> — also caught by the registry lint; here as the empty listing it produces), a smoke
/// profile pointed at another engine than its counterpart, the tally's registration removed from
/// <see cref="TestHost"/> (<see cref="TheRealPlatform_FeedsTheLaneTally"/>), and the listing fixture leaving out one
/// of the profiles that cover this assembly (<see cref="EveryProfile_SelectsTests"/>, which holds the listings to
/// exactly that set) each turn this class red. <c>CiProfileLaneTests.TheSelectionAudit_RunsInExactlyOneCiTestRun</c>
/// holds CI to running it once.
/// </para>
/// </remarks>
[Trait("Category", Category)]
[Collection(nameof(ProfileSelectionAuditCollection))]
public sealed class ProfileSelectionAuditTests(ProfileSelectionAuditTests.Listings listings, ITestOutputHelper output)
    : IClassFixture<ProfileSelectionAuditTests.Listings>
{
    /// <summary>The category <see cref="Selection.PrePush"/> excludes.</summary>
    public const string Category = "SelectionAudit";

    /// <summary>
    /// Each <c>smoke-*</c> profile and the profile whose lane it samples: what a smoke lane runs must be a part
    /// of what its thorough lane runs. Every smoke profile must be listed
    /// (<see cref="EverySmokeProfile_IsPartOfItsThoroughLane"/>).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SmokeCounterparts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["smoke-bs"] = "core",
        ["smoke-nr-frozen"] = "nr-frozen",
        ["smoke-nr-ui"] = "nr-ui-frozen",
        ["smoke-nr-editor"] = "nr-editor-frozen",
        ["smoke-nr-editor-ui"] = "nr-editor-ui-frozen",
        ["smoke-bs-gamedata-ui"] = "bs-ui-gamedata",
    };

    /// <summary>
    /// A Windows path with a backslash after the drive (not a URL's <c>file:///C:/…</c>, which a test may hold as
    /// data), or a Unix home directory — the shapes a path picked up from the machine takes.
    /// </summary>
    private static readonly Regex RootedPath = new(@"(?<![A-Za-z])[A-Za-z]:\\|(^|[\s\[(""'=])/(home|Users)/");

    /// <summary>The directories of this machine a test identity must never contain, in both separator spellings.</summary>
    private static readonly string[] MachinePaths =
    [
        .. new[] { ConcurrencyConfigurationDriftTests.RepoRoot, AppContext.BaseDirectory, Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => p.TrimEnd('/', '\\'))
            .Where(static p => p.Length > 3)
            .SelectMany(static p => new[] { p.Replace('\\', '/'), p.Replace('/', '\\') })
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static bool HasMachinePath(string text) =>
        RootedPath.IsMatch(text) || MachinePaths.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EveryProfile_SelectsTests()
    {
        output.WriteLine($"{listings.ByProfile.Count} profiles listed in {listings.Elapsed.TotalSeconds:F1}s");
        var expected = TestProfiles.All.Where(static p => p.Assemblies.Contains(TestProfiles.Tests, StringComparer.Ordinal)).Select(static p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.True(expected.Count > 1, $"Only {expected.Count} profile(s) cover {TestProfiles.Tests}, so this audit would check almost nothing.");
        Assert.Equal(expected, listings.ByProfile.Keys.Order(StringComparer.Ordinal));
        var problems = listings.ByProfile
            .Where(static kv => kv.Value.ExitCode != 0 || kv.Value.Tests.Count == 0)
            .Select(static kv => $"  {kv.Key}: exit {kv.Value.ExitCode}, {kv.Value.Tests.Count} test(s)\n{Indent(kv.Value.Output)}")
            .ToList();
        Assert.True(problems.Count == 0, "A profile whose listing is empty, or that the app refused, is a lane that runs nothing:\n" + string.Join("\n", problems));
    }

    /// <summary><b>Every lane a profile claims is selected</b>: its listing holds at least one test of the lane's own classes.</summary>
    [Fact]
    public void EveryProfile_SelectsTheLanesItClaims()
    {
        var problems = new List<string>();
        foreach (var (name, listing) in listings.ByProfile)
        {
            var profile = TestProfiles.Find(name)!;
            foreach (var lane in profile.Selection.Claims.Select(EngineLanes.Find).OfType<EngineLane>())
            {
                if (!listing.Tests.Any(t => t.Class is { } c && lane.LaneTests.Contains(c, StringComparer.Ordinal)))
                {
                    problems.Add($"  {name} claims {lane.Trait}, and its listing ({listing.Tests.Count} tests) holds none of "
                        + $"{string.Join(", ", lane.LaneTests.Select(static c => c[(c.LastIndexOf('.') + 1)..]))}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            "A profile claims the lanes it exists to run, and the engine-composition check holds its runs to them; a claimed "
            + "lane its real filter does not select fails every run of the profile with exit 8:\n" + string.Join("\n", problems));
    }

    /// <summary><b>A smoke profile runs part of its thorough lane</b>, never something beside it.</summary>
    [Fact]
    public void EverySmokeProfile_IsPartOfItsThoroughLane()
    {
        var problems = TestProfiles.All
            .Where(static p => p.Name.StartsWith("smoke-", StringComparison.Ordinal) && !SmokeCounterparts.ContainsKey(p.Name))
            .Select(static p => $"  {p.Name} has no thorough counterpart in {nameof(SmokeCounterparts)}")
            .ToList();
        problems.AddRange(SmokeCounterparts
            .Where(static kv => TestProfiles.Find(kv.Key) is null || TestProfiles.Find(kv.Value) is null)
            .Select(static kv => $"  {kv.Key} → {kv.Value} names a profile the registry does not have"));

        foreach (var (smoke, thorough) in SmokeCounterparts)
        {
            if (listings.ByProfile.GetValueOrDefault(smoke) is not { } small || listings.ByProfile.GetValueOrDefault(thorough) is not { } large)
            {
                continue;
            }

            var outside = small.Tests.Select(static t => t.DisplayName).Except(large.Tests.Select(static t => t.DisplayName), StringComparer.Ordinal).ToList();
            if (outside.Count > 0)
            {
                problems.Add($"  {smoke} selects {outside.Count} test(s) {thorough} does not, e.g. {outside[0]}");
            }
        }

        Assert.True(problems.Count == 0,
            "A smoke lane is the every-push sample of a thorough lane; one that selects outside it proves nothing about the lane "
            + "it is named for:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>Every engine lane is selected by a profile some CI step runs</b> (and does not let it skip), or carries
    /// <see cref="EngineLane.CiExempt"/> — judged by what the profiles' listings hold.
    /// </summary>
    [Fact]
    public void EveryEngine_IsSelectedByACiProfile_OrCiExempt()
    {
        var ciProfiles = CiProfileRuns.All.Select(static r => r.Profile).OfType<TestProfile>().Distinct().ToList();
        Assert.NotEmpty(ciProfiles);
        var problems = new List<string>();
        foreach (var lane in EngineLanes.All.Where(static l => l.CiExempt is null))
        {
            var by = ciProfiles.FirstOrDefault(p => !p.MaySkip.Any(m => m.Engine == lane.Trait)
                && listings.ByProfile.GetValueOrDefault(p.Name) is { } listing
                && listing.Tests.Any(t => t.Class is { } c && lane.LaneTests.Contains(c, StringComparer.Ordinal)));
            if (by is null)
            {
                problems.Add($"  {lane.Trait}: no profile a CI step runs selects its tests ({string.Join(", ", ciProfiles.Select(static p => p.Name))})");
            }
        }

        Assert.True(problems.Count == 0,
            "An engine no CI profile selects is never run by CI. Run it from a CI step's profile, or say why not in its "
            + "EngineLane.CiExempt:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>No test's identity depends on where the checkout is</b>, and within one listing every uid is distinct —
    /// what <c>--filter</c>, the platform's reports and a rerun address a test by.
    /// </summary>
    [Fact]
    public void TestIdentities_CarryNoRootedPath_AndUidsAreUnique()
    {
        var problems = new List<string>();
        foreach (var (name, listing) in listings.ByProfile)
        {
            problems.AddRange(listing.Tests
                .Where(static t => HasMachinePath(t.DisplayName) || HasMachinePath(t.Uid))
                .Take(3)
                .Select(t => $"  {name}: '{t.DisplayName}' (uid {t.Uid}) carries a rooted path"));
            problems.AddRange(listing.Tests
                .GroupBy(static t => t.Uid, StringComparer.Ordinal)
                .Where(static g => g.Count() > 1)
                .Take(3)
                .Select(g => $"  {name}: uid {g.Key} names {g.Count()} tests ({string.Join(", ", g.Select(static t => t.DisplayName))})"));
        }

        Assert.True(problems.Count == 0, "Test identities must be stable and distinct:\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// <b>The real platform feeds the host's lane tally</b>: a profiled run of the test app executes, and the host's
    /// composition line counts each claimed lane's tests. A tally that never received a result would fail every
    /// profile that claims a lane with exit 8; this says so by name.
    /// </summary>
    [Fact]
    public async Task TheRealPlatform_FeedsTheLaneTally()
    {
        var run = await Listings.RunAppAsync(["--test-profile", "smoke-bs"], TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, $"smoke-bs exited {run.ExitCode}:\n{run.Output}");
        Assert.Contains($"{LaneComposition.Prefix} smoke-bs ({TestProfiles.Tests}): passed — every lane the profile claims executed at least one "
            + "of its own tests: BsRoster 1, BsGameData 1.", run.Output, StringComparison.Ordinal);
    }

    private static string Indent(string text) =>
        string.Join("\n", text.Split('\n').TakeLast(15).Select(static l => "      " + l.TrimEnd('\r')));

    /// <summary>One test in a listing.</summary>
    /// <param name="Uid">The platform's test uid.</param>
    /// <param name="DisplayName">The display name.</param>
    /// <param name="Class">The full name of the class that declares it.</param>
    internal sealed record ListedTest(string Uid, string DisplayName, string? Class);

    /// <summary>One profile's listing: the app's exit code, the tests, and everything it printed.</summary>
    internal sealed record Listing(int ExitCode, IReadOnlyList<ListedTest> Tests, string Output);

    /// <summary>One run of the test app as a child process.</summary>
    internal sealed record AppRun(int ExitCode, string StdOut, string Output);

    /// <summary>Every profile that covers this assembly, listed once for the whole class.</summary>
    public sealed class Listings : IAsyncLifetime
    {
        /// <summary>Each profile's listing, by name.</summary>
        internal IReadOnlyDictionary<string, Listing> ByProfile { get; private set; } = new Dictionary<string, Listing>();

        /// <summary>How long listing them took.</summary>
        internal TimeSpan Elapsed { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var clock = Stopwatch.StartNew();
            var profiles = TestProfiles.All.Where(static p => p.Assemblies.Contains(TestProfiles.Tests, StringComparer.Ordinal)).ToList();
            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, Listing>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(profiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, async (profile, ct) =>
            {
                var run = await RunAppAsync(["--test-profile", profile.Name, "--list-tests", "json"], ct);
                results[profile.Name] = new Listing(run.ExitCode, Parse(run.StdOut), run.Output);
            });
            ByProfile = results;
            Elapsed = clock.Elapsed;
        }

        public ValueTask DisposeAsync() => default;

        /// <summary>
        /// Runs this test executable as a child with <paramref name="args"/>: every registry switch and every
        /// <c>GITHUB_*</c> variable removed, UTF-8 on both pipes, a console of its own, a two-minute bound.
        /// </summary>
        internal static async Task<AppRun> RunAppAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("No process path for the test app.");
            var psi = new ProcessStartInfo(self)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8Stdio.Encoding,
                StandardErrorEncoding = Utf8Stdio.Encoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Started through `dotnet <dll>` (dotnet exec), the process is the host, not the app.
            if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add(typeof(ProfileSelectionAuditTests).Assembly.Location);
            }

            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            foreach (var name in psi.Environment.Keys.Where(static k => k.StartsWith("GITHUB_", StringComparison.Ordinal) || Knobs.Find(k) is not null).ToList())
            {
                psi.Environment.Remove(name);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {self}.");
            var stdOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(bound.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var output = await stdOut;
            return new AppRun(process.ExitCode, output, output + await stdErr);
        }

        /// <summary>The tests of a <c>--list-tests json</c> listing: the JSON object the platform prints after the host's own lines.</summary>
        private static List<ListedTest> Parse(string stdOut)
        {
            var lines = stdOut.Split('\n').Select(static l => l.TrimEnd('\r')).ToList();
            var start = lines.FindIndex(static l => l == "{");
            var end = lines.FindLastIndex(static l => l == "}");
            if (start < 0 || end < start)
            {
                return [];
            }

            using var json = JsonDocument.Parse(string.Join("\n", lines.Skip(start).Take(end - start + 1)));
            return [.. json.RootElement.GetProperty("tests").EnumerateArray().Select(static t =>
            {
                var type = t.TryGetProperty("type", out var ty) ? ty : default;
                var ns = type.ValueKind == JsonValueKind.Object ? type.GetProperty("namespace").GetString() : null;
                var name = type.ValueKind == JsonValueKind.Object ? type.GetProperty("typeName").GetString() : null;
                return new ListedTest(
                    t.GetProperty("uid").GetString()!,
                    t.GetProperty("displayName").GetString()!,
                    name is null ? null : string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}");
            })];
        }
    }
}

/// <summary>
/// The selection audit's collection: it runs alone, after every parallel collection, because its child processes
/// load every core (see <see cref="ProfileSelectionAuditTests"/>).
/// </summary>
[CollectionDefinition(nameof(ProfileSelectionAuditCollection), DisableParallelization = true)]
public sealed class ProfileSelectionAuditCollection;
