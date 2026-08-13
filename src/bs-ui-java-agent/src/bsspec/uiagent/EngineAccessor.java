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
    // Cached handle for the patched engine's bsspecErrorId field (see readErrorId).
    private java.lang.reflect.Field errorIdField;
    private boolean errorIdFieldResolved;

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

        /** constraintId -> source container ids that declare it — see {@link #constraintDeclarers}. */
        Map<String, Set<String>> constraintDeclarers;
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
     * Which segment of a composite id actually DECLARES {@code constraintId}, matching the in-process
     * adapter's rule exactly.
     *
     * <p>A constraint on an entry link and one on the link's target are different constraints (per-link
     * vs shared) and a spec names {@code from} by the declaring one. This must read the SOURCE
     * declaration, not the live merged entry: a constraint declared on a shared target is MERGED into
     * every link's expansion, so asking the runtime "does this segment carry the constraint" answers
     * yes for the outer link too and would pick the route rather than the declarer. So it consults a
     * map built once from the loaded game data's source containers, and returns the first segment that
     * genuinely declares the id (falling back to the whole composite when none does).
     */
    private String declaringEntryOf(String entryId, String constraintId) {
        if (entryId == null || constraintId == null || !entryId.contains("::")) {
            return entryId;
        }
        String[] segments = entryId.split("::");
        Set<String> declarers = constraintDeclarers().get(constraintId);
        if (declarers != null) {
            for (String segment : segments) {
                if (declarers.contains(segment)) {
                    return segment;
                }
            }
        }
        // The map indexes constraints on entries, groups, force-entries and categories from source;
        // a constraint it does NOT know is one declared on the entry LINK itself (a live EntryLink
        // does not surface its own constraints the way a source entry does). Its declaring container
        // is the outermost link -- the first segment -- not the target the link resolves to.
        return segments[0];
    }

    /**
     * constraintId -> the SOURCE container ids that declare it, read once from the loaded game
     * system and catalogues. The mirror of {@code BattleScribeEngine}'s constraint-declarer index,
     * built from the same source shape so both BattleScribe lanes choose the same {@code from} entry.
     */
    private Map<String, Set<String>> constraintDeclarers() {
        ValidationPass pass = validationPass;
        if (pass != null && pass.constraintDeclarers != null) {
            return pass.constraintDeclarers;
        }
        Map<String, Set<String>> map = new HashMap<String, Set<String>>();
        try {
            indexSourceConstraints(getCurrentGameSystem(), map);
            for (Object catalogue : sourceCatalogues()) {
                indexSourceConstraints(catalogue, map);
            }
        } catch (Exception e) {
            System.err.println("[bs-ui-agent] could not index source constraint declarers: " + e);
        }
        if (pass != null) {
            pass.constraintDeclarers = map;
        }
        return map;
    }

    /**
     * The loaded source catalogues. Found by the object-graph scan rather than a named getter: the
     * engine's {@code q()} is overridden on the engine class to return validation errors, so a
     * reflective {@code q()} is ambiguous; {@link #collectInstances} reaches every {@code Catalogue}
     * referenced from the engine/roster and is cached per pass.
     */
    private List<Object> sourceCatalogues() {
        Class<?> catalogueClass = findClass("net.battlescribe.model.data.Catalogue");
        return catalogueClass == null ? Collections.<Object>emptyList() : collectInstances(catalogueClass);
    }

    /**
     * Walks a game system / catalogue / entry / group / link / force-entry, recording every
     * constraint id against the SOURCE container that declares it. Recurses the same containers the
     * in-process walker does (docs/entry-id-construction.md shapes).
     */
    private void indexSourceConstraints(Object container, Map<String, Set<String>> map) {
        if (container == null) {
            return;
        }
        String declarerId = callGetter(container, "getId");
        for (Object constraint : toJavaList(callListGetter(container, "getConstraints"))) {
            String id = callGetter(constraint, "getId");
            if (id != null && declarerId != null) {
                Set<String> declarers = map.get(id);
                if (declarers == null) {
                    declarers = new java.util.HashSet<String>();
                    map.put(id, declarers);
                }
                declarers.add(declarerId);
            }
        }
        for (String getter : SOURCE_CHILD_GETTERS) {
            for (Object child : toJavaList(callListGetter(container, getter))) {
                indexSourceConstraints(child, map);
            }
        }
    }

    private static final String[] SOURCE_CHILD_GETTERS = {
        "getSelectionEntries", "getSharedSelectionEntries",
        "getSelectionEntryGroups", "getSharedSelectionEntryGroups",
        "getEntryLinks", "getForceEntries", "getCategoryEntries",
    };


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
        String ownerId = callGetter(element, "getId");
        // The owner as a spec names it: the element's own target entry, not the link route to it.
        String ownerEntryId = linkTargetOf(callGetter(element, "getEntryId"));
        List<Object> costLimits = "roster".equals(ownerType)
                ? toJavaList(callListGetter(element, "getCostLimits"))
                : null;

        for (Object error : toJavaList(elementErrors)) {
            String message = extractValidationMessage(error);
            String rawId = readErrorId(error);

            JsonObject item = new JsonObject();
            item.addProperty("message", message);
            item.addProperty("ownerType", ownerType);
            if (ownerId != null) {
                item.addProperty("ownerId", ownerId);
            }
            if (ownerEntryId != null) {
                item.addProperty("ownerEntryId", ownerEntryId);
            }

            if (rawId == null) {
                // The one funneled error the engine builds with no id is the roster cost-limit
                // overrun (a.f#v(), added directly). Resolve it by cost name -- the one documented
                // prose path. Anything else without an id is a bug (a missed transform, an engine
                // change) and is reported loudly rather than guessed at.
                String[] cl = "roster".equals(ownerType) ? resolveCostLimitByName(message, costLimits) : null;
                if (cl != null) {
                    item.addProperty("entryId", cl[0]);
                    item.addProperty("constraintId", cl[1]);
                } else {
                    System.err.println("[bs-ui-agent] validation error on " + ownerType
                            + " carried no bsspecErrorId and is not the roster cost-limit bypass: \""
                            + message + "\" -- refusing to guess attribution.");
                }
                errors.add(item);
                continue;
            }

            String[] parsed = parseOneErrorId(rawId);
            String entryId = declaringEntryOf(parsed[0], parsed[1]);
            String constraintId = parsed[1];
            // The engine writes the same third segment "collective" for a hidden-entry error and a
            // collective (same-number) error; the id cannot tell them apart. "(hidden)" in the
            // message means the hidden case, asserted as the reserved pseudo-constraint "hidden".
            if ("collective".equals(constraintId) && message != null
                    && message.toLowerCase(Locale.ROOT).contains("(hidden)")) {
                constraintId = "hidden";
            }
            item.addProperty("entryId", entryId);
            item.addProperty("constraintId", constraintId);

            String[] meta = constraintMetaOf(entryId, constraintId);
            if (meta != null) {
                item.addProperty("constraintType", meta[0]);
                item.addProperty("constraintField", meta[1]);
            }
            errors.add(item);
        }
    }

    /**
     * The constraint id the patched engine hangs on each validation error (bsspecErrorId). Resolves
     * the field once and fails loudly if it is absent -- an unpatched engine must not silently
     * degrade to message-text guessing (mirrors the in-process guard).
     */
    private String readErrorId(Object error) {
        if (error == null) {
            return null;
        }
        if (!errorIdFieldResolved) {
            try {
                errorIdField = error.getClass().getField("bsspecErrorId");
            } catch (NoSuchFieldException e) {
                errorIdField = null;
            }
            errorIdFieldResolved = true;
            if (errorIdField == null) {
                throw new IllegalStateException(
                        "BattleScribe engine error type " + error.getClass().getName()
                        + " has no bsspecErrorId field: the ErrorIdTransformer did not run (see"
                        + " BsUiAgent.premain / src/bs-engine-patch). Refusing message-text attribution.");
            }
        }
        try {
            Object v = errorIdField.get(error);
            return v == null ? null : v.toString();
        } catch (IllegalAccessException e) {
            return null;
        }
    }

    /**
     * Splits one {@code ownerId::entryId::constraintId} id into {entryId, constraintId} by the same
     * rule as {@code BattleScribeErrorIds.ParseOne}: owner dropped, middle segments rejoined as the
     * (possibly link-composite) entry, last segment the constraint.
     */
    private String[] parseOneErrorId(String rawId) {
        String[] parts = rawId.split("::", -1);
        if (parts.length < 3) {
            return new String[] {null, null};
        }
        StringBuilder entryId = new StringBuilder(parts[1]);
        for (int i = 2; i < parts.length - 1; i++) {
            entryId.append("::").append(parts[i]);
        }
        return new String[] {entryId.toString(), parts[parts.length - 1]};
    }

    /** A constraint's {kind, field} on its declaring entry, or null for a pseudo/unknown id. */
    private String[] constraintMetaOf(String entryId, String constraintId) {
        if (entryId == null || constraintId == null) {
            return null;
        }
        try {
            Object entry = findEntryById(entryId);
            if (entry == null) {
                return null;
            }
            for (Object constraint : toJavaList(callListGetter(entry, "getConstraints"))) {
                if (constraintId.equals(callGetter(constraint, "getId"))) {
                    return new String[] {callGetter(constraint, "getType"), callGetter(constraint, "getField")};
                }
            }
        } catch (Exception e) {
            // A lookup failure just means placement falls back to leaving the error where it is.
        }
        return null;
    }

    /** The roster cost-limit overrun's {entryId, constraintId}, matched by cost name. */
    private String[] resolveCostLimitByName(String message, List<Object> costLimits) {
        if (message == null || costLimits == null) {
            return null;
        }
        for (Object limit : costLimits) {
            try {
                String costName = callGetter(limit, "getName");
                if (costName != null && message.contains(costName)) {
                    return new String[] {"costLimits", callGetter(limit, "getTypeId")};
                }
            } catch (Exception e) {
                // skip
            }
        }
        return null;
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
