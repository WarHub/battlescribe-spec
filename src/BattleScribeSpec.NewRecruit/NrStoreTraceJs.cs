namespace BattleScribeSpec.NewRecruit;

/// <summary>
/// Records <em>who mutated NewRecruit's Pinia stores, and from where</em>.
/// </summary>
/// <remarks>
/// <para>
/// The existing diagnostics (<c>NrUiDiagnostics</c>, <c>NrGameDataUiDiagnostics</c>) answer "what is
/// the state now?" — screenshot, DOM, console, a Pinia snapshot. Every NR bug of the last stretch was
/// the other question, "who changed it?", and a snapshot cannot answer that. #334 and #339 were both
/// cracked the same way: wrap the suspect store action, log <c>new Error().stack</c>, read the caller
/// frame. In #339 that named the culprit — a kept-alive editor page re-selecting a deleted list from
/// its <c>activated</c> hook — in one line, after a lot of theorising had not.
/// </para>
/// <para>
/// This is that technique, kept. The watch-list is not generic curiosity; it is the set of actions
/// that have actually caused bugs:
/// <list type="bullet">
///   <item><description>
///     <c>lists.selectList</c> — re-selects the row's <em>system</em> as a side effect, which is how
///     stale list state becomes stale system state (#334, #339).
///   </description></item>
///   <item><description>
///     <c>lists.addList</c> / <c>lists.removeList</c> — the row lifecycle (#336).
///   </description></item>
///   <item><description>
///     <c>systemsStore.selectSystem</c> / <c>selectFirstSystem</c> / <c>initSelectedSystem</c> — the
///     three ways the selected system moves, only one of which is ever deliberate on our side.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why a tree walk is not enough.</b> The obvious alternative — enumerate components and see who
/// holds the stale object — returns a false negative for exactly the case that matters: a component
/// cached by <c>&lt;KeepAlive&gt;</c> is not in the active subtree, so walking <c>inst.subTree</c>
/// cannot see it. A caller stack has no such blind spot.
/// </para>
/// <para>
/// Deliberately opt-in. Wrapping replaces the store's function identities, and this repo does not
/// perturb the thing it is measuring — <c>bs-spec compare</c> in particular must never run with this
/// on.
/// </para>
/// </remarks>
internal static class NrStoreTraceJs
{
    /// <summary>
    /// Installs the tracer. Idempotent, so it is safe to call after every reset and navigation.
    /// Evaluates to a short status string naming what was wrapped (or why nothing was).
    /// <para>
    /// The buffer lives on <c>window</c>, so a full page load clears it. That is the right scope:
    /// what a failure needs is the mutations of the spec that failed, not the whole session.
    /// </para>
    /// </summary>
    public const string InstallJs = """
        (limit) => {
            const pinia = document.querySelector('#__nuxt')?.__vue_app__?.config?.globalProperties?.$pinia;
            if (!pinia) return 'pinia not reachable — tracer not installed';
            window.__bsspecStoreTrace = window.__bsspecStoreTrace || [];
            const cap = limit > 0 ? limit : 200;
            const wrapped = [];
            const watch = {
                lists: ['selectList', 'addList', 'removeList', 'selectFirstList', 'loadListsFromIndexDB'],
                systemsStore: ['selectSystem', 'selectFirstSystem', 'initSelectedSystem'],
            };
            for (const storeId of Object.keys(watch)) {
                const store = pinia._s?.get(storeId);
                if (!store) continue;
                for (const fn of watch[storeId]) {
                    if (typeof store[fn] !== 'function' || store[fn].__bsspecTraced) continue;
                    const orig = store[fn].bind(store);
                    const traced = function (...args) {
                        const sys = pinia._s?.get('systemsStore');
                        const lists = pinia._s?.get('lists');
                        const entry = {
                            action: storeId + '.' + fn,
                            // Rows and systems are large and cyclic; record only what identifies them.
                            args: args.map(a => (a && typeof a === 'object')
                                ? ('{' + (a.list_key ?? a.id ?? a.row?.list_key ?? '?') + '}')
                                : String(a)),
                            selectedSystemBefore: sys?.selectedSystem?.id ?? null,
                            listCount: (lists?.listData || []).length,
                            lastSelectedListKey: lists?.lastSelectedListKey ?? null,
                            // The load-bearing field. Frames 2+ skip this wrapper itself.
                            stack: (new Error().stack || '').split('\n').slice(2, 7)
                                .map(s => s.trim().replace(/https?:\/\/[^ )]*\/_nuxt\//g, ''))
                                .join(' | '),
                        };
                        let result;
                        try {
                            result = orig(...args);
                        } finally {
                            entry.selectedSystemAfter = sys?.selectedSystem?.id ?? null;
                            // Ring buffer: a runaway loop must not exhaust the page's memory.
                            if (window.__bsspecStoreTrace.length >= cap) window.__bsspecStoreTrace.shift();
                            window.__bsspecStoreTrace.push(entry);
                        }
                        return result;
                    };
                    traced.__bsspecTraced = true;
                    store[fn] = traced;
                    wrapped.push(storeId + '.' + fn);
                }
            }
            return wrapped.length ? 'traced: ' + wrapped.join(', ') : 'already installed';
        }
        """;

    /// <summary>Reads the recorded mutations back as pretty JSON, or null when the tracer is off.</summary>
    public const string ReadJs = """
        () => window.__bsspecStoreTrace && window.__bsspecStoreTrace.length
            ? JSON.stringify(window.__bsspecStoreTrace, null, 2)
            : null
        """;

    /// <summary>
    /// Environment variable that turns tracing on. Set by <c>bs-spec run --trace-store</c>, and
    /// readable directly for a bare host or an xUnit run.
    /// <para>
    /// An environment variable rather than a plumbed flag because it must survive the hop into the
    /// <c>bs-engine-host</c> child process, which is the same reason <c>compare</c>'s
    /// <c>--config-a</c>/<c>--config-b</c> take that shape. This is a diagnostics switch, not a
    /// policy knob — the retired-knob rule (<c>NR_PARALLEL</c>, <c>BS_UI_KEEP_ALIVE</c>) is about
    /// inputs to <c>ConcurrencyPolicy</c>, which this is not.
    /// </para>
    /// </summary>
    public const string EnableVariable = "NR_TRACE_STORE";

    /// <summary>True when store tracing has been requested for this process.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnableVariable) is "1" or "true";
}
