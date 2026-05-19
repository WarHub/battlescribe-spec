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
            case "createRosterAction":
                return createRosterAction(params);
            case "addForceAction":
                return addForceAction(params);
            case "addChildForceAction":
                return addChildForceAction(params);
            case "removeForceAction":
                return removeForceAction(params);
            case "selectChildEntryAction":
                return selectChildEntryAction(params);
            case "deselectSelectionAction":
                return deselectSelectionAction(params);
            case "setSelectionCountAction":
                return setSelectionCountAction(params);
            case "setCostLimitAction":
                return setCostLimitAction(params);
            case "setCustomizationAction":
                return setCustomizationAction(params);
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
    // Phase 2: Dialog-based actions
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Creates a new roster (first force). Opens the New Roster dialog,
     * selects game system, optionally sets cost limit, adds a force, and closes.
     *
     * @param params JSON: {forceEntryId, catalogueId, gameSystemName, costLimit (optional int)}
     */
    public String createRosterAction(String params) {
        JsonObject p = parseParams(params);
        String forceEntryId = requireString(p, "forceEntryId");
        String catalogueId = requireString(p, "catalogueId");
        String gameSystemName = requireString(p, "gameSystemName");
        int costLimit = getIntParam(p, "costLimit", -1);

        // Fire "New Roster" button (async because it opens a modal dialog)
        runOnFx(() -> fireButtonAsync("#btnNewRoster", MAIN_WINDOW));
        waitForWindow(NEW_ROSTER_WINDOW);

        // Select game system in the combo
        runOnFx(() -> {
            selectComboBoxItemByText("#cboGameSystem", gameSystemName, NEW_ROSTER_WINDOW);
        });
        sleep(300);

        // Set cost limit if specified
        if (costLimit >= 0) {
            runOnFx(() -> {
                setSpinnerInWindow(NEW_ROSTER_WINDOW, costLimit);
            });
        }

        // Open Add Force dialog
        runOnFx(() -> fireButtonAsync("#btnAddForce", NEW_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW);

        // Select catalogue and force entry in Add Force dialog
        runOnFx(() -> {
            selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW);
        });
        sleep(300);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW);

        // Close New Roster dialog
        runOnFx(() -> fireButtonAsync("#btnDone", NEW_ROSTER_WINDOW));
        waitForWindowClose(NEW_ROSTER_WINDOW);

        // Wait for engine to be available and read state
        waitForEngineAvailable();
        JsonObject after = readRosterState();

        // The first force is the only force
        JsonArray forces = after.has("forces") ? after.getAsJsonArray("forces") : new JsonArray();
        if (forces.size() == 0) {
            throw new RuntimeException("createRosterAction: no forces found after roster creation");
        }
        JsonObject createdForce = forces.get(0).getAsJsonObject();
        return buildForceOutputs(createdForce).toString();
    }

    /**
     * Adds a force via the Edit Roster dialog (roster already exists).
     *
     * @param params JSON: {forceEntryId, catalogueId}
     */
    public String addForceAction(String params) {
        JsonObject p = parseParams(params);
        String forceEntryId = requireString(p, "forceEntryId");
        String catalogueId = requireString(p, "catalogueId");

        JsonObject before = readRosterState();

        // Open Edit Roster dialog
        runOnFx(() -> fireButtonAsync("#btnEditRoster", MAIN_WINDOW));
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Open Add Force sub-dialog
        runOnFx(() -> fireButtonAsync("#btnAddForce", EDIT_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW);

        // Select catalogue and force entry
        runOnFx(() -> selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW));
        sleep(300);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW);

        // Close Edit Roster
        runOnFx(() -> fireButton("#btnDone", EDIT_ROSTER_WINDOW));
        waitForWindowClose(EDIT_ROSTER_WINDOW);

        // Poll for new force
        JsonObject after = waitForStateChange(state -> findNewForce(before, state) != null);
        JsonObject createdForce = findNewForce(before, after);
        return buildForceOutputs(createdForce).toString();
    }

    /**
     * Adds a child force under a parent force.
     *
     * @param params JSON: {parentForceId, forceEntryId, catalogueId}
     */
    public String addChildForceAction(String params) {
        JsonObject p = parseParams(params);
        String parentForceId = requireString(p, "parentForceId");
        String forceEntryId = requireString(p, "forceEntryId");
        String catalogueId = requireString(p, "catalogueId");

        JsonObject before = readRosterState();

        // Open Edit Roster, select parent force
        runOnFx(() -> fireButtonAsync("#btnEditRoster", MAIN_WINDOW));
        waitForWindow(EDIT_ROSTER_WINDOW);

        runOnFx(() -> selectTreeItemById("#treeForces", parentForceId, EDIT_ROSTER_WINDOW));

        // Add Force
        runOnFx(() -> fireButtonAsync("#btnAddForce", EDIT_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW);

        runOnFx(() -> selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW));
        sleep(300);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW);

        // Close Edit Roster
        runOnFx(() -> fireButton("#btnDone", EDIT_ROSTER_WINDOW));
        waitForWindowClose(EDIT_ROSTER_WINDOW);

        // Poll for new force (child of parent)
        JsonObject after = waitForStateChange(state -> findNewForce(before, state) != null);
        JsonObject createdForce = findNewForce(before, after);
        return buildForceOutputs(createdForce).toString();
    }

    /**
     * Removes a force via Edit Roster → click tree cell button (X) → confirm YES.
     *
     * @param params JSON: {forceId}
     */
    public String removeForceAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");

        JsonObject before = readRosterState();
        // Verify force exists
        if (findForceById(before, forceId) == null) {
            throw new RuntimeException("Force not found: " + forceId);
        }

        // Open Edit Roster
        runOnFx(() -> fireButtonAsync("#btnEditRoster", MAIN_WINDOW));
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Click the remove button on the force's tree cell (fires async, triggers confirm dialog)
        runOnFx(() -> clickTreeCellButton("#treeForces", forceId, EDIT_ROSTER_WINDOW));
        sleep(500);

        // Dismiss confirmation dialog
        runOnFx(() -> clickButtonByText("YES", CONFIRM_WINDOW));

        // Close Edit Roster
        sleep(300);
        runOnFx(() -> fireButton("#btnDone", EDIT_ROSTER_WINDOW));
        waitForWindowClose(EDIT_ROSTER_WINDOW);

        // Poll until force is gone
        waitForStateChange(state -> findForceById(state, forceId) == null);

        JsonObject result = new JsonObject();
        result.addProperty("removed", true);
        return result.toString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 4: Specialized actions (cost limits, customization)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Sets a cost limit value via Edit Roster dialog.
     * Opens Edit Roster, finds the spinner by cost name label, sets value, closes.
     *
     * @param params JSON: {costTypeId, costName, value}
     */
    public String setCostLimitAction(String params) {
        JsonObject p = parseParams(params);
        String costTypeId = requireString(p, "costTypeId");
        String costName = requireString(p, "costName");
        int value = getIntParam(p, "value", -1);
        if (value < 0) throw new RuntimeException("value must be >= 0");

        // Open Edit Roster
        runOnFx(() -> fireButtonAsync("#btnEditRoster", MAIN_WINDOW));
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Set cost limit spinner by name
        runOnFx(() -> setSpinnerValueByLabel(costName, value, EDIT_ROSTER_WINDOW));

        // Close Edit Roster
        runOnFx(() -> fireButton("#btnDone", EDIT_ROSTER_WINDOW));
        waitForWindowClose(EDIT_ROSTER_WINDOW);

        JsonObject result = new JsonObject();
        result.addProperty("set", true);
        result.addProperty("costTypeId", costTypeId);
        result.addProperty("value", value);
        return result.toString();
    }

    /**
     * Sets custom name and/or notes on a force or selection.
     * Opens the customization dialog via the Customise Name button.
     *
     * @param params JSON: {forceId, selectionId (optional), customName (optional), customNotes (optional)}
     */
    public String setCustomizationAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String selectionId = p.has("selectionId") && !p.get("selectionId").isJsonNull()
                ? p.get("selectionId").getAsString() : null;
        String customName = p.has("customName") && !p.get("customName").isJsonNull()
                ? p.get("customName").getAsString() : null;
        String customNotes = p.has("customNotes") && !p.get("customNotes").isJsonNull()
                ? p.get("customNotes").getAsString() : null;

        // Select the target (selection or force) in the roster tree
        String targetId = selectionId != null ? selectionId : forceId;
        runOnFx(() -> selectTreeItemById("#treeRoster", targetId, MAIN_WINDOW));
        sleep(300);

        // Click the Customise Name button (async — it opens a modal)
        runOnFx(() -> fireButtonAsync("#btnCustomiseName", MAIN_WINDOW));
        sleep(500);

        // Wait for the customization dialog — could be "Customise" or similar title
        String customizeWindow = waitForFirstWindow("Customise", "Customize", "Name");
        if (customizeWindow == null) {
            throw new RuntimeException("Customization dialog did not appear");
        }

        // Set custom name if provided
        if (customName != null) {
            final String cw = customizeWindow;
            runOnFx(() -> setTextField(cw, customName, "#txtName", "#txtCustomName", "TextField"));
        }

        // Set custom notes if provided
        if (customNotes != null) {
            final String cw = customizeWindow;
            runOnFx(() -> setTextArea(cw, customNotes, "#txtNotes", "#txtCustomNotes", "TextArea"));
        }

        // Confirm the dialog
        final String cw = customizeWindow;
        runOnFx(() -> {
            if (!tryFireButton("#btnDone", cw)) {
                clickButtonByText("Done", cw);
            }
        });
        waitForWindowClose(customizeWindow);

        JsonObject result = new JsonObject();
        result.addProperty("set", true);
        return result.toString();
    }

    /**
     * Waits for the first window matching any of the given title substrings.
     * Returns the matched window title, or null if timeout.
     */
    private String waitForFirstWindow(String... titlePatterns) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            AtomicReference<String> found = new AtomicReference<>();
            runOnFx(() -> {
                for (Window w : Window.getWindows()) {
                    if (!(w instanceof Stage)) continue;
                    Stage s = (Stage) w;
                    String title = s.getTitle();
                    if (title == null) continue;
                    for (String pattern : titlePatterns) {
                        if (title.contains(pattern)) {
                            found.set(title);
                            return;
                        }
                    }
                }
            });
            if (found.get() != null) return found.get();
            sleep(POLL_INTERVAL_MS);
        }
        return null;
    }

    /**
     * Sets a TextField identified by CSS selectors. Tries each selector in order.
     * Must be called from the FX thread.
     */
    private void setTextField(String windowTitle, String text, String... selectors) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (String selector : selectors) {
            Node node = scene.getRoot().lookup(selector);
            if (node instanceof TextField) {
                ((TextField) node).setText(text);
                return;
            }
        }
        // Fallback: find any TextField
        for (Node n : scene.getRoot().lookupAll("TextField")) {
            if (n instanceof TextField) {
                ((TextField) n).setText(text);
                return;
            }
        }
        throw new RuntimeException("TextField not found in " + windowTitle);
    }

    /**
     * Sets a TextArea identified by CSS selectors. Tries each selector in order.
     * Must be called from the FX thread.
     */
    private void setTextArea(String windowTitle, String text, String... selectors) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (String selector : selectors) {
            Node node = scene.getRoot().lookup(selector);
            if (node instanceof TextArea) {
                ((TextArea) node).setText(text);
                return;
            }
        }
        // Fallback: find any TextArea
        for (Node n : scene.getRoot().lookupAll("TextArea")) {
            if (n instanceof TextArea) {
                ((TextArea) n).setText(text);
                return;
            }
        }
        throw new RuntimeException("TextArea not found in " + windowTitle);
    }

    /**
     * Tries to fire a button by CSS selector. Returns true if successful.
     * Must be called from the FX thread.
     */
    private boolean tryFireButton(String selector, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) return false;
        Node node = scene.getRoot().lookup(selector);
        if (node instanceof ButtonBase) {
            ((ButtonBase) node).fire();
            return true;
        }
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Phase 3: Edit-panel actions (child entries, deselect, count)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Selects a child entry under a parent selection by clicking the edit panel control.
     * Selects the parent in the roster tree, then finds the control labeled with the
     * entry's name and clicks/increments it.
     *
     * @param params JSON: {forceId, parentSelectionId, entryId}
     */
    public String selectChildEntryAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String parentSelectionId = requireString(p, "parentSelectionId");
        String entryId = requireString(p, "entryId");
        // entryName passed from C# (resolved from catalog data)
        String entryName = p.has("entryName") && !p.get("entryName").isJsonNull()
                ? p.get("entryName").getAsString() : null;

        JsonObject before = readRosterState();

        // Select the parent selection in the roster tree
        runOnFx(() -> selectTreeItemById("#treeRoster", parentSelectionId, MAIN_WINDOW));
        sleep(500);

        // Resolve entry name: prefer passed name, fall back to roster state lookup
        if (entryName == null || entryName.isEmpty()) {
            entryName = resolveEntryName(before, entryId);
        }

        // Click the control by label (spinner increment, button fire, or checkbox toggle)
        final String labelText = entryName;
        runOnFx(() -> clickControlByLabel(labelText, MAIN_WINDOW, null));

        // Wait for a new child selection to appear under the parent
        JsonObject after = waitForStateChange(state -> {
            JsonObject parent = findSelectionById(state, parentSelectionId);
            if (parent == null) return false;
            JsonObject beforeParent = findSelectionById(before, parentSelectionId);
            if (beforeParent == null) return true;
            return childSelectionCount(parent) > childSelectionCount(beforeParent);
        });

        // Find the new child selection (in after but not in before)
        JsonObject createdSelection = findNewChildSelection(before, after, parentSelectionId, entryId);
        JsonObject result = new JsonObject();
        if (createdSelection != null) {
            result.addProperty("selectionId", getStringField(createdSelection, "id"));
        }
        return result.toString();
    }

    /**
     * Deselects (removes) a selection by clicking its edit panel control in decrement mode,
     * or using Delete key if no decrement control is available.
     *
     * @param params JSON: {forceId, selectionId}
     */
    public String deselectSelectionAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String selectionId = requireString(p, "selectionId");
        String passedEntryName = p.has("entryName") && !p.get("entryName").isJsonNull()
                ? p.get("entryName").getAsString() : null;

        JsonObject state = readRosterState();
        JsonObject selection = findSelectionById(state, selectionId);
        if (selection == null) {
            throw new RuntimeException("Selection not found: " + selectionId);
        }

        String entryId = getStringField(selection, "entryId");
        String entryName = passedEntryName != null ? passedEntryName : resolveEntryName(state, entryId);
        String parentId = findSelectionParentId(state, selectionId);
        if (parentId == null) parentId = forceId;

        // Select the parent in the roster tree
        final String parentIdFinal = parentId;
        runOnFx(() -> selectTreeItemById("#treeRoster", parentIdFinal, MAIN_WINDOW));
        sleep(500);

        // Try decrement via control by label
        final String finalEntryName = entryName;
        AtomicReference<Boolean> clicked = new AtomicReference<>(false);
        runOnFx(() -> {
            clicked.set(tryClickControlByLabel(finalEntryName, MAIN_WINDOW, "decrement"));
        });

        if (!clicked.get()) {
            // Fallback: select the selection itself and press DELETE
            runOnFx(() -> {
                selectTreeItemById("#treeRoster", selectionId, MAIN_WINDOW);
            });
            sleep(300);
            runOnFx(() -> pressKey(KeyCode.DELETE, "#treeRoster", MAIN_WINDOW, false));
        }

        // Wait for selection to disappear
        waitForStateChange(s -> findSelectionById(s, selectionId) == null);

        JsonObject result = new JsonObject();
        result.addProperty("removed", true);
        return result.toString();
    }

    /**
     * Sets the selection count (number) by finding the spinner in the edit panel.
     * If count is 0, delegates to deselect.
     *
     * @param params JSON: {forceId, selectionId, count}
     */
    public String setSelectionCountAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String selectionId = requireString(p, "selectionId");
        int count = getIntParam(p, "count", -1);
        if (count < 0) throw new RuntimeException("count must be >= 0");

        if (count == 0) {
            // Deselect (remove) the selection
            JsonObject deselectParams = new JsonObject();
            deselectParams.addProperty("forceId", forceId);
            deselectParams.addProperty("selectionId", selectionId);
            return deselectSelectionAction(deselectParams.toString());
        }

        JsonObject state = readRosterState();
        JsonObject selection = findSelectionById(state, selectionId);
        if (selection == null) {
            throw new RuntimeException("Selection not found: " + selectionId);
        }

        String entryId = getStringField(selection, "entryId");
        String entryName = resolveEntryName(state, entryId);
        String parentId = findSelectionParentId(state, selectionId);
        if (parentId == null) parentId = forceId;

        // Select the parent in the roster tree
        final String parentIdFinal = parentId;
        runOnFx(() -> selectTreeItemById("#treeRoster", parentIdFinal, MAIN_WINDOW));
        sleep(500);

        // Set spinner value by label
        final String finalEntryName = entryName;
        runOnFx(() -> setSpinnerValueByLabel(finalEntryName, count, MAIN_WINDOW));

        // Wait for count to match
        waitForStateChange(s -> {
            JsonObject sel = findSelectionById(s, selectionId);
            return sel != null && getIntField(sel, "number", -1) == count;
        });

        JsonObject result = new JsonObject();
        result.addProperty("set", true);
        result.addProperty("count", count);
        return result.toString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Edit-panel helpers (label-based control lookup)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Clicks the edit panel control (Spinner/Button/CheckBox) by its sibling label text.
     * Must be called from the FX thread.
     */
    private void clickControlByLabel(String labelText, String windowTitle, String action) {
        if (!tryClickControlByLabel(labelText, windowTitle, action)) {
            throw new RuntimeException("Control not found for label: " + labelText);
        }
    }

    /**
     * Tries to click an edit panel control by label. Returns true if found and clicked.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private boolean tryClickControlByLabel(String text, String windowTitle, String action) {
        Scene scene = findScene(windowTitle);
        if (scene == null) return false;

        // Look for Label → sibling Spinner/Button
        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String lt = label.getText();
            if (lt == null || !lt.contains(text)) continue;

            javafx.scene.Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    Spinner<Object> spinner = (Spinner<Object>) sibling;
                    boolean decrement = "decrement".equals(action);
                    if (decrement) {
                        spinner.getValueFactory().decrement(1);
                    } else {
                        spinner.getValueFactory().increment(1);
                    }
                    return true;
                }
                if (sibling instanceof Button) {
                    ((Button) sibling).fire();
                    return true;
                }
            }
        }

        // Look for CheckBox by text
        for (Node cbNode : scene.getRoot().lookupAll(".check-box")) {
            if (!(cbNode instanceof CheckBox)) continue;
            CheckBox cb = (CheckBox) cbNode;
            String cbText = cb.getText();
            if (cbText != null && cbText.contains(text)) {
                cb.fire();
                return true;
            }
        }
        return false;
    }

    /**
     * Sets a Spinner's value by its sibling label. Increments/decrements to reach target.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private void setSpinnerValueByLabel(String text, int value, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String lt = label.getText();
            if (lt == null || !lt.contains(text)) continue;

            javafx.scene.Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    Spinner<Object> spinner = (Spinner<Object>) sibling;
                    Object currentVal = spinner.getValue();
                    int currentInt = (currentVal instanceof Number) ? ((Number) currentVal).intValue() : 0;
                    if (currentInt == value) return;
                    int delta = value - currentInt;
                    SpinnerValueFactory<Object> factory = spinner.getValueFactory();
                    if (delta > 0) {
                        for (int i = 0; i < delta; i++) factory.increment(1);
                    } else {
                        for (int i = 0; i < -delta; i++) factory.decrement(1);
                    }
                    return;
                }
            }
        }
        throw new RuntimeException("Spinner not found for label: " + text);
    }

    /**
     * Resolves an entry name from an entryId by searching the roster state.
     * Finds any selection with matching entryId and returns its name.
     */
    private String resolveEntryName(JsonObject rosterState, String entryId) {
        // Search all forces for a selection with this entryId
        for (JsonObject force : allForces(rosterState)) {
            String name = findEntryNameInSelections(force, entryId);
            if (name != null) return name;
        }
        // Fallback: use the entryId itself (might work for display)
        return entryId;
    }

    private String findEntryNameInSelections(JsonObject scope, String entryId) {
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections != null) {
            for (JsonElement el : selections) {
                if (!el.isJsonObject()) continue;
                JsonObject sel = el.getAsJsonObject();
                if (entryId.equals(getStringField(sel, "entryId"))) {
                    return getStringField(sel, "name");
                }
                String found = findEntryNameInSelections(sel, entryId);
                if (found != null) return found;
            }
        }
        JsonArray children = scope.has("children") ? scope.getAsJsonArray("children") : null;
        if (children != null) {
            for (JsonElement el : children) {
                if (!el.isJsonObject()) continue;
                JsonObject child = el.getAsJsonObject();
                if (entryId.equals(getStringField(child, "entryId"))) {
                    return getStringField(child, "name");
                }
                String found = findEntryNameInSelections(child, entryId);
                if (found != null) return found;
            }
        }
        return null;
    }

    /**
     * Gets the number of child selections in a selection/force.
     */
    private int childSelectionCount(JsonObject scope) {
        int count = 0;
        if (scope.has("selections")) {
            count += scope.getAsJsonArray("selections").size();
        }
        if (scope.has("children")) {
            count += scope.getAsJsonArray("children").size();
        }
        return count;
    }

    /**
     * Finds a new child selection under parentSelectionId that wasn't there before.
     */
    private JsonObject findNewChildSelection(JsonObject before, JsonObject after,
                                              String parentSelectionId, String entryId) {
        JsonObject afterParent = findSelectionById(after, parentSelectionId);
        if (afterParent == null) return null;
        JsonObject beforeParent = findSelectionById(before, parentSelectionId);
        Set<String> beforeIds = new HashSet<>();
        if (beforeParent != null) {
            collectChildSelectionIds(beforeParent, beforeIds);
        }

        return findNewChildInScope(afterParent, beforeIds, entryId);
    }

    private void collectChildSelectionIds(JsonObject scope, Set<String> ids) {
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections != null) {
            for (JsonElement el : selections) {
                if (!el.isJsonObject()) continue;
                String id = getStringField(el.getAsJsonObject(), "id");
                if (id != null) ids.add(id);
            }
        }
        JsonArray children = scope.has("children") ? scope.getAsJsonArray("children") : null;
        if (children != null) {
            for (JsonElement el : children) {
                if (!el.isJsonObject()) continue;
                String id = getStringField(el.getAsJsonObject(), "id");
                if (id != null) ids.add(id);
            }
        }
    }

    private JsonObject findNewChildInScope(JsonObject scope, Set<String> beforeIds, String entryId) {
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections != null) {
            for (JsonElement el : selections) {
                if (!el.isJsonObject()) continue;
                JsonObject sel = el.getAsJsonObject();
                String id = getStringField(sel, "id");
                if (id != null && !beforeIds.contains(id)
                        && entryId.equals(getStringField(sel, "entryId"))) {
                    return sel;
                }
            }
        }
        JsonArray children = scope.has("children") ? scope.getAsJsonArray("children") : null;
        if (children != null) {
            for (JsonElement el : children) {
                if (!el.isJsonObject()) continue;
                JsonObject child = el.getAsJsonObject();
                String id = getStringField(child, "id");
                if (id != null && !beforeIds.contains(id)
                        && entryId.equals(getStringField(child, "entryId"))) {
                    return child;
                }
            }
        }
        return null;
    }

    /**
     * Finds the parent selection ID of a given selection (traverses the roster tree).
     */
    private String findSelectionParentId(JsonObject rosterState, String selectionId) {
        for (JsonObject force : allForces(rosterState)) {
            String found = findParentIdInScope(force, selectionId, getStringField(force, "id"));
            if (found != null) return found;
        }
        return null;
    }

    private String findParentIdInScope(JsonObject scope, String selectionId, String scopeId) {
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections != null) {
            for (JsonElement el : selections) {
                if (!el.isJsonObject()) continue;
                JsonObject sel = el.getAsJsonObject();
                String id = getStringField(sel, "id");
                if (selectionId.equals(id)) return scopeId;
                String found = findParentIdInScope(sel, selectionId, id);
                if (found != null) return found;
            }
        }
        JsonArray children = scope.has("children") ? scope.getAsJsonArray("children") : null;
        if (children != null) {
            for (JsonElement el : children) {
                if (!el.isJsonObject()) continue;
                JsonObject child = el.getAsJsonObject();
                String id = getStringField(child, "id");
                if (selectionId.equals(id)) return scopeId;
                String found = findParentIdInScope(child, selectionId, id);
                if (found != null) return found;
            }
        }
        return null;
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
                String title = s.getTitle();
                if (title != null && s.isShowing() && matchesWindowTitle(title, titleFragment)) {
                    return true;
                }
            }
        }
        return false;
    }

    /**
     * Matches window title using exact match or starts-with (with space separator)
     * to avoid false positives when dialog names appear in the main window title.
     * E.g., "New Roster" should NOT match "Roster Editor 2.03.21 - New Roster (GS v1)".
     */
    private boolean matchesWindowTitle(String actual, String expected) {
        return actual.equals(expected) || actual.startsWith(expected + " ");
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

    /**
     * Fires a ButtonBase by its CSS selector. Uses Platform.runLater internally
     * so the action can open a modal dialog without deadlocking.
     * Must be called from the FX thread.
     */
    private void fireButtonAsync(String selector, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(selector);
        if (node == null) throw new RuntimeException("Button not found: " + selector + " in " + windowTitle);
        if (node instanceof ButtonBase) {
            Platform.runLater(() -> ((ButtonBase) node).fire());
        } else {
            throw new RuntimeException("Node " + selector + " is not a ButtonBase: " + node.getClass().getSimpleName());
        }
    }

    /**
     * Fires a ButtonBase synchronously. Must be called from the FX thread.
     */
    private void fireButton(String selector, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(selector);
        if (node == null) throw new RuntimeException("Button not found: " + selector + " in " + windowTitle);
        if (node instanceof ButtonBase) {
            ((ButtonBase) node).fire();
        } else {
            throw new RuntimeException("Node " + selector + " is not a ButtonBase: " + node.getClass().getSimpleName());
        }
    }

    /**
     * Selects a ComboBox item by matching the item's getId() method via reflection.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private void selectComboBoxItemById(String selector, String targetId, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(selector);
        if (node == null) throw new RuntimeException("ComboBox not found: " + selector + " in " + windowTitle);
        if (!(node instanceof ComboBox)) throw new RuntimeException("Not a ComboBox: " + selector);

        ComboBox<Object> combo = (ComboBox<Object>) node;
        for (int i = 0; i < combo.getItems().size(); i++) {
            Object item = combo.getItems().get(i);
            if (item == null) continue;
            String itemId = getObjectId(item);
            if (targetId.equals(itemId)) {
                combo.getSelectionModel().select(i);
                return;
            }
        }
        // Fallback: try toString().contains(targetId)
        for (int i = 0; i < combo.getItems().size(); i++) {
            Object item = combo.getItems().get(i);
            if (item != null && item.toString().contains(targetId)) {
                combo.getSelectionModel().select(i);
                return;
            }
        }
        throw new RuntimeException("ComboBox item with id '" + targetId + "' not found in " + selector);
    }

    /**
     * Selects a ComboBox item by matching display text (substring).
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private void selectComboBoxItemByText(String selector, String text, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(selector);
        if (node == null) throw new RuntimeException("ComboBox not found: " + selector + " in " + windowTitle);
        if (!(node instanceof ComboBox)) throw new RuntimeException("Not a ComboBox: " + selector);

        ComboBox<Object> combo = (ComboBox<Object>) node;
        for (int i = 0; i < combo.getItems().size(); i++) {
            Object item = combo.getItems().get(i);
            if (item != null && item.toString().contains(text)) {
                combo.getSelectionModel().select(i);
                return;
            }
        }
        // If exact match not found, select first item as fallback
        if (combo.getItems().size() > 0) {
            combo.getSelectionModel().select(0);
        }
    }

    /**
     * Gets an object's ID by calling getId() via reflection.
     */
    private String getObjectId(Object obj) {
        try {
            Method getId = obj.getClass().getMethod("getId");
            Object result = getId.invoke(obj);
            return result != null ? result.toString() : null;
        } catch (Exception e) {
            return null;
        }
    }

    /**
     * Selects a tree item by ID token in a specific window (overload for dialogs).
     * Must be called from the FX thread.
     */
    private void selectTreeItemById(String treeSelector, String id, String windowTitle) {
        String token = ":" + id + ":";
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(treeSelector);
        if (node == null) throw new RuntimeException("TreeView not found: " + treeSelector + " in " + windowTitle);
        if (!(node instanceof TreeView)) throw new RuntimeException("Not a TreeView: " + treeSelector);

        @SuppressWarnings("unchecked")
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), token);
        if (item == null) throw new RuntimeException("Tree item not found for id: " + id + " in " + windowTitle);

        tree.getSelectionModel().select(item);
    }

    /**
     * Clicks the remove button (X) inside a tree cell. The button is fired via
     * Platform.runLater because it typically triggers a modal confirmation dialog.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private void clickTreeCellButton(String treeSelector, String id, String windowTitle) {
        String token = ":" + id + ":";
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(treeSelector);
        if (node == null) throw new RuntimeException("TreeView not found: " + treeSelector);
        if (!(node instanceof TreeView)) throw new RuntimeException("Not a TreeView: " + treeSelector);

        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), token);
        if (item == null) throw new RuntimeException("Tree item not found for id: " + id);

        // Select and scroll to the item to ensure cell is rendered
        tree.getSelectionModel().select(item);
        int row = tree.getRow(item);
        tree.scrollTo(row);

        // Find the tree cell and its embedded button
        for (Node cellNode : tree.lookupAll(".tree-cell")) {
            if (cellNode instanceof TreeCell) {
                TreeCell<Object> cell = (TreeCell<Object>) cellNode;
                if (cell.getTreeItem() == item && !cell.isEmpty()) {
                    Node graphic = cell.getGraphic();
                    if (graphic != null) {
                        // Look for a Button in the graphic
                        Button btn = findButtonInNode(graphic);
                        if (btn != null) {
                            Platform.runLater(btn::fire);
                            return;
                        }
                    }
                    // Also check cell children directly
                    for (Node child : cell.getChildrenUnmodifiable()) {
                        if (child instanceof Button) {
                            Button b = (Button) child;
                            Platform.runLater(b::fire);
                            return;
                        }
                    }
                }
            }
        }
        throw new RuntimeException("Remove button not found for force: " + id);
    }

    private Button findButtonInNode(Node node) {
        if (node instanceof Button) return (Button) node;
        if (node instanceof javafx.scene.Parent) {
            for (Node child : ((javafx.scene.Parent) node).getChildrenUnmodifiable()) {
                Button found = findButtonInNode(child);
                if (found != null) return found;
            }
        }
        return null;
    }

    /**
     * Clicks a button by its text content in a specific window.
     * Searches all ButtonBase nodes. Must be called from the FX thread.
     */
    private void clickButtonByText(String text, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) {
            // Window might not be found by exact title, try contains as last resort
            for (Window w : Window.getWindows()) {
                if (w instanceof Stage && ((Stage) w).getScene() != null) {
                    Stage s = (Stage) w;
                    if (s.getTitle() != null && matchesWindowTitle(s.getTitle(), windowTitle)) {
                        scene = s.getScene();
                        break;
                    }
                }
            }
        }
        if (scene == null) throw new RuntimeException("Scene not found for: " + windowTitle);

        // Search for button with matching text
        for (Node node : scene.getRoot().lookupAll(".button")) {
            if (node instanceof ButtonBase) {
                ButtonBase btn = (ButtonBase) node;
                String btnText = btn.getText();
                if (btnText != null && btnText.contains(text)) {
                    btn.fire();
                    return;
                }
            }
        }
        throw new RuntimeException("Button with text '" + text + "' not found in " + windowTitle);
    }

    /**
     * Sets the first Spinner found in a window to the given value.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    private void setSpinnerInWindow(String windowTitle, int value) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (Node node : scene.getRoot().lookupAll("Spinner")) {
            if (node instanceof Spinner) {
                Spinner<Integer> spinner = (Spinner<Integer>) node;
                spinner.getValueFactory().setValue(value);
                return;
            }
        }
        throw new RuntimeException("No Spinner found in " + windowTitle);
    }

    /**
     * Waits for the engine to become available (findEngine succeeds).
     */
    private void waitForEngineAvailable() {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            String result = engineAccessor.findEngine();
            if (result.contains("\"found\":true") || result.contains("\"found\": true")) {
                return;
            }
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Engine did not become available within " + WINDOW_TIMEOUT_MS + "ms");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Scene/Window Resolution (FX thread)
    // ═══════════════════════════════════════════════════════════════════

    private Scene findScene(String windowTitle) {
        for (Window w : Window.getWindows()) {
            if (w instanceof Stage) {
                Stage s = (Stage) w;
                if (windowTitle == null || windowTitle.isEmpty()
                        || (s.getTitle() != null && matchesWindowTitle(s.getTitle(), windowTitle))) {
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
     * Finds any force in 'after' that wasn't in 'before'.
     */
    private JsonObject findNewForce(JsonObject before, JsonObject after) {
        Set<String> beforeIds = collectAllForceIds(before);
        for (JsonObject force : allForces(after)) {
            String id = getStringField(force, "id");
            if (id != null && !beforeIds.contains(id)) {
                return force;
            }
        }
        return null;
    }

    /**
     * Builds the ActionOutputs JSON for a created force (forceId + child selections map).
     */
    private JsonObject buildForceOutputs(JsonObject force) {
        JsonObject result = new JsonObject();
        result.addProperty("forceId", getStringField(force, "id"));

        // Collect all selection entryId → selectionId mappings in the force
        JsonObject selections = new JsonObject();
        collectAllSelectionEntryIds(force, selections);
        if (selections.entrySet().size() > 0) {
            result.add("selections", selections);
        }
        return result;
    }

    private void collectAllSelectionEntryIds(JsonObject scope, JsonObject result) {
        JsonArray selections = scope.has("selections") ? scope.getAsJsonArray("selections") : null;
        if (selections == null) return;
        for (JsonElement el : selections) {
            if (!el.isJsonObject()) continue;
            JsonObject sel = el.getAsJsonObject();
            String id = getStringField(sel, "id");
            String entryId = getStringField(sel, "entryId");
            if (id != null && entryId != null && !result.has(entryId)) {
                result.addProperty(entryId, id);
            }
            collectAllSelectionEntryIds(sel, result);
        }
        // Also collect from children field
        JsonArray children = scope.has("children") ? scope.getAsJsonArray("children") : null;
        if (children != null) {
            for (JsonElement el : children) {
                if (!el.isJsonObject()) continue;
                JsonObject child = el.getAsJsonObject();
                String id = getStringField(child, "id");
                String entryId = getStringField(child, "entryId");
                if (id != null && entryId != null && !result.has(entryId)) {
                    result.addProperty(entryId, id);
                }
                collectAllSelectionEntryIds(child, result);
            }
        }
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

    private static int getIntParam(JsonObject params, String key, int defaultValue) {
        return getIntField(params, key, defaultValue);
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
