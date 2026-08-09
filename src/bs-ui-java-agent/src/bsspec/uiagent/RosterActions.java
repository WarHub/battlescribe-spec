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
import java.util.function.Function;
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
    /**
     * BattleScribe's native "Continue? Roster has not been saved. Do you want to save the Roster
     * now?" (YES/NO/CANCEL) prompt. It appears when {@code #btnNewRoster} is fired while a roster
     * from a PREVIOUS spec is still open and unsaved — the expected, benign shape of roster
     * warm-reuse. {@code createRosterAction} declares this dialog expected during its own flow and
     * dismisses it with NO (discard — each spec is independent); it never appears on a cold start
     * (no roster open) so this adds no behavior there.
     */
    private static final String CONTINUE_WINDOW = "Continue?";

    /** Passed as currentCount when the control must be a spinner and no count applies. */
    private static final int NO_COUNT_CONTEXT = -1;

    private static final int POLL_INTERVAL_MS = 200;
    private static final int STATE_POLL_TIMEOUT_MS = 10_000;
    private static final int WINDOW_TIMEOUT_MS = 15_000;
    private static final int FX_TIMEOUT_MS = 30_000;
    /** No dialog is allowed to be open when a high-level action returns — the default (empty) post-condition. */
    private static final String[] NO_DIALOGS_ALLOWED = {};

    private final EngineAccessor engineAccessor;

    public RosterActions(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
    }

    /**
     * Dispatches a high-level action method by name, then enforces the post-condition that no
     * unexpected modal dialog is left open (see {@link DialogInspector#assertNoUnexpectedModals}).
     * Every action here is expected to leave the app back in a stable, dialog-free state; if one
     * is still up (e.g. an "Error" dialog the action's own flow didn't anticipate), that's a bug
     * surfaced as a clear failure here rather than silently returning a result while a dialog sits
     * on screen.
     */
    public String dispatch(String method, String params) {
        String result = dispatchAction(method, params);
        DialogInspector.assertNoUnexpectedModals(NO_DIALOGS_ALLOWED);
        return result;
    }

    private String dispatchAction(String method, String params) {
        switch (method) {
            case "rosterDuplicateSelectionAction":
                return duplicateSelectionAction(params);
            case "rosterDuplicateForceAction":
                return duplicateForceAction(params);
            case "rosterSelectEntryAction":
                return selectEntryAction(params);
            case "rosterCreateRosterAction":
                return createRosterAction(params);
            case "rosterAddForceAction":
                return addForceAction(params);
            case "rosterAddChildForceAction":
                return addChildForceAction(params);
            case "rosterRemoveForceAction":
                return removeForceAction(params);
            case "rosterSelectChildEntryAction":
                return selectChildEntryAction(params);
            case "rosterDeselectSelectionAction":
                return deselectSelectionAction(params);
            case "rosterSetSelectionCountAction":
                return setSelectionCountAction(params);
            case "rosterSetCostLimitAction":
                return setCostLimitAction(params);
            case "rosterSetCustomizationAction":
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

        if (TREE_TRACE) {
            System.err.println("[agent] tree trace: after selecting force " + forceId
                    + runOnFxGet(() -> "\n  roster:    " + describeTree("#treeRoster")
                            + "\n  catalogue: " + describeTree("#treeCatalogue")));
        }

        // Wait for the entry to be THERE — under THIS force — rather than for 300ms. Scoping is
        // not belt-and-braces: #treeCatalogue lists every force's own copy of the same entries, so
        // an unscoped wait is satisfied by a sibling force's item and an unscoped click adds the
        // selection to that force instead. See resolveTreeScope.
        waitForTreeItem("#treeCatalogue", forceId, entryId);

        // Phase 2: Double-click the entry in the catalogue tree, recording what the ROSTER tree
        // believed was selected at that moment rather than assuming it. When two forces come from
        // the same force entry their catalogue trees are identical, so the wait above cannot tell a
        // rebuilt tree from a stale one and the click lands wherever BattleScribe thinks it is —
        // which the timeout below would otherwise report as "the click did nothing".
        //
        // Read inside the click's own dispatch: a separate runOnFxGet would add an FX round trip to
        // every selectEntry in the suite, and each one contends with BattleScribe's own work on that
        // single thread.
        String treeSelection = runOnFxGet(() -> {
            String selected = describeTreeSelection("#treeRoster");
            clickTreeItemById("#treeCatalogue", forceId, entryId, true);
            return selected;
        });

        JsonObject after = waitForStateChange(
                state -> findCreatedSelection(before, state, forceId, null, entryId) != null,
                state -> describeMissingSelection(state, forceId, null, entryId)
                        + "; roster tree had selected: " + treeSelection);

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
        String rosterName = getStringParam(p, "rosterName", null);

        // Fire "New Roster" button (async because it opens a modal dialog)
        runOnFx(() -> fireButtonAsync("#btnNewRoster", MAIN_WINDOW));
        waitForNewRosterWindowDismissingContinuePrompt();

        // Select game system in the combo
        runOnFx(() -> {
            selectComboBoxItemByText("#cboGameSystem", gameSystemName, NEW_ROSTER_WINDOW);
        });
        // No sleep: the game system's effect on this dialog is that #btnAddForce becomes usable,
        // and the Add Force window wait below is the condition that proves it.

        // Set cost limit if specified
        if (costLimit >= 0) {
            runOnFx(() -> {
                setSpinnerInWindow(NEW_ROSTER_WINDOW, costLimit);
            });
        }

        // Open Add Force dialog
        runOnFx(() -> fireButtonAsync("#btnAddForce", NEW_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW, NEW_ROSTER_WINDOW);

        // Select catalogue and force entry in Add Force dialog
        runOnFx(() -> {
            selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW);
        });
        // Choosing a catalogue repopulates the force-entry combo asynchronously. Wait for the
        // entry to be OFFERED rather than sleeping — see waitForComboBoxItem.
        waitForComboBoxItem("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW, NEW_ROSTER_WINDOW);

        // Close New Roster dialog
        runOnFx(() -> fireButtonAsync("#btnDone", NEW_ROSTER_WINDOW));
        waitForWindowClose(NEW_ROSTER_WINDOW);

        // Wait for engine to be available and set roster name via UI
        waitForEngineAvailable();
        if (rosterName != null && !rosterName.isEmpty()) {
            runOnFx(() -> openEditRoster());
            waitForWindow(EDIT_ROSTER_WINDOW);
            runOnFx(() -> {
                setTextField(EDIT_ROSTER_WINDOW, rosterName, "#txtName");
            });
            runOnFx(() -> fireButton("#btnDone", EDIT_ROSTER_WINDOW));
            waitForWindowClose(EDIT_ROSTER_WINDOW);
        }
        JsonObject after = readRosterState();

        // The first force is the only force
        JsonArray forces = after.has("forces") ? after.getAsJsonArray("forces") : new JsonArray();
        if (forces.size() == 0) {
            throw new RuntimeException("rosterCreateRosterAction: no forces found after roster creation");
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
        runOnFx(() -> openEditRoster());
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Open Add Force sub-dialog
        runOnFx(() -> fireButtonAsync("#btnAddForce", EDIT_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW, EDIT_ROSTER_WINDOW);

        // Select catalogue and force entry
        runOnFx(() -> selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW));
        // See waitForComboBoxItem: the force-entry list is rebuilt from the chosen catalogue.
        waitForComboBoxItem("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW, EDIT_ROSTER_WINDOW);

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
        runOnFx(() -> openEditRoster());
        waitForWindow(EDIT_ROSTER_WINDOW);

        runOnFx(() -> selectTreeItemById("#treeForces", parentForceId, EDIT_ROSTER_WINDOW));

        // Add Force
        runOnFx(() -> fireButtonAsync("#btnAddForce", EDIT_ROSTER_WINDOW));
        waitForWindow(ADD_FORCE_WINDOW, EDIT_ROSTER_WINDOW);

        runOnFx(() -> selectComboBoxItemById("#cboCatalogue", catalogueId, ADD_FORCE_WINDOW));
        // See waitForComboBoxItem: the force-entry list is rebuilt from the chosen catalogue.
        waitForComboBoxItem("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
        runOnFx(() -> {
            selectComboBoxItemById("#cboForceEntry", forceEntryId, ADD_FORCE_WINDOW);
            fireButton("#btnDone", ADD_FORCE_WINDOW);
        });
        waitForWindowClose(ADD_FORCE_WINDOW, EDIT_ROSTER_WINDOW);

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
        runOnFx(() -> openEditRoster());
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Click the remove button on the force's tree cell (fires async, triggers confirm dialog)
        runOnFx(() -> clickTreeCellButton("#treeForces", forceId, EDIT_ROSTER_WINDOW));

        // The remove fires async and raises a confirmation. Wait for that window rather than
        // guessing at it: clicking YES into a dialog that has not appeared does nothing, and the
        // force then survives a "removeForce" that reported success.
        waitForWindow(CONFIRM_WINDOW, EDIT_ROSTER_WINDOW);
        runOnFx(() -> clickButtonByText("YES", CONFIRM_WINDOW));

        // Close Edit Roster once the confirmation is gone.
        waitForWindowClose(CONFIRM_WINDOW, EDIT_ROSTER_WINDOW);
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
        runOnFx(() -> openEditRoster());
        waitForWindow(EDIT_ROSTER_WINDOW);

        // Set cost limit spinner by name
        // NO_COUNT_CONTEXT: a roster cost limit is a spinner by nature. Passing a real count here
        // would let the add-button branch fire, which would be a different operation entirely.
        runOnFx(() -> setSpinnerValueByLabel(costName, value, NO_COUNT_CONTEXT, EDIT_ROSTER_WINDOW));

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
     * Uses direct reflection to call showCustomiseSelectableDialog() on the
     * RosterEditorWindowController, bypassing the supporter check entirely.
     * The context menu code path in BS desktop proves this method works without supporter status.
     *
     * @param params JSON: {forceId, selectionId (optional), customName (optional), customNotes (optional)}
     */
    public String setCustomizationAction(String params) {
        JsonObject p = parseParams(params);
        String forceId = requireString(p, "forceId");
        String selectionId = p.has("selectionId") && !p.get("selectionId").isJsonNull()
                ? p.get("selectionId").getAsString() : null;
        String categoryEntryId = p.has("categoryEntryId") && !p.get("categoryEntryId").isJsonNull()
                ? p.get("categoryEntryId").getAsString() : null;
        String customName = p.has("customName") && !p.get("customName").isJsonNull()
                ? p.get("customName").getAsString() : null;
        String customNotes = p.has("customNotes") && !p.get("customNotes").isJsonNull()
                ? p.get("customNotes").getAsString() : null;

        // Select the target in the roster tree
        if (categoryEntryId != null) {
            // Categories don't have :id: tokens in their display text.
            // Find the category tree item by matching the backing object's getEntryId().
            runOnFx(() -> selectCategoryTreeItem("#treeRoster", forceId, categoryEntryId));
        } else {
            String targetId = selectionId != null ? selectionId : forceId;
            runOnFx(() -> selectTreeItemById("#treeRoster", targetId, MAIN_WINDOW));
        }
        // Call showCustomiseSelectableDialog via reflection on the controller.
        // This is the same code path as the context menu "Customise Name..." item,
        // which has NO supporter check (unlike the edit panel button).
        //
        // No sleeps around this: the reflective call runs on the FX thread (so the selection above
        // has been applied by the time it executes), and the dialog it opens is awaited below.
        runOnFx(() -> invokeShowCustomiseDialog(MAIN_WINDOW));

        // Wait for the customization dialog
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
     * Invokes showCustomiseSelectableDialog on the RosterEditorWindowController
     * by getting the currently selected object from treeRoster and passing it.
     * Uses Platform.runLater because showCustomiseSelectableDialog calls showAndWait()
     * which blocks the FX thread until the dialog is closed.
     * Must be called from the FX thread.
     */
    private void invokeShowCustomiseDialog(String windowTitle) {
        Object controller = engineAccessor.getControllerInstance();
        if (controller == null) {
            throw new RuntimeException("Controller instance not available");
        }

        try {
            // Get treeRoster field from controller
            Scene scene = findScene(windowTitle);
            if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
            Node treeNode = scene.getRoot().lookup("#treeRoster");
            if (treeNode == null) throw new RuntimeException("#treeRoster not found");

            // Call getSelectedObject() on the SortedTreeView
            Method getSelectedObject = treeNode.getClass().getMethod("getSelectedObject");
            Object selectedObject = getSelectedObject.invoke(treeNode);
            if (selectedObject == null) {
                throw new RuntimeException("No object selected in treeRoster");
            }

            // Find showCustomiseSelectableDialog method
            Method showDialog = null;
            for (Method m : controller.getClass().getMethods()) {
                if (m.getName().equals("showCustomiseSelectableDialog") && m.getParameterCount() == 1) {
                    showDialog = m;
                    break;
                }
            }
            if (showDialog == null) {
                throw new RuntimeException("showCustomiseSelectableDialog method not found on controller");
            }

            // Use Platform.runLater because showCustomiseSelectableDialog calls showAndWait()
            // which blocks the FX thread (modal dialog nested event loop).
            final Method dialog = showDialog;
            final Object selected = selectedObject;
            Platform.runLater(() -> {
                try {
                    dialog.invoke(controller, selected);
                } catch (Exception e) {
                    throw new RuntimeException("showCustomiseSelectableDialog invocation failed: " + e.getMessage(), e);
                }
            });
        } catch (RuntimeException e) {
            throw e;
        } catch (Exception e) {
            throw new RuntimeException("Failed to invoke showCustomiseSelectableDialog: " + e.getMessage(), e);
        }
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
            // The target dialog didn't exist on this iteration's enumeration (we'd have returned
            // above), but it can open in the gap before this check — so allow the titles we're
            // waiting for (same lost-update race as waitForWindow). Nothing ELSE should be showing.
            DialogInspector.assertNoUnexpectedModals(titlePatterns);
            sleep(POLL_INTERVAL_MS);
        }
        return null;
    }

    /**
     * Sets a TextField identified by CSS selectors. Tries each selector in order.
     * After setting text, fires a synthetic KeyEvent to trigger any onKeyReleased handlers.
     * Must be called from the FX thread.
     */
    private void setTextField(String windowTitle, String text, String... selectors) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (String selector : selectors) {
            Node node = scene.getRoot().lookup(selector);
            if (node instanceof TextField) {
                ((TextField) node).setText(text);
                fireKeyReleased(node);
                return;
            }
        }
        // Fallback: find any TextField
        for (Node n : scene.getRoot().lookupAll("TextField")) {
            if (n instanceof TextField) {
                ((TextField) n).setText(text);
                fireKeyReleased(n);
                return;
            }
        }
        throw new RuntimeException("TextField not found in " + windowTitle);
    }

    /**
     * Sets a TextArea identified by CSS selectors. Tries each selector in order.
     * After setting text, fires a synthetic KeyEvent to trigger any onKeyReleased handlers.
     * Must be called from the FX thread.
     */
    private void setTextArea(String windowTitle, String text, String... selectors) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);

        for (String selector : selectors) {
            Node node = scene.getRoot().lookup(selector);
            if (node instanceof TextArea) {
                ((TextArea) node).setText(text);
                fireKeyReleased(node);
                return;
            }
        }
        // Fallback: find any TextArea
        for (Node n : scene.getRoot().lookupAll("TextArea")) {
            if (n instanceof TextArea) {
                ((TextArea) n).setText(text);
                fireKeyReleased(n);
                return;
            }
        }
        throw new RuntimeException("TextArea not found in " + windowTitle);
    }

    /**
     * Fires a synthetic KEY_RELEASED event on a node to trigger onKeyReleased handlers.
     * BattleScribe's CustomiseSelectionWindowController uses onKeyReleased to persist
     * text field values back to the model object.
     */
    private void fireKeyReleased(Node node) {
        KeyEvent released = new KeyEvent(
                KeyEvent.KEY_RELEASED, "", "", KeyCode.SPACE,
                false, false, false, false);
        node.fireEvent(released);
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
        // No sleep after a tree selection. selectTreeItemById runs inside runOnFx, which blocks
        // until the FX task completes, and JavaFX dispatches selection listeners synchronously —
        // so the edit panel below has already been rebuilt when this returns.
        //
        // If BattleScribe ever defers that rebuild internally, the next step fails LOUDLY with
        // "control not found" rather than acting on a stale panel, which is the failure mode to
        // want here.

        // Resolve entry name: prefer passed name, fall back to roster state lookup
        if (entryName == null || entryName.isEmpty()) {
            entryName = resolveEntryName(before, entryId);
        }

        // Click the control by label (spinner increment, button fire, or checkbox toggle)
        final String labelText = entryName;
        runOnFx(() -> clickControlByLabel(labelText, MAIN_WINDOW, null));

        // Wait for the click to land, in any of the three shapes it can take.
        //
        // The child COUNT is the wrong thing to watch, and was the only thing watched. A COLLECTIVE
        // entry does not gain a second child when selected again — BattleScribe increments the one
        // already there. And a member of a single-choice GROUP does not gain one either: it
        // REPLACES whichever member was chosen before, so the count is identical on both sides
        // while the child that is there is a different entry entirely.
        //
        // What actually happened, in every case, is that the parent now holds a child for the
        // requested entry that it did not hold before — or holds more of one it did.
        JsonObject after = waitForStateChange(state -> {
            JsonObject parent = findSelectionById(state, parentSelectionId);
            if (parent == null) return false;
            JsonObject beforeParent = findSelectionById(before, parentSelectionId);
            if (beforeParent == null) return true;
            return findNewChildSelection(before, state, parentSelectionId, entryId) != null
                    || childNumberIncreased(beforeParent, parent, entryId);
        }, state -> {
            JsonObject parent = findSelectionById(state, parentSelectionId);
            if (parent == null) {
                return "parent selection " + parentSelectionId + " is no longer in the roster";
            }
            JsonObject beforeParent = findSelectionById(before, parentSelectionId);
            return "clicking control '" + labelText + "' left parent " + parentSelectionId
                    + " with the same child count ("
                    + (beforeParent == null ? "?" : childSelectionCount(beforeParent))
                    + " -> " + childSelectionCount(parent) + "), children now: "
                    + describeSelections(childSelectionsOf(parent))
                    + "; wanted entryId '" + entryId + "'";
        });

        // Find the new child selection (in after but not in before). A collective entry produced no
        // new child — it incremented the existing one — so fall back to that, or the step's
        // `selectionId` output would be absent and every later step referencing it would fail
        // somewhere else entirely.
        JsonObject createdSelection = findNewChildSelection(before, after, parentSelectionId, entryId);
        if (createdSelection == null) {
            JsonObject parent = findSelectionById(after, parentSelectionId);
            if (parent != null) {
                for (JsonObject child : childSelectionsOf(parent)) {
                    if (isSelectionOfEntry(child, entryId)) {
                        createdSelection = child;
                        break;
                    }
                }
            }
        }

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
        // No sleep after a tree selection. selectTreeItemById runs inside runOnFx, which blocks
        // until the FX task completes, and JavaFX dispatches selection listeners synchronously —
        // so the edit panel below has already been rebuilt when this returns.
        //
        // If BattleScribe ever defers that rebuild internally, the next step fails LOUDLY with
        // "control not found" rather than acting on a stale panel, which is the failure mode to
        // want here.

        // Try decrement via control by label
        final String finalEntryName = entryName;
        AtomicReference<Boolean> clicked = new AtomicReference<>(false);
        runOnFx(() -> {
            clicked.set(tryClickControlByLabel(finalEntryName, MAIN_WINDOW, "decrement"));
        });

        if (!clicked.get()) {
            // Fallback: select the selection itself and press DELETE
            // Both calls block on the FX thread and the selection is applied synchronously, so
            // the key press below already lands on the intended row.
            runOnFx(() -> {
                selectTreeItemById("#treeRoster", selectionId, MAIN_WINDOW);
            });
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
        // No sleep after a tree selection. selectTreeItemById runs inside runOnFx, which blocks
        // until the FX task completes, and JavaFX dispatches selection listeners synchronously —
        // so the edit panel below has already been rebuilt when this returns.
        //
        // If BattleScribe ever defers that rebuild internally, the next step fails LOUDLY with
        // "control not found" rather than acting on a stale panel, which is the failure mode to
        // want here.

        // Set spinner value by label
        final String finalEntryName = entryName;
        final int currentCount = getIntField(selection, "number", 1);
        runOnFx(() -> setSpinnerValueByLabel(finalEntryName, count, currentCount, MAIN_WINDOW));

        // Wait for the count to match — as the spinner's own value, OR as the per-model total.
        //
        // The spinner on a COLLECTIVE selection is per model: set it to 2 under a parent of 3 and
        // BattleScribe stores number = 6, because a collective selection's number is the total
        // across the parent's models. Waiting only for `number == count` therefore waits out the
        // full timeout on exactly those specs and reports it as the spinner not having taken.
        final int parentNumber = parentNumberOf(state, selectionId);
        final String countedEntryId = entryId;
        final String countScopeId = parentId;
        waitForStateChange(s -> {
            JsonObject sel = findSelectionById(s, selectionId);
            if (sel != null) {
                int number = getIntField(sel, "number", -1);
                if (number == count || number == count * parentNumber) return true;
            }
            // An INSTANCED entry counts by siblings, not by number: the panel's "+" adds another
            // selection rather than raising this one's. Either shape means the count was reached.
            return countSiblingsOfEntry(s, countScopeId, countedEntryId) == count;
        }, s -> {
            JsonObject sel = findSelectionById(s, selectionId);
            return "setting '" + finalEntryName + "' to " + count + " left selection " + selectionId
                    + (sel == null ? " gone from the roster" : " at number " + getIntField(sel, "number", -1))
                    + " and " + countSiblingsOfEntry(s, countScopeId, countedEntryId)
                    + " sibling(s) of that entry (wanted number " + count + ", or "
                    + (count * parentNumber) + " for a collective under a parent of " + parentNumber
                    + ", or " + count + " siblings for an instanced entry)";
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
            // Say what the panel DOES offer. "Control not found for label: Sword" cannot
            // distinguish an entry the panel never rendered from one rendered under a different
            // name from one whose label carries no control — three different bugs.
            throw new RuntimeException("Control not found for label: " + labelText
                    + "; panel offers: " + describeControlLabels(windowTitle));
        }
    }

    /** The edit panel's labels and whether each has a control beside it. FX thread only. */
    private String describeControlLabels(String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) return "(scene not found)";
        List<String> described = new ArrayList<>();
        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            String text = ((Label) labelNode).getText();
            if (text == null || text.isEmpty()) continue;
            javafx.scene.Parent parent = labelNode.getParent();
            String control = "(no control)";
            if (parent != null) {
                for (Node sibling : parent.getChildrenUnmodifiable()) {
                    if (sibling == labelNode) continue;
                    if (sibling instanceof Spinner || sibling instanceof ButtonBase) {
                        control = sibling.getClass().getSimpleName();
                        break;
                    }
                }
            }
            described.add("'" + text + "' -> " + control);
        }

        // Controls that carry their own text rather than sitting beside a Label. Listing them is
        // the point: leaving them out is what made a panel full of radio buttons look empty.
        for (String styleClass : new String[] { ".check-box", ".radio-button" }) {
            for (Node node : scene.getRoot().lookupAll(styleClass)) {
                if (!(node instanceof Labeled)) continue;
                String text = ((Labeled) node).getText();
                if (text == null || text.isEmpty()) continue;
                described.add("'" + text + "' -> " + node.getClass().getSimpleName() + " (self-labelled)");
            }
        }

        return described.isEmpty() ? "(no labels)" : described.toString();
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

        // Look for RadioButton by text.
        //
        // A selectionEntryGroup that permits one choice is not rendered as a row per entry: the
        // GROUP gets one heading and its members become radio buttons, which carry their own text
        // instead of sitting beside a Label. Both loops above therefore looked straight past them,
        // and the panel appeared to offer the heading and nothing else.
        //
        // Selecting an already-selected radio is a no-op in JavaFX rather than a re-fire, so this
        // reports "already chosen" as success: the caller's postcondition is that this entry IS
        // the group's choice, and it is.
        for (Node rbNode : scene.getRoot().lookupAll(".radio-button")) {
            if (!(rbNode instanceof RadioButton)) continue;
            RadioButton rb = (RadioButton) rbNode;
            String rbText = rb.getText();
            if (rbText == null || !rbText.contains(text)) continue;
            // fire() and nothing else. RadioButton.fire() selects it and notifies the ToggleGroup,
            // which is what BattleScribe listens to; setSelected() beforehand only flips the state
            // fire() is about to toggle, so the pair can leave it deselected.
            if (!rb.isSelected()) {
                rb.fire();
            }
            return true;
        }
        return false;
    }

    /**
     * Sets a Spinner's value by its sibling label. Increments/decrements to reach target.
     * Must be called from the FX thread.
     */
    @SuppressWarnings("unchecked")
    /**
     * Sets a labelled count control in the edit panel.
     *
     * <p>{ currentCount} is the selection's own number, read from roster state. A spinner
     * reports its own value and does not need it; an add BUTTON has no value to read, so the
     * caller's knowledge of where the count is now is the only way to know how many times to fire.
     */
    private void setSpinnerValueByLabel(String text, int value, int currentCount, String windowTitle) {
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

                // An INSTANCED entry gets a "+" button where a collective one gets a spinner:
                // BattleScribe offers "add another of these" rather than "how many of these".
                // Screenshot of the Squad's panel with a Sergeant already in it:
                //
                //     Options
                //     [ + ]  Sergeant • 12pts
                //
                // Looking only for a Spinner reported that as "Spinner not found" — an entry the
                // panel was offering, under its own name, one control along.
                if (sibling instanceof ButtonBase && currentCount != NO_COUNT_CONTEXT) {
                    int delta = value - currentCount;
                    if (delta <= 0) {
                        // Removal is not this control's job; the instance rows carry their own
                        // close buttons. Say so rather than firing "+" a negative number of times.
                        throw new RuntimeException("Cannot reduce '" + text + "' from " + currentCount
                                + " to " + value + " through the panel: it offers an add button, not a"
                                + " spinner. Removal goes through the instance row's close control.");
                    }
                    for (int i = 0; i < delta; i++) {
                        ((ButtonBase) sibling).fire();
                    }
                    return;
                }
            }
        }
        throw new RuntimeException("Spinner not found for label: " + text
                + "; panel offers: " + describeControlLabels(windowTitle));
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
    /**
     * The {@code number} of {@code selectionId}'s parent SELECTION, or 1 when its parent is a force
     * (a force has no multiplicity, so a collective directly under one is not scaled).
     */
    private int parentNumberOf(JsonObject state, String selectionId) {
        String parentId = findSelectionParentId(state, selectionId);
        if (parentId == null) return 1;
        JsonObject parent = findSelectionById(state, parentId);
        return parent == null ? 1 : getIntField(parent, "number", 1);
    }

    /**
     * True when a child of {@code parent} for {@code entryId} carries a higher {@code number} than
     * it did in {@code beforeParent} — how a collective entry records a second selection.
     */
    private boolean childNumberIncreased(JsonObject beforeParent, JsonObject parent, String entryId) {
        Map<String, Integer> before = new HashMap<>();
        for (JsonObject child : childSelectionsOf(beforeParent)) {
            String id = getStringField(child, "id");
            if (id != null && isSelectionOfEntry(child, entryId)) {
                before.put(id, getIntField(child, "number", 1));
            }
        }
        for (JsonObject child : childSelectionsOf(parent)) {
            String id = getStringField(child, "id");
            if (id == null || !isSelectionOfEntry(child, entryId)) continue;
            Integer was = before.get(id);
            if (was != null && getIntField(child, "number", 1) > was) return true;
        }
        return false;
    }

    /** How many selections of {@code entryId} sit directly under {@code scopeId}. */
    private int countSiblingsOfEntry(JsonObject state, String scopeId, String entryId) {
        int count = 0;
        for (JsonObject sel : getSelectionsInScope(state, scopeId, null)) {
            if (isSelectionOfEntry(sel, entryId)) count++;
        }
        for (JsonObject sel : allSelections(state)) {
            if (!scopeId.equals(getStringField(sel, "id"))) continue;
            for (JsonObject child : childSelectionsOf(sel)) {
                if (isSelectionOfEntry(child, entryId)) count++;
            }
        }
        return count;
    }

    /** The child selections {@link #childSelectionCount} counts, for diagnostics. */
    private List<JsonObject> childSelectionsOf(JsonObject scope) {
        List<JsonObject> result = new ArrayList<>();
        for (String key : new String[] { "selections", "children" }) {
            if (!scope.has(key)) continue;
            for (JsonElement el : scope.getAsJsonArray(key)) {
                if (el.isJsonObject()) result.add(el.getAsJsonObject());
            }
        }
        return result;
    }

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
                if (id != null && !beforeIds.contains(id) && isSelectionOfEntry(sel, entryId)) {
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
                if (id != null && !beforeIds.contains(id) && isSelectionOfEntry(child, entryId)) {
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
        return waitForStateChange(predicate, null);
    }

    /**
     * Polls roster state until {@code predicate} holds, or throws at the deadline.
     *
     * <p>{@code describeOnTimeout} renders the LAST state read into the timeout message. It is
     * worth passing wherever the predicate asks a question the state can answer, because
     * "Timed out waiting for state change" on its own says only that this loop ran out — not
     * whether the action did nothing, did something the predicate did not recognise, or did it
     * somewhere else. Those are different bugs and the bare message cannot tell them apart.
     */
    private JsonObject waitForStateChange(
            Predicate<JsonObject> predicate, Function<JsonObject, String> describeOnTimeout) {
        long deadline = System.currentTimeMillis() + STATE_POLL_TIMEOUT_MS;
        RuntimeException lastError = null;
        JsonObject lastState = null;

        while (System.currentTimeMillis() < deadline) {
            try {
                JsonObject state = readRosterState();
                lastState = state;
                if (predicate.test(state)) {
                    return state;
                }
            } catch (RuntimeException e) {
                lastError = e;
            }
            // No dialog is expected while merely polling roster state — fail fast on any (e.g. an
            // "Error" dialog the triggering action's flow didn't anticipate) instead of spinning
            // out this loop's own timeout.
            DialogInspector.assertNoUnexpectedModals(NO_DIALOGS_ALLOWED);
            sleep(POLL_INTERVAL_MS);
        }

        String detail = "";
        if (describeOnTimeout != null && lastState != null) {
            try {
                detail = " — " + describeOnTimeout.apply(lastState);
            } catch (RuntimeException e) {
                detail = " — (could not describe final state: " + e + ")";
            }
        }

        throw new RuntimeException("Timed out waiting for state change" +
                (lastError != null ? ": " + lastError.getMessage() : "") + detail);
    }

    /**
     * Describes why {@link #findCreatedSelection} found nothing, from the final state.
     *
     * <p>Names the three outcomes that produce an identical timeout: the force holds nothing new
     * (the click did not land), it holds something new under a different entryId (the predicate's
     * equality is what failed), or the selection exists but under a different owner (it landed
     * somewhere else — a wrong-force or wrong-depth bug wearing a timeout's clothes).
     */
    private String describeMissingSelection(
            JsonObject state, String forceId, String parentSelectionId, String entryId) {
        JsonObject force = findForceById(state, forceId);
        if (force == null) {
            return "force " + forceId + " is not in the roster at all; forces present: "
                    + describeIds(allForces(state));
        }

        StringBuilder sb = new StringBuilder();
        sb.append("no selection for entryId '").append(entryId).append("' in ");
        sb.append(parentSelectionId == null
                ? "force " + forceId
                : "selection " + parentSelectionId + " of force " + forceId);
        sb.append("; that scope holds: ").append(describeSelections(
                getSelectionsInScope(state, forceId, parentSelectionId)));

        List<String> elsewhere = new ArrayList<>();
        for (JsonObject sel : allSelections(state)) {
            if (Objects.equals(getStringField(sel, "entryId"), entryId)) {
                elsewhere.add(getStringField(sel, "id") + " (" + getStringField(sel, "name") + ")");
            }
        }
        if (!elsewhere.isEmpty()) {
            sb.append("; but the roster DOES hold that entryId elsewhere: ").append(elsewhere);
        }
        return sb.toString();
    }

    private String describeSelections(List<JsonObject> selections) {
        if (selections.isEmpty()) {
            return "(nothing)";
        }
        List<String> parts = new ArrayList<>();
        for (JsonObject sel : selections) {
            parts.add(getStringField(sel, "entryId") + "=" + getStringField(sel, "id")
                    + " (" + getStringField(sel, "name") + " x" + getIntField(sel, "number", 1) + ")");
        }
        return parts.toString();
    }

    private String describeIds(List<JsonObject> objects) {
        List<String> ids = new ArrayList<>();
        for (JsonObject o : objects) {
            ids.add(getStringField(o, "id") + " (" + getStringField(o, "name") + ")");
        }
        return ids.toString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Window Waits
    // ═══════════════════════════════════════════════════════════════════

    /**
     * Waits for a window titled {@code titleFragment} to appear. {@code titleFragment} itself is
     * always allowed by the unexpected-modal check: it is BY DEFINITION expected here — that's
     * what we're waiting for. Without that, there is a lost-update race across the two FX-thread
     * round-trips below: {@code hasWindow} can observe "not open yet", the awaited window opens in
     * the gap, and {@code assertNoUnexpectedModals} then flags the very window this call wants as
     * "unexpected" (observed on a loaded machine as spurious
     * {@code Unexpected modal dialog [Edit Roster]} failures in {@code createRosterAction}).
     * {@link #waitForWindowClose} already self-allows its own title for the mirror-image reason.
     *
     * @param alsoAllowed titles of any OTHER dialog(s) already legitimately open at this point in
     *                    the calling action's flow (e.g. a parent dialog); anything else showing
     *                    fails the wait immediately instead of running out its timeout.
     */
    /**
     * Waits until {@code selector}'s ComboBox actually offers an item whose id is
     * {@code targetId}.
     *
     * <p>This exists because selecting a catalogue REPOPULATES the force-entry combo
     * asynchronously, and the driver used to bridge that with {@code sleep(300)}. A fixed sleep
     * there is not merely slow — it is a correctness hazard. If the repopulation has not happened,
     * {@link #selectComboBoxItemById} runs against the PREVIOUS catalogue's list, and its
     * {@code toString().contains(targetId)} fallback can match a stale entry: the spec corpus
     * reuses generic ids such as {@code fe-1} across catalogues, so the wrong force gets selected
     * and the roster is built wrong while the action reports success.
     *
     * <p>Waiting for the item to be PRESENT removes the guess. When the item genuinely never
     * arrives this throws with the ids actually on offer, which is a far better failure than a
     * roster that is quietly wrong.
     */
    private void waitForComboBoxItem(String selector, String targetId, String windowTitle) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            Boolean present = runOnFxGet(() -> comboBoxHasItem(selector, targetId, windowTitle));
            if (present) return;
            sleep(POLL_INTERVAL_MS);
        }
        String offered = runOnFxGet(() -> describeComboBoxItems(selector, windowTitle));
        throw new RuntimeException(
                "ComboBox '" + selector + "' in " + windowTitle + " never offered item id '"
                        + targetId + "' within " + WINDOW_TIMEOUT_MS + "ms. Offered: " + offered);
    }

    /**
     * Waits until {@code treeSelector} contains a tree item for {@code id}.
     *
     * <p>Selecting a force rebuilds the catalogue tree beside it. Acting on that tree before the
     * rebuild lands either misses the item — a bare "Tree item not found" from a step that had
     * nothing to do with the tree — or, when the previous force offered a like-named entry, hits
     * the WRONG one. The 300ms that used to sit here covered neither case reliably.
     */
    private void waitForTreeItem(String treeSelector, String id) {
        waitForTreeItem(treeSelector, null, id);
    }

    /**
     * Waits until {@code treeSelector} offers an item for {@code id} INSIDE {@code containerId}'s
     * subtree — see {@link #resolveTreeScope} for why the container matters.
     */
    private void waitForTreeItem(String treeSelector, String containerId, String id) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            Boolean present = runOnFxGet(() -> hasTreeItem(treeSelector, containerId, id));
            if (present) return;
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException(
                "Tree '" + treeSelector + "' never offered an item for id '" + id + "'"
                        + (containerId == null ? "" : " under '" + containerId + "'")
                        + " within " + WINDOW_TIMEOUT_MS + "ms");
    }

    /** True when {@code treeSelector} holds an item carrying this id token. FX thread only. */
    @SuppressWarnings("unchecked")
    private boolean hasTreeItem(String treeSelector, String containerId, String id) {
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) return false;
        Node node = scene.getRoot().lookup(treeSelector);
        if (!(node instanceof TreeView)) return false;
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> scope = resolveTreeScope(tree, containerId);
        return scope != null && findTreeItemByText(scope, ":" + id + ":") != null;
    }

    /** True when {@code selector}'s ComboBox holds an item with this id. FX thread only. */
    @SuppressWarnings("unchecked")
    private boolean comboBoxHasItem(String selector, String targetId, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) return false;
        Node node = scene.getRoot().lookup(selector);
        if (!(node instanceof ComboBox)) return false;
        ComboBox<Object> combo = (ComboBox<Object>) node;
        for (Object item : combo.getItems()) {
            if (item != null && targetId.equals(getObjectId(item))) return true;
        }
        return false;
    }

    /** The ids a ComboBox is currently offering — for failure messages. FX thread only. */
    @SuppressWarnings("unchecked")
    private String describeComboBoxItems(String selector, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) return "(window not found)";
        Node node = scene.getRoot().lookup(selector);
        if (!(node instanceof ComboBox)) return "(not a ComboBox)";
        ComboBox<Object> combo = (ComboBox<Object>) node;
        StringBuilder sb = new StringBuilder("[");
        for (Object item : combo.getItems()) {
            if (sb.length() > 1) sb.append(", ");
            sb.append(item == null ? "null" : String.valueOf(getObjectId(item)));
        }
        return sb.append("]").toString();
    }

    private void waitForWindow(String titleFragment, String... alsoAllowed) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        String[] allowed = withTitle(titleFragment, alsoAllowed);
        while (System.currentTimeMillis() < deadline) {
            Boolean found = runOnFxGet(() -> hasWindow(titleFragment));
            if (found) return;
            DialogInspector.assertNoUnexpectedModals(allowed);
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Window '" + titleFragment + "' did not appear within " + WINDOW_TIMEOUT_MS + "ms");
    }

    /**
     * Waits for the "New Roster" window to appear after firing {@code #btnNewRoster}. Under
     * roster warm-reuse, the previous spec's roster can still be open and unsaved: BattleScribe
     * pops its native {@value #CONTINUE_WINDOW} confirmation (YES/NO/CANCEL) before it will let a
     * new roster replace it. That's benign and expected here — dismiss it with NO (discard; each
     * spec is independent — never save a previous spec's leftovers) via {@code #btnNegative} and
     * keep waiting for "New Roster" to appear. On a cold start (no roster open), the prompt never
     * appears and this behaves exactly like a plain {@link #waitForWindow}.
     */
    private void waitForNewRosterWindowDismissingContinuePrompt() {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        boolean dismissing = false;
        while (System.currentTimeMillis() < deadline) {
            if (runOnFxGet(() -> hasWindow(NEW_ROSTER_WINDOW))) return;
            if (runOnFxGet(() -> hasWindow(CONTINUE_WINDOW))) {
                if (!dismissing) {
                    dismissing = true;
                    runOnFx(() -> fireButtonAsync("#btnNegative", CONTINUE_WINDOW));
                }
                sleep(POLL_INTERVAL_MS);
                continue;
            }
            // Both NEW_ROSTER_WINDOW (what we're waiting for — it can open in the gap between the
            // hasWindow check above and this assert) and CONTINUE_WINDOW (declared expected by this
            // action; may still be closing after we fired NO) are legitimate here.
            DialogInspector.assertNoUnexpectedModals(NEW_ROSTER_WINDOW, CONTINUE_WINDOW);
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Window '" + NEW_ROSTER_WINDOW + "' did not appear within " + WINDOW_TIMEOUT_MS + "ms");
    }

    /**
     * Waits for a window titled {@code titleFragment} to close. {@code titleFragment} itself is
     * always allowed (it's expected to still be open on every iteration until it isn't).
     *
     * @param alsoAllowed titles of any OTHER dialog(s) already legitimately open (e.g. an ancestor
     *                    dialog one level up); anything else showing fails the wait immediately.
     */
    private void waitForWindowClose(String titleFragment, String... alsoAllowed) {
        long deadline = System.currentTimeMillis() + WINDOW_TIMEOUT_MS;
        String[] allowed = withTitle(titleFragment, alsoAllowed);
        while (System.currentTimeMillis() < deadline) {
            Boolean found = runOnFxGet(() -> hasWindow(titleFragment));
            if (!found) return;
            DialogInspector.assertNoUnexpectedModals(allowed);
            sleep(POLL_INTERVAL_MS);
        }
        throw new RuntimeException("Window '" + titleFragment + "' did not close within " + WINDOW_TIMEOUT_MS + "ms");
    }

    private static String[] withTitle(String title, String[] extra) {
        String[] result = new String[extra.length + 1];
        result[0] = title;
        System.arraycopy(extra, 0, result, 1, extra.length);
        return result;
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

    /** Set {@code BS_UI_TREE_TRACE=1} to dump both roster trees around a selectEntry. */
    private static final boolean TREE_TRACE = "1".equals(System.getenv("BS_UI_TREE_TRACE"));

    /** A TreeView's visible structure, for diagnostics. FX thread only. */
    @SuppressWarnings("unchecked")
    private String describeTree(String treeSelector) {
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) return "(main window scene not found)";
        Node node = scene.getRoot().lookup(treeSelector);
        if (!(node instanceof TreeView)) return "(not a TreeView: " + treeSelector + ")";
        TreeView<Object> tree = (TreeView<Object>) node;
        StringBuilder sb = new StringBuilder();
        appendTreeItem(tree.getRoot(), 0, sb);
        return sb.toString();
    }

    private void appendTreeItem(TreeItem<Object> item, int depth, StringBuilder sb) {
        if (item == null) return;
        sb.append("\n    ");
        for (int i = 0; i < depth; i++) sb.append("  ");
        sb.append(item.getValue());
        for (TreeItem<Object> child : item.getChildren()) {
            appendTreeItem(child, depth + 1, sb);
        }
    }

    /** The text of a TreeView's selected item, for diagnostics. FX thread only. */
    @SuppressWarnings("unchecked")
    private String describeTreeSelection(String treeSelector) {
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) return "(main window scene not found)";
        Node node = scene.getRoot().lookup(treeSelector);
        if (!(node instanceof TreeView)) return "(not a TreeView: " + treeSelector + ")";
        TreeItem<Object> item = ((TreeView<Object>) node).getSelectionModel().getSelectedItem();
        if (item == null) return "(nothing)";
        Object value = item.getValue();
        return value == null ? "(item with null value)" : String.valueOf(value);
    }

    /**
     * Clicks (or double-clicks) a tree item located by ID token.
     */
    private void clickTreeItemById(String treeSelector, String id, boolean doubleClick) {
        clickTreeItemById(treeSelector, null, id, doubleClick);
    }

    /**
     * Clicks (or double-clicks) the item for {@code id} INSIDE {@code containerId}'s subtree —
     * see {@link #resolveTreeScope} for why the container matters.
     */
    private void clickTreeItemById(String treeSelector, String containerId, String id, boolean doubleClick) {
        String token = ":" + id + ":";
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) throw new RuntimeException("Main window scene not found");
        Node node = scene.getRoot().lookup(treeSelector);
        if (node == null) throw new RuntimeException("TreeView not found: " + treeSelector);
        if (!(node instanceof TreeView)) throw new RuntimeException("Not a TreeView: " + treeSelector);

        @SuppressWarnings("unchecked")
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> scope = resolveTreeScope(tree, containerId);
        if (scope == null) {
            throw new RuntimeException(
                    "Tree '" + treeSelector + "' has no subtree for container id: " + containerId);
        }

        TreeItem<Object> item = findTreeItemByText(scope, token);
        if (item == null) {
            throw new RuntimeException("Tree item not found for id: " + id
                    + (containerId == null ? "" : " under " + containerId));
        }

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
     * Opens the Edit Roster dialog by clicking the #btnEditRoster label.
     * In BattleScribe, #btnEditRoster is a Label with onMouseClicked (NOT a Button),
     * so we fire mouse click events on it. Uses Platform.runLater because
     * the click handler opens a modal dialog (showAndWait).
     * Must be called from the FX thread.
     */
    private void openEditRoster() {
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) throw new RuntimeException("Main window scene not found");
        Node node = scene.getRoot().lookup("#btnEditRoster");
        if (node == null) throw new RuntimeException("#btnEditRoster not found in main window");

        // Schedule the click asynchronously — the handler opens a modal showAndWait dialog
        Platform.runLater(() -> {
            double x = node.getBoundsInLocal().getWidth() / 2;
            double y = node.getBoundsInLocal().getHeight() / 2;
            node.fireEvent(new MouseEvent(MouseEvent.MOUSE_PRESSED,
                    x, y, x, y, MouseButton.PRIMARY, 1,
                    false, false, false, false, true, false, false, false, false, false, null));
            node.fireEvent(new MouseEvent(MouseEvent.MOUSE_RELEASED,
                    x, y, x, y, MouseButton.PRIMARY, 1,
                    false, false, false, false, false, false, false, false, false, false, null));
            node.fireEvent(new MouseEvent(MouseEvent.MOUSE_CLICKED,
                    x, y, x, y, MouseButton.PRIMARY, 1,
                    false, false, false, false, false, false, false, false, false, false, null));
        });
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
        ButtonBase btn = resolveButton(node, selector);
        Platform.runLater(btn::fire);
    }

    /**
     * Fires a ButtonBase synchronously. Must be called from the FX thread.
     */
    private void fireButton(String selector, String windowTitle) {
        Scene scene = findScene(windowTitle);
        if (scene == null) throw new RuntimeException("Scene not found: " + windowTitle);
        Node node = scene.getRoot().lookup(selector);
        if (node == null) throw new RuntimeException("Button not found: " + selector + " in " + windowTitle);
        ButtonBase btn = resolveButton(node, selector);
        btn.fire();
    }

    /**
     * Resolves a node to a ButtonBase. If the node itself is a ButtonBase, returns it.
     * If it's a Label/Node inside a ButtonBase (e.g., the button's text label), walks up
     * the parent chain to find the enclosing ButtonBase.
     * If neither works, searches scene for a ButtonBase with the same ID.
     */
    private ButtonBase resolveButton(Node node, String selector) {
        if (node instanceof ButtonBase) {
            return (ButtonBase) node;
        }
        // Walk parent chain — the node might be a Label inside the Button
        Node parent = node.getParent();
        while (parent != null) {
            if (parent instanceof ButtonBase) {
                return (ButtonBase) parent;
            }
            parent = parent.getParent();
        }
        // Fallback: search all nodes matching this selector for a ButtonBase
        Scene scene = node.getScene();
        if (scene != null) {
            for (Node n : scene.getRoot().lookupAll(selector)) {
                if (n instanceof ButtonBase) {
                    return (ButtonBase) n;
                }
            }
        }
        throw new RuntimeException("Node " + selector + " is not a ButtonBase: " + node.getClass().getSimpleName()
                + " (and no ButtonBase found in parent chain or scene)");
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
        throw new RuntimeException("No ComboBox item matching '" + text + "' found in " + combo.getItems().size() + " items");
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
     * Selects a category tree item by navigating: find force by :forceId: token,
     * then find child whose backing object's getEntryId() matches categoryEntryId.
     * Categories don't display :id: tokens in their text.
     */
    @SuppressWarnings("unchecked")
    private void selectCategoryTreeItem(String treeSelector, String forceId, String categoryEntryId) {
        Scene scene = findScene(MAIN_WINDOW);
        if (scene == null) throw new RuntimeException("Main window scene not found");
        Node node = scene.getRoot().lookup(treeSelector);
        if (!(node instanceof TreeView)) throw new RuntimeException("TreeView not found: " + treeSelector);

        TreeView<Object> tree = (TreeView<Object>) node;
        // Find force tree item
        String forceToken = ":" + forceId + ":";
        TreeItem<Object> forceItem = findTreeItemByText(tree.getRoot(), forceToken);
        if (forceItem == null) throw new RuntimeException("Force tree item not found for id: " + forceId);

        // Find category among force's children by checking backing object's getEntryId()
        TreeItem<Object> categoryItem = null;
        for (TreeItem<Object> child : forceItem.getChildren()) {
            Object val = child.getValue();
            if (val == null) continue;
            try {
                Method m = val.getClass().getMethod("getEntryId");
                Object entryId = m.invoke(val);
                if (categoryEntryId.equals(entryId)) {
                    categoryItem = child;
                    break;
                }
            } catch (Exception e) {
                // Not a category-like object, skip
            }
        }
        if (categoryItem == null) {
            throw new RuntimeException("Category tree item not found for entryId: " + categoryEntryId + " under force: " + forceId);
        }
        tree.getSelectionModel().select(categoryItem);
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
            try {
                JsonObject json = new JsonParser().parse(result).getAsJsonObject();
                if (json.has("found") && json.get("found").getAsBoolean()) {
                    return;
                }
            } catch (Exception e) {
                // Parse failure — engine not ready yet
            }
            // This is exactly the incident's failure mode: BattleScribe can pop a modal "Error"
            // dialog while constructing the new roster's engine state, which otherwise leaves
            // this loop spinning until its own timeout with no clue why. No dialog is legitimately
            // expected here — fail fast on anything showing.
            DialogInspector.assertNoUnexpectedModals(NO_DIALOGS_ALLOWED);
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

    /**
     * The subtree an id search should be confined to: {@code containerId}'s item, or the whole
     * tree when no container is named.
     *
     * <p><b>#treeCatalogue is not per-force.</b> It holds the entire roster — one subtree per
     * force, each offering that force's own copy of the same catalogue entries. So every force in
     * a multi-force roster carries an item reading {@code Target:se-target:…}, and an unscoped
     * {@link #findTreeItemByText} returns whichever comes first in tree order. Clicking it adds the
     * selection to THAT force, silently, and the caller's wait then times out looking in the force
     * it asked for — which is what 20 specs were doing.
     *
     * <p>Returns null when the container is not in the tree, so callers report a missing FORCE
     * rather than a missing entry.
     */
    private TreeItem<Object> resolveTreeScope(TreeView<Object> tree, String containerId) {
        if (containerId == null) {
            return tree.getRoot();
        }
        return findTreeItemByText(tree.getRoot(), ":" + containerId + ":");
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

    /**
     * True when a selection's reported entryId is the one that was asked for.
     *
     * <p>Not equality. A selection made from an ENTRY LINK reports the composite
     * {@code linkId::targetId} — <code>link-alpha::shared-unit</code> for a spec that asked for
     * <code>link-alpha</code> — so equality answers "no selection was created" about a selection
     * sitting right there. That surfaced as a 10s poll timing out in every spec whose action
     * addresses a link, and read as the click having done nothing.
     *
     * <p>Matching is one-directional on purpose: the composite may be widened by a prefix, so its
     * segments are candidates for the requested id, but a request for the composite is not
     * satisfied by a selection carrying only one segment of it.
     */
    private boolean isSelectionOfEntry(JsonObject selection, String entryId) {
        String actual = getStringField(selection, "entryId");
        if (actual == null || entryId == null) return false;
        if (actual.equals(entryId)) return true;
        for (String segment : actual.split("::")) {
            if (segment.equals(entryId)) return true;
        }
        return false;
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
            if (isSelectionOfEntry(sel, entryId)) {
                return sel;
            }
        }

        // Fallback: check for number increment (non-instanced entry)
        List<JsonObject> beforeSelections = getSelectionsInScope(before, forceId, parentSelectionId);
        Map<String, Integer> beforeNumbers = new HashMap<>();
        for (JsonObject sel : beforeSelections) {
            String id = getStringField(sel, "id");
            if (id != null && isSelectionOfEntry(sel, entryId)) {
                beforeNumbers.put(id, getIntField(sel, "number", 1));
            }
        }
        for (JsonObject sel : afterSelections) {
            String id = getStringField(sel, "id");
            if (id != null && isSelectionOfEntry(sel, entryId)) {
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

    private static String getStringParam(JsonObject params, String key, String defaultValue) {
        if (params.has(key) && !params.get(key).isJsonNull()) {
            return params.get(key).getAsString();
        }
        return defaultValue;
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
