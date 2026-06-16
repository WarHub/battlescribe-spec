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

    @SuppressWarnings("unused")
    private final EngineAccessor engineAccessor;
    /** Cached controller — cleared on each new setup call. */
    private Object cachedController = null;

    public DataEditorActions(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
    }

    // ─── Dispatch ────────────────────────────────────────────────────────────

    public String dispatch(String method, String params) {
        JsonObject p = params != null && !params.isEmpty() && !params.equals("{}")
                ? new JsonParser().parse(params).getAsJsonObject()
                : new JsonObject();

        if ("editorLoadFilesAction".equals(method))   return loadFiles(p);
        if ("editorAddEntryAction".equals(method))    return addEntry(p);
        if ("editorRemoveEntryAction".equals(method)) return removeEntry(p);
        if ("editorSetFieldAction".equals(method))    return setField(p);
        if ("editorAddLinkAction".equals(method))     return addLink(p);
        if ("editorGetDataState".equals(method))      return getDataState(p);
        throw new IllegalArgumentException("Unknown data editor action: " + method);
    }

    // ─── Actions ─────────────────────────────────────────────────────────────

    private String loadFiles(JsonObject params) {
        cachedController = null; // reset cache on new load
        String gstPath = requireString(params, "gstPath");
        JsonArray catPathsArr = params.has("catPaths") ? params.get("catPaths").getAsJsonArray() : new JsonArray();

        Object ctrl = findController();
        Object dataSource = runOnFxGet(() -> ctrl.getClass().getMethod("getDataSource").invoke(ctrl));

        Object flatEntry;
        if (catPathsArr.size() > 0) {
            String catPath = catPathsArr.get(0).getAsString();
            Method f = getMethod(dataSource.getClass(), "f", String.class, boolean.class);
            flatEntry = invoke(f, dataSource, catPath, false);
        } else {
            Method c = getMethod(dataSource.getClass(), "c", String.class, boolean.class);
            flatEntry = invoke(c, dataSource, gstPath, false);
        }
        if (flatEntry == null) throw new RuntimeException("Data source returned null for the given path");

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
        return "{}";
    }

    private String addEntry(JsonObject params) {
        String parentId = requireString(params, "parentId");
        String entryType = requireString(params, "entryType");
        String name = optString(params, "name");

        Object ctrl = findController();
        TreeItem<Object> parentItem = runOnFxGet(() -> findTreeItemById(ctrl, parentId));
        if (parentItem == null) throw new RuntimeException("Tree item not found: " + parentId);

        // Snapshot the full tree — the tree may have container nodes with no IDs,
        // and may be rebuilt after the add operation, making parentItem stale.
        Set<String> before = runOnFxGet(() -> subtreeIds(getTreeView(ctrl).getRoot()));
        boolean parentIsRoot = runOnFxGet(() -> isRootEntry(parentItem.getValue()));
        String addMethod = actAddMethodName(entryType, parentIsRoot);
        runOnFx(() -> selectItem(ctrl, parentItem));
        sleep(500);
        runOnFx(() -> invokeCtrl(ctrl, addMethod));

        String newId = waitForNewIdInTree(ctrl, before);
        if (name != null) setFieldOnEntry(ctrl, newId, "name", name);

        JsonObject result = new JsonObject();
        result.addProperty("entryId", newId);
        return result.toString();
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

    private String addLink(JsonObject params) {
        String parentId = requireString(params, "parentId");
        String linkType = requireString(params, "linkType");
        String targetId = requireString(params, "targetId");

        Object ctrl = findController();
        TreeItem<Object> parentItem = runOnFxGet(() -> findTreeItemById(ctrl, parentId));
        if (parentItem == null) throw new RuntimeException("Parent tree item not found: " + parentId);

        Set<String> before = runOnFxGet(() -> subtreeIds(getTreeView(ctrl).getRoot()));
        String addMethod = actAddMethodName(linkType);
        runOnFx(() -> selectItem(ctrl, parentItem));
        sleep(500);
        runOnFx(() -> invokeCtrl(ctrl, addMethod));

        String linkId = waitForNewIdInTree(ctrl, before);
        setFieldOnEntry(ctrl, linkId, "targetId", targetId);

        JsonObject result = new JsonObject();
        result.addProperty("entryId", linkId);
        return result.toString();
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
        return tree == null ? null : findItemRecursive(tree.getRoot(), id);
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

    private void setFieldOnEntry(Object ctrl, String entryId, String field, String value) {
        TreeItem<Object> item = runOnFxGet(() -> findTreeItemById(ctrl, entryId));
        if (item == null) throw new RuntimeException("Tree item not found for setField: " + entryId);
        runOnFx(() -> selectItem(ctrl, item));
        sleep(200);

        String cssId = fieldToCssId(field);
        VBox pnl = runOnFxGet(() -> (VBox) ctrl.getClass().getMethod("getPnlEditor").invoke(ctrl));

        // Try to find the field in the edit panel (2s grace period).
        // Fields that are not displayed in the panel fall back to reflective model mutation.
        Node node = waitForFieldNodeOptional(pnl, cssId, 2000);
        if (node != null) {
            runOnFx(() -> setNodeValue(node, cssId, value));
        } else {
            setFieldReflectively(item.getValue(), field, value);
        }
        sleep(200);
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
                // Items may be domain objects (match by id, e.g. cboDefaultSelection holds
                // INamed entries) or plain values/enums (match by display text, e.g. cboType).
                if (value.equals(tryStr(cbItem, "getId")) || cbItem.toString().equalsIgnoreCase(value)) {
                    match = cbItem;
                    break;
                }
            }
            cb.setValue(match != null ? match : value);
            // Writeback is also via the combo's onAction handler — fire it after setValue.
            cb.fireEvent(new javafx.event.ActionEvent());
        } else {
            throw new RuntimeException("Field #" + cssId + " has unsupported type: " + node.getClass().getSimpleName());
        }
    }

    /**
     * Set a field directly on the model object via reflection.
     * Used when the field has no corresponding edit panel control.
     */
    private void setFieldReflectively(Object modelObj, String field, String value) {
        if (modelObj == null) throw new RuntimeException("Model object is null for reflective setField");
        String setterName = "set" + Character.toUpperCase(field.charAt(0)) + field.substring(1);
        Class<?> cls = modelObj.getClass();
        while (cls != null && cls != Object.class) {
            for (Method m : cls.getDeclaredMethods()) {
                if (m.getName().equals(setterName) && m.getParameterCount() == 1) {
                    m.setAccessible(true);
                    try {
                        m.invoke(modelObj, coerceValue(m.getParameterTypes()[0], value));
                        return;
                    } catch (Exception e) {
                        throw new RuntimeException("Reflective setField failed for " + field, e);
                    }
                }
            }
            cls = cls.getSuperclass();
        }
        throw new RuntimeException("No setter found for field: " + field + " on " + modelObj.getClass().getName());
    }

    private static Object coerceValue(Class<?> type, String value) {
        if (value == null) return null;
        if (type == String.class) return value;
        if (type == boolean.class || type == Boolean.class) return Boolean.parseBoolean(value);
        if (type == int.class || type == Integer.class) return Integer.parseInt(value);
        return value;
    }

    private static String fieldToCssId(String field) {
        if ("name".equals(field))       return "txtName";
        if ("id".equals(field))         return "txtUniqueId";
        if ("targetId".equals(field))   return "txtTargetId";
        if ("hidden".equals(field))     return "chkHidden";
        if ("collective".equals(field)) return "chkCollective";
        if ("imported".equals(field))   return "chkImport";
        if ("type".equals(field))       return "cboType";
        if ("defaultSelectionEntryId".equals(field)) return "cboDefaultSelection";
        return "txt" + Character.toUpperCase(field.charAt(0)) + field.substring(1);
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
        if ("publication".equals(entryType))               return "actAddPublication";
        if ("catalogueLink".equals(entryType))             return "actAddCatalogueLink";
        throw new RuntimeException("Unknown entry type: " + entryType);
    }

    // ─── State serialization ──────────────────────────────────────────────────

    private JsonObject buildGameSystemJson(Object gs) {
        JsonObject o = new JsonObject();
        putStr(o, "id", gs, "getId");
        putStr(o, "name", gs, "getName");
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
        return o;
    }

    private JsonObject buildCatalogueJson(Object cat) {
        JsonObject o = new JsonObject();
        putStr(o, "id", cat, "getId");
        putStr(o, "name", cat, "getName");
        putStr(o, "gameSystemId", cat, "getGameSystemId");
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
        return o;
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
        putFieldIfPresent(fields, entry, "getTargetId", "targetId");
        putFieldIfPresent(fields, entry, "getPublicationId", "publicationId");
        putFieldIfPresent(fields, entry, "getPage", "page");
        putFieldIfPresent(fields, entry, "getDefaultSelectionEntryId", "defaultSelectionEntryId");
        putBoolField(fields, entry, "collective", "isCollective", "getCollective");
        putBoolField(fields, entry, "imported", "isImported", "getImported");
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
        addChildren(children, entry, "getForceEntries",           "forceEntry");
        addChildren(children, entry, "getCategoryEntries",        "categoryEntry");
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

