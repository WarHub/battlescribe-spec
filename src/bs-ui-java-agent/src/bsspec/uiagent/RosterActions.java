package bsspec.uiagent;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import javafx.application.Platform;
import javafx.scene.Node;
import javafx.scene.Scene;
import javafx.scene.control.*;
import javafx.scene.input.KeyCode;
import javafx.scene.input.KeyEvent;
import javafx.scene.input.MouseButton;
import javafx.scene.input.MouseEvent;
import javafx.stage.Stage;
import javafx.stage.Window;

import java.lang.reflect.Method;
import java.util.*;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.Predicate;

/**
 * High-level roster action orchestration. Each method implements a complete
 * IRosterEngine action as a single RPC call, handling all UI interaction
 * sequences, FX thread dispatch, window waits, state polling, and output
 * extraction internally.
 *
 * <p>All public action methods run on the server's background (IO) thread
 * and internally dispatch to the JavaFX Application Thread as needed.
 *
 * <p>Addressing is purely ID-based. Tree items are located via {@code :id:}
 * substring tokens. ComboBox items are matched by calling {@code getId()}
 * on the backing Java objects via reflection. Edit panel labels are resolved
 * by traversing the engine's roster tree to find entry names by ID.
 */
public class RosterActions {

    private static final String MAIN_WINDOW = "Roster Editor";
    private static final String EDIT_ROSTER_WINDOW = "Edit Roster";
    private static final String NEW_ROSTER_WINDOW = "New Roster";
    private static final String ADD_FORCE_WINDOW = "Add Force";
    private static final String CONFIRM_WINDOW = "Confirm";

    private static final int POLL_INTERVAL_MS = 200;
    private static final int STATE_POLL_TIMEOUT_MS = 10_000;
    private static final int WINDOW_TIMEOUT_MS = 15_000;
    private static final int FX_TIMEOUT_MS = 30_000;

    private final EngineAccessor engineAccessor;

    public RosterActions(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
    }

    /**
     * Dispatches a high-level action method by name.
     */
    public String dispatch(String method, String params) {
        switch (method) {
            case "duplicateSelectionAction":
                return duplicateSelectionAction(params);
            case "duplicateForceAction":
                return duplicateForceAction(params);
            case "selectEntryAction":
                return selectEntryAction(params);
            default:
                throw new IllegalArgumentException("Unknown action: " + method);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 1: Simple actions (tree select + keyboard/click + poll)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Duplicates a selection: select in roster tree → Ctrl+D → poll for new selection.
     */
    public String duplicateSelectionAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String selectionId = requireString(p, "selectionId");

        JsonObject before = readRosterState();
        // Validate selection exists in the specified force
        JsonObject beforeForce = findForceById(before, forceId);
        if (beforeForce == null) throw new RuntimeException("Force not found: " + forceId);
        JsonObject original = findSelectionById(beforeForce, selectionId);
        if (original == null) throw new RuntimeException("Selection '" + selectionId + "' not found in force '" + forceId + "'");

        runOnFx(() -> {
            selectTreeItemById("#treeRoster", selectionId);
            pressKey(KeyCode.D, "#treeRoster", MAIN_WINDOW, true);
        });

        String originalEntryId = getStringField(original, "entryId");
        String originalName = getStringField(original, "name");

        JsonObject after = waitForStateChange(state -> {
            // Scope search to the same force
            JsonObject afterForce = findForceById(state, forceId);
            if (afterForce == null) return false;
            Set<String> beforeIds = collectSelectionIdsInForce(beforeForce);
            return findNewSelectionMatching(afterForce, beforeIds, originalEntryId, originalName) != null;
        });

        JsonObject afterForce = findForceById(after, forceId);
        Set<String> beforeIds = collectSelectionIdsInForce(beforeForce);
        JsonObject duplicated = findNewSelectionMatching(afterForce, beforeIds, originalEntryId, originalName);
        if (duplicated == null) throw new RuntimeException("Could not find duplicated selection");

        JsonObject result = new JsonObject();
        result.addProperty("selectionId", getStringField(duplicated, "id"));
        return result.toString();
    }

    /**
     * Duplicates a force: select in roster tree → Ctrl+D → poll for new force.
     */
    public String duplicateForceAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");

        JsonObject before = readRosterState();

        runOnFx(() -> {
            selectTreeItemById("#treeRoster", forceId);
            pressKey(KeyCode.D, "#treeRoster", MAIN_WINDOW, true);
        });

        JsonObject after = waitForStateChange(state -> {
            JsonObject duplicated = findDuplicatedForce(before, state, forceId);
            return duplicated != null;
        });

        JsonObject duplicated = findDuplicatedForce(before, after, forceId);
        JsonObject result = new JsonObject();
        result.addProperty("forceId", duplicated.get("id").getAsString());
        return result.toString();
    }

    /**
     * Selects an entry in the catalogue tree: select force in roster tree →
     * double-click entry in catalogue tree → poll for new selection.
     *
     * Split into two FX phases: first select the force (which populates the
     * catalogue tree), then click the entry in the now-populated catalogue.
     */
    public String selectEntryAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String entryId = requireString(p, "entryId");

        JsonObject before = readRosterState();

        // Phase 1: Select the force in the roster tree
        runOnFx(() -> selectTreeItemById("#treeRoster", forceId));

        // Brief pause to allow catalogue tree to refresh for the selected force
        sleep(300);

        // Phase 2: Double-click the entry in the catalogue tree
        runOnFx(() -> clickTreeItemById("#treeCatalogue", entryId, true));

        JsonObject after = waitForStateChange(state ->
                findCreatedSelection(before, state, forceId, null, entryId) != null);

        JsonObject created = findCreatedSelection(before, after, forceId, null, entryId);
        return buildSelectionOutputs(before, after, created).toString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // FX Thread Dispatch
    // ═══════════════════════════════════════════════════════════════════

    @FunctionalInterface
    private interface FxAction {
        void run() throws Exception;
    }

    /**
     * Executes an action on the JavaFX Application Thread and waits for completion.
     */
    private void runOnFx(FxAction action) {
        if (Platform.isFxApplicationThread()) {
            try {
                action.run();
            } catch (RuntimeException e) {
                throw e;
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
            return;
        }

        CompletableFuture<Void> future = new CompletableFuture<>();
        Platform.runLater(() -> {
            try {
                action.run();
                future.complete(null);
            } catch (Exception e) {
                future.completeExceptionally(e);
            }
        });

        try {
            future.get(FX_TIMEOUT_MS, TimeUnit.MILLISECONDS);
        } catch (java.util.concurrent.TimeoutException e) {
            throw new RuntimeException("FX thread did not respond within " + FX_TIMEOUT_MS + "ms");
        } catch (java.util.concurrent.ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException) throw (RuntimeException) cause;
            throw new RuntimeException(cause);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted waiting for FX thread", e);
        }
    }

    /**
     * Executes an action on the FX thread that returns a value.
     */
    private <T> T runOnFxGet(java.util.concurrent.Callable<T> action) {
        if (Platform.isFxApplicationThread()) {
            try {
                return action.call();
            } catch (RuntimeException e) {
                throw e;
            } catch (Exception e) {
                throw new RuntimeException(e);
            }
        }

        CompletableFuture<T> future = new CompletableFuture<>();
        Platform.runLater(() -> {
            try {
                future.complete(action.call());
            } catch (Exception e) {
                future.completeExceptionally(e);
            }
        });

        try {
            return future.get(FX_TIMEOUT_MS, TimeUnit.MILLISECONDS);
        } catch (java.util.concurrent.TimeoutException e) {
            throw new RuntimeException("FX thread did not respond within " + FX_TIMEOUT_MS + "ms");
        } catch (java.util.concurrent.ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException) throw (RuntimeException) cause;
            throw new RuntimeException(cause);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted waiting for FX thread", e);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // State Polling
    // ═══════════════════════════════════════════════════════════════════

    private JsonObject readRosterState() {
        String json = engineAccessor.getRosterState();
        JsonElement parsed = new JsonParser().parse(json);
        if (!parsed.isJsonObject()) {
            throw new RuntimeException("getRosterState returned non-object: " + json);
        }
        JsonObject obj = parsed.getAsJsonObject();
        if (obj.has("error")) {
            throw new RuntimeException("getRosterState error: " + obj.get("error").getAsString());
        }
        return obj;
    }

    private JsonObject waitForStateChange(Predicate<JsonObject> predicate) {
        long deadline = System.currentTimeMillis() + STATE_POLL_TIMEOUT_MS;
        RuntimeException lastError = null;

        while (System.currentTimeMillis() < deadline) {
            try {
                JsonObject state = readRosterState();
                if (predicate.test(state)) {
                    return state;
                }
            } catch (RuntimeException e) {
                lastError = e;
            }
            sleep(POLL_INTERVAL_MS);
        }

        throw new RuntimeException("Timed out waiting for state change" +
                (lastError != null ? ": " + lastError.getMessage() : ""));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Window Waits
    // ═══════════════════════════════════════════════════════════════════

    private void waitForWindow(String titleFragment) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            Boolean found = runOnFxGet(() -> hasWindow(titleFragment));
            if (found) return;
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Window '" + titleFragment + "' did not appear within " + WINDOW_TIMEOUT_MS + "ms");
    }

    private void waitForWindowClose(String titleFragment) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            Boolean found = runOnFxGet(() -> hasWindow(titleFragment));
            if (!found) return;
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Window '" + titleFragment + "' did not close within " + WINDOW_TIMEOUT_MS + "ms");
    }

    private boolean hasWindow(String titleFragment) {
        for (Window w : Window.getWindows()) {
            if (w instanceof Stage) {
                Stage s = (Stage) w;
                if (s.getTitle() != null && s.getTitle().contains(titleFragment) && s.isShowing()) {
                    return true;
                }
            }
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // UI Interaction Primitives (run on FX thread)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Finds and selects a tree item whose text contains the ID token {@code :id:}.
     */
    private void selectTreeItemById(String treeSelector, String id) {
        String token = ":" + id + ":";
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) throw new RuntimeException("Main window scene not found");
        Node node = scene.getRoot().lookup(treeSelector);
        if (node == null) throw new RuntimeException("TreeView not found: " + treeSelector);
        if (!(node instanceof TreeView)) throw new RuntimeException("Not a TreeView: " + treeSelector);

        @SuppressWarnings("unchecked")
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), token);
        if (item == null) throw new RuntimeException("Tree item not found for id: " + id);

        tree.getSelectionModel().select(item);
    }

    /**
     * Clicks (or double-clicks) a tree item located by ID token.
     */
    private void clickTreeItemById(String treeSelector, String id, boolean doubleClick) {
        String token = ":" + id + ":";
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) throw new RuntimeException("Main window scene not found");
        Node node = scene.getRoot().lookup(treeSelector);
        if (node == null) throw new RuntimeException("TreeView not found: " + treeSelector);
        if (!(node instanceof TreeView)) throw new RuntimeException("Not a TreeView: " + treeSelector);

        @SuppressWarnings("unchecked")
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), token);
        if (item == null) throw new RuntimeException("Tree item not found for id: " + id);

        // Select the item first
        tree.getSelectionModel().select(item);
        int row = tree.getRow(item);
        tree.scrollTo(row);

        // Find the rendered TreeCell and fire mouse events on it
        Node target = tree;
        for (Node child : tree.lookupAll(".tree-cell")) {
            if (child instanceof TreeCell) {
                @SuppressWarnings("unchecked")
                TreeCell<Object> cell = (TreeCell<Object>) child;
                if (cell.getTreeItem() == item && !cell.isEmpty()) {
                    target = cell;
                    break;
                }
            }
        }

        fireMouseClick(target, doubleClick);
    }

    private void pressKey(KeyCode keyCode, String selector, String windowTitle, boolean ctrl) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found for: " + windowTitle);
        Node target = selector != null ? scene.getRoot().lookup(selector) : scene.getFocusOwner();
        if (target == null) target = scene.getRoot();

        KeyEvent pressed = new KeyEvent(
                KeyEvent.KEY_PRESSED, "", "", keyCode,
                false, ctrl, false, false);
        KeyEvent released = new KeyEvent(
                KeyEvent.KEY_RELEASED, "", "", keyCode,
                false, ctrl, false, false);

        target.fireEvent(pressed);
        target.fireEvent(released);
    }

    private void fireMouseClick(Node target, boolean doubleClick) {
        double x = target.getBoundsInLocal().getWidth() / 2;
        double y = target.getBoundsInLocal().getHeight() / 2;
        int clickCount = doubleClick ? 2 : 1;

        MouseEvent pressed = new MouseEvent(MouseEvent.MOUSE_PRESSED,
                x, y, x, y, MouseButton.PRIMARY, clickCount,
                false, false, false, false, true, false, false, false, false, false, null);
        MouseEvent released = new MouseEvent(MouseEvent.MOUSE_RELEASED,
                x, y, x, y, MouseButton.PRIMARY, clickCount,
                false, false, false, false, false, false, false, false, false, false, null);
        MouseEvent clicked = new MouseEvent(MouseEvent.MOUSE_CLICKED,
                x, y, x, y, MouseButton.PRIMARY, clickCount,
                false, false, false, false, false, false, false, false, false, false, null);

        target.fireEvent(pressed);
        target.fireEvent(released);
        target.fireEvent(clicked);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scene/Window Resolution (FX thread)
    // ═══════════════════════════════════════════════════════════════════

    private Scene findScene(String windowTitle) {
        for (Window w : Window.getWindows()) {
            if (w instanceof Stage) {
                Stage s = (Stage) w;
                if (windowTitle == null || windowTitle.isEmpty()
                        || (s.getTitle() != null && s.getTitle().contains(windowTitle))) {
                    if (s.getScene() != null) {
                        return s.getScene();
                    }
                }
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Tree Traversal
    // ═══════════════════════════════════════════════════════════════════

    private TreeItem<Object> findTreeItemByText(TreeItem<Object> item, String text) {
        if (item == null) return null;
        Object val = item.getValue();
        if (val != null && val.toString().contains(text)) {
            return item;
        }
        for (TreeItem<Object> child : item.getChildren()) {
            TreeItem<Object> found = findTreeItemByText(child, text);
            if (found != null) return found;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // State Diffing — Find Created/Duplicated Entities
    // ═══════════════════════════════════════════════════════════════════

    private JsonObject findDuplicatedForce(JsonObject before, JsonObject after, String originalForceId) {
        Set<String> beforeIds = collectAllForceIds(before);
        JsonObject original = findForceById(before, originalForceId);
        if (original == null) return null;

        String originalName = getStringField(original, "name");
        String originalCatalogueId = getStringField(original, "catalogueId");

        for (JsonObject force : allForces(after)) {
            String id = getStringField(force, "id");
            if (id == null || beforeIds.contains(id)) continue;
            if (Objects.equals(getStringField(force, "name"), originalName)
                    && Objects.equals(getStringField(force, "catalogueId"), originalCatalogueId)) {
                return force;
            }
        }
        return null;
    }

    /**
     * Collects all selection IDs within a single force (including nested children).
     */
    private Set<String> collectSelectionIdsInForce(JsonObject force) {
        Set<String> ids = new HashSet<>();
        JsonArray selections = force.has("selections") ? force.getAsJsonArray("selections") : null;
        if (selections != null) {
            collectSelectionIdsRecursive(selections, ids);
        }
        return ids;
    }

    private void collectSelectionIdsRecursive(JsonArray selections, Set<String> ids) {
        for (JsonElement el : selections) {
            if (!el.isJsonObject()) continue;
            JsonObject sel = el.getAsJsonObject();
            String id = getStringField(sel, "id");
            if (id != null) ids.add(id);
            JsonArray children = sel.has("children") ? sel.getAsJsonArray("children") : null;
            if (children != null) collectSelectionIdsRecursive(children, ids);
        }
    }

    /**
     * Finds a new selection (not in beforeIds) matching the given entryId and name.
     */
    private JsonObject findNewSelectionMatching(JsonObject force, Set<String> beforeIds, String entryId, String name) {
        JsonArray selections = force.has("selections") ? force.getAsJsonArray("selections") : null;
        if (selections == null) return null;
        return findNewSelectionMatchingInArray(selections, beforeIds, entryId, name);
    }

    private JsonObject findNewSelectionMatchingInArray(JsonArray selections, Set<String> beforeIds, String entryId, String name) {
        for (JsonElement el : selections) {
            if (!el.isJsonObject()) continue;
            JsonObject sel = el.getAsJsonObject();
            String id = getStringField(sel, "id");
            if (id != null && !beforeIds.contains(id)
                    && Objects.equals(getStringField(sel, "entryId"), entryId)
                    && Objects.equals(getStringField(sel, "name"), name)) {
                return sel;
            }
            JsonArray children = sel.has("children") ? sel.getAsJsonArray("children") : null;
            if (children != null) {
                JsonObject found = findNewSelectionMatchingInArray(children, beforeIds, entryId, name);
                if (found != null) return found;
            }
        }
        return null;
    }

    private JsonObject findCreatedSelection(
            JsonObject before, JsonObject after,
            String forceId, String parentSelectionId, String entryId) {
        Set<String> beforeIds = collectAllSelectionIds(before);

        // Get selections from the target scope in after-state
        List<JsonObject> afterSelections = getSelectionsInScope(after, forceId, parentSelectionId);

        for (JsonObject sel : afterSelections) {
            String id = getStringField(sel, "id");
            if (id == null || beforeIds.contains(id)) continue;
            if (Objects.equals(getStringField(sel, "entryId"), entryId)) {
                return sel;
            }
        }

        // Fallback: check for number increment (non-instanced entry)
        List<JsonObject> beforeSelections = getSelectionsInScope(before, forceId, parentSelectionId);
        Map<String, Integer> beforeNumbers = new HashMap<>();
        for (JsonObject sel : beforeSelections) {
            String id = getStringField(sel, "id");
            if (id != null && Objects.equals(getStringField(sel, "entryId"), entryId)) {
                beforeNumbers.put(id, getIntField(sel, "number", 1));
            }
        }
        for (JsonObject sel : afterSelections) {
            String id = getStringField(sel, "id");
            if (id != null && Objects.equals(getStringField(sel, "entryId"), entryId)) {
                int afterNum = getIntField(sel, "number", 1);
                Integer beforeNum = beforeNumbers.get(id);
                if (beforeNum != null && afterNum > beforeNum) {
                    return sel;
                }
            }
        }
        return null;
    }

    private JsonObject buildSelectionOutputs(JsonObject before, JsonObject after, JsonObject created) {
        JsonObject result = new JsonObject();
        result.addProperty("selectionId", getStringField(created, "id"));

        // Collect new child selection IDs (entryId → selectionId map)
        Set<String> beforeIds = collectAllSelectionIds(before);
        JsonObject selections = new JsonObject();
        collectNewChildSelectionIds(created, beforeIds, selections);
        if (selections.entrySet().size() > 0) {
            result.add("selections", selections);
        }
        return result;
    }

    private void collectNewChildSelectionIds(JsonObject selection, Set<String> beforeIds, JsonObject result) {
        JsonArray children = selection.has("children") ? selection.getAsJsonArray("children") : null;
        if (children == null) return;

        for (JsonElement child : children) {
            if (!child.isJsonObject()) continue;
            JsonObject childObj = child.getAsJsonObject();
            String id = getStringField(childObj, "id");
            String entryId = getStringField(childObj, "entryId");
            if (id != null && entryId != null && !beforeIds.contains(id)) {
                if (!result.has(entryId)) {
                    result.addProperty(entryId, id);
                }
            }
            collectNewChildSelectionIds(childObj, beforeIds, result);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // JSON Navigation Helpers
    // ═══════════════════════════════════════════════════════════════════

    private Set<String> collectAllSelectionIds(JsonObject rosterState) {
        Set<String> ids = new HashSet<>();
        for (JsonObject sel : allSelections(rosterState)) {
            String id = getStringField(sel, "id");
            if (id != null) ids.add(id);
        }
        return ids;
    }

    private Set<String> collectAllForceIds(JsonObject rosterState) {
        Set<String> ids = new HashSet<>();
        for (JsonObject force : allForces(rosterState)) {
            String id = getStringField(force, "id");
            if (id != null) ids.add(id);
        }
        return ids;
    }

    private List<JsonObject> allForces(JsonObject rosterState) {
        List<JsonObject> result = new ArrayList<>();
        JsonArray forces = rosterState.has("forces") ? rosterState.getAsJsonArray("forces") : null;
        if (forces == null) return result;
        collectForcesRecursive(forces, result);
        return result;
    }

    private void collectForcesRecursive(JsonArray forces, List<JsonObject> result) {
        for (JsonElement el : forces) {
            if (!el.isJsonObject()) continue;
            JsonObject force = el.getAsJsonObject();
            result.add(force);
            JsonArray childForces = force.has("childForces") ? force.getAsJsonArray("childForces") : null;
            if (childForces != null) {
                collectForcesRecursive(childForces, result);
            }
        }
    }

    private List<JsonObject> allSelections(JsonObject rosterState) {
        List<JsonObject> result = new ArrayList<>();
        for (JsonObject force : allForces(rosterState)) {
            JsonArray selections = force.has("selections") ? force.getAsJsonArray("selections") : null;
            if (selections != null) {
                collectSelectionsRecursive(selections, result);
            }
        }
        return result;
    }

    private void collectSelectionsRecursive(JsonArray selections, List<JsonObject> result) {
        for (JsonElement el : selections) {
            if (!el.isJsonObject()) continue;
            JsonObject sel = el.getAsJsonObject();
            result.add(sel);
            JsonArray children = sel.has("children") ? sel.getAsJsonArray("children") : null;
            if (children != null) {
                collectSelectionsRecursive(children, result);
            }
        }
    }

    private List<JsonObject> getSelectionsInScope(JsonObject rosterState, String forceId, String parentSelectionId) {
        List<JsonObject> result = new ArrayList<>();
        JsonObject force = findForceById(rosterState, forceId);
        if (force == null) return result;

        if (parentSelectionId == null) {
            JsonArray selections = force.has("selections") ? force.getAsJsonArray("selections") : null;
            if (selections != null) {
                for (JsonElement el : selections) {
                    if (el.isJsonObject()) result.add(el.getAsJsonObject());
                }
            }
        } else {
            JsonObject parent = findSelectionById(force, parentSelectionId);
            if (parent != null) {
                JsonArray children = parent.has("children") ? parent.getAsJsonArray("children") : null;
                if (children != null) {
                    for (JsonElement el : children) {
                        if (el.isJsonObject()) result.add(el.getAsJsonObject());
                    }
                }
            }
        }
        return result;
    }

    private JsonObject findForceById(JsonObject rosterState, String forceId) {
        for (JsonObject force : allForces(rosterState)) {
            if (Objects.equals(getStringField(force, "id"), forceId)) {
                return force;
            }
        }
        return null;
    }

    private JsonObject findSelectionById(JsonObject scope, String selectionId) {
        // scope can be a rosterState or a force — walk all selections
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections != null) {
            JsonObject found = findSelectionByIdInArray(selections, selectionId);
            if (found != null) return found;
        }
        // Also walk forces if present (rosterState level)
        JsonArray forces = scope.has("forces") ? scope.getAsJsonArray("forces") : null;
        if (forces != null) {
            for (JsonElement el : forces) {
                if (!el.isJsonObject()) continue;
                JsonObject found = findSelectionById(el.getAsJsonObject(), selectionId);
                if (found != null) return found;
            }
        }
        JsonArray childForces = scope.has("childForces") ? scope.getAsJsonArray("childForces") : null;
        if (childForces != null) {
            for (JsonElement el : childForces) {
                if (!el.isJsonObject()) continue;
                JsonObject found = findSelectionById(el.getAsJsonObject(), selectionId);
                if (found != null) return found;
            }
        }
        return null;
    }

    private JsonObject findSelectionByIdInArray(JsonArray selections, String selectionId) {
        for (JsonElement el : selections) {
            if (!el.isJsonObject()) continue;
            JsonObject sel = el.getAsJsonObject();
            if (Objects.equals(getStringField(sel, "id"), selectionId)) {
                return sel;
            }
            JsonArray children = sel.has("children") ? sel.getAsJsonArray("children") : null;
            if (children != null) {
                JsonObject found = findSelectionByIdInArray(children, selectionId);
                if (found != null) return found;
            }
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Utility
    // ═══════════════════════════════════════════════════════════════════

    private static JsonObject parseParams(String paramsJson) {
        if (paramsJson == null || paramsJson.isEmpty()) return new JsonObject();
        JsonElement el = new JsonParser().parse(paramsJson);
        return el != null && el.isJsonObject() ? el.getAsJsonObject() : new JsonObject();
    }

    private static String requireString(JsonObject params, String key) {
        JsonElement el = params.get(key);
        if (el == null || el.isJsonNull()) {
            throw new IllegalArgumentException("Missing required parameter: " + key);
        }
        return el.getAsString();
    }

    private static String getStringField(JsonObject obj, String key) {
        JsonElement el = obj.get(key);
        return (el != null && !el.isJsonNull()) ? el.getAsString() : null;
    }

    private static int getIntField(JsonObject obj, String key, int defaultValue) {
        JsonElement el = obj.get(key);
        return (el != null && !el.isJsonNull()) ? el.getAsInt() : defaultValue;
    }

    private static void sleep(int ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted", e);
        }
    }
}
