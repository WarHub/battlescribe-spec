namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// The one correct way to delete a roster row out of NewRecruit's <c>lists</c> Pinia store,
/// shared by every engine that creates one (the store-direct
/// <see cref="NewRecruitRosterEngine"/> and the UI driver's <c>NrRosterUiEngine</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>lists.deleteList</c> action.</b> Cleanup in both engines used to call
/// <c>listsStore.deleteList?.(key)</c>; because of the <c>?.</c> that has always been a silent
/// no-op, so no spec ever deleted the roster it created and a single pooled engine finished a
/// suite run carrying 46-54 accumulated rows. That accumulation is the soil PR #334's
/// intermittent "export landed on /app/MyLists" failure grew in: NR's editor page resolves the
/// <c>:list</c> route param through <c>findListByKey(key, [selectedSystem.id, selectedSystem.bsid])</c>,
/// so once dozens of stale rows from other specs were present, NR's own page code could re-select
/// a previous spec's system and make the current roster unresolvable.
/// </para>
/// <para>
/// The name <c>deleteList</c> does exist in NR — just not as a store action. It is a method on the
/// MyLists <em>page component</em> (<c>deleteList(row, skipConfirm) { this.$listStore.removeListVue(row, false, skipConfirm) }</c>)
/// and it is the server RPC verb (<c>Bt("deleteList", list_key)</c>). Neither is reachable as
/// <c>listsStore.deleteList</c>.
/// </para>
/// <para>
/// The three real actions, read out of the recorded NR bundle (<c>_nuxt/CA9992uS.js</c>):
/// <list type="bullet">
///   <item><description>
///     <c>removeList(row)</c> — purely local: splices <c>listData</c>, rebuilds <c>treeData</c>,
///     deletes the row from the Dexie/IndexedDB table. <b>This is the one we want.</b>
///   </description></item>
///   <item><description>
///     <c>removeListVue(row, browserOnly, skipConfirm)</c> — the UI wrapper: builds a confirmation
///     dialog, then calls <c>removeList</c> and, unless <c>browserOnly</c>, <c>deleteListFromServer</c>.
///     Correct only with <c>(row, true, true)</c>, and then it is just <c>removeList</c> plus a
///     dialog we would rather not mount.
///   </description></item>
///   <item><description>
///     <c>deleteListFromServer(row)</c> — issues a real <c>deleteList</c> RPC. Wrong here: the
///     frozen HAR suites must not attempt server calls.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <c>removeList</c> takes the <b>row object</b>, not the key, and splices at
/// <c>listData.findIndex(r =&gt; r.list_key == row.list_key)</c> <em>without guarding -1</em> — handing
/// it a key it cannot find would <c>splice(-1, 1)</c> and delete the LAST row instead of nothing.
/// So the helper resolves the row out of <c>listData</c> first and skips keys that are already gone.
/// </para>
/// </remarks>
internal static class NrListStoreJs
{
    /// <summary>
    /// A JS fragment declaring <c>const bsspecDeleteLists = async (listsStore, keys) =&gt; …</c>.
    /// Interpolate it into an <c>EvaluateAsync</c> blob before use. Returns <c>null</c> when every
    /// key is gone from <c>listData</c>, or a diagnostic string naming what survived and why.
    /// <para>
    /// Note what it deliberately does <em>not</em> do: optional-chain the call. A store action that
    /// disappears must produce a message, not a pass. Verifying the rows are actually gone
    /// afterwards is the same rule applied to the outcome rather than the API surface.
    /// </para>
    /// </summary>
    public const string DeleteListsFn = """
        const bsspecDeleteLists = async (listsStore, keys) => {
            // NOT optional-chained, and checked by name so the failure says which action vanished
            // rather than surfacing as a bare "is not a function".
            if (typeof listsStore.removeList !== 'function') {
                const actions = [];
                for (const k in listsStore) {
                    if (typeof listsStore[k] === 'function') actions.push(k);
                }
                return "lists store has no removeList() action — NR's store API changed. "
                    + 'Actions present: ' + actions.join(', ');
            }
            const problems = [];
            for (const key of keys) {
                if (!key) continue;
                // Resolve the row: removeList splices at findIndex() without guarding -1, so an
                // unknown key would delete the LAST row rather than nothing.
                const row = (listsStore.listData || []).find(r => r.list_key === key);
                if (!row) continue; // already gone — nothing to do
                try {
                    await listsStore.removeList(row);
                } catch (e) {
                    // Keep going: one row failing to delete must not strand the rest.
                    problems.push('removeList(' + key + ') threw: ' + (e?.message ?? String(e)));
                }
            }
            // Cleanup that reports success without having deleted anything is exactly the bug this
            // helper replaces, so the outcome is checked rather than assumed.
            const left = keys.filter(k => k && (listsStore.listData || []).some(r => r.list_key === k));
            if (left.length) {
                problems.push('rows still in listData after removeList: ' + left.join(', '));
            }
            return problems.length ? problems.join('; ') : null;
        };
        """;
}
