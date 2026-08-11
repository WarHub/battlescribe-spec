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
import java.util.Comparator;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Locale;
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

    /**
     * Set {@code BS_UI_VALIDATION_TRACE=1} to print every validation error with each id source that
     * could name it. Off by default; on, it is one line per error per state read.
     *
     * <p>Worth having as a switch rather than as a temporary edit: which element carries usable ids
     * varies by owner type, and the only way to find out is to look at all of them at once.
     */
    private static final boolean VALIDATION_TRACE = "1".equals(System.getenv("BS_UI_VALIDATION_TRACE"));

    private final Instrumentation instrumentation;

    // Cached references
    private Object engineInstance;
    private Object controllerInstance;
    private Class<?> engineClass;
    private Class<?> rosterClass;
    private Method getRosterMethod;

    /**
     * Classes {@link #findClass} has already resolved.
     *
     * <p><b>Hits only, never misses.</b> A name that is not loaded yet may be loaded later — the app
     * loads its model classes as data arrives — so a remembered miss would be a permanent one. The
     * asymmetry costs nothing: a miss is the case where the scan found no candidate, which is also
     * the case a cache could not have shortened.
     */
    private final Map<String, Class<?>> classesByName = new HashMap<String, Class<?>>();

    /** Per-class field lists for the object-graph walks — see {@link #traversableFieldsOf}. */
    private final Map<Class<?>, List<Field>> traversableFieldsByClass =
            new IdentityHashMap<Class<?>, List<Field>>();

    /**
     * What the {@link #getValidationErrors()} call in progress has already worked out, or null when
     * no call is in progress. See {@link ValidationPass}.
     */
    private ValidationPass validationPass;

    public EngineAccessor(Instrumentation instrumentation) {
        this.instrumentation = instrumentation;
    }

    /**
     * Answers one pass of validation-error collection reuses, rather than recomputing per error.
     *
     * <p>Resolving one error's {@code from} can cost a full reflective walk of the object graph and
     * a roster search, and a roster with N errors asks the same handful of questions N times over a
     * model that cannot change while the pass runs. Caching per PASS rather than for the session is
     * the point: the roster does change between passes, and an entry absent now may exist after the
     * next selection — a session-scoped "not found" would outlive the fact that produced it.
     *
     * <p>No locking, and none needed: {@code JsonRpcServer.acceptLoop} calls {@code handleClient}
     * inline and reads one connection's requests in a loop, and its FX dispatch blocks that thread
     * until the FX task returns. One request is in flight at a time.
     */
    private static final class ValidationPass {
        /** Instances of a class reachable from the engine and roster — see {@link #collectInstances}. */
        final Map<Class<?>, List<Object>> instances = new IdentityHashMap<Class<?>, List<Object>>();

        /** An entry's declared constraints as id -> value — see {@link #constraintValuesOf}. */
        final Map<String, Map<String, Integer>> constraintValues = new HashMap<String, Map<String, Integer>>();
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

        // One pass, one set of answers — and none of them outlive it. See ValidationPass.
        validationPass = new ValidationPass();
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
        } finally {
            validationPass = null;
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
        // Engine data layer returns forces in insertion order. The desktop UI's
        // SortedTreeView + RosterNameNodeComparator sorts by getName() case-insensitive.
        // Pre-resolve names eagerly to avoid non-transitive comparator from reflection errors.
        List<Object> items = new ArrayList<Object>(size);
        String[] names = new String[size];
        for (int i = 0; i < size; i++) {
            Object item = list.getClass().getMethod("get", int.class).invoke(list, i);
            items.add(item);
            names[i] = String.valueOf(callGetter(item, "getName"));
        }
        Integer[] indices = new Integer[size];
        for (int i = 0; i < size; i++) indices[i] = i;
        java.util.Arrays.sort(indices, (a, b) -> names[a].compareToIgnoreCase(names[b]));
        for (int idx : indices) {
            forces.add(serializeForce(items.get(idx)));
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

        // Forces carry profiles just as selections do — a force entry can declare them directly.
        // Omitting them here reported every force as having none, which reads as "BattleScribe
        // does not attach profiles to forces" rather than as this serializer never asking.
        result.add("profiles", serializeProfileList(callListGetter(force, "getProfiles")));

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
        // Engine data layer returns selections in insertion order. The desktop UI's
        // SortedTreeView + RosterNameNodeComparator sorts by getName() case-insensitive.
        // Pre-resolve names eagerly to avoid non-transitive comparator from reflection errors.
        List<Object> items = new ArrayList<Object>(size);
        String[] names = new String[size];
        for (int i = 0; i < size; i++) {
            Object item = list.getClass().getMethod("get", int.class).invoke(list, i);
            items.add(item);
            names[i] = String.valueOf(callGetter(item, "getName"));
        }
        Integer[] indices = new Integer[size];
        for (int i = 0; i < size; i++) indices[i] = i;
        java.util.Arrays.sort(indices, (a, b) -> names[a].compareToIgnoreCase(names[b]));
        for (int idx : indices) {
            selections.add(serializeSelection(items.get(idx), pubNameMap, force));
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
            result.addProperty("type", type != null ? type.toString().toLowerCase(Locale.ROOT) : null);
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

    /** Whether the cost TYPE this cost belongs to is declared hidden. */
    private boolean isCostTypeHidden(String typeId) {
        try {
            Object costType = findCostTypeById(typeId);
            if (costType == null) {
                return false;
            }
            Object hidden = callGetterObject(costType, "isHidden");
            return hidden instanceof Boolean && (Boolean) hidden;
        } catch (Exception e) {
            return false;
        }
    }

    private JsonArray serializeCostList(Object list) throws Exception {
        JsonArray costs = new JsonArray();
        if (list == null) return costs;
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            Object cost = list.getClass().getMethod("get", int.class).invoke(list, i);
            JsonObject item = new JsonObject();
            item.addProperty("name", callGetter(cost, "getName"));
            String typeId = callGetter(cost, "getTypeId");
            item.addProperty("typeId", typeId);
            // `hidden` is declared on the cost TYPE, not on the cost, so it has to be resolved
            // through the game system. Omitting it reported every cost as visible — which reads as
            // BattleScribe ignoring the flag rather than as this serializer never asking for it.
            item.addProperty("hidden", isCostTypeHidden(typeId));
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
            item.addProperty("customName", callGetter(category, "getCustomName"));
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

    /**
     * Reads {@code ownerId::entryId::constraintId} into entry id → its candidate constraint ids.
     *
     * <p><b>This is a HAND-KEPT MIRROR, not a shared implementation.</b> The normative rule and the
     * reasoning behind it live in {@code src/BattleScribeSpec.TestKit/Roster/BattleScribeErrorIds.cs};
     * this agent runs inside the BattleScribe JVM and cannot call it, so the only thing keeping the
     * two together is that someone changes both. Be honest about what that buys: nothing enforces
     * it at build time. What does exist is {@code tests/Features/BattleScribeErrorIdsTests.cs},
     * which pins the cases the two are meant to answer identically —
     *
     * <ul>
     *   <li>{@code owner::entry::con} → {@code {entry: [con]}}, the only shape observed in the corpus
     *   <li>{@code owner::link::entry::con} → {@code {link::entry: [con]}} — the middle segments are
     *       part of the composite ENTRY id (docs/entry-id-construction.md), so only the LAST segment
     *       is ever the constraint. Inferred, not observed.
     *   <li>repeats of one id collapse, and the surviving order is the order first seen
     *   <li>two constraints on one entry both survive, in listed order
     *   <li>fewer than three segments, and nulls, are dropped
     * </ul>
     *
     * <p>Two things changed here and both had been silent. The split is now unlimited where it was
     * {@code split("::", 3)}, which left {@code parts[2]} holding the whole remaining tail — a
     * four-segment id answered {@code entry::con} as a constraint id. And the dedupe is new; it
     * matters because the surviving ORDER is what {@link #pickConstraintId} walks when the message
     * quotes no value to decide on.
     */
    private Map<String, List<String>> parseValidationErrorIds(List<String> errorIds) {
        Map<String, List<String>> errorIdMap = new HashMap<String, List<String>>();
        for (String errorId : errorIds) {
            if (errorId == null) {
                continue;
            }
            // -1, not 0: an unlimited split still has to KEEP trailing empty segments, because
            // C#'s String.Split does and this has to answer the same for `owner::entry::`.
            String[] parts = errorId.split("::", -1);
            if (parts.length >= 3) {
                // parts[0] is the owner and is dropped; parts[1..n-2] rejoined is the entry id;
                // the last segment alone is the constraint id.
                StringBuilder entryId = new StringBuilder(parts[1]);
                for (int i = 2; i < parts.length - 1; i++) {
                    entryId.append("::").append(parts[i]);
                }
                String entryKey = entryId.toString();
                String constraintId = parts[parts.length - 1];
                List<String> constraintIds = errorIdMap.get(entryKey);
                if (constraintIds == null) {
                    constraintIds = new ArrayList<String>();
                    errorIdMap.put(entryKey, constraintIds);
                }
                if (!constraintIds.contains(constraintId)) {
                    constraintIds.add(constraintId);
                }
            }
        }
        return errorIdMap;
    }

    /**
     * Resolves the {@code entryId/constraintId} a spec asserts as {@code from}.
     *
     * <p><b>The error object cannot answer this.</b> {@code net.battlescribe.engine.b.a} is
     * constructed as {@code (Object, String)} and exposes the object as {@code a()}, which reads
     * like the source constraint and is not: it is the ROSTER ELEMENT the error hangs on — a
     * {@code model.roster.Category} whose parent is the {@code Force} — carrying runtime ids
     * regenerated on every recalculation. Reading ids off it produces a well-formed and entirely
     * wrong {@code from}, which is worse than none. Measured with {@code BS_UI_VALIDATION_TRACE=1}.
     *
     * <p>So the only carriers are {@code getValidationErrorIds()}, which is where the spec-side ids
     * genuinely live, and failing that the message text. Both are used below.
     */
    /**
     * Resolves {@code entryId/constraintId} by reading the message against the LOADED DATA, when
     * {@code getValidationErrorIds()} offered nothing.
     *
     * <p>This is a port of {@code BattleScribeEngine.ResolveEntryFromMessage}, and deliberately so:
     * it is the same question about the same Java model, and the in-process adapter is the
     * reference every spec's `from` was written against. Reimplementing the rule differently here
     * would make the two BattleScribe engines disagree by accident rather than agree by
     * construction.
     *
     * <p>Message matching is a poor way to recover provenance and it is the only one available.
     * BattleScribe attaches no constraint to a validation error — the object it does attach is the
     * roster element the error hangs on — and `getValidationErrorIds()` comes back empty on every
     * element for these errors, measured with {@code BS_UI_VALIDATION_TRACE=1}. So the rendered
     * text is the sole carrier, and the entry NAME plus the constraint's own type and value are
     * what it carries.
     */
    /**
     * What a selection made through an entry link IS, as specs name it.
     *
     * <p>Such a selection reports its entryId as the composite {@code linkId::targetId}. That is
     * the right answer to "how did this get here" and the wrong one to "what is it": specs address
     * the error's owner by the ENTRY, {@code shared-unit}, not by the route taken to it. The route
     * still matters for {@code from}, which is why only the owner is reduced — see
     * {@link #declaringEntryOf}.
     */
    private String linkTargetOf(String entryId) {
        if (entryId == null || !entryId.contains("::")) {
            return entryId;
        }
        String[] parts = entryId.split("::");
        return parts[parts.length - 1];
    }

    /**
     * Which segment of a composite id actually DECLARES {@code constraintId}.
     *
     * <p>A constraint on the link and a constraint on its target are different constraints with
     * different meanings — per-link versus shared — and a spec asserts which one fired by naming
     * its owner. Reporting the composite for both loses exactly that distinction, so each segment
     * is asked whether the constraint is its own.
     */
    private String declaringEntryOf(String entryId, String constraintId) {
        if (entryId == null || constraintId == null || !entryId.contains("::")) {
            return entryId;
        }
        for (String segment : entryId.split("::")) {
            if (constraintValuesOf(segment).containsKey(constraintId)) {
                return segment;
            }
        }
        return entryId;
    }

    private ValidationRef resolveRefFromMessage(String message) {
        if (message == null) {
            return null;
        }

        // Order matters: the most specific owner first. A message names its container as well as
        // its subject — "Troops cannot have any selections of Hidden Unit" holds both a category
        // name and an entry name — and the entry is the one the spec asserts.
        for (String className : CONSTRAINT_OWNER_CLASSES) {
            ValidationRef ref = message.contains("(hidden)")
                    ? matchHiddenOwner(findClass(className), message)
                    : matchConstraintOwner(findClass(className), message);
            if (ref != null) {
                return ref;
            }
        }
        return null;
    }

    /**
     * Owners a constraint can hang on, most specific first.
     *
     * <p>{@code ForceEntry} is here because a force-count constraint ("must have 1 more forces
     * from Patrol") belongs to the force entry, not to any selection — leaving it out left that
     * whole family of errors with no {@code from} at all.
     */
    private static final String[] CONSTRAINT_OWNER_CLASSES = {
        "net.battlescribe.model.data.SelectionEntry",
        "net.battlescribe.model.data.SelectionEntryGroup",
        "net.battlescribe.model.data.EntryLink",
        "net.battlescribe.model.data.ForceEntry",
    };

    /**
     * The entry a hidden-entry error is ABOUT, which is not the one it is reported on.
     *
     * <p>BattleScribe renders "Troops cannot have any selections of Hidden Unit (hidden)" on the
     * category. Taking the reported owner names the container; the spec asserts the entry that is
     * hidden, so it is read out of the message like any other.
     */
    private ValidationRef matchHiddenOwner(Class<?> ownerClass, String message) {
        if (ownerClass == null) {
            return null;
        }
        for (Object owner : collectInstances(ownerClass)) {
            String name = callGetter(owner, "getName");
            String ownerId = callGetter(owner, "getId");
            if (name != null && !name.isEmpty() && ownerId != null && message.contains(name)) {
                return new ValidationRef(ownerId, "hidden");
            }
        }
        return null;
    }

    /** The best (entry, constraint) pair among instances of {@code ownerClass}, or null. */
    private ValidationRef matchConstraintOwner(Class<?> ownerClass, String message) {
        if (ownerClass == null) {
            return null;
        }

        ValidationRef fallback = null;
        for (Object owner : collectInstances(ownerClass)) {
            String name = callGetter(owner, "getName");
            if (name == null || name.isEmpty() || !message.contains(name)) {
                continue;
            }
            String ownerId = callGetter(owner, "getId");
            if (ownerId == null) {
                continue;
            }

            for (Object constraint : toJavaList(callListGetter(owner, "getConstraints"))) {
                String type = callGetter(constraint, "getType");
                if (!messageMatchesConstraintKind(message, type)) {
                    continue;
                }
                String constraintId = callGetter(constraint, "getId");
                if (constraintId == null) {
                    continue;
                }
                // The value disambiguates two constraints of the same kind on one entry, so a
                // value match wins outright; otherwise keep the first kind match and keep looking.
                int value = (int) parseDouble(callGetter(constraint, "getValue"));
                if (message.contains("maximum " + value) || message.contains("minimum " + value)) {
                    return new ValidationRef(ownerId, constraintId);
                }
                if (fallback == null) {
                    fallback = new ValidationRef(ownerId, constraintId);
                }
            }
        }
        return fallback;
    }

    /** Whether the message's phrasing is the one BattleScribe renders for this constraint type. */
    private boolean messageMatchesConstraintKind(String message, String type) {
        if ("min".equals(type)) {
            return message.contains("must have") || message.contains("must spend");
        }
        if ("max".equals(type)) {
            return message.contains("too many") || message.contains("too much");
        }
        return false;
    }

    private double parseDouble(String value) {
        try {
            return value == null ? 0 : Double.parseDouble(value);
        } catch (NumberFormatException e) {
            return 0;
        }
    }

    private ValidationRef resolveValidationRef(
            Map<String, List<String>> errorIdMap, String ownerType, String message, String ownerEntryId) {
        String lowerMessage = message != null ? message.toLowerCase(Locale.ROOT) : null;

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
                // The id list cannot say which constraint fired, and a SHORT list is not evidence
                // that it can. It is not per-error: BattleScribe lists ids the ELEMENT knows
                // about, and one element carries every error raised under it.
                //
                // Measured on `constraint-shared-flag`: the force reports exactly one id,
                // `…::shared-unit::con-max-shared`, while carrying three errors — two of them
                // raised by `con-max-per-link`, which appears in no list anywhere. Trusting a
                // one-element list therefore answered `con-max-shared` for a message reading
                // `(maximum 2)`, naming a constraint whose limit is 3 and which that message
                // rules out.
                //
                // So the rendered VALUE decides whenever it disagrees with the list, at any list
                // size: a candidate the message contradicts is not a weaker witness than the
                // message, it is a refuted one.
                String picked = pickConstraintId(candidateEntryId, entry.getValue(), lowerMessage);
                if (!constraintValueMatchesMessage(candidateEntryId, picked, lowerMessage)) {
                    ValidationRef byMessage = resolveRefFromMessage(message);
                    if (byMessage != null
                            && constraintValueMatchesMessage(
                                    byMessage.entryId, byMessage.constraintId, lowerMessage)) {
                        return byMessage;
                    }
                }

                return new ValidationRef(candidateEntryId, picked);
            }
        }

        // Hidden error: "(hidden)" in message → the entry the message NAMES, then errorIdMap.
        if (lowerMessage != null && lowerMessage.contains("(hidden)")) {
            // Read the message first. errorIdMap's keys here are the error's OWNER — the category
            // that refused the selection — and the spec asserts the entry that is hidden.
            ValidationRef named = resolveRefFromMessage(message);
            if (named != null) {
                return named;
            }
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

        // Last resort, and for most constraint errors the ONLY one: read the message against the
        // loaded data. Everything above needs an id from `getValidationErrorIds()`, which comes
        // back empty on every element for those errors.
        ValidationRef fromMessage = resolveRefFromMessage(message);
        if (fromMessage != null) {
            return fromMessage;
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
            if (costTypeName != null && containsIgnoreCase(lowerMessage, costTypeName.toLowerCase(Locale.ROOT))) {
                return new ValidationRef("costLimits", constraintId);
            }
        }

        if (constraintIds.size() == 1) {
            return new ValidationRef("costLimits", constraintIds.get(0));
        }
        return null;
    }

    /**
     * Which of an entry's constraints raised this error.
     *
     * <p>An entry can carry several of the same kind — a per-link maximum and a shared maximum,
     * say — and taking the first is a coin toss between them. The rendered VALUE is what tells them
     * apart, so a constraint whose value the message quotes wins; the first is only a fallback for
     * when nothing distinguishes them.
     */
    private String pickConstraintId(String entryId, List<String> constraintIds, String lowerMessage) {
        if (constraintIds == null || constraintIds.isEmpty()) {
            return null;
        }
        if (lowerMessage != null && lowerMessage.contains("(hidden)")) {
            return "hidden";
        }
        if (lowerMessage != null && constraintIds.size() > 1) {
            for (Map.Entry<String, Integer> candidate : constraintValuesOf(entryId).entrySet()) {
                if (!constraintIds.contains(candidate.getKey())) {
                    continue;
                }
                int value = candidate.getValue();
                if (lowerMessage.contains("maximum " + value) || lowerMessage.contains("minimum " + value)) {
                    return candidate.getKey();
                }
            }
        }
        return constraintIds.get(0);
    }

    /**
     * Whether the message quotes the limit this constraint actually declares.
     *
     * <p>The test a candidate has to survive before it is believed. A message rendering
     * {@code (maximum 2)} is positive evidence for a constraint whose value is 2 and evidence
     * AGAINST one whose value is 3 — so this separates "the only candidate offered" from "the
     * candidate the app's own text supports". Absent a value on either side the answer is false,
     * which leaves the caller on its existing path rather than inventing a preference.
     */
    private boolean constraintValueMatchesMessage(String entryId, String constraintId, String lowerMessage) {
        if (entryId == null || constraintId == null || lowerMessage == null) {
            return false;
        }
        // A composite entryId names the route; the constraint is declared by one segment of it.
        Integer value = constraintValuesOf(declaringEntryOf(entryId, constraintId)).get(constraintId);
        if (value == null) {
            return false;
        }
        return lowerMessage.contains("maximum " + value) || lowerMessage.contains("minimum " + value);
    }

    /**
     * An entry's own constraints as id -> declared value.
     *
     * <p>Asked repeatedly for the same few ids while resolving one roster's errors — once per
     * candidate in {@link #pickConstraintId}, once per segment in {@link #declaringEntryOf}, and
     * again by {@link #constraintValueMatchesMessage} — and every ask is a roster search. Hence the
     * per-pass memory; see {@link ValidationPass} for why it is per pass and not per session.
     */
    private Map<String, Integer> constraintValuesOf(String entryId) {
        ValidationPass pass = validationPass;
        if (pass != null) {
            Map<String, Integer> remembered = pass.constraintValues.get(entryId);
            if (remembered != null) {
                return remembered;
            }
        }

        Map<String, Integer> values = readConstraintValues(entryId);
        if (pass != null) {
            pass.constraintValues.put(entryId, values);
        }
        return values;
    }

    /** {@link #constraintValuesOf} without the per-pass memory — the lookup itself. */
    private Map<String, Integer> readConstraintValues(String entryId) {
        Map<String, Integer> values = new HashMap<String, Integer>();
        try {
            Object entry = findEntryById(entryId);
            if (entry == null) {
                return values;
            }
            for (Object constraint : toJavaList(callListGetter(entry, "getConstraints"))) {
                String id = callGetter(constraint, "getId");
                if (id != null) {
                    values.put(id, (int) parseDouble(callGetter(constraint, "getValue")));
                }
            }
        } catch (Exception e) {
            // A lookup failure just means no tiebreak is available.
        }
        return values;
    }

    private boolean containsIgnoreCase(String message, String candidate) {
        if (message == null || candidate == null || candidate.isEmpty()) {
            return false;
        }
        return message.toLowerCase(Locale.ROOT).contains(candidate.toLowerCase(Locale.ROOT));
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
                if (name != null && lowerMessage.contains(name.toLowerCase(Locale.ROOT))) {
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
        if (VALIDATION_TRACE) {
            System.err.println("[agent] validation trace: element ownerType=" + ownerType
                    + " ownerEntryId=" + ownerEntryId
                    + " errorIds=" + errorIdList
                    + " errorCount=" + toJavaList(elementErrors).size());
        }
        for (Object error : toJavaList(elementErrors)) {
            String message = extractValidationMessage(error);
            ValidationRef validationRef = resolveValidationRef(errorIdMap, ownerType, message, ownerEntryId);
            JsonObject item = new JsonObject();
            item.addProperty("message", message);
            item.addProperty("ownerType", ownerType);
            item.addProperty("ownerId", callGetter(element, "getId"));
            if (ownerEntryId != null) {
                item.addProperty("ownerEntryId", linkTargetOf(ownerEntryId));
            }
            if (validationRef.entryId != null && validationRef.constraintId != null) {
                // A composite entryId names the ROUTE to the entry; the constraint belongs to one
                // element on that route, and which one is what distinguishes a per-link limit from
                // a shared one.
                item.addProperty(
                        "entryId",
                        declaringEntryOf(validationRef.entryId, validationRef.constraintId));
            } else if (validationRef.entryId != null) {
                item.addProperty("entryId", validationRef.entryId);
            }
            if (validationRef.constraintId != null) {
                item.addProperty("constraintId", validationRef.constraintId);
            }
            if (VALIDATION_TRACE) {
                Object attached = callGetterObject(error, "a");
                System.err.println("[agent] validation trace: ownerType=" + ownerType
                        + " ownerEntryId=" + ownerEntryId
                        + " ownErrorIds=" + errorIdList
                        + " attached=" + (attached == null ? null : attached.getClass().getName())
                        + " resolved=" + validationRef.entryId + "/" + validationRef.constraintId
                        + " message=" + message);
            }
            if (validationRef.entryId == null || validationRef.constraintId == null) {
                // An unresolved ref is not an absent error: the error IS reported, with its owner
                // and message intact, and only `from` is missing — so the spec fails saying the
                // error was "not found in" a list that visibly contains it. Say what could not be
                // resolved, and from what, or the next reader diagnoses it as a missing error.
                System.err.println("[agent] validation ref unresolved: ownerType=" + ownerType
                        + " ownerEntryId=" + ownerEntryId
                        + " entryId=" + validationRef.entryId
                        + " constraintId=" + validationRef.constraintId
                        + " errorIds=" + errorIdList
                        + " message=" + message);
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

    /**
     * Every instance of {@code targetClass} reachable from the engine and the current roster.
     *
     * <p>Same traversal as {@link #findObjectById}, collecting rather than stopping at the first
     * match, and with the same 10k visit ceiling — the object graph has cycles and a data file can
     * be large, so an unbounded walk is a hang rather than an answer.
     */
    private List<Object> collectInstances(Class<?> targetClass) {
        if (targetClass == null) {
            return new ArrayList<Object>();
        }

        ValidationPass pass = validationPass;
        if (pass != null) {
            List<Object> remembered = pass.instances.get(targetClass);
            if (remembered != null) {
                return remembered;
            }
        }

        List<Object> found = scanInstances(targetClass);
        if (pass != null) {
            pass.instances.put(targetClass, found);
        }
        return found;
    }

    /** {@link #collectInstances} without the per-pass memory — the walk itself. */
    private List<Object> scanInstances(Class<?> targetClass) {
        List<Object> found = new ArrayList<Object>();

        Object roster;
        try {
            roster = getCurrentRoster();
        } catch (Exception e) {
            roster = null;
        }

        Set<Object> visited = Collections.newSetFromMap(new IdentityHashMap<Object, Boolean>());
        ArrayDeque<Object> pending = new ArrayDeque<Object>();
        for (Object root : new Object[] { engineInstance, roster }) {
            if (root != null) {
                pending.add(root);
            }
        }

        while (!pending.isEmpty()) {
            if (visited.size() > GRAPH_WALK_VISIT_CEILING) {
                reportWalkTruncated("instance scan for " + targetClass.getName(), visited.size());
                break;
            }

            Object current = pending.removeFirst();
            if (current == null || isLeafValue(current) || !visited.add(current)) {
                continue;
            }

            if (targetClass.isInstance(current)) {
                found.add(current);
            }

            enqueueReferences(current, pending);
        }
        return found;
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
            if (visited.size() > GRAPH_WALK_VISIT_CEILING) {
                reportWalkTruncated(
                        "search for " + targetClass.getName() + " id '" + id + "'", visited.size());
                return null;
            }

            enqueueReferences(current, pending);
        }

        return null;
    }

    /**
     * The visit ceiling both object-graph walks stop at.
     *
     * <p>The graph has cycles and a real data file is large, so an unbounded walk is a hang rather
     * than an answer. But a walk that stops early is one that may not have reached what it was
     * asked for, and both walks report that as a plain negative — no instances, or no such id.
     * Hence {@link #reportWalkTruncated}: the two are different facts and only one of them is
     * about the roster.
     */
    private static final int GRAPH_WALK_VISIT_CEILING = 10000;

    /**
     * Says that a walk ran out of ceiling rather than out of graph.
     *
     * <p>Unconditional, not behind {@code BS_UI_VALIDATION_TRACE}: this is the one event that makes
     * a negative answer untrustworthy, it is rare, and a trace flag is only ever set by someone who
     * already suspects the thing this line would have told them.
     */
    private void reportWalkTruncated(String what, int visited) {
        System.err.println("[agent] object-graph walk truncated: " + what + " stopped after "
                + visited + " objects (ceiling " + GRAPH_WALK_VISIT_CEILING
                + "); a negative result here may mean 'not reached', not 'not present'");
    }

    /**
     * Queues everything {@code current} references, in an order that does not change between runs.
     *
     * <p>One implementation for both walks, so they cannot come to disagree about what traversing an
     * object means — they were two copies of this, and the copies are what let the ceiling check
     * drift into different places.
     *
     * <p><b>Fields are taken in name order</b> because {@link Class#getDeclaredFields()} is
     * explicitly documented as returning them in no particular order. Both walks then answer
     * order-sensitive questions off that enumeration — {@link #findObjectById} returns the first
     * match it reaches, {@link #matchConstraintOwner} keeps the first kind-match as its fallback —
     * so leaving the order to the JVM leaves those answers to the JVM too, and a run that picked the
     * right one is no evidence about the next.
     */
    private void enqueueReferences(Object current, ArrayDeque<Object> pending) {
        if (current instanceof Iterable) {
            for (Object item : (Iterable<?>) current) {
                if (item != null) {
                    pending.addLast(item);
                }
            }
            return;
        }
        if (current instanceof Map) {
            for (Object item : ((Map<?, ?>) current).values()) {
                if (item != null) {
                    pending.addLast(item);
                }
            }
            return;
        }
        if (current.getClass().isArray()) {
            int len = Array.getLength(current);
            for (int i = 0; i < len; i++) {
                Object item = Array.get(current, i);
                if (item != null) {
                    pending.addLast(item);
                }
            }
            return;
        }
        if (!shouldTraverseObject(current.getClass())) {
            return;
        }

        for (Class<?> c = current.getClass(); c != null && c != Object.class; c = c.getSuperclass()) {
            for (Field field : traversableFieldsOf(c)) {
                try {
                    Object value = field.get(current);
                    if (value != null && !isLeafValue(value)) {
                        pending.addLast(value);
                    }
                } catch (Exception e) {
                    // ignore inaccessible fields
                }
            }
        }
    }

    /**
     * {@code cls}'s own non-static reference fields, name-ordered and already made accessible.
     *
     * <p>Remembered per class for the session, which a field list can be: a loaded class does not
     * grow fields. That makes the sort free after the first visit, and the walks visit the same few
     * hundred model classes over and over.
     */
    private List<Field> traversableFieldsOf(Class<?> cls) {
        List<Field> remembered = traversableFieldsByClass.get(cls);
        if (remembered != null) {
            return remembered;
        }

        List<Field> fields = new ArrayList<Field>();
        for (Field field : cls.getDeclaredFields()) {
            if (Modifier.isStatic(field.getModifiers()) || field.getType().isPrimitive()) {
                continue;
            }
            try {
                field.setAccessible(true);
            } catch (RuntimeException e) {
                // Not readable here; keep it anyway so the get() below reports it the same way it
                // always did, rather than making inaccessibility look like absence.
            }
            fields.add(field);
        }
        Collections.sort(fields, Comparator.comparing(Field::getName));

        traversableFieldsByClass.put(cls, fields);
        return fields;
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

    /**
     * The loaded class with this name, or null.
     *
     * <p>A linear scan of every class the JVM has loaded — thousands, in an app the size of this
     * one. That is affordable at setup and was not on the validation path, where resolving a single
     * error's {@code from} asks for four classes by name and a roster can carry dozens of errors.
     * Hence {@link #classesByName}, which remembers what it finds.
     */
    private Class<?> findClass(String name) {
        Class<?> cached = classesByName.get(name);
        if (cached != null) {
            return cached;
        }

        for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
            if (cls.getName().equals(name)) {
                classesByName.put(name, cls);
                return cls;
            }
        }
        return null;
    }

}
