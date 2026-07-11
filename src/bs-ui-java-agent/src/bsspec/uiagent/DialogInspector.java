package bsspec.uiagent;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import javafx.application.Platform;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.control.Labeled;
import javafx.scene.control.TextInputControl;
import javafx.stage.Modality;
import javafx.stage.Stage;
import javafx.stage.Window;

import java.awt.Rectangle;
import java.awt.Robot;
import java.awt.Toolkit;
import java.awt.image.BufferedImage;
import java.io.File;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import javax.imageio.ImageIO;

/**
 * Diagnoses what's actually on screen when the app is blocked behind a modal dialog, and
 * enforces a post-condition contract on the agent's high-level actions.
 *
 * <p>Motivating incident: BattleScribe desktop pops a modal error dialog (title "Error",
 * thrown from e.g. {@code RosterEditorWindowController.newRoster}) when its game data is
 * swapped underneath a live JVM. Without this, the in-flight agent action never completes,
 * polling loops spin until timeout, and the operator only sees an opaque
 * {@code TimeoutException} — no idea the app is sitting there showing an error dialog. Worse,
 * some unhandled dialogs (e.g. the unrelated "Continue? Roster has not been saved…" prompt that
 * roster warm-reuse can leave dangling) have nothing that ever dismisses them, so a naive
 * "is this the one specific dialog I know about" check would still hang forever on anything
 * it didn't anticipate by name.
 *
 * <p><b>Contract (allowlist, not denylist):</b> after any action completes, NO modal/dialog
 * window should be open — the app should be back to a stable, non-modal state. Each action (or
 * each step of a multi-step wait) declares which modal(s), if any, are legitimately expected to
 * still be open; most declare none. ANY other showing, non-main-window dialog is unexpected —
 * regardless of its title — and is a failure. This catches the "Continue?" prompt and the
 * "Error" dialog and anything else we've never seen before, without maintaining a list of known
 * bad titles.
 *
 * <p>Key JavaFX fact that makes this possible: a modal {@code showAndWait} runs a nested event
 * loop, so {@code Platform.runLater} tasks STILL execute while a modal is up — this inspector
 * can enumerate windows and scrape a dialog's text even while an action is blocked behind it.
 * All entry points here are safe to call from any thread; they marshal to the FX thread
 * internally (except {@link #listOpenDialogs()}, which the caller is expected to already be
 * running on the FX thread, e.g. via a registered FX-thread RPC method).
 */
public final class DialogInspector {

    private static final int FX_MARSHAL_TIMEOUT_SECONDS = 5;
    /** How much of the joined "details" text (everything after the first/summary control) to keep. */
    private static final int DETAILS_TRUNCATE_LENGTH = 500;
    /** The app's own main window(s): RosterActions drives "Roster Editor"; DataEditorActions drives "Data Editor". */
    private static final String[] MAIN_APP_WINDOW_TITLES = {"Roster Editor", "Data Editor"};
    /**
     * Windows that are NEVER flagged as an unexpected dialog, regardless of which action or wait
     * is checking: {@link #MAIN_APP_WINDOW_TITLES}, and BattleScribe's generic transient
     * "fetching data" spinner ("Loading... — Getting game systems...", "Loading... — Loading
     * data...", etc.). Unlike "Continue?" or "Error" — terminal states nothing ever dismisses on
     * its own — "Loading..." is a self-resolving progress indicator that can legitimately appear
     * at many different points as BattleScribe populates a dialog or resolves game data, so it
     * isn't scoped to specific call sites the way {@code waitForWindow}'s {@code alsoAllowed} is.
     * If it ever got GENUINELY stuck, the calling wait's own timeout still bounds the hang — just
     * without the instant diagnostic this feature gives for other unexpected dialogs.
     */
    private static final String[] ALWAYS_ALLOWED_TITLES = {"Roster Editor", "Data Editor", "Loading..."};

    private DialogInspector() {
    }

    /** Summary of one open window: title, whether it's modal, and its scraped visible text. */
    public static final class DialogInfo {
        public final String title;
        public final boolean modal;
        public final String text;

        DialogInfo(String title, boolean modal, String text) {
            this.title = title;
            this.modal = modal;
            this.text = text;
        }
    }

    /**
     * Enumerates all currently open/showing JavaFX windows and scrapes their visible text.
     * Must be called on the FX thread — callers exposing this via JSON-RPC must register
     * the method in {@code JsonRpcServer.FX_THREAD_METHODS}.
     *
     * <p>Skips scraping the app's own main window(s) ("Roster Editor" / "Data Editor") — this is
     * called on EVERY poll-loop iteration (every {@code POLL_INTERVAL_MS}) via
     * {@link #assertNoUnexpectedModals}, and those windows are always exempt anyway (see
     * {@link #ALWAYS_ALLOWED_TITLES}), so walking their full scene graph (the roster/catalogue
     * tree, potentially large) every ~200ms is pure, avoidable overhead on the SAME single FX
     * thread that BattleScribe's own dialog-close handling runs on — competing for that thread
     * measurably slowed down real dialog transitions during testing (observer-effect: the
     * diagnostic check itself was making the very timeout it's meant to catch more likely).
     */
    public static List<DialogInfo> listOpenDialogs() {
        List<DialogInfo> result = new ArrayList<>();
        for (Window w : new ArrayList<>(Window.getWindows())) {
            if (!(w instanceof Stage)) continue;
            Stage stage = (Stage) w;
            if (!stage.isShowing()) continue;
            String title = stage.getTitle();
            boolean modal = stage.getModality() != Modality.NONE;
            String text = isMainAppWindow(title) ? null : scrapeText(stage);
            result.add(new DialogInfo(title, modal, text));
        }
        return result;
    }

    private static boolean isMainAppWindow(String title) {
        return title != null && matchesAny(title, MAIN_APP_WINDOW_TITLES);
    }

    /** Serializes {@link #listOpenDialogs()} results to a JSON array for the {@code getOpenDialogs} RPC. */
    public static JsonArray toJson(List<DialogInfo> dialogs) {
        JsonArray arr = new JsonArray();
        for (DialogInfo d : dialogs) {
            JsonObject o = new JsonObject();
            o.addProperty("title", d.title);
            o.addProperty("modal", d.modal);
            o.addProperty("text", d.text);
            arr.add(o);
        }
        return arr;
    }

    /**
     * Post-condition / poll-loop guard: throws if any showing window other than
     * {@link #ALWAYS_ALLOWED_TITLES} and the caller-declared {@code allowedTitles} is currently
     * open. Titles are matched the same way window waits
     * elsewhere match them: exact, or {@code prefix + " " + ...} (so e.g. the roster-name suffix
     * on the main window title doesn't defeat the match).
     *
     * <p>Call this in two places:
     *  <ol>
     *   <li>At the end of each high-level action, with that action's declared allowed set
     *       (default: none) — the general post-condition.</li>
     *   <li>Inside a poll/wait loop, with whatever dialogs that specific wait is already
     *       legitimately working with (e.g. a parent dialog that's expected to still be open) —
     *       so a stray, unrecognized modal fails the action immediately instead of the loop
     *       spinning until its own timeout.</li>
     *  </ol>
     *
     * <p>Safe to call from any thread — marshals to the FX thread internally, and works even
     * while a modal {@code showAndWait} nested event loop has the FX thread's call stack blocked
     * deeper down, because {@code Platform.runLater} tasks still run during that nested loop.
     *
     * @throws RuntimeException describing the unexpected dialog (title, scraped text, and a
     *         best-effort full-display screenshot path) if one is found.
     */
    public static void assertNoUnexpectedModals(String... allowedTitles) {
        List<DialogInfo> dialogs = runOnFxThread(DialogInspector::listOpenDialogs);
        if (dialogs == null) return; // couldn't verify (FX thread unresponsive) — don't mask that with a false failure here

        for (DialogInfo d : dialogs) {
            if (d.title == null) continue;
            if (matchesAny(d.title, ALWAYS_ALLOWED_TITLES)) continue;
            if (matchesAny(d.title, allowedTitles)) continue;
            String screenshot = captureFullDisplayScreenshot();
            throw new RuntimeException("Unexpected modal dialog [" + d.title + "]: "
                    + (d.text != null ? d.text : "(no text captured)")
                    + (screenshot != null ? " (screenshot: " + screenshot + ")" : ""));
        }
    }

    private static boolean matchesAny(String title, String[] candidates) {
        for (String candidate : candidates) {
            if (title.equals(candidate) || title.startsWith(candidate + " ")) return true;
        }
        return false;
    }

    /**
     * Captures the ENTIRE display (not just a window's own JavaFX scene — a modal dialog is a
     * separate {@code Stage}, so {@code Scene.snapshot()} on the blocked window would miss it)
     * via AWT Robot, and writes it to a uniquely-named PNG in the system temp directory. Never
     * throws — headless/no-display environments or any capture failure just yield no
     * screenshot, so this can never break the agent. Called at most once per detected failure.
     */
    private static String captureFullDisplayScreenshot() {
        try {
            Robot robot = new Robot();
            Rectangle screenRect = new Rectangle(Toolkit.getDefaultToolkit().getScreenSize());
            BufferedImage image = robot.createScreenCapture(screenRect);
            File file = new File(System.getProperty("java.io.tmpdir"), "bsui-modal-" + System.currentTimeMillis() + ".png");
            ImageIO.write(image, "png", file);
            return file.getAbsolutePath();
        } catch (Throwable t) {
            return null;
        }
    }

    /**
     * Scrapes a dialog's visible text. BattleScribe's Error dialog puts the summary message in
     * one control and the stack/details in another, so this joins ALL non-empty
     * Labeled/TextInputControl/TextArea text in document order, treating the first as the
     * summary and truncating the rest (details) to a sane length.
     */
    private static String scrapeText(Stage stage) {
        if (stage.getScene() == null || stage.getScene().getRoot() == null) return null;
        List<String> parts = new ArrayList<>();
        collectText(stage.getScene().getRoot(), parts, new LinkedHashSet<>());
        if (parts.isEmpty()) return null;
        if (parts.size() == 1) return parts.get(0);
        String summary = parts.get(0);
        String details = truncate(String.join(" | ", parts.subList(1, parts.size())), DETAILS_TRUNCATE_LENGTH);
        return summary + " — " + details;
    }

    private static void collectText(Node node, List<String> parts, Set<String> seen) {
        if (node == null || !node.isVisible()) return;
        String text = extractText(node);
        if (text != null) {
            String trimmed = text.trim();
            if (!trimmed.isEmpty() && seen.add(trimmed)) {
                parts.add(trimmed);
            }
        }
        if (node instanceof Parent) {
            for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
                collectText(child, parts, seen);
            }
        }
    }

    private static String extractText(Node node) {
        // Labeled covers Label/Button/etc.; TextInputControl covers TextField AND TextArea
        // (the BS Error dialog's stack trace/details control is a TextArea).
        if (node instanceof Labeled) return ((Labeled) node).getText();
        if (node instanceof TextInputControl) return ((TextInputControl) node).getText();
        return null;
    }

    private static String truncate(String s, int max) {
        if (s == null || s.length() <= max) return s;
        return s.substring(0, max) + "…";
    }

    private static <T> T runOnFxThread(Callable<T> task) {
        if (Platform.isFxApplicationThread()) {
            try {
                return task.call();
            } catch (Exception e) {
                return null;
            }
        }
        CompletableFuture<T> future = new CompletableFuture<>();
        Platform.runLater(() -> {
            try {
                future.complete(task.call());
            } catch (Exception e) {
                future.completeExceptionally(e);
            }
        });
        try {
            return future.get(FX_MARSHAL_TIMEOUT_SECONDS, TimeUnit.SECONDS);
        } catch (Exception e) {
            return null;
        }
    }
}
