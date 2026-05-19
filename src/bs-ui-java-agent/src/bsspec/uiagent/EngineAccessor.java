package bsspec.uiagent;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.lang.instrument.Instrumentation;
import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Discovers and accesses the BattleScribe roster engine running in the same JVM.
 * Uses reflection since the engine classes are obfuscated.
 *
 * <p>Access path: find controller with a field of the engine type,
 * then call engine.a() to get the Roster.
 */
public class EngineAccessor {

    private final Instrumentation instrumentation;

    // Cached references
    private Object engineInstance;
    private Object controllerInstance;
    private Class<?> engineClass;
    private Class<?> rosterClass;
    private Method getRosterMethod;

    public EngineAccessor(Instrumentation instrumentation) {
        this.instrumentation = instrumentation;
    }

    /**
     * Returns the cached RosterEditorWindowController instance, or null if not yet discovered.
     */
    public Object getControllerInstance() {
        return controllerInstance;
    }

    public Object getEngineInstance() {
        return engineInstance;
    }

    private static JsonObject parseParams(String paramsJson) {
        if (paramsJson == null || paramsJson.isEmpty()) {
            return new JsonObject();
        }

        JsonElement paramsValue = new JsonParser().parse(paramsJson);
        return paramsValue != null && paramsValue.isJsonObject() ? paramsValue.getAsJsonObject() : new JsonObject();
    }

    private static String getString(JsonObject params, String key, String defaultValue) {
        JsonElement value = params.get(key);
        return value != null && !value.isJsonNull() ? value.getAsString() : defaultValue;
    }

    /**
     * Attempts to find the engine instance.
     * Strategy: find the FXML controller via scene graph node properties,
     * then read its engine field.
     */
    public String findEngine() {
        if (engineInstance != null) {
            JsonObject response = new JsonObject();
            response.addProperty("found", true);
            response.addProperty("engineClass", engineClass.getName());
            response.addProperty("cached", true);
            return response.toString();
        }

        engineClass = findClass("net.battlescribe.engine.a.f");
        if (engineClass == null) {
            JsonObject response = new JsonObject();
            response.addProperty("found", false);
            response.addProperty("error", "Engine class net.battlescribe.engine.a.f not loaded");
            return response.toString();
        }

        List<String> tried = new ArrayList<>();

        Class<?> controllerClass = findClass("net.battlescribe.desktop.rostereditor.RosterEditorWindowController");

        // Strategy 1: Extract controller from event handler on a known FXML button.
        // FXML-bound handlers are lambdas/anonymous classes that capture the controller.
        javafx.scene.Scene scene = findMainScene();
        if (scene != null && controllerClass != null) {
            tried.add("scene:found");
            // Find a button with known fx:id and get its onAction handler
            javafx.scene.Node btnNode = scene.getRoot().lookup("#btnNewRoster");
            if (btnNode instanceof javafx.scene.control.ButtonBase) {
                javafx.event.EventHandler<?> handler =
                        ((javafx.scene.control.ButtonBase) btnNode).getOnAction();
                if (handler != null) {
                    tried.add("handler_type:" + handler.getClass().getName());
                    // The handler is typically a lambda or inner class that captures 'this'
                    // (the controller). Walk its fields to find the controller instance.
                    Object controller = extractControllerFromHandler(handler, controllerClass);
                    if (controller != null) {
                        tried.add("controller:found_via_handler");
                        controllerInstance = controller;
                        Object eng = readEngineFromController(controller, controllerClass);
                        if (eng != null) {
                            engineInstance = eng;
                            cacheRosterAccess();
                            patchEngineThreadCount(eng);
                            JsonObject response = new JsonObject();
                            response.addProperty("found", true);
                            response.addProperty("engineClass", engineClass.getName());
                            response.addProperty("via", "handler.controller.b");
                            return response.toString();
                        }
                        tried.add("engine_field_null_on_controller");
                    }
                } else {
                    tried.add("no_onAction_handler");
                }
            } else {
                tried.add("btnNewRoster_not_found");
            }

            // Strategy 2: Check node properties/userData (fallback)
            javafx.scene.Parent root = scene.getRoot();
            Object controller = findControllerFromNode(root, controllerClass);
            if (controller != null) {
                tried.add("controller:found_via_properties");
                controllerInstance = controller;
                Object eng = readEngineFromController(controller, controllerClass);
                if (eng != null) {
                    engineInstance = eng;
                    cacheRosterAccess();
                    patchEngineThreadCount(eng);
                    JsonObject response = new JsonObject();
                    response.addProperty("found", true);
                    response.addProperty("engineClass", engineClass.getName());
                    response.addProperty("via", "controller.b");
                    return response.toString();
                }
                tried.add("engine_field_null");
            }
        }

        // Strategy 3: Scan for static engine references
        for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
            String name = cls.getName();
            if (!name.startsWith("net.battlescribe")) continue;
            for (Field f : cls.getDeclaredFields()) {
                if (Modifier.isStatic(f.getModifiers()) && engineClass.isAssignableFrom(f.getType())) {
                    try {
                        f.setAccessible(true);
                        Object eng = f.get(null);
                        if (eng != null) {
                            engineInstance = eng;
                            cacheRosterAccess();
                            patchEngineThreadCount(eng);
                            JsonObject response = new JsonObject();
                            response.addProperty("found", true);
                            response.addProperty("engineClass", engineClass.getName());
                            response.addProperty("via", "static:" + name + "." + f.getName());
                            return response.toString();
                        }
                    } catch (Exception e) {
                        tried.add("static_error:" + e.getMessage());
                    }
                }
            }
        }

        JsonObject response = new JsonObject();
        response.addProperty("found", false);
        response.add("tried", toJsonArray(tried));
        return response.toString();
    }

    /**
     * Extracts the controller instance from a FXML event handler.
     * Path: ControllerMethodEventHandler.handler → MethodHandler.controller → the controller.
     */
    private Object extractControllerFromHandler(Object handler, Class<?> controllerClass) {
        return findInstanceInObjectTree(handler, controllerClass, 4);
    }

    /**
     * Recursively searches an object's fields for an instance of the target class.
     */
    private Object findInstanceInObjectTree(Object obj, Class<?> targetClass, int maxDepth) {
        if (obj == null || maxDepth <= 0) return null;
        if (targetClass.isInstance(obj)) return obj;

        Class<?> cls = obj.getClass();
        while (cls != null && cls != Object.class) {
            for (Field f : cls.getDeclaredFields()) {
                if (f.getType().isPrimitive()) continue;
                if (Modifier.isStatic(f.getModifiers())) continue;
                try {
                    f.setAccessible(true);
                    Object val = f.get(obj);
                    if (val == null) continue;
                    if (targetClass.isInstance(val)) return val;
                    // Go deeper for non-JDK-core types
                    String vName = val.getClass().getName();
                    if (vName.startsWith("javafx.fxml") || vName.startsWith("net.battlescribe")) {
                        Object found = findInstanceInObjectTree(val, targetClass, maxDepth - 1);
                        if (found != null) return found;
                    }
                } catch (Exception e) {
                    // ignore
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    /**
     * Reads the engine instance from a controller object.
     */
    private Object readEngineFromController(Object controller, Class<?> controllerClass) {
        try {
            Field engineField = controllerClass.getDeclaredField("b");
            engineField.setAccessible(true);
            Object eng = engineField.get(controller);
            if (eng != null && engineClass != null && engineClass.isInstance(eng)) {
                return eng;
            }
        } catch (Exception e) {
            // field not found or not accessible
        }
        return null;
    }

    /**
     * Finds the controller instance associated with a JavaFX node tree.
     * Walks up the node hierarchy checking properties and userData.
     */
    private Object findControllerFromNode(javafx.scene.Node node, Class<?> controllerClass) {
        // Check node properties for controller key
        if (node != null && node.getProperties() != null) {
            for (Object key : node.getProperties().keySet()) {
                Object val = node.getProperties().get(key);
                if (controllerClass.isInstance(val)) {
                    return val;
                }
            }
            // Check userData
            if (controllerClass.isInstance(node.getUserData())) {
                return node.getUserData();
            }
        }

        // Recurse into children
        if (node instanceof javafx.scene.Parent) {
            for (javafx.scene.Node child : ((javafx.scene.Parent) node).getChildrenUnmodifiable()) {
                Object found = findControllerFromNode(child, controllerClass);
                if (found != null) return found;
            }
        }

        // If not found via properties, try a different approach:
        // find a node with known fx:id, then match it against controller fields
        return null;
    }

    /**
     * Finds the main Roster Editor scene.
     */
    private javafx.scene.Scene findMainScene() {
        for (javafx.stage.Window w : javafx.stage.Window.getWindows()) {
            if (w instanceof javafx.stage.Stage) {
                javafx.stage.Stage s = (javafx.stage.Stage) w;
                if (s.getTitle() != null && s.getTitle().contains("Roster Editor")) {
                    return s.getScene();
                }
            }
        }
        return null;
    }

    /**
     * Set engine's thread count to 1 to prevent multi-threaded validation.
     * The Desktop app uses 8 threads which can cause issues when we call engine
     * methods from outside the normal FX event loop.
     */
    private void patchEngineThreadCount(Object engine) {
        try {
            // Dump all int fields in the engine class for diagnostics
            Class<?> cls = engine.getClass();
            System.err.println("[agent] Engine class: " + cls.getName() + " (superclass: " + cls.getSuperclass().getName() + ")");
            for (Field f : cls.getDeclaredFields()) {
                if (f.getType() == int.class) {
                    f.setAccessible(true);
                    System.err.println("[agent]   int field '" + f.getName() + "' = " + f.getInt(engine));
                }
            }
            // Also check superclass int fields
            for (Field f : cls.getSuperclass().getDeclaredFields()) {
                if (f.getType() == int.class) {
                    f.setAccessible(true);
                    System.err.println("[agent]   super int field '" + f.getName() + "' = " + f.getInt(engine));
                }
            }

            // The thread count is a private final int field 'a' in the engine class (net.battlescribe.engine.a.f)
            Field threadCountField = cls.getDeclaredField("a");
            threadCountField.setAccessible(true);
            int current = threadCountField.getInt(engine);
            if (current > 1) {
                // Use sun.misc.Unsafe to bypass final field restriction
                Field unsafeField = sun.misc.Unsafe.class.getDeclaredField("theUnsafe");
                unsafeField.setAccessible(true);
                sun.misc.Unsafe unsafe = (sun.misc.Unsafe) unsafeField.get(null);
                long offset = unsafe.objectFieldOffset(threadCountField);
                unsafe.putInt(engine, offset, 1);
                int verify = threadCountField.getInt(engine);
                System.err.println("[agent] Patched engine thread count: " + current + " -> " + verify);
            } else {
                System.err.println("[agent] Engine thread count already " + current + ", no patch needed");
            }
        } catch (Exception e) {
            System.err.println("[agent] Could not patch thread count: " + e.getClass().getSimpleName() + ": " + e.getMessage());
            e.printStackTrace(System.err);
        }
    }

    /**
     * Reads the current roster state as JSON.
     * Requires the engine to have been found first.
     */
    public String getRosterState() {
        if (engineInstance == null) {
            return errorJson("Engine not found. Call findEngine first.");
        }

        try {
            Object roster = getRosterMethod.invoke(engineInstance);
            if (roster == null) {
                return errorJson("No roster loaded");
            }
            return serializeRoster(roster).toString();
        } catch (Exception e) {
            return errorJson(buildExceptionMessage(e));
        }
    }

    public String setRosterName(String params) {
        JsonObject paramsObject = parseParams(params);
        if (engineInstance == null) {
            return errorJson("Engine not found. Call findEngine first.");
        }
        try {
            String name = getString(paramsObject, "name", null);
            if (name == null || name.isEmpty()) {
                return errorJson("Missing 'name' parameter.");
            }
            Object roster = getRosterMethod.invoke(engineInstance);
            if (roster == null) {
                return errorJson("No roster loaded.");
            }
            Method setNameMethod = roster.getClass().getMethod("setName", String.class);
            setNameMethod.invoke(roster, name);
            return jsonBooleanResult("set", true);
        } catch (Exception e) {
            return errorJson("setRosterName: " + e.getMessage());
        }
    }

    /**
     * Exports the current roster as BattleScribe XML (.ros format).
     * Uses the DataUtils serializer (net.battlescribe.a.c.e) method:
     *   public static void a(Roster roster, OutputStream outputStream)
     */
    public String exportRosterXml() {
        if (engineInstance == null) {
            return errorJson("Engine not found. Call findEngine first.");
        }
        try {
            Object roster = getRosterMethod.invoke(engineInstance);
            if (roster == null) {
                return errorJson("No roster loaded.");
            }
            // Find DataUtils class (net.battlescribe.a.c.e)
            Class<?> dataUtilsClass = Class.forName("net.battlescribe.a.c.e");
            // Find the write method: public static void a(Roster, OutputStream)
            Class<?> rosterClass = roster.getClass();
            Method writeMethod = null;
            for (Method m : dataUtilsClass.getDeclaredMethods()) {
                if (m.getName().equals("a")
                        && m.getParameterCount() == 2
                        && m.getParameterTypes()[0].isAssignableFrom(rosterClass)
                        && m.getParameterTypes()[1] == java.io.OutputStream.class
                        && m.getReturnType() == void.class) {
                    writeMethod = m;
                    break;
                }
            }
            if (writeMethod == null) {
                return errorJson("DataUtils write method not found.");
            }
            java.io.ByteArrayOutputStream baos = new java.io.ByteArrayOutputStream(65536);
            writeMethod.invoke(null, roster, baos);
            String xml = baos.toString("UTF-8");
            JsonObject response = new JsonObject();
            response.addProperty("xml", xml);
            return response.toString();
        } catch (Exception e) {
            return errorJson("exportRosterXml: " + buildExceptionMessage(e));
        }
    }

    public String getValidationErrors() {
        if (engineInstance == null) {
            return errorJson("Engine not found. Call findEngine first.");
        }

        try {
            Object roster = getCurrentRoster();
            JsonArray errors = new JsonArray();
            if (roster == null) {
                return errors.toString();
            }

            collectValidationErrors(roster, "roster", errors);
            for (Object force : toJavaList(callListGetter(roster, "getForces"))) {
                collectForceValidationErrors(force, errors);
            }
            return errors.toString();
        } catch (Exception e) {
            return errorJson("getValidationErrors failed: " + buildExceptionMessage(e));
        }
    }

    /**
     * Reads roster state including forces, selections, costs.
     */
    private JsonObject serializeRoster(Object roster) throws Exception {
        JsonObject result = new JsonObject();
        result.addProperty("name", callGetter(roster, "getName"));
        result.addProperty("gameSystemId", callGetter(roster, "getGameSystemId"));
        result.addProperty("gameSystemName", callGetter(roster, "getGameSystemName"));
        result.add("costs", serializeCostList(callListGetter(roster, "getCosts")));
        result.add("costLimits", serializeCostList(callListGetter(roster, "getCostLimits")));
        result.add("forces", serializeForceList(callListGetter(roster, "getForces")));
        return result;
    }

    private JsonArray serializeForceList(Object list) throws Exception {
        JsonArray forces = new JsonArray();
        if (list == null) return forces;
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            Object force = list.getClass().getMethod("get", int.class).invoke(list, i);
            forces.add(serializeForce(force));
        }
        return forces;
    }

    private JsonObject serializeForce(Object force) throws Exception {
        JsonObject result = new JsonObject();
        result.addProperty("id", callGetter(force, "getId"));
        result.addProperty("name", callGetter(force, "getName"));
        result.addProperty("catalogueId", callGetter(force, "getCatalogueId"));
        result.addProperty("entryId", callGetter(force, "getEntryId"));
        result.addProperty("catalogueName", callGetter(force, "getCatalogueName"));
        result.addProperty("customName", callGetter(force, "getCustomName"));
        result.addProperty("customNotes", callGetter(force, "getCustomNotes"));
        result.addProperty("hidden", resolveForceHidden(force));
        result.addProperty("publicationId", callGetter(force, "getPublicationId"));
        result.addProperty("page", callGetter(force, "getPage"));

        Object ruleList = callListGetter(force, "getRules");
        result.add("rules", serializeRuleList(ruleList));

        Object catList = callListGetter(force, "getCategories");
        result.add("categories", serializeCategoryList(catList));

        Object pubList = callListGetter(force, "getPublications");
        result.add("publications", serializePublicationList(pubList));

        Map<String, String> pubNameMap = new HashMap<String, String>();
        if (pubList != null) {
            int pubSize = (int) pubList.getClass().getMethod("size").invoke(pubList);
            for (int i = 0; i < pubSize; i++) {
                Object pub = pubList.getClass().getMethod("get", int.class).invoke(pubList, i);
                String pubId = callGetter(pub, "getId");
                String pubName = callGetter(pub, "getName");
                if (pubId != null && pubName != null) {
                    pubNameMap.put(pubId, pubName);
                }
            }
        }

        result.add("selections", serializeSelectionList(callListGetter(force, "getSelections"), pubNameMap, force));
        result.add("childForces", serializeForceList(callListGetter(force, "getForces")));

        if (catList == null) {
            result.addProperty("_debug_class", force.getClass().getName());
            StringBuilder methods = new StringBuilder();
            for (Method m : force.getClass().getMethods()) {
                if (m.getName().startsWith("get") && m.getParameterCount() == 0) {
                    if (methods.length() > 0) methods.append(",");
                    methods.append(m.getName());
                }
            }
            result.addProperty("_debug_methods", methods.toString());
        }

        return result;
    }

    private JsonArray serializeSelectionList(Object list, Map<String, String> pubNameMap, Object force) throws Exception {
        JsonArray selections = new JsonArray();
        if (list == null) return selections;
        int size = (int) list.getClass().getMethod("size").invoke(list);
        List<Object> items = new ArrayList<Object>(size);
        for (int i = 0; i < size; i++) {
            items.add(list.getClass().getMethod("get", int.class).invoke(list, i));
        }
        items.sort((a, b) -> {
            try {
                String nameA = String.valueOf(callGetter(a, "getName"));
                String nameB = String.valueOf(callGetter(b, "getName"));
                return nameA.compareToIgnoreCase(nameB);
            } catch (Exception e) {
                return 0;
            }
        });
        for (Object item : items) {
            selections.add(serializeSelection(item, pubNameMap, force));
        }
        return selections;
    }

    private JsonObject serializeSelection(Object sel, Map<String, String> pubNameMap, Object force) throws Exception {
        JsonObject result = new JsonObject();
        result.addProperty("id", callGetter(sel, "getId"));
        result.addProperty("name", callGetter(sel, "getName"));
        result.addProperty("entryId", callGetter(sel, "getEntryId"));
        result.addProperty("entryGroupId", callGetter(sel, "getEntryGroupId"));
        result.addProperty("page", callGetter(sel, "getPage"));
        String pubId = callGetter(sel, "getPublicationId");
        result.addProperty("publicationId", pubId);
        result.addProperty("publicationName", pubId != null ? pubNameMap.get(pubId) : null);
        result.addProperty("customName", callGetter(sel, "getCustomName"));
        result.addProperty("customNotes", callGetter(sel, "getCustomNotes"));
        result.add("categories", serializeCategoryList(callListGetter(sel, "getCategories")));
        result.add("profiles", serializeProfileList(callListGetter(sel, "getProfiles")));
        result.add("rules", serializeRuleList(callListGetter(sel, "getRules")));

        try {
            Method m = sel.getClass().getMethod("getType");
            Object type = m.invoke(sel);
            result.addProperty("type", type != null ? type.toString().toLowerCase() : null);
        } catch (Exception e) {
            result.add("type", JsonNull.INSTANCE);
        }

        try {
            Method m = sel.getClass().getMethod("getNumber");
            Object number = m.invoke(sel);
            if (number instanceof Number) {
                result.addProperty("number", (Number) number);
            } else {
                result.addProperty("number", 1);
            }
        } catch (Exception e) {
            result.addProperty("number", 1);
        }

        result.addProperty("hidden", resolveSelectionHidden(force, sel));
        result.add("costs", serializeCostList(callListGetter(sel, "getCosts")));
        result.add("children", serializeSelectionList(callListGetter(sel, "getSelections"), pubNameMap, force));
        return result;
    }

    private JsonArray serializeCostList(Object list) throws Exception {
        JsonArray costs = new JsonArray();
        if (list == null) return costs;
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            Object cost = list.getClass().getMethod("get", int.class).invoke(list, i);
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(cost, "getName"));
            item.addProperty("typeId", callGetter(cost, "getTypeId"));
            try {
                Method m = cost.getClass().getMethod("getValue");
                Object value = m.invoke(cost);
                if (value instanceof Number) {
                    item.addProperty("value", (Number) value);
                } else {
                    item.addProperty("value", 0);
                }
            } catch (Exception e) {
                item.addProperty("value", 0);
            }
            costs.add(item);
        }
        return costs;
    }

    private JsonArray serializeCategoryList(Object list) throws Exception {
        JsonArray categoriesJson = new JsonArray();
        if (list == null) return categoriesJson;
        List<Object> categories = toJavaList(list);
        for (Object category : categories) {
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(category, "getName"));
            item.addProperty("entryId", callGetter(category, "getEntryId"));
            try {
                Method m = findMethod(category.getClass(), "isPrimary");
                item.addProperty("primary", m != null && Boolean.TRUE.equals(m.invoke(category)));
            } catch (Exception e) {
                item.addProperty("primary", false);
            }
            item.addProperty("customNotes", callGetter(category, "getCustomNotes"));
            item.addProperty("publicationId", callGetter(category, "getPublicationId"));
            item.addProperty("page", callGetter(category, "getPage"));
            categoriesJson.add(item);
        }
        return categoriesJson;
    }

    private JsonArray serializePublicationList(Object list) throws Exception {
        JsonArray publicationsJson = new JsonArray();
        if (list == null) return publicationsJson;
        List<Object> publications = toJavaList(list);
        for (Object publication : publications) {
            JsonObject item = new JsonObject();
            item.addProperty("id", callGetter(publication, "getId"));
            item.addProperty("name", callGetter(publication, "getName"));
            publicationsJson.add(item);
        }
        return publicationsJson;
    }

    private JsonArray serializeProfileList(Object list) throws Exception {
        JsonArray profilesJson = new JsonArray();
        if (list == null) return profilesJson;
        List<Object> profiles = toJavaList(list);
        for (Object profile : profiles) {
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(profile, "getName"));
            item.addProperty("typeId", callGetter(profile, "getTypeId"));
            item.addProperty("typeName", callGetter(profile, "getTypeName"));
            try {
                Method m = findMethod(profile.getClass(), "isHidden");
                item.addProperty("hidden", m != null && Boolean.TRUE.equals(m.invoke(profile)));
            } catch (Exception e) {
                item.addProperty("hidden", false);
            }
            item.addProperty("page", callGetter(profile, "getPage"));
            item.addProperty("publicationId", callGetter(profile, "getPublicationId"));
            item.addProperty("publicationName", callGetter(profile, "getPublicationName"));
            item.add("characteristics", serializeCharacteristicList(callListGetter(profile, "getCharacteristics")));
            profilesJson.add(item);
        }
        return profilesJson;
    }

    private JsonArray serializeRuleList(Object list) throws Exception {
        JsonArray rulesJson = new JsonArray();
        if (list == null) return rulesJson;
        List<Object> rules = toJavaList(list);
        for (Object rule : rules) {
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(rule, "getName"));
            item.addProperty("description", callGetter(rule, "getDescription"));
            try {
                Method m = findMethod(rule.getClass(), "isHidden");
                item.addProperty("hidden", m != null && Boolean.TRUE.equals(m.invoke(rule)));
            } catch (Exception e) {
                item.addProperty("hidden", false);
            }
            item.addProperty("page", callGetter(rule, "getPage"));
            item.addProperty("publicationId", callGetter(rule, "getPublicationId"));
            item.addProperty("publicationName", callGetter(rule, "getPublicationName"));
            rulesJson.add(item);
        }
        return rulesJson;
    }

    private JsonArray serializeCharacteristicList(Object list) throws Exception {
        JsonArray characteristicsJson = new JsonArray();
        if (list == null) return characteristicsJson;
        List<Object> characteristics = toJavaList(list);
        for (Object characteristic : characteristics) {
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(characteristic, "getName"));
            item.addProperty("typeId", callGetter(characteristic, "getTypeId"));
            item.addProperty("value", callGetter(characteristic, "getValue"));
            characteristicsJson.add(item);
        }
        return characteristicsJson;
    }

    // --- Reflection helpers ---

    private Object getCurrentRoster() throws Exception {
        ensureEngineFound();
        return getRosterMethod != null ? getRosterMethod.invoke(engineInstance) : null;
    }

    private void ensureEngineFound() {
        if (engineInstance == null || engineClass == null || getRosterMethod == null) {
            throw new IllegalStateException("Engine not found. Call findEngine first.");
        }
    }

    private Object findAvailableEntryForParent(Object parent, String entryId) throws Exception {
        Class<?> selectionClass = findClass("net.battlescribe.model.roster.Selection");
        Class<?> selectionEntryClass = findClass("net.battlescribe.model.data.SelectionEntry");
        if (selectionEntryClass == null) {
            return null;
        }

        if (selectionClass != null && selectionClass.isInstance(parent)) {
            String parentEntryId = callGetter(parent, "getEntryId");
            Object parentEntry = findEntryById(parentEntryId);
            return parentEntry != null ? findObjectById(selectionEntryClass, entryId, parentEntry) : null;
        }

        Method method = findMethod(engineClass, "a", new Class<?>[] { parent.getClass() }, List.class);
        if (method == null) {
            return null;
        }
        Object entries = method.invoke(engineInstance, parent);
        return findObjectById(selectionEntryClass, entryId, entries);
    }

    private Object findEntryById(String entryId) throws Exception {
        Class<?> selectionEntryClass = findClass("net.battlescribe.model.data.SelectionEntry");
        if (selectionEntryClass == null) {
            return null;
        }

        Object roster = getCurrentRoster();
        if (roster != null) {
            for (Object force : toJavaList(callListGetter(roster, "getForces"))) {
                Object found = findEntryByIdInForce(force, entryId, selectionEntryClass);
                if (found != null) {
                    return found;
                }
            }
        }

        return findObjectById(selectionEntryClass, entryId, engineInstance, roster);
    }

    private Object findEntryByIdInForce(Object force, String entryId, Class<?> selectionEntryClass) throws Exception {
        Object forceContext = getForceContext(force);
        if (forceContext != null) {
            Method method = findMethod(forceContext.getClass(), "i", new Class<?>[] { String.class }, Object.class);
            if (method != null) {
                for (String candidate : candidateIds(entryId)) {
                    Object found = method.invoke(forceContext, candidate);
                    if (selectionEntryClass.isInstance(found)) {
                        return found;
                    }
                }
            }
        }

        Object available = findAvailableEntryForParent(force, entryId);
        if (available != null) {
            return available;
        }

        for (Object childForce : toJavaList(callListGetter(force, "getForces"))) {
            Object found = findEntryByIdInForce(childForce, entryId, selectionEntryClass);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private Object findCostTypeById(String costTypeId) throws Exception {
        Class<?> costTypeClass = findClass("net.battlescribe.model.data.CostType");
        if (costTypeClass == null) {
            return null;
        }
        return findObjectById(costTypeClass, costTypeId, engineInstance, getCurrentRoster());
    }

    private Object getCurrentGameSystem() throws Exception {
        Object roster = getCurrentRoster();
        Object gameSystem = callGetterObject(roster, "getGameSystem");
        if (gameSystem != null) {
            return gameSystem;
        }
        // Try exact type name match on engine instance fields
        gameSystem = findFieldValueByTypeName(engineInstance, "net.battlescribe.model.data.GameSystem");
        if (gameSystem != null) {
            return gameSystem;
        }
        // Search controller fields (controller manages the data model)
        if (controllerInstance != null) {
            gameSystem = findFieldValueByTypeName(controllerInstance, "net.battlescribe.model.data.GameSystem");
            if (gameSystem != null) {
                return gameSystem;
            }
            // Broader search on controller
            gameSystem = findFieldValueByTypeNameContains(controllerInstance, "GameSystem");
            if (gameSystem != null) {
                return gameSystem;
            }
            // Search all fields of controller recursively (1 level deep)
            Class<?> cls = controllerInstance.getClass();
            while (cls != null && cls != Object.class) {
                for (java.lang.reflect.Field field : cls.getDeclaredFields()) {
                    try {
                        field.setAccessible(true);
                        Object value = field.get(controllerInstance);
                        if (value != null && !isLeafValue(value)) {
                            Object gs = findFieldValueByTypeNameContains(value, "GameSystem");
                            if (gs != null) {
                                return gs;
                            }
                        }
                    } catch (Exception e) { /* ignore */ }
                }
                cls = cls.getSuperclass();
            }
        }
        // Try via CatalogueManager
        Object catMgr = getCatalogueManager();
        if (catMgr != null) {
            gameSystem = findFieldValueByTypeNameContains(catMgr, "GameSystem");
            if (gameSystem != null) {
                return gameSystem;
            }
        }
        // Last resort: get gameSystemId from roster and BFS from engine/controller
        String gsId = callGetter(roster, "getGameSystemId");
        if (gsId != null && controllerInstance != null) {
            // Search for an object with matching getId()
            gameSystem = findObjectWithId(controllerInstance, gsId, "GameSystem");
            if (gameSystem != null) {
                return gameSystem;
            }
        }
        return null;
    }

    private Object findObjectWithId(Object root, String targetId, String typeHint) {
        if (root == null) return null;
        Set<Object> visited = Collections.newSetFromMap(new IdentityHashMap<Object, Boolean>());
        ArrayDeque<Object> queue = new ArrayDeque<>();
        queue.add(root);
        while (!queue.isEmpty()) {
            Object current = queue.removeFirst();
            if (current == null || !visited.add(current) || isLeafValue(current)) continue;
            // Check if this object has getId matching and class contains hint
            if (current.getClass().getName().contains(typeHint)) {
                String id = callGetter(current, "getId");
                if (targetId.equals(id)) {
                    return current;
                }
            }
            // Expand: handle collections specially
            if (visited.size() > 10000) break;
            if (current instanceof java.util.Collection) {
                for (Object item : (java.util.Collection<?>) current) {
                    if (item != null && !visited.contains(item)) queue.add(item);
                }
            } else if (current instanceof java.util.Map) {
                for (Object value : ((java.util.Map<?,?>) current).values()) {
                    if (value != null && !visited.contains(value)) queue.add(value);
                }
            } else {
                // Expand fields
                Class<?> cls = current.getClass();
                String className = cls.getName();
                // Only traverse net.battlescribe classes and java.util classes
                if (!className.startsWith("net.battlescribe.") && !className.startsWith("java.util.")) continue;
                while (cls != null && cls != Object.class) {
                    for (java.lang.reflect.Field field : cls.getDeclaredFields()) {
                        try {
                            field.setAccessible(true);
                            Object value = field.get(current);
                            if (value != null && !isLeafValue(value) && !visited.contains(value)) {
                                queue.add(value);
                            }
                        } catch (Exception e) { /* ignore */ }
                    }
                    cls = cls.getSuperclass();
                }
            }
        }
        return null;
    }

    private Object findFieldValueByTypeNameContains(Object target, String typeFragment) {
        if (target == null) return null;
        Class<?> cls = target.getClass();
        while (cls != null && cls != Object.class) {
            for (java.lang.reflect.Field field : cls.getDeclaredFields()) {
                try {
                    field.setAccessible(true);
                    Object value = field.get(target);
                    if (value != null && value.getClass().getName().contains(typeFragment)) {
                        return value;
                    }
                } catch (Exception e) { /* ignore */ }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private Object getCatalogueManager() {
        try {
            Method method = findMethod(engineClass, "d", new Class<?>[0], Object.class);
            if (method != null) {
                Object manager = method.invoke(engineInstance);
                if (manager != null && "net.battlescribe.engine.a.d".equals(manager.getClass().getName())) {
                    return manager;
                }
            }
        } catch (Exception e) {
            // Fall back to field scan below.
        }
        return findFieldValueByTypeName(engineInstance, "net.battlescribe.engine.a.d");
    }

    private Object findFieldValueByTypeName(Object target, String typeName) {
        if (target == null || typeName == null) {
            return null;
        }
        Class<?> cls = target.getClass();
        while (cls != null && cls != Object.class) {
            for (Field field : cls.getDeclaredFields()) {
                try {
                    field.setAccessible(true);
                    Object value = field.get(target);
                    if (value != null && typeName.equals(value.getClass().getName())) {
                        return value;
                    }
                } catch (Exception e) {
                    // Ignore inaccessible fields and continue.
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private Object getForceContext(Object force) throws Exception {
        // Try exact type match first
        Method method = findMethod(engineClass, "e", new Class<?>[] { force.getClass() }, Object.class);
        if (method != null) {
            return method.invoke(engineInstance, force);
        }
        // Fallback: find "e" with 1 parameter, any type (the engine likely has only one such method)
        Class<?> c = engineClass;
        while (c != null && c != Object.class) {
            for (Method m : c.getDeclaredMethods()) {
                if ("e".equals(m.getName()) && m.getParameterCount() == 1
                        && !m.getReturnType().isPrimitive() && m.getReturnType() != Void.class) {
                    Class<?> paramType = m.getParameterTypes()[0];
                    if (paramType.isInstance(force)) {
                        m.setAccessible(true);
                        return m.invoke(engineInstance, force);
                    }
                }
            }
            c = c.getSuperclass();
        }
        return null;
    }

    private boolean resolveForceHidden(Object force) {
        try {
            Object forceContext = getForceContext(force);
            if (forceContext == null) {
                return false;
            }
            // Get original ForceEntry via forceContext.e(force.getEntryId())
            String forceEntryId = callGetter(force, "getEntryId");
            if (forceEntryId == null) {
                return false;
            }
            Method getForceEntryMethod = findMethod(forceContext.getClass(), "e", new Class<?>[] { String.class }, Object.class);
            if (getForceEntryMethod == null) {
                return false;
            }
            Object originalForceEntry = null;
            for (String candidate : candidateIds(forceEntryId)) {
                originalForceEntry = getForceEntryMethod.invoke(forceContext, candidate);
                if (originalForceEntry != null) break;
            }
            if (originalForceEntry == null) {
                return false;
            }
            // Resolve: engine.a(forceContext, force, originalEntry, true)
            Method resolveMethod = null;
            Class<?> ec = engineClass;
            while (ec != null && ec != Object.class) {
                for (Method m : ec.getDeclaredMethods()) {
                    if ("a".equals(m.getName()) && m.getParameterCount() == 4) {
                        Class<?>[] pts = m.getParameterTypes();
                        if (pts[0].isInstance(forceContext) && pts[1].isInstance(force)
                                && pts[2].isInstance(originalForceEntry) && pts[3] == boolean.class
                                && !m.getReturnType().isPrimitive()) {
                            m.setAccessible(true);
                            resolveMethod = m;
                            break;
                        }
                    }
                }
                if (resolveMethod != null) break;
                ec = ec.getSuperclass();
            }
            if (resolveMethod == null) {
                return false;
            }
            Object resolved = resolveMethod.invoke(engineInstance, forceContext, force, originalForceEntry, true);
            if (resolved != null) {
                Method isHidden = findMethod(resolved.getClass(), "isHidden", 0);
                if (isHidden != null) {
                    Object result = isHidden.invoke(resolved);
                    if (result instanceof Boolean) {
                        return ((Boolean) result).booleanValue();
                    }
                }
            }
        } catch (Exception e) {
            // fall through
        }
        return false;
    }

    private boolean resolveSelectionHidden(Object force, Object selection) {
        try {
            Object resolvedEntry = resolveModifiedSelectionEntry(force, selection);
            if (resolvedEntry != null) {
                Method isHiddenMethod = findMethod(resolvedEntry.getClass(), "isHidden", 0);
                if (isHiddenMethod != null) {
                    Object hidden = isHiddenMethod.invoke(resolvedEntry);
                    if (hidden instanceof Boolean) {
                        return ((Boolean) hidden).booleanValue();
                    }
                    if (hidden != null) {
                        return Boolean.parseBoolean(hidden.toString());
                    }
                }
                // Try "getHidden" or field "hidden" as fallbacks
                Method getHidden = findMethod(resolvedEntry.getClass(), "getHidden", 0);
                if (getHidden != null) {
                    Object hidden = getHidden.invoke(resolvedEntry);
                    if (hidden instanceof Boolean) {
                        return ((Boolean) hidden).booleanValue();
                    }
                }
            } else {
                String entryId = callGetter(selection, "getEntryId");
                System.err.println("[agent] resolveSelectionHidden: resolvedEntry is null for entryId=" + entryId);
            }
        } catch (Exception e) {
            System.err.println("[agent] resolveSelectionHidden error: " + e.getMessage());
        }

        try {
            Method method = selection.getClass().getMethod("isHidden");
            Object hidden = method.invoke(selection);
            if (hidden instanceof Boolean) {
                return ((Boolean) hidden).booleanValue();
            }
            return hidden != null && Boolean.parseBoolean(hidden.toString());
        } catch (Exception e) {
            return false;
        }
    }

    private Object resolveModifiedSelectionEntry(Object force, Object selection) throws Exception {
        if (force == null || engineInstance == null || engineClass == null) {
            return null;
        }

        Object forceContext = getForceContext(force);
        if (forceContext == null) {
            System.err.println("[agent] resolveModifiedEntry: forceContext null");
            return null;
        }

        String entryId = callGetter(selection, "getEntryId");
        if (entryId == null) {
            return null;
        }

        Method getEntryMethod = findMethod(forceContext.getClass(), "i", new Class<?>[] { String.class }, Object.class);
        if (getEntryMethod == null) {
            // Try broader search
            Class<?> fcClass = forceContext.getClass();
            while (fcClass != null && fcClass != Object.class) {
                for (Method m : fcClass.getDeclaredMethods()) {
                    if ("i".equals(m.getName()) && m.getParameterCount() == 1
                            && m.getParameterTypes()[0] == String.class
                            && !m.getReturnType().isPrimitive()) {
                        m.setAccessible(true);
                        getEntryMethod = m;
                        break;
                    }
                }
                if (getEntryMethod != null) break;
                fcClass = fcClass.getSuperclass();
            }
            if (getEntryMethod == null) {
                System.err.println("[agent] resolveModifiedEntry: no method 'i(String)' on " + forceContext.getClass().getName());
                return null;
            }
        }

        Object originalEntry = null;
        for (String candidate : candidateIds(entryId)) {
            originalEntry = getEntryMethod.invoke(forceContext, candidate);
            if (originalEntry != null) {
                break;
            }
        }
        if (originalEntry == null) {
            System.err.println("[agent] resolveModifiedEntry: originalEntry null for entryId=" + entryId);
            return null;
        }

        // Find engine.a(ForceContext, Selection, Entry, boolean) → resolved entry
        // The return type must be a data class (not roster Selection)
        Method resolveMethod = null;
        Class<?> ec = engineClass;
        while (ec != null && ec != Object.class) {
            for (Method m : ec.getDeclaredMethods()) {
                if ("a".equals(m.getName()) && m.getParameterCount() == 4) {
                    Class<?>[] pts = m.getParameterTypes();
                    if (pts[0].isInstance(forceContext) && pts[1].isInstance(selection)
                            && pts[2].isInstance(originalEntry) && pts[3] == boolean.class
                            && !m.getReturnType().isPrimitive()
                            && !m.getReturnType().getName().contains(".roster.")) {
                        m.setAccessible(true);
                        resolveMethod = m;
                        break;
                    }
                }
            }
            if (resolveMethod != null) break;
            ec = ec.getSuperclass();
        }
        if (resolveMethod == null) {
            System.err.println("[agent] resolveModifiedEntry: no method 'a(FC,Sel,Entry,bool)' on engine. "
                    + "FC=" + forceContext.getClass().getName()
                    + " Sel=" + selection.getClass().getName()
                    + " Entry=" + originalEntry.getClass().getName());
            return null;
        }
        return resolveMethod.invoke(engineInstance, forceContext, selection, originalEntry, true);
    }

    private static final class ValidationRef {
        private final String entryId;
        private final String constraintId;

        private ValidationRef(String entryId, String constraintId) {
            this.entryId = entryId;
            this.constraintId = constraintId;
        }
    }

    private Map<String, List<String>> parseValidationErrorIds(List<String> errorIds) {
        Map<String, List<String>> errorIdMap = new HashMap<String, List<String>>();
        for (String errorId : errorIds) {
            if (errorId == null) {
                continue;
            }
            String[] parts = errorId.split("::", 3);
            if (parts.length >= 3) {
                List<String> constraintIds = errorIdMap.get(parts[1]);
                if (constraintIds == null) {
                    constraintIds = new ArrayList<String>();
                    errorIdMap.put(parts[1], constraintIds);
                }
                constraintIds.add(parts[2]);
            }
        }
        return errorIdMap;
    }

    private ValidationRef resolveValidationRef(Map<String, List<String>> errorIdMap, String ownerType, String message, String ownerEntryId) {
        String lowerMessage = message != null ? message.toLowerCase() : null;

        if ("roster".equals(ownerType)) {
            ValidationRef rosterCostLimitRef = resolveRosterCostLimitRef(errorIdMap, lowerMessage);
            if (rosterCostLimitRef != null) {
                return rosterCostLimitRef;
            }
        }

        for (Map.Entry<String, List<String>> entry : errorIdMap.entrySet()) {
            String candidateEntryId = entry.getKey();
            if ("costLimits".equals(candidateEntryId)) {
                continue;
            }
            String entryName = getEntryName(candidateEntryId);
            if (containsIgnoreCase(message, entryName)) {
                return new ValidationRef(candidateEntryId, pickConstraintId(entry.getValue(), lowerMessage));
            }
        }

        // Hidden error: "(hidden)" in message → entryId from errorIdMap or ownerEntryId
        if (lowerMessage != null && lowerMessage.contains("(hidden)")) {
            for (Map.Entry<String, List<String>> entry : errorIdMap.entrySet()) {
                if (!"costLimits".equals(entry.getKey())) {
                    return new ValidationRef(entry.getKey(), "hidden");
                }
            }
            // Fallback: use ownerEntryId as the entry reference
            if (ownerEntryId != null) {
                return new ValidationRef(ownerEntryId, "hidden");
            }
        }

        // Cost limit fallback for roster errors
        if ("roster".equals(ownerType)) {
            List<String> constraintIds = errorIdMap.get("costLimits");
            if (constraintIds != null && !constraintIds.isEmpty()) {
                return new ValidationRef("costLimits", constraintIds.get(0));
            }
            // Fallback: if message contains "over" or "limit" and we can find cost type
            if (lowerMessage != null && (lowerMessage.contains("over") || lowerMessage.contains("limit"))) {
                String costTypeId = extractCostTypeIdFromMessage(lowerMessage);
                if (costTypeId != null) {
                    return new ValidationRef("costLimits", costTypeId);
                }
            }
        }

        return new ValidationRef(null, null);
    }

    private ValidationRef resolveRosterCostLimitRef(Map<String, List<String>> errorIdMap, String lowerMessage) {
        List<String> constraintIds = errorIdMap.get("costLimits");
        if (constraintIds == null || constraintIds.isEmpty()) {
            return null;
        }

        boolean looksLikeCostLimit = lowerMessage == null
                || lowerMessage.contains("over")
                || lowerMessage.contains("too much");
        if (!looksLikeCostLimit) {
            return null;
        }

        for (String constraintId : constraintIds) {
            String costTypeName = getCostTypeName(constraintId);
            if (costTypeName != null && containsIgnoreCase(lowerMessage, costTypeName.toLowerCase())) {
                return new ValidationRef("costLimits", constraintId);
            }
        }

        if (constraintIds.size() == 1) {
            return new ValidationRef("costLimits", constraintIds.get(0));
        }
        return null;
    }

    private String pickConstraintId(List<String> constraintIds, String lowerMessage) {
        if (constraintIds == null || constraintIds.isEmpty()) {
            return null;
        }
        if (lowerMessage != null && lowerMessage.contains("(hidden)")) {
            return "hidden";
        }
        return constraintIds.get(0);
    }

    private boolean containsIgnoreCase(String message, String candidate) {
        if (message == null || candidate == null || candidate.isEmpty()) {
            return false;
        }
        return message.toLowerCase().contains(candidate.toLowerCase());
    }

    private String getEntryName(String entryId) {
        try {
            Object entry = findEntryById(entryId);
            return entry != null ? callGetter(entry, "getName") : null;
        } catch (Exception e) {
            return null;
        }
    }

    private String getCostTypeName(String costTypeId) {
        try {
            Object costType = findCostTypeById(costTypeId);
            return costType != null ? callGetter(costType, "getName") : null;
        } catch (Exception e) {
            return null;
        }
    }

    private String extractCostTypeIdFromMessage(String lowerMessage) {
        // Search gameSystem costTypes for one whose name appears in the message
        try {
            Object gs = getCurrentGameSystem();
            if (gs == null) return null;
            Object costTypes = callListGetter(gs, "getCostTypes");
            if (costTypes == null) return null;
            for (Object ct : toJavaList(costTypes)) {
                String id = callGetter(ct, "getId");
                String name = callGetter(ct, "getName");
                if (name != null && lowerMessage.contains(name.toLowerCase())) {
                    return id;
                }
            }
        } catch (Exception e) {
            // ignore
        }
        return null;
    }

    private void collectForceValidationErrors(Object force, JsonArray errors) throws Exception {
        collectValidationErrors(force, "force", errors);
        for (Object category : toJavaList(callListGetter(force, "getCategories"))) {
            collectValidationErrors(category, "category", errors);
        }
        for (Object selection : toJavaList(callListGetter(force, "getSelections"))) {
            collectSelectionValidationErrors(selection, errors);
        }
        for (Object childForce : toJavaList(callListGetter(force, "getForces"))) {
            collectForceValidationErrors(childForce, errors);
        }
    }

    private void collectSelectionValidationErrors(Object selection, JsonArray errors) throws Exception {
        collectValidationErrors(selection, "selection", errors);
        for (Object category : toJavaList(callListGetter(selection, "getCategories"))) {
            collectValidationErrors(category, "category", errors);
        }
        for (Object child : toJavaList(callListGetter(selection, "getSelections"))) {
            collectSelectionValidationErrors(child, errors);
        }
    }

    private void collectValidationErrors(Object element, String ownerType, JsonArray errors)
            throws Exception {
        Object elementErrors = callListGetter(element, "getValidationErrors");
        Object errorIds = callListGetter(element, "getValidationErrorIds");
        List<String> errorIdList = extractStrings(errorIds);
        Map<String, List<String>> errorIdMap = parseValidationErrorIds(errorIdList);
        String ownerEntryId = callGetter(element, "getEntryId");
        for (Object error : toJavaList(elementErrors)) {
            String message = extractValidationMessage(error);
            ValidationRef validationRef = resolveValidationRef(errorIdMap, ownerType, message, ownerEntryId);
            JsonObject item = new JsonObject();
            item.addProperty("message", message);
            item.addProperty("ownerType", ownerType);
            item.addProperty("ownerId", callGetter(element, "getId"));
            if (ownerEntryId != null) {
                item.addProperty("ownerEntryId", ownerEntryId);
            }
            if (validationRef.entryId != null) {
                item.addProperty("entryId", validationRef.entryId);
            }
            if (validationRef.constraintId != null) {
                item.addProperty("constraintId", validationRef.constraintId);
            }
            if (!errorIdList.isEmpty()) {
                item.add("errorIds", toJsonArray(errorIdList));
            }
            errors.add(item);
        }
    }

    private String extractValidationMessage(Object error) {
        if (error == null) {
            return null;
        }
        try {
            Method method = findMethod(error.getClass(), "b", 0);
            if (method != null) {
                Object value = method.invoke(error);
                if (value != null) {
                    return value.toString();
                }
            }
        } catch (Exception e) {
            // fall back to toString
        }
        return error.toString();
    }

    private Object findObjectById(Class<?> targetClass, String id, Object... roots) throws Exception {
        if (targetClass == null || id == null) {
            return null;
        }

        Set<Object> visited = Collections.newSetFromMap(new IdentityHashMap<Object, Boolean>());
        ArrayDeque<Object> pending = new ArrayDeque<Object>();
        for (Object root : roots) {
            if (root != null) {
                pending.add(root);
            }
        }

        while (!pending.isEmpty()) {
            Object current = pending.removeFirst();
            if (current == null || isLeafValue(current) || !visited.add(current)) {
                continue;
            }

            if (targetClass.isInstance(current) && matchesId(callGetter(current, "getId"), id)) {
                return current;
            }
            if (visited.size() > 10000) {
                return null;
            }

            if (current instanceof Iterable) {
                for (Object item : (Iterable<?>) current) {
                    if (item != null) {
                        pending.addLast(item);
                    }
                }
                continue;
            }
            if (current instanceof Map) {
                for (Object item : ((Map<?, ?>) current).values()) {
                    if (item != null) {
                        pending.addLast(item);
                    }
                }
                continue;
            }
            if (current.getClass().isArray()) {
                int len = Array.getLength(current);
                for (int i = 0; i < len; i++) {
                    Object item = Array.get(current, i);
                    if (item != null) {
                        pending.addLast(item);
                    }
                }
                continue;
            }
            if (!shouldTraverseObject(current.getClass())) {
                continue;
            }

            Class<?> c = current.getClass();
            while (c != null && c != Object.class) {
                for (Field f : c.getDeclaredFields()) {
                    if (Modifier.isStatic(f.getModifiers()) || f.getType().isPrimitive()) {
                        continue;
                    }
                    try {
                        f.setAccessible(true);
                        Object value = f.get(current);
                        if (value != null && !isLeafValue(value)) {
                            pending.addLast(value);
                        }
                    } catch (Exception e) {
                        // ignore inaccessible fields
                    }
                }
                c = c.getSuperclass();
            }
        }

        return null;
    }

    private boolean shouldTraverseObject(Class<?> cls) {
        if (cls == null) {
            return false;
        }
        String name = cls.getName();
        return name.startsWith("net.battlescribe.")
                || name.startsWith("java.util.")
                || cls.isArray();
    }

    private boolean isLeafValue(Object value) {
        if (value == null) {
            return true;
        }
        Class<?> cls = value.getClass();
        return value instanceof String
                || value instanceof Number
                || value instanceof Boolean
                || value instanceof Character
                || value instanceof Class
                || cls.isEnum();
    }

    private List<String> candidateIds(String id) {
        List<String> result = new ArrayList<String>();
        if (id == null || id.isEmpty()) {
            return result;
        }
        result.add(id);
        if (id.contains("::")) {
            String[] parts = id.split("::");
            for (String part : parts) {
                if (!part.isEmpty() && !result.contains(part)) {
                    result.add(part);
                }
            }
        }
        return result;
    }

    private boolean matchesId(String actualId, String expectedId) {
        if (actualId == null || expectedId == null) {
            return false;
        }
        if (actualId.equals(expectedId)) {
            return true;
        }
        for (String candidate : candidateIds(actualId)) {
            if (candidate.equals(expectedId)) {
                return true;
            }
        }
        for (String candidate : candidateIds(expectedId)) {
            if (candidate.equals(actualId)) {
                return true;
            }
        }
        return false;
    }

    private List<String> extractStrings(Object values) {
        List<String> result = new ArrayList<String>();
        for (Object value : toJavaList(values)) {
            if (value != null) {
                result.add(value.toString());
            }
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    private List<Object> toJavaList(Object value) {
        if (value == null) {
            return Collections.emptyList();
        }
        if (value instanceof List) {
            return (List<Object>) value;
        }
        if (value instanceof Iterable) {
            List<Object> result = new ArrayList<Object>();
            for (Object item : (Iterable<?>) value) {
                result.add(item);
            }
            return result;
        }
        try {
            Method sizeMethod = value.getClass().getMethod("size");
            Method getMethod = value.getClass().getMethod("get", int.class);
            int size = ((Number) sizeMethod.invoke(value)).intValue();
            List<Object> result = new ArrayList<Object>(size);
            for (int i = 0; i < size; i++) {
                result.add(getMethod.invoke(value, Integer.valueOf(i)));
            }
            return result;
        } catch (Exception e) {
            return Collections.emptyList();
        }
    }

    private JsonArray toJsonArray(List<String> values) {
        JsonArray array = new JsonArray();
        for (String value : values) {
            array.add(new com.google.gson.JsonPrimitive(value));
        }
        return array;
    }

    private String errorJson(String message) {
        JsonObject response = new JsonObject();
        response.addProperty("error", message);
        return response.toString();
    }

    private String jsonBooleanResult(String key, boolean value) {
        JsonObject response = new JsonObject();
        response.addProperty(key, value);
        return response.toString();
    }

    private String buildExceptionMessage(Exception e) {
        String msg = e.getClass().getSimpleName() + ": " + e.getMessage();
        if (e instanceof java.lang.reflect.InvocationTargetException) {
            Throwable cause = ((java.lang.reflect.InvocationTargetException) e).getTargetException();
            if (cause != null) {
                msg += " [cause: " + cause.getClass().getSimpleName() + ": " + cause.getMessage() + "]";
            }
        }
        return msg;
    }

    private void cacheRosterAccess() {
        try {
            // The method a() on the engine (or base class c) returns the Roster
            getRosterMethod = engineClass.getMethod("a");
            getRosterMethod.setAccessible(true);
            rosterClass = getRosterMethod.getReturnType();
        } catch (Exception e) {
            System.err.println("[bs-ui-agent] Failed to cache roster access: " + e.getMessage());
        }
    }

    private String callGetter(Object obj, String methodName) {
        Object result = callGetterObject(obj, methodName);
        return result != null ? result.toString() : null;
    }

    private Object callGetterObject(Object obj, String methodName) {
        if (obj == null) {
            return null;
        }
        try {
            Method m = findMethod(obj.getClass(), methodName);
            if (m == null) return null;
            return m.invoke(obj);
        } catch (Exception e) {
            return null;
        }
    }

    private Object callListGetter(Object obj, String methodName) {
        if (obj == null) {
            return null;
        }
        try {
            Method m = findMethod(obj.getClass(), methodName);
            if (m == null) return null;
            return m.invoke(obj);
        } catch (Exception e) {
            return null;
        }
    }

    private Method findMethod(Class<?> cls, String name) {
        return findMethod(cls, name, 0);
    }

    private Method findMethod(Class<?> cls, String name, int paramCount) {
        Class<?> c = cls;
        while (c != null && c != Object.class) {
            for (Method m : c.getDeclaredMethods()) {
                if (m.getName().equals(name) && m.getParameterCount() == paramCount) {
                    m.setAccessible(true);
                    return m;
                }
            }
            c = c.getSuperclass();
        }
        return null;
    }

    private Method findMethod(Class<?> cls, String preferredName, Class<?>[] paramTypes, Class<?> returnType) {
        Class<?> c = cls;
        while (c != null && c != Object.class) {
            for (Method m : c.getDeclaredMethods()) {
                if (preferredName != null && !m.getName().equals(preferredName)) {
                    continue;
                }
                if (m.getParameterCount() != paramTypes.length) {
                    continue;
                }
                if (returnType != null && !wrapPrimitive(returnType).isAssignableFrom(wrapPrimitive(m.getReturnType()))) {
                    continue;
                }
                Class<?>[] params = m.getParameterTypes();
                boolean match = true;
                for (int i = 0; i < params.length; i++) {
                    Class<?> expected = wrapPrimitive(paramTypes[i]);
                    Class<?> actual = wrapPrimitive(params[i]);
                    if (!actual.isAssignableFrom(expected) && !expected.isAssignableFrom(actual)) {
                        match = false;
                        break;
                    }
                }
                if (match) {
                    m.setAccessible(true);
                    return m;
                }
            }
            c = c.getSuperclass();
        }
        return null;
    }

    private Class<?> wrapPrimitive(Class<?> cls) {
        if (cls == null || !cls.isPrimitive()) {
            return cls;
        }
        if (cls == Boolean.TYPE) return Boolean.class;
        if (cls == Byte.TYPE) return Byte.class;
        if (cls == Character.TYPE) return Character.class;
        if (cls == Short.TYPE) return Short.class;
        if (cls == Integer.TYPE) return Integer.class;
        if (cls == Long.TYPE) return Long.class;
        if (cls == Float.TYPE) return Float.class;
        if (cls == Double.TYPE) return Double.class;
        if (cls == Void.TYPE) return Void.class;
        return cls;
    }

    private Class<?> findClass(String name) {
        for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
            if (cls.getName().equals(name)) {
                return cls;
            }
        }
        return null;
    }

    /**
     * Patches the supporter pass check on the main window controller to always return true.
     * Strategy: find the controller, walk its class hierarchy for a field holding supporter pass(es),
     * and inject a valid one. Falls back to direct method override via field patching.
     */
    public String patchSupporterPass() {
        List<String> log = new ArrayList<>();

        // Strategy: Use Instrumentation to retransform the class, patching
        // hasValidSupporterPass() to always return true (iconst_1, ireturn)
        try {
            Method hasValid = null;
            Class<?> targetClass = null;

            // Find the method in the class hierarchy
            Class<?> controllerClass = findClass("net.battlescribe.desktop.rostereditor.RosterEditorWindowController");
            if (controllerClass != null) {
                hasValid = findMethodInHierarchy(controllerClass, "hasValidSupporterPass");
            }
            if (hasValid == null) {
                // Try finding it in all loaded classes
                for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
                    if (cls.getName().contains("BattleScribeWindowController")) {
                        hasValid = findMethodInHierarchy(cls, "hasValidSupporterPass");
                        if (hasValid != null) break;
                    }
                }
            }

            if (hasValid == null) {
                log.add("method_not_found");
                return buildPatchResult(false, log);
            }

            targetClass = hasValid.getDeclaringClass();
            log.add("target:" + targetClass.getName());

            // Check if already patched
            javafx.scene.Scene scene = findMainScene();
            if (scene != null) {
                Object controller = findControllerInstance(controllerClass, scene);
                if (controller != null) {
                    hasValid.setAccessible(true);
                    boolean current = (boolean) hasValid.invoke(controller);
                    if (current) {
                        log.add("already_valid");
                        return buildPatchResult(true, log);
                    }
                }
            }

            // Retransform the class to patch the method bytecode
            if (!instrumentation.isRetransformClassesSupported()) {
                log.add("retransform_not_supported");
                return buildPatchResult(false, log);
            }
            if (!instrumentation.isModifiableClass(targetClass)) {
                log.add("class_not_modifiable");
                return buildPatchResult(false, log);
            }

            final String methodName = "hasValidSupporterPass";
            final Class<?> finalTargetClass = targetClass;

            java.lang.instrument.ClassFileTransformer transformer =
                new java.lang.instrument.ClassFileTransformer() {
                    @Override
                    public byte[] transform(ClassLoader loader, String className,
                            Class<?> classBeingRedefined, java.security.ProtectionDomain protectionDomain,
                            byte[] classfileBuffer) {
                        if (classBeingRedefined != finalTargetClass) return null;
                        return patchMethodToReturnTrue(classfileBuffer, methodName);
                    }
                };

            instrumentation.addTransformer(transformer, true);
            try {
                instrumentation.retransformClasses(targetClass);
                log.add("retransformed");
            } finally {
                instrumentation.removeTransformer(transformer);
            }

            // Verify the patch worked
            if (scene != null) {
                Object controller = findControllerInstance(controllerClass, scene);
                if (controller != null) {
                    hasValid.setAccessible(true);
                    boolean after = (boolean) hasValid.invoke(controller);
                    log.add("after_patch=" + after);
                    return buildPatchResult(after, log);
                }
            }
            // Can't verify but retransformation succeeded
            return buildPatchResult(true, log);

        } catch (Throwable e) {
            java.io.StringWriter sw = new java.io.StringWriter();
            e.printStackTrace(new java.io.PrintWriter(sw));
            log.add("error:" + e.getClass().getSimpleName() + ":" + e.getMessage());
            log.add("stack:" + sw.toString().replace("\n", " | ").replace("\"", "'"));
            return buildPatchResult(false, log);
        }
    }

    private Object findControllerInstance(Class<?> controllerClass, javafx.scene.Scene scene) {
        if (scene == null || controllerClass == null) return null;
        javafx.scene.Node btnNode = scene.getRoot().lookup("#btnNewRoster");
        if (btnNode instanceof javafx.scene.control.ButtonBase) {
            javafx.event.EventHandler<?> handler = ((javafx.scene.control.ButtonBase) btnNode).getOnAction();
            if (handler != null) {
                Object c = extractControllerFromHandler(handler, controllerClass);
                if (c != null) return c;
            }
        }
        return findControllerFromNode(scene.getRoot(), controllerClass);
    }

    /**
     * Patches a method in raw class bytes to always return true (iconst_1, ireturn).
     * Rebuilds the Code attribute to be minimal (no StackMapTable, no exception table).
     */
    private byte[] patchMethodToReturnTrue(byte[] classBytes, String targetMethodName) {
        try {
            // Parse constant pool to find the method name's UTF8 index
            int pos = 8; // skip magic(4) + minor(2) + major(2)
            int cpCount = readU2(classBytes, pos);
            pos += 2;

            // Build a map of constant pool UTF8 entries
            String[] utf8Entries = new String[cpCount];
            for (int i = 1; i < cpCount; i++) {
                int tag = classBytes[pos] & 0xFF;
                pos++;
                switch (tag) {
                    case 1: // CONSTANT_Utf8
                        int len = readU2(classBytes, pos);
                        pos += 2;
                        utf8Entries[i] = new String(classBytes, pos, len, "UTF-8");
                        pos += len;
                        break;
                    case 7: case 8: case 16: case 19: case 20: // 2-byte refs
                        pos += 2;
                        break;
                    case 3: case 4: case 9: case 10: case 11: case 12:
                    case 17: case 18: // 4-byte
                        pos += 4;
                        break;
                    case 5: case 6: // 8-byte (long/double) - takes 2 slots
                        pos += 8;
                        i++; // skip next slot
                        break;
                    case 15: // MethodHandle
                        pos += 3;
                        break;
                    default:
                        return null; // unknown tag, bail out
                }
            }

            // Skip access_flags(2) + this_class(2) + super_class(2)
            pos += 6;
            // Skip interfaces
            int interfaceCount = readU2(classBytes, pos);
            pos += 2 + interfaceCount * 2;
            // Skip fields
            int fieldCount = readU2(classBytes, pos);
            pos += 2;
            for (int i = 0; i < fieldCount; i++) {
                pos += 6; // access_flags + name_index + descriptor_index
                int attrCount = readU2(classBytes, pos);
                pos += 2;
                for (int j = 0; j < attrCount; j++) {
                    pos += 2; // attr_name_index
                    int attrLen = readU4(classBytes, pos);
                    pos += 4 + attrLen;
                }
            }

            // Now at methods - find the target and rebuild class bytes
            int methodCount = readU2(classBytes, pos);
            pos += 2;
            for (int i = 0; i < methodCount; i++) {
                int nameIndex = readU2(classBytes, pos + 2);
                int descIndex = readU2(classBytes, pos + 4);
                pos += 6;

                String mName = (nameIndex > 0 && nameIndex < cpCount) ? utf8Entries[nameIndex] : null;
                String mDesc = (descIndex > 0 && descIndex < cpCount) ? utf8Entries[descIndex] : null;

                int attrCount = readU2(classBytes, pos);
                pos += 2;

                if (targetMethodName.equals(mName) && "()Z".equals(mDesc)) {
                    // Found the method! Rebuild class bytes with new Code attribute
                    for (int j = 0; j < attrCount; j++) {
                        int attrNameIndex = readU2(classBytes, pos);
                        int attrLen = readU4(classBytes, pos + 2);
                        String attrName = (attrNameIndex > 0 && attrNameIndex < cpCount)
                            ? utf8Entries[attrNameIndex] : null;

                        if ("Code".equals(attrName)) {
                            // Build new Code attribute content:
                            // max_stack=1, max_locals=1, code_length=2,
                            // code=[iconst_1, ireturn], exception_table_count=0, attributes_count=0
                            byte[] newCodeContent = new byte[] {
                                0, 1,       // max_stack = 1
                                0, 1,       // max_locals = 1
                                0, 0, 0, 2, // code_length = 2
                                0x04,       // iconst_1
                                (byte) 0xAC, // ireturn
                                0, 0,       // exception_table_length = 0
                                0, 0        // attributes_count = 0
                            };
                            int newAttrLen = newCodeContent.length; // 14

                            // Build new class bytes: everything before this attr's length,
                            // then new length + content, then everything after original attr
                            int attrHeaderStart = pos; // attr_name_index position
                            int attrDataStart = pos + 6; // start of attr content
                            int attrEnd = pos + 6 + attrLen; // end of original attr

                            byte[] result = new byte[classBytes.length - attrLen + newAttrLen];
                            // Copy everything up to attr_name_index (inclusive) + 2 bytes
                            System.arraycopy(classBytes, 0, result, 0, pos + 2);
                            // Write new attr_length
                            result[pos + 2] = (byte) ((newAttrLen >> 24) & 0xFF);
                            result[pos + 3] = (byte) ((newAttrLen >> 16) & 0xFF);
                            result[pos + 4] = (byte) ((newAttrLen >> 8) & 0xFF);
                            result[pos + 5] = (byte) (newAttrLen & 0xFF);
                            // Write new Code content
                            System.arraycopy(newCodeContent, 0, result, pos + 6, newAttrLen);
                            // Copy everything after original Code attribute
                            System.arraycopy(classBytes, attrEnd, result, pos + 6 + newAttrLen,
                                classBytes.length - attrEnd);
                            return result;
                        }
                        pos += 6 + attrLen;
                    }
                } else {
                    // Skip this method's attributes
                    for (int j = 0; j < attrCount; j++) {
                        pos += 2;
                        int attrLen = readU4(classBytes, pos);
                        pos += 4 + attrLen;
                    }
                }
            }
        } catch (Exception e) {
            // bytecode patching failed
        }
        return null; // return null means "don't transform"
    }

    private static int readU2(byte[] data, int offset) {
        return ((data[offset] & 0xFF) << 8) | (data[offset + 1] & 0xFF);
    }

    private static int readU4(byte[] data, int offset) {
        return ((data[offset] & 0xFF) << 24) | ((data[offset + 1] & 0xFF) << 16)
             | ((data[offset + 2] & 0xFF) << 8) | (data[offset + 3] & 0xFF);
    }

    private Method findMethodInHierarchy(Class<?> cls, String methodName) {
        while (cls != null && !cls.getName().equals("java.lang.Object")) {
            try {
                return cls.getDeclaredMethod(methodName);
            } catch (NoSuchMethodException e) {
                cls = cls.getSuperclass();
            }
        }
        return null;
    }

    private String buildPatchResult(boolean patched, List<String> log) {
        JsonObject response = new JsonObject();
        response.addProperty("patched", patched);
        response.add("log", toJsonArray(log));
        return response.toString();
    }
}
