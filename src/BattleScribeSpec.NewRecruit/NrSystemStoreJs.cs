namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Releasing a spec's synthetic game system from NewRecruit's <c>systemsStore</c> — every place it
/// was registered, not just the one the loader wrote to. Shared by both engines that register one
/// (<see cref="NewRecruitRosterEngine"/> and the UI driver's <c>NrRosterUiEngine</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deleting the <c>localLibrary</c> entry does not release the system.</b> NR's
/// <c>getOrCreateLocalSystem</c> writes it in two places — <c>localLibrary[id]</c> and, through
/// <c>setSystem</c>, the shared <c>library</c> that carries every system NR knows about. Cleanup
/// cleared the first. The second accumulated one entry per spec, all of them called <c>gs-1</c>,
/// because every synthetic spec uses the same id.
/// </para>
/// <para>
/// That is invisible until something resolves a system <em>by id</em>. Setup does not — it hands
/// <c>selectSystem</c> the object it just created. NewRecruit's own roster importer does:
/// <c>importBs</c> reads <c>gameSystemId</c> out of the file, calls <c>getSystem(id)</c> and then
/// <c>selectSystem(id)</c>, and builds the roster against whatever those return.
/// </para>
/// <para>
/// Measured at the point of failure, with <c>roundtrip-load-roster</c> on the NR-UI lane — which
/// passes alone and failed after certain other specs, reporting the loaded roster's Squad with the
/// file's Trooper missing: <c>library.index['gs-1']</c> was this spec's system,
/// <c>localLibrary['gs-1']</c> was the same object, and <c>_selectedSystem</c> was <b>neither</b> —
/// same id, same book names, left over from a spec that had already been cleaned up. That is the
/// catalogue the import used.
/// </para>
/// </remarks>
internal static class NrSystemStoreJs
{
    /// <summary>
    /// A JS fragment declaring <c>const bsspecReleaseLocalSystems = (sysStore) =&gt; …</c>, which
    /// removes every locally-loaded system from <c>library.index</c>, <c>library.array</c> and
    /// <c>localLibrary</c>, and clears the selection when it pointed at one of them. Interpolate it
    /// into an <c>EvaluateAsync</c> blob before use. Returns a diagnostic string, or null.
    /// <para>
    /// Scoped to the ids in <c>localLibrary</c> — the systems this session loaded from files — so
    /// NR's own remote library is untouched. Clearing a selection that survived would otherwise hand
    /// the next spec an object no registry can reach.
    /// </para>
    /// </summary>
    public const string ReleaseLocalSystemsFn = """
        const bsspecReleaseLocalSystems = (sysStore) => {
            if (!sysStore) return null;
            const local = sysStore.localLibrary || {};
            const ids = new Set(Object.keys(local));
            if (ids.size === 0) return null;

            const library = sysStore.library;
            if (library?.index) {
                for (const id of ids) delete library.index[id];
            }
            // Back-to-front so the indices behind each removal stay valid. This list is the other
            // half of the leak: an id resolved through it can answer with a copy the index no
            // longer holds.
            if (Array.isArray(library?.array)) {
                for (let i = library.array.length - 1; i >= 0; i--) {
                    const system = library.array[i];
                    if (system && ids.has(String(system.id))) library.array.splice(i, 1);
                }
            }
            for (const id of ids) delete local[id];

            const selected = sysStore._selectedSystem;
            if (selected && ids.has(String(selected.id))) sysStore._selectedSystem = null;

            const left = [...ids].filter(id =>
                (library?.index && library.index[id])
                || (Array.isArray(library?.array) && library.array.some(s => s && String(s.id) === id))
                || local[id]);
            return left.length ? 'systems still registered after release: ' + left.join(', ') : null;
        };
        """;
}
