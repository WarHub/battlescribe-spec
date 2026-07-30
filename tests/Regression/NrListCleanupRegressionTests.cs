using BattleScribeSpec.NewRecruit;
using BattleScribeSpec.Protocol;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Tests.Regression;

/// <summary>
/// Guards the one thing <c>NewRecruitRosterEngine.Cleanup</c> exists to do: delete the roster row
/// the spec created out of NR's <c>lists</c> store.
/// </summary>
/// <remarks>
/// <para>
/// Cleanup used to call <c>listsStore.deleteList?.(key)</c>. The <c>lists</c> store has no
/// <c>deleteList</c> action — that name belongs to the MyLists page component and to the server RPC
/// verb — so the optional call resolved to <c>undefined</c> and returned without doing anything.
/// Cleanup reported success on every spec while deleting nothing, and a single pooled engine
/// finished a suite run carrying 46-54 accumulated rows.
/// </para>
/// <para>
/// The accumulation was not cosmetic. NR's editor page resolves the <c>:list</c> route param with
/// <c>findListByKey(key, [selectedSystem.id, selectedSystem.bsid])</c> — filtered to the currently
/// selected system. With dozens of stale rows from other specs present, NR's own page code could
/// call <c>selectList</c> on a previous spec's row, re-select that row's system, and leave the
/// roster under test unresolvable; roster export then landed on <c>/app/MyLists</c> instead of the
/// editor route (PR #334, which fixed the symptom by re-asserting the correct system before
/// navigating).
/// </para>
/// <para>
/// So the assertion is on row count, measured on a real pooled engine across repeated
/// setup/cleanup cycles: whatever <c>listData</c> held before the first spec, it must hold again
/// after every cleanup. A cleanup that silently deletes nothing fails here on the second cycle.
/// </para>
/// </remarks>
/// <remarks>
/// <c>Category=Conformance</c> despite being a regression test, following
/// <c>NewRecruitEnginePoolResourceMetricsTests</c>: this drives a real Chromium over the frozen HAR,
/// and <c>Category!=Conformance</c> is the only thing keeping browser tests out of CI's offline
/// unit/lint step. The <c>Engine</c> trait is what actually places it — <c>core</c> excludes it,
/// <c>nr-frozen</c> and <c>pre-push</c> run it.
/// </remarks>
[Collection("FrozenNrRoster")]
[Trait("Category", "Conformance")]
[Trait("Engine", "FrozenNrRoster")]
public sealed class NrListCleanupRegressionTests(ITestOutputHelper output, FrozenNrRosterFixture fixture)
{
    /// <summary>
    /// How many setup/cleanup cycles to run. The bug this guards is monotonic — one leaked row per
    /// spec — so a handful of cycles is enough to separate "deletes nothing" from "deletes".
    /// </summary>
    private const int Cycles = 4;

    [Fact]
    public async Task Cleanup_DeletesTheRowItCreated_SoListDataDoesNotGrowAcrossSpecs()
    {
        Assert.SkipWhen(!fixture.Available,
            "Frozen HAR file not found or NR_FROZEN_SKIP=true — skipping frozen NR tests");

        // Assert, not skip: past the HAR gate above we are in a real frozen-NR lane, where finding
        // no specs means discovery broke. A skip there would be this bug's own shape — a green
        // report for work that never happened.
        var specs = LoadInlineSpecs(Cycles);
        Assert.True(specs.Count > 0,
            "No inline newrecruit specs discovered — nothing to exercise cleanup with.");

        using var handle = await fixture.AcquireAsync(TestContext.Current.CancellationToken);
        var engine = handle.Engine;

        var baseline = await ReadListRowCountAsync(engine);
        output.WriteLine($"baseline listData rows: {baseline}");

        var afterEachCleanup = new List<int>();
        var leakedKeys = new List<string>();

        for (var i = 0; i < specs.Count; i++)
        {
            var (specId, gameSystem, catalogues) = specs[i];
            engine.SetTestContext(specId);

            var setupErrors = engine.Setup(gameSystem, catalogues);
            Assert.True(setupErrors.Count == 0,
                $"Setup failed for '{specId}': {string.Join("; ", setupErrors)}");

            // The row this cycle created — proof the cycle had something to clean up, so a Setup
            // that quietly stopped creating lists can't make this test pass by doing nothing.
            var createdKey = await engine.Browser.Page.EvaluateAsync<string?>(
                "() => window.__bsspec?.row?.list_key ?? null");
            Assert.False(string.IsNullOrEmpty(createdKey),
                $"Spec '{specId}' created no list row — nothing for cleanup to delete.");

            var duringRun = await ReadListRowCountAsync(engine);
            Assert.True(duringRun > baseline,
                $"Spec '{specId}' added no row to listData (still {duringRun}).");

            engine.Cleanup();

            var afterCleanup = await ReadListRowCountAsync(engine);
            afterEachCleanup.Add(afterCleanup);

            if (await ListRowExistsAsync(engine, createdKey!))
            {
                leakedKeys.Add($"{specId} -> {createdKey}");
            }

            output.WriteLine(
                $"cycle {i + 1}/{specs.Count} '{specId}': {baseline} -> {duringRun} -> {afterCleanup}");
        }

        Assert.True(leakedKeys.Count == 0,
            "Cleanup left the rows it created in NR's lists store: " + string.Join(", ", leakedKeys));

        // The headline property: no growth. Stated over every cycle rather than just the last so
        // the failure message shows the shape of the leak (steady climb vs. a single stuck row).
        Assert.True(
            afterEachCleanup.TrueForAll(count => count == baseline),
            $"listData grew across {specs.Count} setup/cleanup cycles: baseline {baseline}, "
                + $"after each cleanup [{string.Join(", ", afterEachCleanup)}]. "
                + "Cleanup is not deleting the roster row it created.");
    }

    private static Task<int> ReadListRowCountAsync(NewRecruitRosterEngine engine) =>
        engine.Browser.Page.EvaluateAsync<int>("""
            () => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                return (lists?.listData || []).length;
            }
            """);

    private static Task<bool> ListRowExistsAsync(NewRecruitRosterEngine engine, string listKey) =>
        engine.Browser.Page.EvaluateAsync<bool>("""
            (listKey) => {
                const pinia = document.querySelector('#__nuxt')
                    ?.__vue_app__?.config?.globalProperties?.$pinia;
                const lists = pinia?._s?.get('lists');
                return (lists?.listData || []).some(r => r.list_key === listKey);
            }
            """, listKey);

    /// <summary>
    /// The first <paramref name="count"/> discovered specs that apply to <c>newrecruit</c> and carry
    /// inline setup data. Real specs rather than one synthesized game system, because the leak is
    /// about rows piling up across <em>different</em> specs sharing a pooled engine — which is how
    /// the suite actually runs.
    /// </summary>
    private static List<(string Id, ProtocolGameSystem GameSystem, ProtocolCatalogue[] Catalogues)>
        LoadInlineSpecs(int count)
    {
        var loaded = new List<(string, ProtocolGameSystem, ProtocolCatalogue[])>();

        foreach (var (path, _) in ConformanceTestBase.AllSpecPaths())
        {
            if (loaded.Count >= count)
            {
                break;
            }

            SpecFile spec;
            try
            {
                spec = SpecLoader.Load(path);
            }
            catch
            {
                continue; // a spec that won't load is the conformance suite's problem, not ours
            }

            // dataSource specs read game data off disk through a different setup path; the inline
            // ones exercise the addList/removeList round trip this test is about.
            if (spec.Setup.DataSource is { Length: > 0 } || !spec.IsApplicableTo("newrecruit"))
            {
                continue;
            }

            try
            {
                var (gameSystem, catalogues) = SpecLoader.GetSetupData(spec.Setup, spec.Id);
                loaded.Add((spec.Id, gameSystem, catalogues));
            }
            catch
            {
                // Same: unloadable setup data is not what this test measures.
            }
        }

        return loaded;
    }
}
