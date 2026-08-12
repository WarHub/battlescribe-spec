package bsspec.uiagent;

import java.util.ArrayList;
import java.util.List;

/**
 * Notices exceptions that reach the JavaFX Application Thread's uncaught handler, and fails the
 * action they happened during.
 *
 * <p><b>Why one of these is not just a line in a log.</b> JavaFX does not hand such an exception
 * back to whoever caused it. A throw inside a property listener or an event handler goes to
 * {@code Thread.currentThread().getUncaughtExceptionHandler()} and the toolkit carries on, so the
 * agent's own {@code runOnFx} returns normally — from its side nothing failed. What actually
 * happened is that the event dispatch was abandoned at the point it threw: whatever it had not done
 * yet does not happen, and the action reports success over the top of it.
 *
 * <p>From the outside that is indistinguishable from the intermittent "Timed out waiting for state
 * change" failures this lane sees. The driver asks for something, the exception aborts the dispatch
 * that would have applied it, and the poll then spends its whole timeout describing the absence
 * rather than the cause. The only trace is a stack on the agent JVM's stderr, which nothing reads
 * unless {@code BSUI_AGENT_STDERR_LOG} was deliberately set beforehand — so the defect that
 * motivated this class ({@link Spinners}, about twenty throws per lane run) sat in a green lane
 * indefinitely.
 *
 * <p><b>The contract</b> mirrors {@link DialogInspector#assertNoUnexpectedModals}, deliberately:
 * {@link #beginAction()} at the start of a high-level action and {@link #assertNone} at the end,
 * with no allowlist. No uncaught exception on the FX thread is ever expected, so any of them is a
 * failure that names the action it happened during instead of a symptom several steps away.
 *
 * <p>The FX thread only. Exceptions on other threads are still printed exactly as before, but
 * BattleScribe runs its own background work and this class does not claim to know which of that is
 * legitimate; the FX thread is the one the agent drives and the one whose abandoned dispatches show
 * up as the agent's own actions half-happening.
 *
 * <p>All entry points are safe to call from any thread.
 */
public final class FxExceptionMonitor {

    /**
     * Matched by name because the handler runs ON the throwing thread, and this must be answerable
     * without touching {@code Platform} — it can fire before the toolkit exists. It is the same
     * name the JVM itself prints in "Exception in thread ...".
     */
    private static final String FX_THREAD_NAME = "JavaFX Application Thread";

    /**
     * How many DISTINCT descriptions to keep per action; repeats of one already held are counted
     * and dropped. One defect routinely throws more than once — a mistyped spinner value throws
     * twice per write, once from the control's text listener and once from the value factory's own
     * range check — and a loop of them would otherwise bury the report in identical stacks. The
     * count reported stays exact regardless of either limit.
     */
    private static final int MAX_DESCRIBED = 5;

    private static final Object LOCK = new Object();

    /** Descriptions since {@link #beginAction()}, capped at {@link #MAX_DESCRIBED}. */
    private static final List<String> described = new ArrayList<>();

    /** How many there actually were, which is not {@code described.size()} once capped. */
    private static int count;

    /** What was handling uncaught exceptions before us, so its behaviour is added to, not replaced. */
    private static volatile Thread.UncaughtExceptionHandler previous;

    /** The handler we installed, so {@link #arm()} can tell whether it is still in place. */
    private static volatile Thread.UncaughtExceptionHandler ours;

    private FxExceptionMonitor() {
    }

    /**
     * Starts watching, from as early as the agent can run — before the FX toolkit exists, so there
     * is no window in which one of these goes unseen.
     *
     * <p>Installed as the JVM-wide default handler rather than on the FX thread, which the agent has
     * no reference to yet: a thread with no handler of its own asks its ThreadGroup, and the root
     * group defers to this one. That is the same route the un-handled trace already takes today.
     */
    public static void install() {
        arm();
    }

    /**
     * Opens a fresh window for one action: re-arms, and drops anything recorded before now.
     *
     * <p>Dropping is the honest choice. Something thrown between two actions belongs to neither, and
     * attributing it to whichever ran next would name the wrong one; it is still printed to stderr,
     * exactly as it was before this class existed, so nothing is lost — only left unattributed.
     */
    public static void beginAction() {
        arm();
        synchronized (LOCK) {
            described.clear();
            count = 0;
        }
    }

    /**
     * @throws RuntimeException if anything reached the FX thread's uncaught handler since
     *     {@link #beginAction()}, describing what and naming {@code action}.
     */
    public static void assertNone(String action) {
        int seen;
        List<String> traces;
        synchronized (LOCK) {
            if (count == 0) return;
            seen = count;
            traces = new ArrayList<>(described);
            described.clear();
            count = 0;
        }

        StringBuilder message = new StringBuilder()
                .append(action).append(": ").append(seen)
                .append(seen == 1 ? " uncaught exception on the " : " uncaught exceptions on the ")
                .append(FX_THREAD_NAME)
                .append(" — JavaFX abandons the event dispatch that throws, so part of this action")
                .append(" did not happen even though it reported no error");
        if (seen > traces.size()) {
            message.append(" (").append(traces.size()).append(" distinct shown)");
        }
        for (int i = 0; i < traces.size(); i++) {
            message.append(i == 0 ? ": " : " ;; ").append(traces.get(i));
        }
        throw new RuntimeException(message.toString());
    }

    /**
     * Makes ours the default handler, keeping whatever was there to delegate to.
     *
     * <p>Re-checked per action rather than installed once, because a later
     * {@code setDefaultUncaughtExceptionHandler} by the app would otherwise unhook this silently and
     * the check would pass forever by seeing nothing. The check itself is one identity comparison.
     */
    private static synchronized void arm() {
        Thread.UncaughtExceptionHandler current = Thread.getDefaultUncaughtExceptionHandler();
        if (ours != null && current == ours) return;
        previous = current;
        ours = FxExceptionMonitor::handle;
        Thread.setDefaultUncaughtExceptionHandler(ours);
    }

    /**
     * Records FX-thread throwables and then lets the previous behaviour happen regardless.
     *
     * <p>Never throws: this runs while the JVM is already reporting a failure, and a second one
     * raised from here would replace the first with a worse one.
     */
    private static void handle(Thread thread, Throwable error) {
        try {
            if (thread != null && FX_THREAD_NAME.equals(thread.getName())) {
                String description = JsonRpcServer.describeThrowable(error);
                synchronized (LOCK) {
                    count++;
                    if (described.size() < MAX_DESCRIBED && !described.contains(description)) {
                        described.add(description);
                    }
                }
            }
        } catch (Throwable ignored) {
            // Recording is the optional half; printing below is not.
        }

        Thread.UncaughtExceptionHandler delegate = previous;
        if (delegate != null) {
            delegate.uncaughtException(thread, error);
            return;
        }
        // Nothing was handling these before, so keep doing what the JVM did unaided. The stderr
        // stream this writes to is the harness's only window into the agent (see
        // BSUI_AGENT_STDERR_LOG), and installing a handler is precisely what would otherwise have
        // turned these traces off at the moment we started depending on them.
        System.err.print("Exception in thread \"" + (thread == null ? "?" : thread.getName()) + "\" ");
        error.printStackTrace(System.err);
    }
}
