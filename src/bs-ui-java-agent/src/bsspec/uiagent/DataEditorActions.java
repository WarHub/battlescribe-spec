package bsspec.uiagent;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import javafx.application.Platform;
import javafx.scene.Node;
import javafx.scene.control.ButtonBase;
import javafx.scene.control.TextField;
import javafx.scene.control.TreeItem;
import javafx.scene.control.TreeView;
import javafx.scene.layout.VBox;
import javafx.stage.Stage;
import javafx.stage.Window;

import java.io.FileOutputStream;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.Callable;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/**
 * Data editor action orchestration for the BattleScribe Data Editor window.
 * Parallel to {@link RosterActions} for the roster editor.
 *
 * <p>All mutations run on the JavaFX application thread via {@link #runOnFx} /
 * {@link #runOnFxGet}. State reads walk the Java model via reflection.
 *
 * <p>The controller is found by locating the Stage whose title starts with
 * {@code "Data Editor"}, then looking up {@code #btnSaveDataFile} and walking
 * the handler's object tree to reach {@code DataEditorWindowController}.
 */
public class DataEditorActions {

    private static final int POLL_MS = 200;
    private static final int POLL_TIMEOUT_MS = 10_000;
    private static final int LOAD_TIMEOUT_MS = 120_000;
    private static final int FX_TIMEOUT_MS = 60_000;
    /** Grace period for an edit-panel control to appear after selecting an entry. */
    private static final int FIELD_GRACE_MS = 2_000;

    @SuppressWarnings("unused")
    private final EngineAccessor engineAccessor;
    /** Cached controller — cleared on each new setup call. */
    private Object cachedController = null;
    /** Path of the file most recently opened — the save target for {@code reload}. */
    private String lastOpenedPath = null;
    /**
     * Identity map for id-less model entries (modifier, modifierGroup, condition,
     * conditionGroup, repeat). BattleScribe gives these no id attribute, so the
     * tree-id diffing used for normal entries cannot detect or reference them.
     * We assign a synthetic UUID on add and resolve it back to the model object
     * (and its tree item, by identity) for later setField/remove. Cleared on load.
     */
    private final java.util.Map<String, Object> idLessEntries = new java.util.HashMap<>();

    public DataEditorActions(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
    }

    // ─── Dispatch ────────────────────────────────────────────────────────────

    public String dispatch(String method, String params) {
        JsonObject p = params != null && !params.isEmpty() && !params.equals("{}")
                ? new JsonParser().parse(params).getAsJsonObject()
                : new JsonObject();

        if ("gamedataLoadFilesAction".equals(method))   return loadFiles(p);
        if ("gamedataOpenCatalogueAction".equals(method)) return openCatalogue(p);
        if ("gamedataAddEntryAction".equals(method))    return addEntry(p);
        if ("gamedataRemoveEntryAction".equals(method)) return removeEntry(p);
        if ("gamedataSetFieldAction".equals(method))    return setField(p);
        if ("gamedataSetCostAction".equals(method))     return setCost(p);
        if ("gamedataSetCharacteristicAction".equals(method)) return setCharacteristic(p);
        if ("gamedataAddLinkAction".equals(method))     return addLink(p);
        if ("gamedataSaveAndReloadAction".equals(method)) return saveAndReload(p);
        if ("gamedataGetDataState".equals(method))      return getDataState(p);
        if ("gamedataGetErrors".equals(method))         return getErrors(p);
        throw new IllegalArgumentException("Unknown gamedata action: " + method);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    /**
     * Setup-time load: stage is done by the C# side; here we open the primary file (the first
     * catalogue, or the game system if there are none) through the editor's real open path so a
     * default document is loaded for specs that don't issue an explicit {@code openCatalogue}.
     */
    private String loadFiles(JsonObject params) {
        cachedController = null; // reset cache on new load
        idLessEntries.clear();
        String gstPath = requireString(params, "gstPath");
        JsonArray catPathsArr = params.has("catPaths") ? params.get("catPaths").getAsJsonArray() : new JsonArray();
        String primary = catPathsArr.size() > 0 ? catPathsArr.get(0).getAsString() : gstPath;
        openCataloguePath(primary);
        return "{}";
    }

    /**
     * The {@code openCatalogue} action: open the selected file via the editor's real open path —
     * {@code dataSource.f(path)} (catalogue) / {@code dataSource.c(path)} (game system) to load it,
     * then the window controller's private {@code a(BaseRootEntry)} display method, exactly as
     * {@code actLoadDataFile} runs after the (un-driveable) native file picker. The C# side passes
     * the staged path for the requested id.
     */
    private String openCatalogue(JsonObject params) {
        idLessEntries.clear(); // reopening rebuilds the tree; old identity refs are stale
        openCataloguePath(requireString(params, "path"));
        return "{}";
    }

    private void openCataloguePath(String path) {
        Object ctrl = findController();
        Object dataSource = runOnFxGet(() -> ctrl.getClass().getMethod("getDataSource").invoke(ctrl));

        boolean isGameSystem = path.endsWith(".gst") || path.endsWith(".gstz");
        Object flatEntry = isGameSystem
                ? invoke(getMethod(dataSource.getClass(), "c", String.class, boolean.class), dataSource, path, false)
                : invoke(getMethod(dataSource.getClass(), "f", String.class, boolean.class), dataSource, path, false);
        if (flatEntry == null) throw new RuntimeException("Data source returned null for the given path: " + path);

        Method loadMethod = findLoadMethod(ctrl.getClass());
        Object finalFlat = flatEntry;
        CompletableFuture<Void> future = new CompletableFuture<>();
        Platform.runLater(() -> {
            try { loadMethod.invoke(ctrl, finalFlat); future.complete(null); }
            catch (Exception e) { future.completeExceptionally(e); }
        });
        try {
            future.get(LOAD_TIMEOUT_MS, TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            throw new RuntimeException("File loading timed out after " + LOAD_TIMEOUT_MS + "ms");
        } catch (Exception e) {
            throw new RuntimeException("File loading failed", e);
        }
        lastOpenedPath = path;
    }

    /**
     * Round-trip save + reload for round-trip specs: serialize the open document's live model back
     * to the file it was opened from (BattleScribe's own DataUtils serializer, the inverse of the
     * load path), then re-open that file through the real open path. A round-trip spec re-asserts
     * its {@code expectedState} after this, so a still-holding assertion proves persistence kept the
     * data. Catalogue specs reopen the catalogue (the game system stays loaded in the data manager).
     */
    private String saveAndReload(JsonObject params) {
        if (lastOpenedPath == null) {
            throw new RuntimeException("No file has been opened; nothing to reload");
        }
        Object ctrl = findController();
        Object dm = runOnFxGet(() -> ctrl.getClass().getMethod("getDataManager").invoke(ctrl));
        if (dm == null) throw new RuntimeException("Data manager not available for reload");
        Object root = tryInvoke(dm, "c");
        if (root == null) throw new RuntimeException("No open document to save for reload");

        serializeToFile(root, lastOpenedPath);

        idLessEntries.clear(); // reopening rebuilds the tree; old identity refs are stale
        openCataloguePath(lastOpenedPath);
        return "{}";
    }

    /**
     * Serialize a BattleScribe model object (GameSystem / Catalogue) to a file via the DataUtils
     * serializer {@code net.battlescribe.a.c.e.a(model, OutputStream)} — the write counterpart of
     * the {@code e}/{@code f} readers the open path uses. The class is obfuscated, so the matching
     * static {@code a} overload is found by parameter shape.
     */
    private void serializeToFile(Object model, String path) {
        try {
            Class<?> dataUtils = Class.forName("net.battlescribe.a.c.e");
            Method serialize = null;
            for (Method m : dataUtils.getMethods()) {
                Class<?>[] pt = m.getParameterTypes();
                if (m.getName().equals("a") && pt.length == 2
                        && OutputStream.class.isAssignableFrom(pt[1])
                        && pt[0].isInstance(model)) {
                    serialize = m;
                    break;
                }
            }
            if (serialize == null) {
                throw new RuntimeException("DataUtils serialize a(" + model.getClass().getName() + ", OutputStream) not found");
            }
            try (OutputStream out = new FileOutputStream(path)) {
                serialize.invoke(null, model, out);
            }
        } catch (RuntimeException e) {
            throw e;
        } catch (Exception e) {
            throw new RuntimeException("Failed to serialize model for reload: " + e.getMessage(), e);
        }
    }

    private String addEntry(JsonObject params) {
        String parentId = requireString(params, "parentId");
        String entryType = requireString(params, "entryType");
        String name = optString(params, "name");

        Object ctrl = findController();
        TreeItem<Object> parentItem = runOnFxGet(() -> findTreeItemById(ctrl, parentId));
        if (parentItem == null) throw new RuntimeException("Tree item not found: " + parentId);

        // Characteristic types are added by the ProfileType edit-panel sub-controller, not the
        // main tree controller. Drive that real sub-controller's add handler.
        if ("characteristicType".equals(entryType)) {
            return addCharacteristicType(ctrl, parentItem, name);
        }

        boolean parentIsRoot = runOnFxGet(() -> isRootEntry(parentItem.getValue()));
        String addMethod = actAddMethodName(entryType, parentIsRoot);
        Object parentModel = parentItem.getValue();
        String getter = addContainerGetter(entryType, parentIsRoot);

        // Fail fast with a clear message for entry types the editor only adds under a
        // precondition. Without this the actAdd* call is a silent no-op and we'd otherwise
        // wait out the full diff-poll timeout before throwing a generic "no new entry" error.
        validateAddPreconditions(ctrl, parentModel, entryType);

        // Detect the new entry by diffing the parent's model child-list rather than scanning
        // the tree for a new id. This works uniformly for id-bearing entries, id-less entries
        // (modifier/condition/repeat/groups), and entries that aren't rendered as tree nodes
        // (e.g. category links). Id-bearing entries return their real id; id-less entries get
        // a synthetic UUID tracked by identity for later setField/remove.
        List<Object> before = new ArrayList<>();
        List<Object> beforeList = runOnFxGet(() -> getList(parentModel, getter));
        if (beforeList != null) before.addAll(beforeList);

        runOnFx(() -> selectItem(ctrl, parentItem));
        sleep(500);
        runOnFx(() -> invokeCtrl(ctrl, addMethod));

        Object newObj = null;
        long deadline = System.currentTimeMillis() + POLL_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            List<Object> after = runOnFxGet(() -> getList(parentModel, getter));
            if (after != null && after.size() > before.size()) {
                for (Object o : after) {
                    if (!containsIdentity(before, o)) { newObj = o; break; }
                }
                if (newObj == null && !after.isEmpty()) newObj = after.get(after.size() - 1);
                if (newObj != null) break;
            }
            sleep(POLL_MS);
        }
        if (newObj == null) {
            throw new RuntimeException("No new " + entryType + " appeared on parent " + getter + "()");
        }

        String id = getId(newObj);
        if (id == null || id.isEmpty()) {
            id = java.util.UUID.randomUUID().toString();
            idLessEntries.put(id, newObj);
        }
        if (name != null) {
            setFieldOnEntry(ctrl, id, "name", name); // drive the panel's #txtName, not the model
        }

        JsonObject result = new JsonObject();
        result.addProperty("entryId", id);
        return result.toString();
    }

    /**
     * Validate that the editor will actually add an entry of {@code entryType} under
     * {@code parentModel}, throwing a clear error when a known precondition is unmet.
     * These mirror the Data Editor's own guards (verified in the decompiled controller):
     * {@code actAddCategoryLink} is a no-op unless a ForceEntry is selected, and
     * {@code actAddProfile}/{@code actAddSharedProfile} need at least one profile type.
     */
    private void validateAddPreconditions(Object ctrl, Object parentModel, String entryType) {
        if ("categoryLink".equals(entryType)) {
            String cn = runOnFxGet(() -> parentModel.getClass().getSimpleName());
            if (!"ForceEntry".equals(cn)) {
                throw new IllegalStateException(
                        "categoryLink can only be added to a ForceEntry; parent is a " + cn);
            }
        }
        if ("profile".equals(entryType) || "sharedProfile".equals(entryType)) {
            if (!runOnFxGet(() -> profileTypeExists(ctrl))) {
                throw new IllegalStateException(
                        "profile requires at least one profileType in the game system or catalogue");
            }
        }
    }

    /** True if a ProfileType exists on the loaded catalogue root or its game system. */
    private boolean profileTypeExists(Object ctrl) {
        Object dm = tryInvoke(ctrl, "getDataManager");
        if (dm == null) return true; // can't determine — don't block
        return hasProfileType(tryInvoke(dm, "c")) || hasProfileType(tryInvoke(dm, "aa"));
    }

    private boolean hasProfileType(Object root) {
        if (root == null) return false;
        List<Object> pts = getList(root, "getProfileTypes");
        return pts != null && !pts.isEmpty();
    }

    /** Parent model container getter for a newly added entry of the given type. */
    private static String addContainerGetter(String entryType, boolean parentIsRoot) {
        if (parentIsRoot) {
            // At a game-system / catalogue root, groups and profiles go to shared containers.
            if ("selectionEntryGroup".equals(entryType)) return "getSharedSelectionEntryGroups";
            if ("profile".equals(entryType))             return "getSharedProfiles";
        }
        switch (entryType) {
            case "selectionEntry":             return "getSelectionEntries";
            case "selectionEntryGroup":        return "getSelectionEntryGroups";
            case "sharedSelectionEntry":       return "getSharedSelectionEntries";
            case "sharedSelectionEntryGroup":  return "getSharedSelectionEntryGroups";
            case "sharedRule":                 return "getSharedRules";
            case "sharedProfile":              return "getSharedProfiles";
            case "sharedInfoGroup":            return "getSharedInfoGroups";
            case "entryLink":                  return "getEntryLinks";
            case "rule":                       return "getRules";
            case "profile":                    return "getProfiles";
            case "infoLink":                   return "getInfoLinks";
            case "infoGroup":                  return "getInfoGroups";
            case "categoryLink":               return "getCategoryLinks";
            case "catalogueLink":              return "getCatalogueLinks";
            case "forceEntry":                 return "getForceEntries";
            case "categoryEntry":              return "getCategoryEntries";
            case "constraint":                 return "getConstraints";
            case "modifier":                   return "getModifiers";
            case "modifierGroup":              return "getModifierGroups";
            case "condition":                  return "getConditions";
            case "conditionGroup":             return "getConditionGroups";
            case "repeat":                     return "getRepeats";
            case "costType":                   return "getCostTypes";
            case "profileType":                return "getProfileTypes";
            case "characteristicType":         return "getCharacteristicTypes";
            case "publication":                return "getPublications";
            default: throw new RuntimeException("Unknown container for entry type: " + entryType);
        }
    }

    /**
     * Add a CharacteristicType by driving the real ProfileType edit-panel sub-controller.
     * Selecting the ProfileType node builds its {@code ProfileTypeEditPanelController}; we
     * reach that live instance from the main controller's panel-controller list and invoke
     * its {@code actAddCharacteristicType()} handler (the same path the panel's ADD button
     * triggers). The new type is detected by diffing the ProfileType's child-list.
     */
    private String addCharacteristicType(Object ctrl, TreeItem<Object> parentItem, String name) {
        Object profileType = parentItem.getValue();

        List<Object> before = new ArrayList<>();
        List<Object> beforeList = runOnFxGet(() -> getList(profileType, "getCharacteristicTypes"));
        if (beforeList != null) before.addAll(beforeList);

        // Select the ProfileType node so its edit panel (and sub-controller) is built.
        runOnFx(() -> selectItem(ctrl, parentItem));
        sleep(500);

        Object panel = runOnFxGet(() -> findPanelController(ctrl, "ProfileTypeEditPanelController"));
        if (panel == null) {
            throw new RuntimeException(
                    "ProfileTypeEditPanelController not active after selecting the profile type");
        }
        runOnFx(() -> panel.getClass().getMethod("actAddCharacteristicType").invoke(panel));

        Object newObj = null;
        long deadline = System.currentTimeMillis() + POLL_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            List<Object> after = runOnFxGet(() -> getList(profileType, "getCharacteristicTypes"));
            if (after != null && after.size() > before.size()) {
                for (Object o : after) {
                    if (!containsIdentity(before, o)) { newObj = o; break; }
                }
                if (newObj == null && !after.isEmpty()) newObj = after.get(after.size() - 1);
                if (newObj != null) break;
            }
            sleep(POLL_MS);
        }
        if (newObj == null) {
            throw new RuntimeException("No new characteristicType appeared on the profile type");
        }

        final Object created = newObj;
        String id = getId(newObj);
        if (id == null || id.isEmpty()) {
            id = java.util.UUID.randomUUID().toString();
            idLessEntries.put(id, newObj);
        }
        if (name != null) {
            // A characteristic type is edited in the ProfileType panel, not the main tree: select it
            // in the list, then type its name into the panel's shared name field.
            setCharacteristicTypeName(ctrl, created, name);
        }
        JsonObject result = new JsonObject();
        result.addProperty("entryId", id);
        return result.toString();
    }

    /** Drive a characteristic type's name via the ProfileType panel (list selection + name field). */
    private void setCharacteristicTypeName(Object ctrl, Object charType, String name) {
        Object panel = runOnFxGet(() -> findPanelControllerWithMethod(ctrl, "getLstCharacteristicTypes"));
        if (panel == null) {
            throw new RuntimeException("setCharacteristicType name: ProfileType panel not active");
        }
        runOnFx(() -> {
            @SuppressWarnings("unchecked")
            javafx.scene.control.ListView<Object> lst =
                    (javafx.scene.control.ListView<Object>) panel.getClass()
                            .getMethod("getLstCharacteristicTypes").invoke(panel);
            lst.getSelectionModel().select(charType); // fires the list listener → name field loads
        });
        sleep(200);
        runOnFx(() -> {
            javafx.scene.control.TextField txt =
                    (javafx.scene.control.TextField) panel.getClass().getMethod("getTxtName").invoke(panel);
            txt.setText(name); // ChangeListener writes the selected characteristic type's name
        });
        sleep(150);
    }

    /**
     * Find a live edit-panel sub-controller whose class name contains {@code nameFragment}.
     * The main {@code DataEditorWindowController} keeps the active panel controllers in a
     * private {@code List<BaseEditPanelController>} field (rebuilt whenever the tree
     * selection changes); we scan its List-typed fields for the matching instance.
     */
    private Object findPanelController(Object ctrl, String nameFragment) {
        Class<?> cls = ctrl.getClass();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                if (!List.class.isAssignableFrom(f.getType())) continue;
                try {
                    f.setAccessible(true);
                    Object val = f.get(ctrl);
                    if (val instanceof List) {
                        for (Object o : (List<?>) val) {
                            if (o != null && o.getClass().getName().contains(nameFragment)) return o;
                        }
                    }
                } catch (Exception ignored) {}
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private static boolean containsIdentity(List<Object> list, Object o) {
        for (Object x : list) {
            if (x == o) return true;
        }
        return false;
    }

    private String removeEntry(JsonObject params) {
        String entryId = requireString(params, "entryId");
        Object ctrl = findController();
        TreeItem<Object> item = runOnFxGet(() -> findTreeItemById(ctrl, entryId));
        if (item == null) throw new RuntimeException("Tree item not found: " + entryId);

        runOnFx(() -> selectItem(ctrl, item));
        sleep(200);
        runOnFx(() -> invokeCtrl(ctrl, "actRemove"));

        long deadline = System.currentTimeMillis() + POLL_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            if (!runOnFxGet(() -> subtreeIds(getTreeView(ctrl).getRoot())).contains(entryId)) return "{}";
            sleep(POLL_MS);
        }
        throw new RuntimeException("Entry " + entryId + " not removed within " + POLL_TIMEOUT_MS + "ms");
    }

    private String setField(JsonObject params) {
        String entryId = requireString(params, "entryId");
        String field = requireString(params, "field");
        String value = params.has("value") && !params.get("value").isJsonNull()
                ? params.get("value").getAsString() : null;
        setFieldOnEntry(findController(), entryId, field, value);
        return "{}";
    }

    /**
     * Set a cost value by driving the cost {@code Spinner<Double>} in the selection-entry panel's
     * {@code pnlCostLimits} TilePane. The spinner's ChangeListener creates the cost if it was absent
     * (nonzero), updates it, or removes it (zero) — full editor semantics. The row order matches
     * {@code dataManager.G()} (all cost types), so we index the matching cost type into the TilePane.
     */
    private String setCost(JsonObject params) {
        String entryId = requireString(params, "entryId");
        String costTypeId = requireString(params, "field"); // "field" carries the cost type id
        String value = params.has("value") && !params.get("value").isJsonNull()
                ? params.get("value").getAsString() : null;

        Object ctrl = findController();
        TreeItem<Object> item = runOnFxGet(() -> findTreeItemById(ctrl, entryId));
        if (item == null) throw new RuntimeException("Tree item not found for setCost: " + entryId);

        runOnFx(() -> selectItem(ctrl, item));
        sleep(200);

        Object panel = runOnFxGet(() -> findPanelControllerWithMethod(ctrl, "getPnlCostLimits"));
        if (panel == null) {
            throw new RuntimeException("setCost: cost panel (getPnlCostLimits) not active for " + entryId);
        }

        Object spinner = runOnFxGet(() -> {
            Object dm = ctrl.getClass().getMethod("getDataManager").invoke(ctrl);
            List<Object> costTypes = getList(dm, "G");
            if (costTypes == null) throw new RuntimeException("setCost: could not list cost types");
            int idx = -1;
            for (int i = 0; i < costTypes.size(); i++) {
                if (costTypeId.equals(tryStr(costTypes.get(i), "getId"))) { idx = i; break; }
            }
            if (idx < 0) throw new RuntimeException("setCost: cost type not found: " + costTypeId);
            javafx.scene.layout.TilePane tile =
                    (javafx.scene.layout.TilePane) panel.getClass().getMethod("getPnlCostLimits").invoke(panel);
            if (idx >= tile.getChildren().size()) {
                throw new RuntimeException("setCost: no cost row at index " + idx + " for " + costTypeId);
            }
            Node row = tile.getChildren().get(idx); // HBox[Label, Spinner]
            for (Node child : ((javafx.scene.layout.Pane) row).getChildren()) {
                if (child instanceof javafx.scene.control.Spinner) return child;
            }
            throw new RuntimeException("setCost: no spinner in cost row " + idx);
        });

        runOnFx(() -> setSpinnerValue((javafx.scene.control.Spinner<?>) spinner, value));
        sleep(150);
        return "{}";
    }

    /**
     * Set a characteristic value by driving its {@code TextArea} in the profile panel's
     * {@code pnlProfile} GridPane (column 1, row = the characteristic's index). Characteristics come
     * from the profile type, so the row must already exist — we never fabricate one.
     */
    private String setCharacteristic(JsonObject params) {
        String entryId = requireString(params, "entryId");
        String key = requireString(params, "field"); // "field" carries the characteristic name or type id
        String value = params.has("value") && !params.get("value").isJsonNull()
                ? params.get("value").getAsString() : "";

        Object ctrl = findController();
        TreeItem<Object> item = runOnFxGet(() -> findTreeItemById(ctrl, entryId));
        if (item == null) throw new RuntimeException("Tree item not found for setCharacteristic: " + entryId);
        Object model = item.getValue();

        runOnFx(() -> selectItem(ctrl, item));
        sleep(200);

        Object panel = runOnFxGet(() -> findPanelControllerWithMethod(ctrl, "getPnlProfile"));
        if (panel == null) {
            throw new RuntimeException("setCharacteristic: profile panel (getPnlProfile) not active for " + entryId);
        }

        Node textArea = runOnFxGet(() -> {
            List<Object> chars = getList(model, "getCharacteristics");
            if (chars == null) {
                throw new RuntimeException("setCharacteristic: entry has no characteristics (not a profile?)");
            }
            int idx = -1;
            for (int i = 0; i < chars.size(); i++) {
                Object ch = chars.get(i);
                if (key.equals(tryStr(ch, "getName")) || key.equals(tryStr(ch, "getTypeId"))) { idx = i; break; }
            }
            if (idx < 0) {
                throw new RuntimeException(
                        "setCharacteristic: '" + key + "' not present on profile (set the profile type first)");
            }
            javafx.scene.layout.GridPane grid =
                    (javafx.scene.layout.GridPane) panel.getClass().getMethod("getPnlProfile").invoke(panel);
            for (Node child : grid.getChildren()) {
                if (!(child instanceof javafx.scene.control.TextArea)) continue;
                Integer row = javafx.scene.layout.GridPane.getRowIndex(child);
                Integer col = javafx.scene.layout.GridPane.getColumnIndex(child);
                if ((col == null ? 0 : col) == 1 && (row == null ? 0 : row) == idx) return child;
            }
            throw new RuntimeException("setCharacteristic: no TextArea at row " + idx);
        });

        runOnFx(() -> setNodeValue(textArea, "characteristic", value));
        sleep(150);
        return "{}";
    }

    /** Find the active edit-panel sub-controller that declares (or inherits) the given method. */
    private Object findPanelControllerWithMethod(Object ctrl, String methodName) {
        Class<?> cls = ctrl.getClass();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                if (!List.class.isAssignableFrom(f.getType())) continue;
                try {
                    f.setAccessible(true);
                    Object val = f.get(ctrl);
                    if (val instanceof List) {
                        for (Object o : (List<?>) val) {
                            if (o == null) continue;
                            try { o.getClass().getMethod(methodName); return o; }
                            catch (NoSuchMethodException ignored) {}
                        }
                    }
                } catch (Exception ignored) {}
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private static double parseDouble(String value) {
        if (value == null || value.isEmpty()) return 0.0;
        try { return Double.parseDouble(value); } catch (NumberFormatException e) { return 0.0; }
    }

    private String addLink(JsonObject params) {
        String parentId = requireString(params, "parentId");
        String linkType = requireString(params, "linkType");
        String targetId = requireString(params, "targetId");

        Object ctrl = findController();
        TreeItem<Object> parentItem = runOnFxGet(() -> findTreeItemById(ctrl, parentId));
        if (parentItem == null) throw new RuntimeException("Parent tree item not found: " + parentId);

        Object parentModel = parentItem.getValue();
        boolean parentIsRoot = runOnFxGet(() -> isRootEntry(parentModel));
        String addMethod = actAddMethodName(linkType);
        String getter = addContainerGetter(linkType, parentIsRoot);

        List<Object> before = new ArrayList<>();
        List<Object> beforeList = runOnFxGet(() -> getList(parentModel, getter));
        if (beforeList != null) before.addAll(beforeList);

        runOnFx(() -> selectItem(ctrl, parentItem));
        sleep(500);
        runOnFx(() -> invokeCtrl(ctrl, addMethod));

        Object newObj = null;
        long deadline = System.currentTimeMillis() + POLL_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            List<Object> after = runOnFxGet(() -> getList(parentModel, getter));
            if (after != null && after.size() > before.size()) {
                for (Object o : after) {
                    if (!containsIdentity(before, o)) { newObj = o; break; }
                }
                if (newObj == null && !after.isEmpty()) newObj = after.get(after.size() - 1);
                if (newObj != null) break;
            }
            sleep(POLL_MS);
        }
        if (newObj == null) {
            throw new RuntimeException("No new " + linkType + " appeared on parent " + getter + "()");
        }

        // Resolve the link id first (id-less links get a synthetic UUID tracked by identity).
        String linkId = getId(newObj);
        if (linkId == null || linkId.isEmpty()) {
            linkId = java.util.UUID.randomUUID().toString();
            idLessEntries.put(linkId, newObj);
        }

        // Mirror how a user links a non-default target: set the Link Type to match the target's kind
        // BEFORE typing the target id, so the editor resolves and retains it (re-typing afterwards
        // would otherwise re-resolve against the wrong type and drop the target). Entry/info links
        // have a Link Type combo; category/catalogue links have a fixed type (kind == null → skip).
        // Set the target through the panel's "Target ID" field. setFieldOnEntry aligns the Link Type
        // to the target's kind first (so the editor resolves and retains a non-default-type target).
        setFieldOnEntry(ctrl, linkId, "targetId", targetId);

        JsonObject result = new JsonObject();
        result.addProperty("entryId", linkId);
        return result.toString();
    }

    /** Resolve a link target's kind (selectionEntry/selectionEntryGroup/rule/profile/infoGroup). */
    private String linkTargetKind(Object ctrl, String targetId) {
        try {
            Object dm = ctrl.getClass().getMethod("getDataManager").invoke(ctrl);
            for (String rootGetter : new String[]{"c", "aa"}) {
                String kind = searchTargetKind(tryInvoke(dm, rootGetter), targetId, new HashSet<>());
                if (kind != null) return kind;
            }
        } catch (Exception ignored) {}
        return null;
    }

    /** Depth-first search of the loaded model (any collection getter) for the target's kind. */
    private String searchTargetKind(Object obj, String targetId, Set<Object> seen) {
        if (obj == null || seen.contains(obj)) return null;
        if (!obj.getClass().getName().startsWith("net.battlescribe.model.data")) return null;
        seen.add(obj);
        if (targetId.equals(tryStr(obj, "getId"))) {
            return linkKindOfClass(obj.getClass().getSimpleName());
        }
        for (Method m : obj.getClass().getMethods()) {
            if (m.getParameterCount() != 0 || !m.getName().startsWith("get")) continue;
            if (!java.util.Collection.class.isAssignableFrom(m.getReturnType())) continue;
            try {
                Object raw = m.invoke(obj);
                if (raw instanceof java.util.Collection) {
                    for (Object c : (java.util.Collection<?>) raw) {
                        String k = searchTargetKind(c, targetId, seen);
                        if (k != null) return k;
                    }
                }
            } catch (Exception ignored) {}
        }
        return null;
    }

    private static String linkKindOfClass(String simpleName) {
        switch (simpleName) {
            case "SelectionEntry":      return "selectionEntry";
            case "SelectionEntryGroup": return "selectionEntryGroup";
            case "Rule":                return "rule";
            case "Profile":             return "profile";
            case "InfoGroup":           return "infoGroup";
            default:                    return null;
        }
    }

    private String getDataState(JsonObject params) {
        Object ctrl = findController();
        Object dm = runOnFxGet(() -> ctrl.getClass().getMethod("getDataManager").invoke(ctrl));
        if (dm == null) return buildEmptyState();

        Object root = tryInvoke(dm, "c");
        if (root == null) return buildEmptyState();

        JsonObject state = new JsonObject();
        String rootClass = root.getClass().getName();
        boolean isCatalogue = rootClass.contains("Catalogue")
                && !rootClass.contains("CatalogueLink")
                && !rootClass.contains("CatalogueManager");

        if (isCatalogue) {
            Object gs = tryInvoke(dm, "aa");
            if (gs != null) state.add("gameSystem", buildGameSystemJson(gs));
            JsonArray cats = new JsonArray();
            cats.add(buildCatalogueJson(root));
            state.add("catalogues", cats);
        } else {
            state.add("gameSystem", buildGameSystemJson(root));
            state.add("catalogues", new JsonArray());
        }
        return state.toString();
    }

    /**
     * Read the Data Editor's validation error list. The window controller exposes the data
     * manager via getDataManager(); its {@code a(boolean)} method returns the list of error
     * objects (net.battlescribe.engine.b.a), each of which is INamed — getName() is the message.
     */
    @SuppressWarnings("unchecked")
    private String getErrors(JsonObject params) {
        Object ctrl = findController();
        Object dm = tryInvoke(ctrl, "getDataManager");

        JsonArray arr = new JsonArray();
        if (dm != null) {
            List<Object> errs = new ArrayList<>();
            try {
                Object raw = dm.getClass().getMethod("a", boolean.class).invoke(dm, true);
                if (raw instanceof java.util.Collection) {
                    errs.addAll((java.util.Collection<Object>) raw);
                }
            } catch (Exception e) {
                throw new RuntimeException("Data Editor error-list method (getDataManager().a(boolean)) failed", e);
            }
            for (Object er : errs) {
                if (er == null) continue;
                JsonObject o = new JsonObject();
                String msg = tryStr(er, "getName");
                if (msg == null || msg.isEmpty()) msg = er.toString();
                o.addProperty("message", msg);
                arr.add(o);
            }
        }

        JsonObject state = new JsonObject();
        state.add("errors", arr);
        return state.toString();
    }

    // ─── Controller discovery ─────────────────────────────────────────────────

    private Object findController() {
        if (cachedController != null) return cachedController;
        Stage stage = runOnFxGet(() -> {
            for (Window w : Window.getWindows()) {
                if (w instanceof Stage) {
                    Stage s = (Stage) w;
                    if (s.getTitle() != null && s.getTitle().startsWith("Data Editor"))
                        return s;
                }
            }
            return null;
        });
        if (stage == null) throw new RuntimeException("Data Editor window not found");

        javafx.scene.Scene scene = stage.getScene();
        Node btn = scene.getRoot().lookup("#btnSaveDataFile");
        if (btn == null) throw new RuntimeException("#btnSaveDataFile not found in Data Editor scene");

        javafx.event.EventHandler<?> handler = ((ButtonBase) btn).getOnAction();
        Object ctrl = findByClassName(handler, "DataEditorWindowController", 4);
        if (ctrl == null) throw new RuntimeException("DataEditorWindowController not found via handler tree");

        cachedController = ctrl;
        return ctrl;
    }

    private Object findByClassName(Object obj, String nameFragment, int depth) {
        if (obj == null || depth <= 0) return null;
        if (obj.getClass().getName().contains(nameFragment)) return obj;
        Class<?> cls = obj.getClass();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (f.getType().isPrimitive() || java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                try {
                    f.setAccessible(true);
                    Object val = f.get(obj);
                    if (val == null) continue;
                    String vn = val.getClass().getName();
                    if (vn.contains(nameFragment)) return val;
                    if (vn.startsWith("javafx.fxml") || vn.startsWith("net.battlescribe")) {
                        Object found = findByClassName(val, nameFragment, depth - 1);
                        if (found != null) return found;
                    }
                } catch (Exception ignored) {}
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    // ─── Tree helpers ─────────────────────────────────────────────────────────

    @SuppressWarnings("unchecked")
    private TreeView<Object> getTreeView(Object ctrl) throws Exception {
        return (TreeView<Object>) ctrl.getClass().getMethod("getTreeData").invoke(ctrl);
    }

    @SuppressWarnings("unchecked")
    private TreeItem<Object> findTreeItemById(Object ctrl, String id) throws Exception {
        TreeView<Object> tree = getTreeView(ctrl);
        if (tree == null) return null;
        // Id-less entries (modifier/condition/repeat/…) are tracked by identity.
        if (idLessEntries.containsKey(id)) {
            return findItemByModel(tree.getRoot(), idLessEntries.get(id));
        }
        return findItemRecursive(tree.getRoot(), id);
    }

    private TreeItem<Object> findItemRecursive(TreeItem<Object> item, String id) {
        if (item == null) return null;
        if (id.equals(getId(item.getValue()))) return item;
        for (TreeItem<Object> child : item.getChildren()) {
            TreeItem<Object> found = findItemRecursive(child, id);
            if (found != null) return found;
        }
        return null;
    }

    /** Find the tree item whose model value is the given object (reference identity). */
    private TreeItem<Object> findItemByModel(TreeItem<Object> item, Object model) {
        if (item == null) return null;
        if (item.getValue() == model) return item;
        for (TreeItem<Object> child : item.getChildren()) {
            TreeItem<Object> found = findItemByModel(child, model);
            if (found != null) return found;
        }
        return null;
    }

    /** Collect all IDs in the subtree rooted at {@code root} (recursive, depth-first). */
    private Set<String> subtreeIds(TreeItem<Object> root) {
        Set<String> ids = new HashSet<>();
        collectIds(root, ids);
        return ids;
    }

    private void collectIds(TreeItem<Object> item, Set<String> ids) {
        if (item == null) return;
        String id = getId(item.getValue());
        if (id != null) ids.add(id);
        for (TreeItem<Object> child : item.getChildren()) {
            collectIds(child, ids);
        }
    }

    /**
     * Poll the full tree until a new ID (not in {@code before}) appears.
     * Uses tree-root scanning so stale {@code parentItem} references don't matter.
     */
    private String waitForNewIdInTree(Object ctrl, Set<String> before) {
        long deadline = System.currentTimeMillis() + POLL_TIMEOUT_MS;
        while (System.currentTimeMillis() < deadline) {
            Set<String> current = runOnFxGet(() -> subtreeIds(getTreeView(ctrl).getRoot()));
            for (String id : current) {
                if (!before.contains(id)) return id;
            }
            sleep(POLL_MS);
        }
        throw new RuntimeException("No new entry appeared in tree within " + POLL_TIMEOUT_MS + "ms");
    }

    @SuppressWarnings("unchecked")
    private void selectItem(Object ctrl, TreeItem<Object> item) throws Exception {
        getTreeView(ctrl).getSelectionModel().select(item);
    }

    private void invokeCtrl(Object ctrl, String method) throws Exception {
        ctrl.getClass().getMethod(method).invoke(ctrl);
    }

    /** True if {@code value} is a game system or catalogue (a {@code BaseRootEntry} subclass). */
    private boolean isRootEntry(Object value) {
        Class<?> c = value == null ? null : value.getClass();
        while (c != null && c != Object.class) {
            if ("BaseRootEntry".equals(c.getSimpleName())) return true;
            c = c.getSuperclass();
        }
        return false;
    }

    // ─── Field editing ────────────────────────────────────────────────────────

    /**
     * Set a field by driving the real edit-panel control (no model bypass). The entry's tree node is
     * selected to build its edit panel, then the mapped control is driven via {@link #setNodeValue}.
     * Two field families need explicit routing because a plain {@code #cssId} lookup can't reach them:
     * a modifier {@code value} (the value control varies by the field's data type) and a
     * constraint/condition/repeat {@code value} (a numeric Spinner, not a text field).
     *
     * <p>No field is ever written to the model directly. Fields the Data Editor has no widget for
     * ({@link #UNEDITABLE_FIELDS}) are skipped — the spec asserts them on engines that can set them.
     * Every other field resolves to a real control or throws; there is no reflective fallback.
     */
    private void setFieldOnEntry(Object ctrl, String entryId, String field, String value) {
        TreeItem<Object> item = runOnFxGet(() -> findTreeItemById(ctrl, entryId));
        if (item == null) {
            throw new RuntimeException(
                    "setField: entry " + entryId + " has no selectable tree node (field=" + field + ")");
        }
        Object itemModel = item.getValue();
        String entryClass = runOnFxGet(() ->
                itemModel != null ? itemModel.getClass().getSimpleName() : "null");

        // The Data Editor has no control for these (CostType's panel edits only `hidden`), so they
        // can't be driven through the real UI. Skip them — never write the model behind the UI's
        // back. The spec asserts them on engines that can set them, via a battlescribe-ui per-engine
        // expectedState override. (A skip, not a fake: getDataState still reads back the real value.)
        if (UNEDITABLE_FIELDS.contains(field)) {
            System.err.println(
                    "[bs-gamedata-ui] field '" + field + "' has no Data Editor control — skipping.");
            return;
        }

        runOnFx(() -> selectItem(ctrl, item));
        sleep(200);

        // Modifier value: the panel makes exactly one of spn/txt/cboBoolean/cboCategories managed
        // based on the field's data type; drive that one.
        if ("value".equals(field) && "Modifier".equals(entryClass)) {
            setModifierValue(ctrl, value);
            sleep(150);
            return;
        }

        String cssId;
        if ("value".equals(field)
                && ("Constraint".equals(entryClass) || "Condition".equals(entryClass)
                    || "Repeat".equals(entryClass))) {
            cssId = "spnValue"; // numeric Spinner — not a txtValue
        } else {
            cssId = fieldToCssId(field);
        }

        VBox pnl = runOnFxGet(() -> (VBox) ctrl.getClass().getMethod("getPnlEditor").invoke(ctrl));

        // For a link target, align the Link Type combo to the target's kind first, so the editor
        // resolves and retains it — typing a target that's invalid for the current type drops it.
        if ("targetId".equals(field) && value != null) {
            String kind = runOnFxGet(() -> linkTargetKind(ctrl, value));
            if (kind != null) alignLinkType(pnl, kind);
        }

        Node node = waitForFieldNodeRequired(pnl, cssId, FIELD_GRACE_MS, field, entryClass);
        runOnFx(() -> setNodeValue(node, cssId, value));
        sleep(200);
    }

    /** Set the link panel's Link Type combo to {@code kind} if it currently offers it (no-op otherwise). */
    private void alignLinkType(VBox pnl, String kind) {
        Node combo = waitForFieldNodeOptional(pnl, "cboType", 500);
        if (!(combo instanceof javafx.scene.control.ComboBox)) return;
        boolean offered = runOnFxGet(() -> {
            for (Object it : ((javafx.scene.control.ComboBox<?>) combo).getItems()) {
                if (it != null && kind.equals(tryStr(it, "getId"))) return true;
            }
            return false;
        });
        if (offered) {
            runOnFx(() -> setNodeValue(combo, "cboType", kind));
            sleep(150);
        }
    }

    /**
     * Fields the BattleScribe Data Editor has no edit-panel control for (verified against the
     * decompiled panels). They can't be driven via the real UI, so {@link #setFieldOnEntry} skips
     * them — the spec asserts them only on engines that can set them, via a battlescribe-ui
     * per-engine expectedState override.
     */
    private static final Set<String> UNEDITABLE_FIELDS = new HashSet<>(java.util.Arrays.asList(
            "defaultCostLimit")); // CostTypeEditPanelController edits only `hidden`

    /**
     * Drive a modifier's value through whichever value control the panel has made managed
     * (spnNumberValue / txtStringValue / cboBooleanValue / cboCategories), selected by the panel
     * from the field's data type — so we never reimplement the field→datatype mapping.
     */
    private void setModifierValue(Object ctrl, String value) {
        Object panel = runOnFxGet(() -> findPanelController(ctrl, "ModifierEditPanelController"));
        if (panel == null) {
            throw new IllegalStateException("setField modifier value: ModifierEditPanelController not active");
        }
        Node node = runOnFxGet(() -> {
            for (String getter : new String[]{
                    "getSpnNumberValue", "getTxtStringValue", "getCboBooleanValue", "getCboCategories"}) {
                Object n = panel.getClass().getMethod(getter).invoke(panel);
                if (n instanceof Node && ((Node) n).isManaged()) {
                    return (Node) n;
                }
            }
            return null;
        });
        if (node == null) {
            throw new IllegalStateException(
                    "setField modifier value: no managed value control (is the modifier field set?)");
        }
        runOnFx(() -> setNodeValue(node, "modifierValue", value));
    }

    /** Like {@link #waitForFieldNodeOptional} but throws (no reflective fallback) when not found. */
    private Node waitForFieldNodeRequired(VBox pnl, String cssId, int timeoutMs, String field, String entryClass) {
        Node node = waitForFieldNodeOptional(pnl, cssId, timeoutMs);
        if (node == null) {
            throw new IllegalStateException(
                    "setField: control #" + cssId + " not found in edit panel for field=" + field
                            + " on " + entryClass);
        }
        return node;
    }

    /** Try to find a node by CSS ID within {@code pnl}; return {@code null} on timeout. */
    private Node waitForFieldNodeOptional(VBox pnl, String cssId, int timeoutMs) {
        long deadline = System.currentTimeMillis() + timeoutMs;
        while (System.currentTimeMillis() < deadline) {
            Node node = runOnFxGet(() -> pnl.lookup("#" + cssId));
            if (node != null) return node;
            sleep(POLL_MS);
        }
        return null;
    }

    private void setNodeValue(Node node, String cssId, String value) throws Exception {
        if (node instanceof TextField) {
            TextField tf = (TextField) node;
            tf.setText(value != null ? value : "");
            tf.fireEvent(new javafx.event.ActionEvent());
        } else if (node instanceof javafx.scene.control.CheckBox) {
            javafx.scene.control.CheckBox cb = (javafx.scene.control.CheckBox) node;
            cb.setSelected(Boolean.parseBoolean(value));
            // The edit panel writes the model in the checkbox's onAction handler;
            // setSelected() alone does not fire it, so dispatch an ActionEvent.
            cb.fireEvent(new javafx.event.ActionEvent());
        } else if (node instanceof javafx.scene.control.ComboBox) {
            @SuppressWarnings("unchecked")
            javafx.scene.control.ComboBox<Object> cb = (javafx.scene.control.ComboBox<Object>) node;
            Object match = null;
            for (Object cbItem : cb.getItems()) {
                if (cbItem == null) continue;
                // Items may be domain objects matched by id (e.g. cboPublication / cboType link types
                // / cboCategories) or by display name (e.g. a profile's typeName picks the ProfileType
                // by name), or plain values/enums matched by display text (e.g. cboBooleanValue).
                if (value.equals(tryStr(cbItem, "getId"))
                        || value.equals(tryStr(cbItem, "getName"))
                        || cbItem.toString().equalsIgnoreCase(value)) {
                    match = cbItem;
                    break;
                }
            }
            cb.setValue(match != null ? match : value);
            // Writeback is also via the combo's onAction handler — fire it after setValue.
            cb.fireEvent(new javafx.event.ActionEvent());
        } else if (node instanceof javafx.scene.control.TextArea) {
            // Rule description, profile characteristics: the panel wires a ChangeListener on the
            // text property, so setText alone fires the writeback (no ActionEvent).
            javafx.scene.control.TextArea ta = (javafx.scene.control.TextArea) node;
            ta.setText(value != null ? value : "");
        } else if (node instanceof javafx.scene.control.Spinner) {
            setSpinnerValue((javafx.scene.control.Spinner<?>) node, value);
        } else {
            throw new RuntimeException("Field #" + cssId + " has unsupported type: " + node.getClass().getSimpleName());
        }
    }

    /**
     * Drive a JavaFX {@code Spinner<Double>} the way the editor's value factory expects: setting
     * the value factory's value updates {@code valueProperty} and fires the panel's
     * {@code ChangeListener<Double>} (cost spinners, modifier/constraint/condition value spinners).
     * The editor-text + commit path is avoided — the value-factory path fires the listener directly.
     */
    @SuppressWarnings({"unchecked", "rawtypes"})
    private void setSpinnerValue(javafx.scene.control.Spinner<?> sp, String value) {
        javafx.scene.control.SpinnerValueFactory vf = sp.getValueFactory();
        if (vf == null) {
            throw new RuntimeException("Spinner has no value factory");
        }
        double d = parseDouble(value);
        // Integer spinners (e.g. spnRevision) reject a Double; match the factory's value type.
        Object current = vf.getValue();
        if (current instanceof Integer) {
            vf.setValue((int) Math.round(d));
        } else {
            vf.setValue(d);
        }
    }

    /**
     * Map a data-model field name to the fx:id of its edit-panel control (verified against the
     * decompiled *EditPanelController classes). {@code value} is NOT here — it is routed by entry
     * type in {@link #setFieldOnEntry} (modifier value control / numeric Spinner).
     */
    private static String fieldToCssId(String field) {
        switch (field) {
            case "name":                    return "txtName";
            case "id":                      return "txtUniqueId";
            case "targetId":                return "txtTargetId";          // LinkEditPanel TextField
            case "hidden":                  return "chkHidden";
            case "collective":              return "chkCollective";
            case "imported":                return "chkImport";
            case "type":                    return "cboType";
            case "typeId":                  return "cboType";              // profile type combo (by id)
            case "typeName":                return "cboType";              // profile type combo (by name)
            case "defaultSelectionEntryId": return "cboDefaultSelection";
            case "publicationId":           return "cboPublication";       // BaseBookData combo
            case "page":                    return "txtPage";
            case "comment":                 return "txtComment";
            case "description":             return "txtDescription";       // Rule TextArea
            case "field":                   return "cboField";             // modifier/query field combo
            case "scope":                   return "cboScope";
            case "childId":                 return "txtChildId";           // FilteredQuery raw id field
            case "shared":                  return "chkShared";
            case "percentValue":            return "chkPercentValue";
            case "includeChildSelections":  return "chkIncludeChildSelections";
            case "includeChildForces":      return "chkIncludeChildForces";
            case "roundUp":                 return "chkRoundUp";
            case "repeats":                 return "spnRepeats";           // Spinner
            case "importRootEntries":       return "chkImportRootEntries";
            case "library":                 return "chkLibrary";
            case "revision":                return "spnRevision";          // Spinner<Integer>
            case "authorName":              return "txtAuthorName";
            case "authorContact":           return "txtContactDetails";    // note: not txtAuthorContact
            case "authorUrl":               return "txtWebsite";           // note: not txtAuthorUrl
            case "readme":                  return "txtReadme";
            case "shortName":               return "txtShortName";
            case "publisher":               return "txtPublisher";
            case "publicationDate":         return "txtPublicationDate";
            case "publisherUrl":            return "txtPublisherUrl";
            default:                        return "txt" + Character.toUpperCase(field.charAt(0)) + field.substring(1);
        }
    }

    // ─── Entry type → actAdd method name ─────────────────────────────────────

    /**
     * Resolve the controller add-method, accounting for the selected parent. When a root
     * entry (game system / catalogue) is selected, a "group" must be added via the
     * {@code actAddShared*} methods — {@code actAddSelectionEntryGroup} only handles a
     * {@code BaseSelectionEntry} parent and is a silent no-op at the root. (A plain
     * selection entry is already handled at the root by {@code actAddSelectionEntry}.)
     */
    private static String actAddMethodName(String entryType, boolean parentIsRoot) {
        if (parentIsRoot && "selectionEntryGroup".equals(entryType)) {
            return "actAddSharedSelectionEntryGroup";
        }
        return actAddMethodName(entryType);
    }

    private static String actAddMethodName(String entryType) {
        if ("selectionEntry".equals(entryType))            return "actAddSelectionEntry";
        if ("selectionEntryGroup".equals(entryType))       return "actAddSelectionEntryGroup";
        if ("entryLink".equals(entryType))                 return "actAddEntryLink";
        if ("infoLink".equals(entryType))                  return "actAddInfoLink";
        if ("categoryLink".equals(entryType))              return "actAddCategoryLink";
        if ("forceEntry".equals(entryType))                return "actAddForceEntry";
        if ("categoryEntry".equals(entryType))             return "actAddCategoryEntry";
        if ("rule".equals(entryType))                      return "actAddRule";
        if ("profile".equals(entryType))                   return "actAddProfile";
        if ("infoGroup".equals(entryType))                 return "actAddInfoGroup";
        if ("constraint".equals(entryType))                return "actAddConstraint";
        if ("modifier".equals(entryType))                  return "actAddModifier";
        if ("modifierGroup".equals(entryType))             return "actAddModifierGroup";
        if ("condition".equals(entryType))                 return "actAddCondition";
        if ("conditionGroup".equals(entryType))            return "actAddConditionGroup";
        if ("repeat".equals(entryType))                    return "actAddRepeat";
        if ("sharedSelectionEntry".equals(entryType))      return "actAddSharedSelectionEntry";
        if ("sharedSelectionEntryGroup".equals(entryType)) return "actAddSharedSelectionEntryGroup";
        if ("sharedProfile".equals(entryType))             return "actAddSharedProfile";
        if ("sharedRule".equals(entryType))                return "actAddSharedRule";
        if ("sharedInfoGroup".equals(entryType))           return "actAddSharedInfoGroup";
        if ("costType".equals(entryType))                  return "actAddCostType";
        if ("profileType".equals(entryType))               return "actAddProfileType";
        if ("characteristicType".equals(entryType))        return "actAddCharacteristicType";
        if ("publication".equals(entryType))               return "actAddPublication";
        if ("catalogueLink".equals(entryType))             return "actAddCatalogueLink";
        throw new RuntimeException("Unknown entry type: " + entryType);
    }

    // ─── State serialization ──────────────────────────────────────────────────

    private JsonObject buildGameSystemJson(Object gs) {
        JsonObject o = new JsonObject();
        putStr(o, "id", gs, "getId");
        putStr(o, "name", gs, "getName");
        o.add("fields", buildRootFields(gs, false));
        o.add("forceEntries",                buildList(gs, "getForceEntries",               "forceEntry"));
        o.add("categoryEntries",             buildList(gs, "getCategoryEntries",            "categoryEntry"));
        o.add("costTypes",                   buildList(gs, "getCostTypes",                  "costType"));
        o.add("profileTypes",                buildList(gs, "getProfileTypes",               "profileType"));
        o.add("publications",                buildList(gs, "getPublications",               "publication"));
        o.add("selectionEntries",            buildList(gs, "getSelectionEntries",           "selectionEntry"));
        o.add("entryLinks",                  buildList(gs, "getEntryLinks",                 "entryLink"));
        o.add("rules",                       buildList(gs, "getRules",                      "rule"));
        o.add("sharedSelectionEntries",      buildList(gs, "getSharedSelectionEntries",     "selectionEntry"));
        o.add("sharedSelectionEntryGroups",  buildList(gs, "getSharedSelectionEntryGroups","selectionEntryGroup"));
        o.add("sharedRules",                 buildList(gs, "getSharedRules",               "rule"));
        o.add("sharedProfiles",              buildList(gs, "getSharedProfiles",            "profile"));
        o.add("sharedInfoGroups",            buildList(gs, "getSharedInfoGroups",          "infoGroup"));
        return o;
    }

    private JsonObject buildCatalogueJson(Object cat) {
        JsonObject o = new JsonObject();
        putStr(o, "id", cat, "getId");
        putStr(o, "name", cat, "getName");
        putStr(o, "gameSystemId", cat, "getGameSystemId");
        o.add("fields", buildRootFields(cat, true));
        o.add("selectionEntries",           buildList(cat, "getSelectionEntries",           "selectionEntry"));
        o.add("entryLinks",                 buildList(cat, "getEntryLinks",                 "entryLink"));
        o.add("rules",                      buildList(cat, "getRules",                      "rule"));
        o.add("forceEntries",               buildList(cat, "getForceEntries",               "forceEntry"));
        o.add("categoryEntries",            buildList(cat, "getCategoryEntries",            "categoryEntry"));
        o.add("publications",               buildList(cat, "getPublications",               "publication"));
        o.add("costTypes",                  buildList(cat, "getCostTypes",                  "costType"));
        o.add("profileTypes",               buildList(cat, "getProfileTypes",               "profileType"));
        o.add("sharedSelectionEntries",     buildList(cat, "getSharedSelectionEntries",     "selectionEntry"));
        o.add("sharedSelectionEntryGroups", buildList(cat, "getSharedSelectionEntryGroups","selectionEntryGroup"));
        o.add("sharedRules",                buildList(cat, "getSharedRules",               "rule"));
        o.add("sharedProfiles",             buildList(cat, "getSharedProfiles",            "profile"));
        o.add("sharedInfoGroups",           buildList(cat, "getSharedInfoGroups",          "infoGroup"));
        o.add("catalogueLinks",             buildList(cat, "getCatalogueLinks",            "catalogueLink"));
        return o;
    }

    /** Root-level metadata fields (author info, revision, library) of a game system / catalogue. */
    private JsonObject buildRootFields(Object root, boolean isCatalogue) {
        JsonObject fields = new JsonObject();
        putFieldIfPresent(fields, root, "getAuthorName", "authorName");
        putFieldIfPresent(fields, root, "getAuthorContact", "authorContact");
        putFieldIfPresent(fields, root, "getAuthorUrl", "authorUrl");
        putFieldIfPresent(fields, root, "getReadme", "readme");
        putNumField(fields, root, "getRevision", "revision");
        if (isCatalogue) {
            putBoolField(fields, root, "library", "isLibrary", "getLibrary");
            putNumField(fields, root, "getGameSystemRevision", "gameSystemRevision");
        }
        return fields;
    }

    private JsonArray buildList(Object obj, String getter, String childType) {
        JsonArray arr = new JsonArray();
        List<Object> list = getList(obj, getter);
        if (list == null) return arr;
        for (Object item : list) arr.add(buildEntryJson(item, childType));
        return arr;
    }

    private JsonObject buildEntryJson(Object entry, String entryType) {
        JsonObject o = new JsonObject();
        putStr(o, "id", entry, "getId");
        putStr(o, "name", entry, "getName");
        o.addProperty("entryType", entryType);
        o.addProperty("hidden", tryBool(entry, "getHidden", "isHidden"));

        JsonObject fields = new JsonObject();
        putFieldIfPresent(fields, entry, "getType", "type");
        putFieldIfPresent(fields, entry, "getComment", "comment");
        putFieldIfPresent(fields, entry, "getTargetId", "targetId");
        putFieldIfPresent(fields, entry, "getPublicationId", "publicationId");
        putFieldIfPresent(fields, entry, "getPage", "page");
        putFieldIfPresent(fields, entry, "getDefaultSelectionEntryId", "defaultSelectionEntryId");
        putBoolField(fields, entry, "collective", "isCollective", "getCollective");
        putBoolField(fields, entry, "imported", "isImported", "getImported");

        // Query / modifier / repeat fields (constraint, modifier, condition, repeat, group)
        putNumField(fields, entry, "getValue", "value");
        putFieldIfPresent(fields, entry, "getField", "field");
        putFieldIfPresent(fields, entry, "getScope", "scope");
        putFieldIfPresent(fields, entry, "getChildId", "childId");
        putBoolField(fields, entry, "shared", "isShared", "getShared");
        putBoolField(fields, entry, "percentValue", "isPercentValue", "getPercentValue");
        putBoolField(fields, entry, "includeChildSelections", "isIncludeChildSelections", "getIncludeChildSelections");
        putBoolField(fields, entry, "includeChildForces", "isIncludeChildForces", "getIncludeChildForces");
        putNumField(fields, entry, "getRepeats", "repeats");
        putBoolField(fields, entry, "roundUp", "isRoundUp", "getRoundUp");

        // Type / description / publication metadata
        putFieldIfPresent(fields, entry, "getTypeId", "typeId");
        putFieldIfPresent(fields, entry, "getTypeName", "typeName");
        putFieldIfPresent(fields, entry, "getDescription", "description");
        putNumField(fields, entry, "getDefaultCostLimit", "defaultCostLimit");
        putBoolField(fields, entry, "primary", "isPrimary", "getPrimary");
        putBoolField(fields, entry, "importRootEntries", "isImportRootEntries", "getImportRootEntries");
        putFieldIfPresent(fields, entry, "getShortName", "shortName");
        putFieldIfPresent(fields, entry, "getPublisher", "publisher");
        putFieldIfPresent(fields, entry, "getPublicationDate", "publicationDate");
        putFieldIfPresent(fields, entry, "getPublisherUrl", "publisherUrl");

        // Costs and characteristics as composite "cost:<typeId>" / "char:<name>" fields,
        // matching the in-process reference engine so assertions are engine-agnostic.
        addCostFields(fields, entry);
        addCharacteristicFields(fields, entry);
        if (!fields.entrySet().isEmpty()) o.add("fields", fields);

        JsonArray children = new JsonArray();
        addChildren(children, entry, "getSelectionEntries",       "selectionEntry");
        addChildren(children, entry, "getSelectionEntryGroups",   "selectionEntryGroup");
        addChildren(children, entry, "getEntryLinks",             "entryLink");
        addChildren(children, entry, "getRules",                  "rule");
        addChildren(children, entry, "getProfiles",               "profile");
        addChildren(children, entry, "getInfoGroups",             "infoGroup");
        addChildren(children, entry, "getInfoLinks",              "infoLink");
        addChildren(children, entry, "getCategoryLinks",          "categoryLink");
        addChildren(children, entry, "getConstraints",            "constraint");
        addChildren(children, entry, "getModifiers",              "modifier");
        addChildren(children, entry, "getModifierGroups",         "modifierGroup");
        addChildren(children, entry, "getConditions",             "condition");
        addChildren(children, entry, "getConditionGroups",        "conditionGroup");
        addChildren(children, entry, "getRepeats",                "repeat");
        addChildren(children, entry, "getForceEntries",           "forceEntry");
        addChildren(children, entry, "getCategoryEntries",        "categoryEntry");
        addChildren(children, entry, "getCharacteristicTypes",    "characteristicType");
        if (children.size() > 0) o.add("children", children);
        return o;
    }

    private void addChildren(JsonArray arr, Object entry, String getter, String childType) {
        List<Object> list = getList(entry, getter);
        if (list == null) return;
        for (Object child : list) arr.add(buildEntryJson(child, childType));
    }

    private static String buildEmptyState() {
        JsonObject s = new JsonObject();
        s.add("catalogues", new JsonArray());
        return s.toString();
    }

    // ─── Reflection helpers ───────────────────────────────────────────────────

    private String getId(Object obj) { return tryStr(obj, "getId"); }

    private void putStr(JsonObject o, String key, Object src, String method) {
        String v = tryStr(src, method);
        if (v != null) o.addProperty(key, v);
    }

    private void putFieldIfPresent(JsonObject fields, Object entry, String getter, String key) {
        String v = tryStr(entry, getter);
        if (v != null && !v.isEmpty()) fields.addProperty(key, v);
    }

    /** Emit a boolean field as a "true"/"false" string, using the first getter that exists. */
    private void putBoolField(JsonObject fields, Object entry, String key, String... getters) {
        for (String g : getters) {
            try {
                Object r = entry.getClass().getMethod(g).invoke(entry);
                if (r instanceof Boolean) {
                    fields.addProperty(key, r.toString());
                    return;
                }
            } catch (Exception ignored) {}
        }
    }

    /**
     * Emit a numeric field, formatting whole doubles without a trailing ".0"
     * (BattleScribe stores values as doubles, but specs read "2" not "2.0").
     * Non-numeric returns (e.g. Modifier.getValue() returns a String) are emitted as-is.
     */
    private void putNumField(JsonObject fields, Object entry, String getter, String key) {
        Method m;
        try {
            m = entry.getClass().getMethod(getter);
        } catch (NoSuchMethodException e) {
            return;
        }
        Object raw;
        try {
            raw = m.invoke(entry);
        } catch (Exception e) {
            return;
        }
        if (raw == null) return;
        if (raw instanceof Double) {
            fields.addProperty(key, formatNum((Double) raw));
        } else if (raw instanceof Number) {
            fields.addProperty(key, raw.toString());
        } else {
            String s = raw.toString();
            if (!s.isEmpty()) fields.addProperty(key, s);
        }
    }

    private static String formatNum(double d) {
        if (d == Math.floor(d) && !Double.isInfinite(d)) {
            return Long.toString((long) d);
        }
        return Double.toString(d);
    }

    private void addCostFields(JsonObject fields, Object entry) {
        List<Object> costs = getList(entry, "getCosts");
        if (costs == null) return;
        for (Object cost : costs) {
            String typeId = tryStr(cost, "getTypeId");
            Object value = invokeOrNull(cost, "getValue");
            if (typeId != null && !typeId.isEmpty() && value instanceof Double) {
                fields.addProperty("cost:" + typeId, formatNum((Double) value));
            }
        }
    }

    private void addCharacteristicFields(JsonObject fields, Object entry) {
        List<Object> chars = getList(entry, "getCharacteristics");
        if (chars == null) return;
        for (Object ch : chars) {
            String name = tryStr(ch, "getName");
            String value = tryStr(ch, "getValue");
            if (name != null && !name.isEmpty()) {
                fields.addProperty("char:" + name, value != null ? value : "");
            }
        }
    }

    private Object invokeOrNull(Object obj, String method) {
        try {
            return obj.getClass().getMethod(method).invoke(obj);
        } catch (Exception e) {
            return null;
        }
    }

    private String tryStr(Object obj, String method) {
        if (obj == null) return null;
        try { Object r = obj.getClass().getMethod(method).invoke(obj); return r != null ? r.toString() : null; }
        catch (Exception e) { return null; }
    }

    private boolean tryBool(Object obj, String... methods) {
        for (String m : methods) {
            try { Object r = obj.getClass().getMethod(m).invoke(obj); if (Boolean.TRUE.equals(r)) return true; }
            catch (Exception ignored) {}
        }
        return false;
    }

    @SuppressWarnings("unchecked")
    private List<Object> getList(Object obj, String method) {
        if (obj == null) return null;
        try {
            Object raw = obj.getClass().getMethod(method).invoke(obj);
            if (raw == null) return null;
            // Handles java.util.List subclasses
            return new ArrayList<>((java.util.Collection<Object>) raw);
        } catch (Exception e) { return null; }
    }

    private Object tryInvoke(Object obj, String method) {
        try { return obj.getClass().getMethod(method).invoke(obj); }
        catch (Exception e) { return null; }
    }

    private Method getMethod(Class<?> cls, String name, Class<?>... types) {
        try { return cls.getMethod(name, types); }
        catch (NoSuchMethodException e) { throw new RuntimeException("Method not found: " + name, e); }
    }

    private Object invoke(Method m, Object obj, Object... args) {
        try { return m.invoke(obj, args); }
        catch (Exception e) { throw new RuntimeException("Invocation failed: " + m.getName(), e); }
    }

    private Method findLoadMethod(Class<?> cls) {
        while (cls != null && cls != Object.class) {
            for (Method m : cls.getDeclaredMethods()) {
                if ("a".equals(m.getName()) && m.getParameterCount() == 1
                        && m.getParameterTypes()[0].getName().contains("BaseRootEntry")) {
                    m.setAccessible(true);
                    return m;
                }
            }
            cls = cls.getSuperclass();
        }
        throw new RuntimeException("Could not find private a(BaseRootEntry) load method");
    }

    // ─── FX thread dispatch ───────────────────────────────────────────────────

    @FunctionalInterface
    private interface FxAction { void run() throws Exception; }

    private void runOnFx(FxAction action) {
        if (Platform.isFxApplicationThread()) {
            try { action.run(); } catch (RuntimeException e) { throw e; } catch (Exception e) { throw new RuntimeException(e); }
            return;
        }
        CompletableFuture<Void> f = new CompletableFuture<>();
        Platform.runLater(() -> {
            try { action.run(); f.complete(null); } catch (Exception e) { f.completeExceptionally(e); }
        });
        await(f);
    }

    private <T> T runOnFxGet(Callable<T> action) {
        if (Platform.isFxApplicationThread()) {
            try { return action.call(); } catch (RuntimeException e) { throw e; } catch (Exception e) { throw new RuntimeException(e); }
        }
        CompletableFuture<T> f = new CompletableFuture<>();
        Platform.runLater(() -> {
            try { f.complete(action.call()); } catch (Exception e) { f.completeExceptionally(e); }
        });
        return await(f);
    }

    private <T> T await(CompletableFuture<T> f) {
        try {
            return f.get(FX_TIMEOUT_MS, TimeUnit.MILLISECONDS);
        } catch (TimeoutException e) {
            throw new RuntimeException("FX thread timed out after " + FX_TIMEOUT_MS + "ms");
        } catch (java.util.concurrent.ExecutionException e) {
            Throwable c = e.getCause();
            if (c instanceof RuntimeException) throw (RuntimeException) c;
            throw new RuntimeException(c);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException("Interrupted", e);
        }
    }

    // ─── Misc ─────────────────────────────────────────────────────────────────

    private static String requireString(JsonObject p, String key) {
        JsonElement e = p.get(key);
        if (e == null || e.isJsonNull()) throw new IllegalArgumentException("Missing param: " + key);
        return e.getAsString();
    }

    private static String optString(JsonObject p, String key) {
        JsonElement e = p.get(key);
        return (e == null || e.isJsonNull()) ? null : e.getAsString();
    }

    private static void sleep(int ms) {
        try { Thread.sleep(ms); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
    }
}

