package bsspec.uiagent;

import com.google.gson.JsonElement;
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
import java.util.concurrent.CountDownLatch;

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

    // Synchronization for async engine operations (selectEntry/deselectEntry)
    private volatile CountDownLatch engineOpLatch;

    public EngineAccessor(Instrumentation instrumentation) {
        this.instrumentation = instrumentation;
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

    private static int getInt(JsonObject params, String key, int defaultValue) {
        JsonElement value = params.get(key);
        return value != null && !value.isJsonNull() ? value.getAsInt() : defaultValue;
    }

    private static double getDouble(JsonObject params, String key, double defaultValue) {
        JsonElement value = params.get(key);
        return value != null && !value.isJsonNull() ? value.getAsDouble() : defaultValue;
    }

    /**
     * Lists all loaded classes in the net.battlescribe package.
     * Used for discovery/exploration.
     */
    public String listBsClasses() {
        StringBuilder sb = new StringBuilder("[");
        boolean first = true;
        for (Class<?> cls : instrumentation.getAllLoadedClasses()) {
            String name = cls.getName();
            if (name.startsWith("net.battlescribe.")) {
                if (!first) sb.append(",");
                first = false;
                sb.append("{\"name\":\"").append(name).append("\"");
                sb.append(",\"simple\":\"").append(cls.getSimpleName()).append("\"");
                sb.append(",\"loader\":\"").append(cls.getClassLoader()).append("\"");
                sb.append("}");
            }
        }
        sb.append("]");
        return sb.toString();
    }

    /**
     * Inspects a class by name, listing fields and methods.
     */
    public String inspectClass(String className) {
        Class<?> cls = findClass(className);
        if (cls == null) {
            return "{\"error\":\"Class not found: " + className + "\"}";
        }

        StringBuilder sb = new StringBuilder("{");
        sb.append("\"name\":\"").append(cls.getName()).append("\"");
        sb.append(",\"superclass\":\"").append(cls.getSuperclass() != null ? cls.getSuperclass().getName() : "null").append("\"");

        // Fields (including inherited)
        sb.append(",\"fields\":[");
        List<Field> allFields = new ArrayList<>();
        Class<?> c = cls;
        while (c != null && !c.getName().equals("java.lang.Object")) {
            for (Field f : c.getDeclaredFields()) {
                allFields.add(f);
            }
            c = c.getSuperclass();
        }
        for (int i = 0; i < allFields.size(); i++) {
            if (i > 0) sb.append(",");
            Field f = allFields.get(i);
            sb.append("{\"name\":\"").append(f.getName()).append("\"");
            sb.append(",\"type\":\"").append(f.getType().getName()).append("\"");
            sb.append(",\"modifiers\":\"").append(Modifier.toString(f.getModifiers())).append("\"");
            sb.append(",\"declaringClass\":\"").append(f.getDeclaringClass().getSimpleName()).append("\"");
            sb.append("}");
        }
        sb.append("]");

        // Methods (non-Object, declared only)
        sb.append(",\"methods\":[");
        Method[] methods = cls.getDeclaredMethods();
        boolean mFirst = true;
        for (Method m : methods) {
            if (m.getDeclaringClass() == Object.class) continue;
            if (!mFirst) sb.append(",");
            mFirst = false;
            sb.append("{\"name\":\"").append(m.getName()).append("\"");
            sb.append(",\"returnType\":\"").append(m.getReturnType().getName()).append("\"");
            sb.append(",\"params\":[");
            Class<?>[] params = m.getParameterTypes();
            for (int j = 0; j < params.length; j++) {
                if (j > 0) sb.append(",");
                sb.append("\"").append(params[j].getName()).append("\"");
            }
            sb.append("]");
            sb.append(",\"modifiers\":\"").append(Modifier.toString(m.getModifiers())).append("\"");
            sb.append("}");
        }
        sb.append("]");

        sb.append("}");
        return sb.toString();
    }

    /**
     * Attempts to find the engine instance.
     * Strategy: find the FXML controller via scene graph node properties,
     * then read its engine field.
     */
    public String findEngine() {
        if (engineInstance != null) {
            return "{\"found\":true,\"engineClass\":\"" + engineClass.getName() + "\",\"cached\":true}";
        }

        engineClass = findClass("net.battlescribe.engine.a.f");
        if (engineClass == null) {
            return "{\"found\":false,\"error\":\"Engine class net.battlescribe.engine.a.f not loaded\"}";
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
                            return "{\"found\":true,\"engineClass\":\"" + engineClass.getName()
                                    + "\",\"via\":\"handler.controller.b\"}";
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
                    return "{\"found\":true,\"engineClass\":\"" + engineClass.getName()
                            + "\",\"via\":\"controller.b\"}";
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
                            return "{\"found\":true,\"engineClass\":\"" + engineClass.getName()
                                    + "\",\"via\":\"static:" + name + "." + f.getName() + "\"}";
                        }
                    } catch (Exception e) {
                        tried.add("static_error:" + e.getMessage());
                    }
                }
            }
        }

        StringBuilder sb = new StringBuilder("{\"found\":false,\"tried\":[");
        for (int i = 0; i < tried.size(); i++) {
            if (i > 0) sb.append(",");
            sb.append("\"").append(escapeJson(tried.get(i))).append("\"");
        }
        sb.append("]}");
        return sb.toString();
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
     * Sets the engine instance from an external source (e.g., found via controller).
     */
    public void setEngineInstance(Object engine) {
        this.engineInstance = engine;
        this.engineClass = engine.getClass();
        cacheRosterAccess();
        patchEngineThreadCount(engine);
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
     * Reads all static fields of a class and returns their values.
     */
    public String readStaticFields(String className) {
        Class<?> cls = findClass(className);
        if (cls == null) {
            return "{\"error\":\"Class not found: " + className + "\"}";
        }
        StringBuilder sb = new StringBuilder("{\"className\":\"" + cls.getName() + "\",\"fields\":[");
        boolean first = true;
        // Walk class hierarchy
        Class<?> c = cls;
        while (c != null && !c.getName().equals("java.lang.Object")) {
            for (Field f : c.getDeclaredFields()) {
                if (!Modifier.isStatic(f.getModifiers())) continue;
                if (!first) sb.append(",");
                first = false;
                f.setAccessible(true);
                sb.append("{\"name\":\"").append(f.getName()).append("\"");
                sb.append(",\"type\":\"").append(f.getType().getName()).append("\"");
                sb.append(",\"declaringClass\":\"").append(c.getSimpleName()).append("\"");
                try {
                    Object val = f.get(null);
                    if (val == null) {
                        sb.append(",\"value\":null");
                    } else {
                        sb.append(",\"valueType\":\"").append(val.getClass().getName()).append("\"");
                        String str = val.toString();
                        if (str.length() > 200) str = str.substring(0, 200) + "...";
                        sb.append(",\"value\":\"").append(escapeJson(str)).append("\"");
                    }
                } catch (Exception e) {
                    sb.append(",\"error\":\"").append(escapeJson(e.getMessage())).append("\"");
                }
                sb.append("}");
            }
            c = c.getSuperclass();
        }
        sb.append("]}");
        return sb.toString();
    }

    /**
     * Reads the current roster state as JSON.
     * Requires the engine to have been found first.
     */
    public String getRosterState() {
        if (engineInstance == null) {
            return "{\"error\":\"Engine not found. Call findEngine first.\"}";
        }

        try {
            Object roster = getRosterMethod.invoke(engineInstance);
            if (roster == null) {
                return "{\"error\":\"No roster loaded\"}";
            }
            return serializeRoster(roster);
        } catch (Exception e) {
            String msg = e.getClass().getSimpleName() + ": " + e.getMessage();
            if (e instanceof java.lang.reflect.InvocationTargetException) {
                Throwable cause = ((java.lang.reflect.InvocationTargetException) e).getTargetException();
                if (cause != null) {
                    msg += " [cause: " + cause.getClass().getSimpleName() + ": " + cause.getMessage() + "]";
                }
            }
            return "{\"error\":\"" + escapeJson(msg) + "\"}";
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
            return "{\"set\":true}";
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
            return "{\"xml\":" + jsonStr(xml) + "}";
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
            if (roster == null) {
                return "[]";
            }

            StringBuilder sb = new StringBuilder("[");
            boolean[] first = new boolean[] { true };
            collectValidationErrors(roster, "roster", sb, first);
            for (Object force : toJavaList(callListGetter(roster, "getForces"))) {
                collectForceValidationErrors(force, sb, first);
            }
            sb.append("]");
            return sb.toString();
        } catch (Exception e) {
            return errorJson("getValidationErrors failed: " + buildExceptionMessage(e));
        }
    }

    /**
     * Reads roster state including forces, selections, costs.
     */
    private String serializeRoster(Object roster) throws Exception {
        Class<?> rClass = roster.getClass();
        StringBuilder sb = new StringBuilder("{");

        sb.append("\"name\":").append(jsonStr(callGetter(roster, "getName")));
        sb.append(",\"gameSystemId\":").append(jsonStr(callGetter(roster, "getGameSystemId")));
        sb.append(",\"gameSystemName\":").append(jsonStr(callGetter(roster, "getGameSystemName")));

        // Costs
        sb.append(",\"costs\":").append(serializeCostList(callListGetter(roster, "getCosts")));
        sb.append(",\"costLimits\":").append(serializeCostList(callListGetter(roster, "getCostLimits")));

        // Forces
        Object forcesList = callListGetter(roster, "getForces");
        sb.append(",\"forces\":").append(serializeForceList(forcesList));

        sb.append("}");
        return sb.toString();
    }

    private String serializeForceList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            if (i > 0) sb.append(",");
            Object force = list.getClass().getMethod("get", int.class).invoke(list, i);
            sb.append(serializeForce(force));
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeForce(Object force) throws Exception {
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"id\":").append(jsonStr(callGetter(force, "getId")));
        sb.append(",\"name\":").append(jsonStr(callGetter(force, "getName")));
        sb.append(",\"catalogueId\":").append(jsonStr(callGetter(force, "getCatalogueId")));
        sb.append(",\"entryId\":").append(jsonStr(callGetter(force, "getEntryId")));
        sb.append(",\"catalogueName\":").append(jsonStr(callGetter(force, "getCatalogueName")));
        sb.append(",\"customName\":").append(jsonStr(callGetter(force, "getCustomName")));
        sb.append(",\"customNotes\":").append(jsonStr(callGetter(force, "getCustomNotes")));
        sb.append(",\"hidden\":").append(resolveForceHidden(force));
        sb.append(",\"publicationId\":").append(jsonStr(callGetter(force, "getPublicationId")));
        sb.append(",\"page\":").append(jsonStr(callGetter(force, "getPage")));

        // Rules — Force.getRules() returns List<Rule>
        Object ruleList = callListGetter(force, "getRules");
        sb.append(",\"rules\":").append(serializeRuleList(ruleList));

        // Categories — Force.getCategories() returns List<Category>
        Object catList = callListGetter(force, "getCategories");
        sb.append(",\"categories\":").append(serializeCategoryList(catList));

        // Publications — Force.getPublications() returns ArrayList<Publication>
        Object pubList = callListGetter(force, "getPublications");
        sb.append(",\"publications\":").append(serializePublicationList(pubList));

        // Build publication name lookup from force publications
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

        // Selections
        Object selList = callListGetter(force, "getSelections");
        sb.append(",\"selections\":").append(serializeSelectionList(selList, pubNameMap, force));

        // Child forces
        Object childForces = callListGetter(force, "getForces");
        sb.append(",\"childForces\":").append(serializeForceList(childForces));

        // Debug: dump class name if categories are empty (for troubleshooting)
        if (catList == null) {
            sb.append(",\"_debug_class\":").append(jsonStr(force.getClass().getName()));
            StringBuilder methods = new StringBuilder();
            for (Method m : force.getClass().getMethods()) {
                if (m.getName().startsWith("get") && m.getParameterCount() == 0) {
                    if (methods.length() > 0) methods.append(",");
                    methods.append(m.getName());
                }
            }
            sb.append(",\"_debug_methods\":").append(jsonStr(methods.toString()));
        }

        sb.append("}");
        return sb.toString();
    }

    private String serializeSelectionList(Object list, Map<String, String> pubNameMap, Object force) throws Exception {
        if (list == null) return "[]";
        int size = (int) list.getClass().getMethod("size").invoke(list);
        // Sort selections alphabetically by name (case-insensitive) to match BattleScribe render-layer ordering
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
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < items.size(); i++) {
            if (i > 0) sb.append(",");
            sb.append(serializeSelection(items.get(i), pubNameMap, force));
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeSelection(Object sel, Map<String, String> pubNameMap, Object force) throws Exception {
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"id\":").append(jsonStr(callGetter(sel, "getId")));
        sb.append(",\"name\":").append(jsonStr(callGetter(sel, "getName")));
        sb.append(",\"entryId\":").append(jsonStr(callGetter(sel, "getEntryId")));
        sb.append(",\"entryGroupId\":").append(jsonStr(callGetter(sel, "getEntryGroupId")));
        sb.append(",\"page\":").append(jsonStr(callGetter(sel, "getPage")));
        String pubId = callGetter(sel, "getPublicationId");
        sb.append(",\"publicationId\":").append(jsonStr(pubId));
        sb.append(",\"publicationName\":").append(jsonStr(pubId != null ? pubNameMap.get(pubId) : null));
        sb.append(",\"customName\":").append(jsonStr(callGetter(sel, "getCustomName")));
        sb.append(",\"customNotes\":").append(jsonStr(callGetter(sel, "getCustomNotes")));
        sb.append(",\"categories\":").append(serializeCategoryList(callListGetter(sel, "getCategories")));
        sb.append(",\"profiles\":").append(serializeProfileList(callListGetter(sel, "getProfiles")));
        sb.append(",\"rules\":").append(serializeRuleList(callListGetter(sel, "getRules")));

        // Type (enum → lowercase name)
        try {
            Method m = sel.getClass().getMethod("getType");
            Object type = m.invoke(sel);
            sb.append(",\"type\":").append(jsonStr(type != null ? type.toString().toLowerCase() : null));
        } catch (Exception e) {
            sb.append(",\"type\":null");
        }

        // Number (getNumber returns int)
        try {
            Method m = sel.getClass().getMethod("getNumber");
            sb.append(",\"number\":").append(m.invoke(sel));
        } catch (Exception e) {
            sb.append(",\"number\":1");
        }

        boolean selHidden = resolveSelectionHidden(force, sel);
        sb.append(",\"hidden\":").append(selHidden);

        // Costs
        sb.append(",\"costs\":").append(serializeCostList(callListGetter(sel, "getCosts")));

        // Child selections
        Object children = callListGetter(sel, "getSelections");
        sb.append(",\"children\":").append(serializeSelectionList(children, pubNameMap, force));

        sb.append("}");
        return sb.toString();
    }

    private String serializeCostList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            if (i > 0) sb.append(",");
            Object cost = list.getClass().getMethod("get", int.class).invoke(list, i);
            sb.append("{");
            sb.append("\"name\":").append(jsonStr(callGetter(cost, "getName")));
            sb.append(",\"typeId\":").append(jsonStr(callGetter(cost, "getTypeId")));
            try {
                Method m = cost.getClass().getMethod("getValue");
                sb.append(",\"value\":").append(m.invoke(cost));
            } catch (Exception e) {
                sb.append(",\"value\":0");
            }
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeCategoryList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        List<Object> categories = toJavaList(list);
        for (int i = 0; i < categories.size(); i++) {
            if (i > 0) sb.append(",");
            Object category = categories.get(i);
            sb.append("{");
            sb.append("\"name\":").append(jsonStr(callGetter(category, "getName")));
            sb.append(",\"entryId\":").append(jsonStr(callGetter(category, "getEntryId")));
            try {
                Method m = findMethod(category.getClass(), "isPrimary");
                sb.append(",\"primary\":").append(m != null ? m.invoke(category) : false);
            } catch (Exception e) {
                sb.append(",\"primary\":false");
            }
            sb.append(",\"customNotes\":").append(jsonStr(callGetter(category, "getCustomNotes")));
            sb.append(",\"publicationId\":").append(jsonStr(callGetter(category, "getPublicationId")));
            sb.append(",\"page\":").append(jsonStr(callGetter(category, "getPage")));
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializePublicationList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        List<Object> publications = toJavaList(list);
        for (int i = 0; i < publications.size(); i++) {
            if (i > 0) sb.append(",");
            Object publication = publications.get(i);
            sb.append("{");
            sb.append("\"id\":").append(jsonStr(callGetter(publication, "getId")));
            sb.append(",\"name\":").append(jsonStr(callGetter(publication, "getName")));
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeProfileList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        List<Object> profiles = toJavaList(list);
        for (int i = 0; i < profiles.size(); i++) {
            if (i > 0) sb.append(",");
            Object profile = profiles.get(i);
            sb.append("{");
            sb.append("\"name\":").append(jsonStr(callGetter(profile, "getName")));
            sb.append(",\"typeId\":").append(jsonStr(callGetter(profile, "getTypeId")));
            sb.append(",\"typeName\":").append(jsonStr(callGetter(profile, "getTypeName")));
            try {
                Method m = findMethod(profile.getClass(), "isHidden");
                sb.append(",\"hidden\":").append(m != null ? m.invoke(profile) : false);
            } catch (Exception e) {
                sb.append(",\"hidden\":false");
            }
            sb.append(",\"page\":").append(jsonStr(callGetter(profile, "getPage")));
            sb.append(",\"publicationId\":").append(jsonStr(callGetter(profile, "getPublicationId")));
            sb.append(",\"publicationName\":").append(jsonStr(callGetter(profile, "getPublicationName")));
            sb.append(",\"characteristics\":").append(serializeCharacteristicList(callListGetter(profile, "getCharacteristics")));
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeRuleList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        List<Object> rules = toJavaList(list);
        for (int i = 0; i < rules.size(); i++) {
            if (i > 0) sb.append(",");
            Object rule = rules.get(i);
            sb.append("{");
            sb.append("\"name\":").append(jsonStr(callGetter(rule, "getName")));
            sb.append(",\"description\":").append(jsonStr(callGetter(rule, "getDescription")));
            try {
                Method m = findMethod(rule.getClass(), "isHidden");
                sb.append(",\"hidden\":").append(m != null ? m.invoke(rule) : false);
            } catch (Exception e) {
                sb.append(",\"hidden\":false");
            }
            sb.append(",\"page\":").append(jsonStr(callGetter(rule, "getPage")));
            sb.append(",\"publicationId\":").append(jsonStr(callGetter(rule, "getPublicationId")));
            sb.append(",\"publicationName\":").append(jsonStr(callGetter(rule, "getPublicationName")));
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeCharacteristicList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        List<Object> characteristics = toJavaList(list);
        for (int i = 0; i < characteristics.size(); i++) {
            if (i > 0) sb.append(",");
            Object characteristic = characteristics.get(i);
            sb.append("{");
            sb.append("\"name\":").append(jsonStr(callGetter(characteristic, "getName")));
            sb.append(",\"typeId\":").append(jsonStr(callGetter(characteristic, "getTypeId")));
            sb.append(",\"value\":").append(jsonStr(callGetter(characteristic, "getValue")));
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
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

    private Object findForceById(String forceId) throws Exception {
        Object roster = getCurrentRoster();
        for (Object force : toJavaList(callListGetter(roster, "getForces"))) {
            Object found = findForceByIdRecursive(force, forceId);
            if (found != null) {
                return found;
            }
        }
        throw new IllegalArgumentException("Force '" + forceId + "' not found.");
    }

    private Object findForceContainingSelection(String selectionId) throws Exception {
        Object roster = getCurrentRoster();
        for (Object force : toJavaList(callListGetter(roster, "getForces"))) {
            if (tryFindSelectionById(force, selectionId) != null) {
                return force;
            }
        }
        return null;
    }

    private Object findForceByIdRecursive(Object force, String forceId) {
        if (matchesId(callGetter(force, "getId"), forceId)) {
            return force;
        }
        for (Object child : toJavaList(callListGetter(force, "getForces"))) {
            Object found = findForceByIdRecursive(child, forceId);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private Object findSelectionById(Object force, String selectionId) {
        Object found = tryFindSelectionById(force, selectionId);
        if (found != null) {
            return found;
        }
        throw new IllegalArgumentException(
                "Selection '" + selectionId + "' not found under force '" + callGetter(force, "getId") + "'.");
    }

    private Object tryFindSelectionById(Object force, String selectionId) {
        for (Object selection : toJavaList(callListGetter(force, "getSelections"))) {
            Object found = findSelectionByIdRecursive(selection, selectionId);
            if (found != null) {
                return found;
            }
        }
        for (Object childForce : toJavaList(callListGetter(force, "getForces"))) {
            Object found = tryFindSelectionById(childForce, selectionId);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private Object findSelectionByIdRecursive(Object selection, String selectionId) {
        if (matchesId(callGetter(selection, "getId"), selectionId)) {
            return selection;
        }
        for (Object child : toJavaList(callListGetter(selection, "getSelections"))) {
            Object found = findSelectionByIdRecursive(child, selectionId);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private Object findSelectionParent(Object force, String selectionId) {
        return tryFindSelectionParent(force, selectionId);
    }

    private Object tryFindSelectionParent(Object force, String selectionId) {
        for (Object selection : toJavaList(callListGetter(force, "getSelections"))) {
            if (matchesId(callGetter(selection, "getId"), selectionId)) {
                return force;
            }
            Object found = findSelectionParentRecursive(selection, selectionId);
            if (found != null) {
                return found;
            }
        }
        for (Object childForce : toJavaList(callListGetter(force, "getForces"))) {
            Object found = tryFindSelectionParent(childForce, selectionId);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    private Object findSelectionParentRecursive(Object selection, String selectionId) {
        for (Object child : toJavaList(callListGetter(selection, "getSelections"))) {
            if (matchesId(callGetter(child, "getId"), selectionId)) {
                return selection;
            }
            Object found = findSelectionParentRecursive(child, selectionId);
            if (found != null) {
                return found;
            }
        }
        return null;
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

    private Object resolveEntryForSelection(Object force, Object selection) throws Exception {
        Object forceContext = getForceContext(force);
        if (forceContext == null) {
            return null;
        }

        String entryId = callGetter(selection, "getEntryId");
        Method method = findMethod(forceContext.getClass(), "i", new Class<?>[] { String.class }, Object.class);
        if (method == null) {
            return null;
        }

        for (String candidate : candidateIds(entryId)) {
            Object found = method.invoke(forceContext, candidate);
            if (found != null) {
                return found;
            }
        }
        return null;
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

    private Object findCatalogueById(String catalogueId) throws Exception {
        Class<?> catalogueClass = findClass("net.battlescribe.model.data.Catalogue");
        if (catalogueClass == null) {
            return null;
        }
        Object roster = getCurrentRoster();
        Object catalogueManager = getCatalogueManager();
        return findObjectById(catalogueClass, catalogueId, catalogueManager, engineInstance, roster);
    }

    private Object findForceEntryById(String forceEntryId, Object gameSystem, Object catalogue) throws Exception {
        Class<?> forceEntryClass = findClass("net.battlescribe.model.data.ForceEntry");
        if (forceEntryClass == null) {
            return null;
        }
        Object roster = getCurrentRoster();
        return findObjectById(forceEntryClass, forceEntryId, gameSystem, catalogue, engineInstance, roster);
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

    private void collectForceValidationErrors(Object force, StringBuilder sb, boolean[] first) throws Exception {
        collectValidationErrors(force, "force", sb, first);
        for (Object category : toJavaList(callListGetter(force, "getCategories"))) {
            collectValidationErrors(category, "category", sb, first);
        }
        for (Object selection : toJavaList(callListGetter(force, "getSelections"))) {
            collectSelectionValidationErrors(selection, sb, first);
        }
        for (Object childForce : toJavaList(callListGetter(force, "getForces"))) {
            collectForceValidationErrors(childForce, sb, first);
        }
    }

    private void collectSelectionValidationErrors(Object selection, StringBuilder sb, boolean[] first) throws Exception {
        collectValidationErrors(selection, "selection", sb, first);
        for (Object category : toJavaList(callListGetter(selection, "getCategories"))) {
            collectValidationErrors(category, "category", sb, first);
        }
        for (Object child : toJavaList(callListGetter(selection, "getSelections"))) {
            collectSelectionValidationErrors(child, sb, first);
        }
    }

    private void collectValidationErrors(Object element, String ownerType, StringBuilder sb, boolean[] first)
            throws Exception {
        Object errors = callListGetter(element, "getValidationErrors");
        Object errorIds = callListGetter(element, "getValidationErrorIds");
        List<String> errorIdList = extractStrings(errorIds);
        Map<String, List<String>> errorIdMap = parseValidationErrorIds(errorIdList);
        String ownerEntryId = callGetter(element, "getEntryId");
        for (Object error : toJavaList(errors)) {
            if (!first[0]) {
                sb.append(",");
            }
            first[0] = false;
            String message = extractValidationMessage(error);
            ValidationRef validationRef = resolveValidationRef(errorIdMap, ownerType, message, ownerEntryId);
            sb.append("{");
            sb.append("\"message\":").append(jsonStr(message));
            sb.append(",\"ownerType\":").append(jsonStr(ownerType));
            sb.append(",\"ownerId\":").append(jsonStr(callGetter(element, "getId")));
            if (ownerEntryId != null) {
                sb.append(",\"ownerEntryId\":").append(jsonStr(ownerEntryId));
            }
            if (validationRef.entryId != null) {
                sb.append(",\"entryId\":").append(jsonStr(validationRef.entryId));
            }
            if (validationRef.constraintId != null) {
                sb.append(",\"constraintId\":").append(jsonStr(validationRef.constraintId));
            }
            if (!errorIdList.isEmpty()) {
                sb.append(",\"errorIds\":").append(serializeStringList(errorIdList));
            }
            sb.append("}");
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

    private List<String> collectSelectionIdsByEntry(Object parent, String entryId) {
        List<String> result = new ArrayList<String>();
        for (Object child : toJavaList(callListGetter(parent, "getSelections"))) {
            if (matchesId(callGetter(child, "getEntryId"), entryId)) {
                String id = callGetter(child, "getId");
                if (id != null) {
                    result.add(id);
                }
            }
        }
        return result;
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

    private List<String> extractIds(Object values) {
        List<String> ids = new ArrayList<String>();
        for (Object value : toJavaList(values)) {
            String id = callGetter(value, "getId");
            if (id != null) {
                ids.add(id);
            }
        }
        return ids;
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

    private String serializeStringList(List<String> values) {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < values.size(); i++) {
            if (i > 0) {
                sb.append(",");
            }
            sb.append(jsonStr(values.get(i)));
        }
        sb.append("]");
        return sb.toString();
    }

    private String errorJson(String message) {
        return "{\"error\":" + jsonStr(message) + "}";
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
     * Explicitly invokes the controller's catalogue tree rebuild method.
     * Runs off-FX-thread: waits for engine pool to settle, then schedules all work on FX thread.
     */
    public String rebuildCatalogueTree(String params) {
        try {
            // Wait for engine thread pool to settle after bg thread operations
            Thread.sleep(1000);

            // Do everything on FX thread (scene graph access + p() invocation)
            final CountDownLatch fxDone = new CountDownLatch(1);
            final String[] result = {null};
            javafx.application.Platform.runLater(() -> {
                try {
                    Class<?> controllerClass = findClass("net.battlescribe.desktop.rostereditor.RosterEditorWindowController");
                    javafx.scene.Scene scene = findMainScene();
                    if (scene == null || controllerClass == null) {
                        result[0] = errorJson("Cannot find scene or controller class.");
                        return;
                    }
                    Object controller = findControllerInstance(controllerClass, scene);
                    if (controller == null) {
                        result[0] = errorJson("Controller instance not found.");
                        return;
                    }
                    Method rebuildMethod = null;
                    for (Method m : controllerClass.getDeclaredMethods()) {
                        if (m.getName().equals("p") && m.getParameterCount() == 0 && m.getReturnType() == void.class) {
                            rebuildMethod = m;
                            break;
                        }
                    }
                    if (rebuildMethod == null) {
                        result[0] = errorJson("Method p() not found on controller. Methods: " + listMethodNames(controllerClass));
                        return;
                    }
                    rebuildMethod.setAccessible(true);
                    rebuildMethod.invoke(controller);
                    System.err.println("[agent] rebuildCatalogueTree: invoked controller.p()");
                    result[0] = "{\"rebuilt\":true}";
                } catch (Exception e) {
                    result[0] = errorJson("rebuildCatalogueTree: " + e.getClass().getSimpleName() + ": " + e.getMessage());
                } finally {
                    fxDone.countDown();
                }
            });

            boolean completed = fxDone.await(20, java.util.concurrent.TimeUnit.SECONDS);
            if (!completed) {
                return errorJson("rebuildCatalogueTree: FX execution timed out (20s) — controller.p() likely deadlocked");
            }
            return result[0];
        } catch (Exception e) {
            return errorJson("rebuildCatalogueTree: " + e.getClass().getSimpleName() + ": " + e.getMessage());
        }
    }

    private String listMethodNames(Class<?> cls) {
        StringBuilder sb = new StringBuilder();
        for (Method m : cls.getDeclaredMethods()) {
            if (m.getParameterCount() == 0 && m.getReturnType() == void.class) {
                if (sb.length() > 0) sb.append(",");
                sb.append(m.getName());
            }
        }
        return sb.toString();
    }

    /**
     * Sets customName/customNotes on a category within a force, via engine model API.
     */
    public String setCategoryCustomNotes(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String forceId = getString(paramsObject, "forceId", null);
            String categoryEntryId = getString(paramsObject, "categoryEntryId", null);
            String customName = getString(paramsObject, "customName", null);
            String customNotes = getString(paramsObject, "customNotes", null);

            Object force = findForceById(forceId);
            Object catList = callListGetter(force, "getCategories");
            if (catList == null) {
                return errorJson("Force has no categories.");
            }
            int size = (int) catList.getClass().getMethod("size").invoke(catList);
            for (int i = 0; i < size; i++) {
                Object cat = catList.getClass().getMethod("get", int.class).invoke(catList, i);
                String entryId = callGetter(cat, "getEntryId");
                if (categoryEntryId.equals(entryId)) {
                    if (customName != null && !customName.isEmpty()) {
                        Method m = findMethod(cat.getClass(), "setCustomName", 1);
                        if (m != null) m.invoke(cat, customName);
                    }
                    if (customNotes != null && !customNotes.isEmpty()) {
                        Method m = findMethod(cat.getClass(), "setCustomNotes", 1);
                        if (m != null) m.invoke(cat, customNotes);
                    }
                    return "{\"set\":true}";
                }
            }
            return errorJson("Category '" + categoryEntryId + "' not found in force.");
        } catch (Exception e) {
            return errorJson("setCategoryCustomNotes: " + e.getMessage());
        }
    }

    /**
     * Adds a new root force via the engine API.
     * params: { "catalogueId": "...", "forceEntryId": "..." }
     */
    public String addForceViaEngine(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String catalogueId = getString(paramsObject, "catalogueId", null);
            String forceEntryId = getString(paramsObject, "forceEntryId", null);
            String parentForceId = getString(paramsObject, "parentForceId", null);
            if (catalogueId == null || catalogueId.isEmpty()) {
                return errorJson("Missing 'catalogueId' parameter.");
            }
            if (forceEntryId == null || forceEntryId.isEmpty()) {
                return errorJson("Missing 'forceEntryId' parameter.");
            }

            Object gameSystem = getCurrentGameSystem();
            if (gameSystem == null) {
                StringBuilder diag = new StringBuilder("GameSystem not found. Controller fields: ");
                if (controllerInstance != null) {
                    Class<?> c = controllerInstance.getClass();
                    while (c != null && c != Object.class) {
                        for (java.lang.reflect.Field f : c.getDeclaredFields()) {
                            try {
                                f.setAccessible(true);
                                Object v = f.get(controllerInstance);
                                diag.append(f.getName()).append("=")
                                    .append(v != null ? v.getClass().getName() : "null").append("; ");
                            } catch (Exception ex) { /* ignore */ }
                        }
                        c = c.getSuperclass();
                    }
                }
                return errorJson(diag.toString());
            }

            Object catalogue = findCatalogueById(catalogueId);
            if (catalogue == null) {
                return errorJson("Catalogue not found: " + catalogueId);
            }

            Object forceEntry = findForceEntryById(forceEntryId, gameSystem, catalogue);
            if (forceEntry == null) {
                return errorJson("ForceEntry not found: " + forceEntryId);
            }

            final Object gs = gameSystem;
            final Object cat = catalogue;
            final Object fe = forceEntry;
            final Map<Object, Object> linkedCatalogues = new HashMap<Object, Object>();
            final List<Object> favourites = new ArrayList<Object>();
            final List<Object> errors = new ArrayList<Object>();

            // Child force: engine.b(parentForce, gameSystem, catalogue, linkedCats, forceEntry, favourites, errors)
            if (parentForceId != null && !parentForceId.isEmpty()) {
                Object parentForce = findForceById(parentForceId);
                if (parentForce == null) {
                    return errorJson("Parent force not found: " + parentForceId);
                }

                Method childForceMethod = findAddChildForceMethod(parentForce, gameSystem, catalogue, forceEntry);
                if (childForceMethod == null) {
                    return errorJson("Engine addChildForce method not found (7-param b()).");
                }
                childForceMethod.setAccessible(true);

                final Object pf = parentForce;
                final Method method = childForceMethod;
                final CountDownLatch latch = new CountDownLatch(1);
                engineOpLatch = latch;
                new Thread(() -> {
                    try {
                        resetEngineLoadingFlag();
                        method.invoke(engineInstance, pf, gs, cat, linkedCatalogues, fe, favourites, errors);
                        System.err.println("[agent] addForceViaEngine(child): parentForceId=" + parentForceId
                                + " forceEntryId=" + forceEntryId + " done");
                    } catch (Exception ex) {
                        System.err.println("[agent] addForceViaEngine(child) error: " + ex.getMessage());
                        ex.printStackTrace(System.err);
                    } finally {
                        resetEngineLoadingFlag();
                        latch.countDown();
                    }
                }, "bs-addChildForce").start();

                return "{\"added\":true}";
            }

            // Root force: engine.b(gameSystem, catalogue, linkedCats, forceEntry, favourites, errors)
            Method addForceMethod = findAddForceMethod(gameSystem, catalogue, forceEntry);
            if (addForceMethod == null) {
                return errorJson("Engine addForce method not found.");
            }
            addForceMethod.setAccessible(true);

            final Method method = addForceMethod;
            final CountDownLatch latch = new CountDownLatch(1);
            engineOpLatch = latch;
            new Thread(() -> {
                try {
                    resetEngineLoadingFlag();
                    method.invoke(engineInstance, gs, cat, linkedCatalogues, fe, favourites, errors);
                    System.err.println("[agent] addForceViaEngine: catalogueId=" + catalogueId
                            + " forceEntryId=" + forceEntryId + " done");
                } catch (Exception ex) {
                    System.err.println("[agent] addForceViaEngine error: " + ex.getMessage());
                    ex.printStackTrace(System.err);
                } finally {
                    resetEngineLoadingFlag();
                    latch.countDown();
                }
            }, "bs-addForce").start();

            return "{\"added\":true}";
        } catch (Exception e) {
            return errorJson("addForceViaEngine: " + e.getMessage());
        }
    }

    /**
     * Removes a force via the engine API.
     * params: { "forceId": "..." }
     */
    public String removeForceViaEngine(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String forceId = getString(paramsObject, "forceId", null);
            if (forceId == null || forceId.isEmpty()) {
                return errorJson("Missing 'forceId' parameter.");
            }

            Object force = findForceById(forceId);
            Method removeForceMethod = findRemoveForceMethod(force);
            if (removeForceMethod == null) {
                return errorJson("Engine removeForce method not found.");
            }
            removeForceMethod.setAccessible(true);

            final Object f = force;
            final Method method = removeForceMethod;
            final CountDownLatch latch = new CountDownLatch(1);
            engineOpLatch = latch;
            new Thread(() -> {
                try {
                    resetEngineLoadingFlag();
                    method.invoke(engineInstance, f);
                    System.err.println("[agent] removeForceViaEngine: forceId=" + forceId + " done");
                } catch (Exception ex) {
                    System.err.println("[agent] removeForceViaEngine error: " + ex.getMessage());
                    ex.printStackTrace(System.err);
                } finally {
                    resetEngineLoadingFlag();
                    latch.countDown();
                }
            }, "bs-removeForce").start();

            return "{\"removed\":true}";
        } catch (Exception e) {
            return errorJson("removeForceViaEngine: " + e.getMessage());
        }
    }

    /**
     * Selects an entry via engine API on a bg thread. Fallback when catalogue tree UI is stale.
     * params: { "forceId": "...", "entryId": "..." }
     * Uses catMgr.R() to get available entries, then engine.b(force, entry) to select.
     */
    public String selectEntryViaEngine(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String forceId = getString(paramsObject, "forceId", null);
            String entryId = getString(paramsObject, "entryId", null);
            String parentSelectionId = getString(paramsObject, "parentSelectionId", null);

            Object force = findForceById(forceId);
            if (force == null) {
                return errorJson("Force not found: " + forceId);
            }

            // Determine the parent: either a selection (for child entries) or the force (for root entries)
            Object parent = force;
            if (parentSelectionId != null && !parentSelectionId.isEmpty()) {
                Object parentSel = findSelectionById(force, parentSelectionId);
                if (parentSel == null) {
                    return errorJson("Parent selection not found: " + parentSelectionId);
                }
                parent = parentSel;
            }

            // Get CatalogueManager for this force: engine.e(force)
            Object catMgr = getForceContext(force);
            if (catMgr == null) {
                return errorJson("CatalogueManager not found for force: " + forceId);
            }

            // Get available entries from CatalogueManager.R() — same as IKVM engine does
            Method getEntriesMethod = findMethod(catMgr.getClass(), "R", new Class<?>[0], List.class);
            if (getEntriesMethod == null) {
                // Try uppercase/lowercase variants
                for (String name : new String[] {"R", "r"}) {
                    getEntriesMethod = findMethod(catMgr.getClass(), name, new Class<?>[0], List.class);
                    if (getEntriesMethod != null) break;
                }
            }
            if (getEntriesMethod == null) {
                return errorJson("CatalogueManager.R() method not found. Class: " + catMgr.getClass().getName());
            }
            getEntriesMethod.setAccessible(true);
            Object entriesList = getEntriesMethod.invoke(catMgr);
            List<Object> entries = toJavaList(entriesList);

            // Find the target entry by ID
            Object dataEntry = null;
            for (Object entry : entries) {
                String eid = callGetter(entry, "getId");
                if (entryId.equals(eid)) {
                    dataEntry = entry;
                    break;
                }
                // Check composite IDs (linkId::targetId)
                if (eid != null && eid.contains("::")) {
                    for (String part : eid.split("::")) {
                        if (entryId.equals(part)) { dataEntry = entry; break; }
                    }
                    if (dataEntry != null) break;
                }
            }
            if (dataEntry == null) {
                // Fallback to findEntryById
                dataEntry = findEntryById(entryId);
                System.err.println("[agent] selectEntryViaEngine: entry '" + entryId + "' not in catMgr.R() list ("
                        + entries.size() + " entries), using findEntryById fallback");
            }
            if (dataEntry == null) {
                return errorJson("Data entry not found: " + entryId + " (catMgr entries: " + entries.size() + ")");
            }

            // Use engine.b(parent, entry) — "selectEntry" with 2 args (same as IKVM adapter)
            Method selectEntry = findSelectEntryMethod(parent, dataEntry);
            if (selectEntry == null) {
                return errorJson("Engine selectEntry method not found for: "
                        + parent.getClass().getSimpleName() + " / " + dataEntry.getClass().getSimpleName());
            }
            selectEntry.setAccessible(true);

            System.err.println("[agent] selectEntryViaEngine: forceId=" + forceId + " entryId=" + entryId
                    + " parent=" + parent.getClass().getSimpleName()
                    + " entry=" + dataEntry.getClass().getSimpleName() + ".getId()=" + callGetter(dataEntry, "getId")
                    + " method=" + selectEntry.getName() + "(" + selectEntry.getParameterCount() + " params)"
                    + " (bg thread)");
            final Object e = dataEntry;
            final Object p = parent;
            // Use b(parent, entry) which calls t() for full refresh (validation, costs, flags).
            // Safe on bg thread with threadCount=1 (no pool deadlock) and pipe drain active.
            final Method createMethod = selectEntry;

            final CountDownLatch latch = new CountDownLatch(1);
            engineOpLatch = latch;
            new Thread(() -> {
                try {
                    System.err.println("[agent] selectEntryViaEngine: bg thread started, resetting loading flag...");
                    resetEngineLoadingFlag();
                    System.err.println("[agent] selectEntryViaEngine: invoking b(parent, entry)...");
                    Object result = createMethod.invoke(engineInstance, p, e);
                    System.err.println("[agent] selectEntryViaEngine: invoke returned");
                    resetEngineLoadingFlag();
                    int resultSize = 0;
                    if (result != null) {
                        try { resultSize = (int) result.getClass().getMethod("size").invoke(result); }
                        catch (Exception ignore) {}
                    }
                    System.err.println("[agent] selectEntryViaEngine: done, returned " + resultSize + " selections");
                } catch (Exception ex) {
                    System.err.println("[agent] selectEntryViaEngine error: " + ex.getMessage());
                    ex.printStackTrace(System.err);
                } finally {
                    resetEngineLoadingFlag();
                    latch.countDown();
                }
            }, "bs-selectEntry-api").start();

            return "{\"selected\":true}";
        } catch (Exception e) {
            return errorJson("selectEntryViaEngine: " + e.getMessage());
        }
    }

    /**
     * Sets the selection count via selectEntry/deselectEntry loop (matching the BS Desktop UI behavior).
     * Called when UI steering (spinner) isn't available (e.g., model-type child selections).
     * Uses async Platform.runLater for the engine calls to avoid FX thread starvation.
     */
    /**
     * Deselects a selection directly via engine.m(selection) — bypasses getNumChanges/isDuplicate.
     * Params: forceId (String, optional), selectionId (String)
     */
    public String deselectEntryViaEngine(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String forceId = getString(paramsObject, "forceId", null);
            String selectionId = getString(paramsObject, "selectionId", null);
            if (selectionId == null || selectionId.isEmpty()) {
                return errorJson("selectionId is required.");
            }

            // Find the force containing this selection
            Object force = null;
            if (forceId != null && !forceId.isEmpty()) {
                force = findForceById(forceId);
            }
            if (force == null) {
                force = findForceContainingSelection(selectionId);
            }
            if (force == null) {
                return errorJson("Force not found for selectionId '" + selectionId + "'.");
            }

            // Find the selection
            Object selection = findSelectionById(force, selectionId);
            if (selection == null) {
                return errorJson("Selection '" + selectionId + "' not found.");
            }

            // Find engine.m(Selection) → void
            Method deselectEntry = findDeselectEntryMethod(selection);
            if (deselectEntry == null) {
                return errorJson("Engine method deselectEntry (m) not found.");
            }

            System.err.println("[agent] deselectEntryViaEngine: selectionId=" + selectionId);

            // Run on bg thread to avoid FX deadlock (engine.m → t() may interact with UI)
            final Object sel = selection;
            final Method dm = deselectEntry;
            final CountDownLatch latch = new CountDownLatch(1);
            engineOpLatch = latch;
            new Thread(() -> {
                try {
                    System.err.println("[agent] deselectEntry: invoking...");
                    dm.invoke(engineInstance, sel);
                    System.err.println("[agent] deselectEntry: done");
                } catch (Exception ex) {
                    System.err.println("[agent] deselectEntry error: " + ex.getMessage());
                    ex.printStackTrace(System.err);
                } finally {
                    resetEngineLoadingFlag();
                    latch.countDown();
                }
            }, "bs-deselectEntry").start();

            return "{\"deselected\":true}";
        } catch (Exception e) {
            return errorJson("deselectEntryViaEngine: " + e.getMessage());
        }
    }

    public String setSelectionCount(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String forceId = getString(paramsObject, "forceId", null);
            String selectionId = getString(paramsObject, "selectionId", null);
            int count = getInt(paramsObject, "count", -1);
            if (count < 0) {
                return errorJson("Missing or invalid 'count' parameter.");
            }

            Object force;
            if (forceId != null && !forceId.isEmpty()) {
                force = findForceById(forceId);
            } else {
                // Search all forces for the selection
                force = findForceContainingSelection(selectionId);
                if (force == null) {
                    return errorJson("Could not find force containing selection '" + selectionId + "'.");
                }
            }
            Object selection = findSelectionById(force, selectionId);

            // Find parent of this selection (Force or parent Selection)
            Object parent = findSelectionParent(force, selectionId);
            if (parent == null) {
                return errorJson("Could not find parent for selection '" + selectionId + "'.");
            }

            // Get the selection's entryId and find the data entry
            String entryId = callGetter(selection, "getEntryId");
            Object dataEntry = resolveEntryForSelection(force, selection);
            if (dataEntry == null) {
                dataEntry = findEntryById(entryId);
            }
            if (dataEntry == null) {
                return errorJson("Data entry not found for entryId '" + entryId + "'.");
            }

            // Use getNumChanges (engine.b(parent, entry, count) → int) to compute delta
            Method getNumChanges = findGetNumChangesMethod(parent, dataEntry);
            if (getNumChanges == null) {
                return errorJson("Engine method getNumChanges not found.");
            }
            int delta = (int) getNumChanges.invoke(engineInstance, parent, dataEntry, count);
            System.err.println("[agent] setSelectionCount: selectionId=" + selectionId
                    + " entryId=" + entryId + " count=" + count + " delta=" + delta
                    + " parent=" + parent.getClass().getSimpleName()
                    + " entry=" + dataEntry.getClass().getSimpleName());

            if (delta == 0) {
                return "{\"set\":true,\"count\":" + count + ",\"delta\":0}";
            }

            if (delta > 0) {
                // Use selectEntry on a bg thread. With threadCount patched to 1,
                // the thread pool issue is eliminated. t() should complete.
                Method selectEntry = findSelectEntryMethod(parent, dataEntry);
                if (selectEntry == null) {
                    return errorJson("Engine method selectEntry not found.");
                }
                System.err.println("[agent] setSelectionCount: will call selectEntry " + delta + " time(s) on bg thread");
                final Object p = parent, e = dataEntry;
                final Method m = selectEntry;
                final int d = delta;
                final CountDownLatch latch = new CountDownLatch(1);
                engineOpLatch = latch;
                new Thread(() -> {
                    try {
                        for (int i = 0; i < d; i++) {
                            System.err.println("[agent] selectEntry: invoking (" + (i+1) + "/" + d + ")...");
                            m.invoke(engineInstance, p, e);
                            System.err.println("[agent] selectEntry: done (" + (i+1) + "/" + d + ")");
                        }
                    } catch (Exception ex) {
                        System.err.println("[agent] selectEntry error: " + ex.getClass().getSimpleName() + ": " + ex.getMessage());
                        ex.printStackTrace(System.err);
                    } finally {
                        resetEngineLoadingFlag();
                        latch.countDown();
                    }
                }, "bs-selectEntry").start();
            } else {
                Method deselectEntry = findDeselectEntryMethod(selection);
                if (deselectEntry == null) {
                    return errorJson("Engine method deselectEntry not found.");
                }
                final Object sel = selection;
                final Method dm = deselectEntry;
                final int absDelta = -delta;
                final CountDownLatch latch = new CountDownLatch(1);
                engineOpLatch = latch;
                new Thread(() -> {
                    try {
                        for (int i = 0; i < absDelta; i++) {
                            System.err.println("[agent] deselectEntry: invoking (" + (i+1) + "/" + absDelta + ")...");
                            dm.invoke(engineInstance, sel);
                            System.err.println("[agent] deselectEntry: done (" + (i+1) + "/" + absDelta + ")");
                        }
                    } catch (Exception ex) {
                        System.err.println("[agent] deselectEntry error: " + ex.getMessage());
                        ex.printStackTrace(System.err);
                    } finally {
                        resetEngineLoadingFlag();
                        latch.countDown();
                    }
                }, "bs-deselectEntry").start();
            }

            return "{\"set\":true,\"count\":" + count + ",\"delta\":" + delta + "}";
        } catch (Exception e) {
            return errorJson("setSelectionCount: " + e.getMessage());
        }
    }

    /**
     * Sets a cost limit on the roster via the engine API.
     * Params: costTypeId (String), value (double)
     * Runs the engine call on a bg thread (calls t() which logs to stdout).
     */
    public String setCostLimit(String params) {
        JsonObject paramsObject = parseParams(params);
        try {
            ensureEngineFound();
            String costTypeId = getString(paramsObject, "costTypeId", null);
            double value = getDouble(paramsObject, "value", -1.0);
            if (costTypeId == null || costTypeId.isEmpty()) {
                return errorJson("Missing 'costTypeId' parameter.");
            }

            Object costType = findCostTypeById(costTypeId);
            if (costType == null) {
                return errorJson("CostType not found: " + costTypeId);
            }

            // Find engine.a(CostType, double) — setCostLimit
            Method setCostLimitMethod = null;
            for (Method m : engineClass.getDeclaredMethods()) {
                if (m.getName().equals("a") && m.getParameterCount() == 2) {
                    Class<?>[] pts = m.getParameterTypes();
                    if (pts[0].isAssignableFrom(costType.getClass()) && pts[1] == double.class) {
                        setCostLimitMethod = m;
                        break;
                    }
                }
            }
            if (setCostLimitMethod == null) {
                return errorJson("Engine setCostLimit method not found.");
            }
            setCostLimitMethod.setAccessible(true);

            System.err.println("[agent] setCostLimit: costTypeId=" + costTypeId + " value=" + value);
            final Method method = setCostLimitMethod;
            final Object ct = costType;
            final double v = value;
            final CountDownLatch latch = new CountDownLatch(1);
            engineOpLatch = latch;
            new Thread(() -> {
                try {
                    resetEngineLoadingFlag();
                    method.invoke(engineInstance, ct, v);
                    System.err.println("[agent] setCostLimit: done");
                } catch (Exception ex) {
                    System.err.println("[agent] setCostLimit error: " + ex.getMessage());
                    ex.printStackTrace(System.err);
                } finally {
                    resetEngineLoadingFlag();
                    latch.countDown();
                }
            }, "bs-setCostLimit").start();

            return "{\"set\":true,\"costTypeId\":" + jsonStr(costTypeId) + ",\"value\":" + value + "}";
        } catch (Exception e) {
            return errorJson("setCostLimit: " + e.getMessage());
        }
    }

    /**
     * Invokes t()'s sub-methods individually on the current thread (FX thread),
     * bypassing the synchronized t() wrapper that deadlocks.
     * t() does: u(), a(false,true), v(), d(), w(), b(false), g().a(f()), g().a()
     * We call all except v() (validation uses thread pool, can hang) and perf logging.
     */
    private void invokeRefreshSubMethods() {
        try {
            // u() — mark changed
            Method u = engineClass.getDeclaredMethod("u");
            u.setAccessible(true);
            u.invoke(engineInstance);

            // a(false, true) — cost refresh
            Method costRefresh = null;
            for (Method m : engineClass.getDeclaredMethods()) {
                if (m.getName().equals("a") && m.getParameterCount() == 2) {
                    Class<?>[] pts = m.getParameterTypes();
                    if (pts[0] == boolean.class && pts[1] == boolean.class) {
                        costRefresh = m;
                        break;
                    }
                }
            }
            if (costRefresh != null) {
                costRefresh.setAccessible(true);
                costRefresh.invoke(engineInstance, false, true);
                System.err.println("[agent] invokeRefreshSubMethods: cost refresh done");
            } else {
                System.err.println("[agent] invokeRefreshSubMethods: cost refresh method not found");
            }

            // d() — clear cache (no-arg void)
            try {
                Method d = engineClass.getDeclaredMethod("d");
                d.setAccessible(true);
                d.invoke(engineInstance);
            } catch (NoSuchMethodException ignored) {}

            // w() — clear changed (no-arg void)
            try {
                Method w = engineClass.getDeclaredMethod("w");
                w.setAccessible(true);
                w.invoke(engineInstance);
            } catch (NoSuchMethodException ignored) {}

            // b(false) — set loading=false
            for (Method m : engineClass.getDeclaredMethods()) {
                if (m.getName().equals("b") && m.getParameterCount() == 1) {
                    Class<?>[] pts = m.getParameterTypes();
                    if (pts[0] == boolean.class) {
                        m.setAccessible(true);
                        m.invoke(engineInstance, false);
                        break;
                    }
                }
            }

            System.err.println("[agent] invokeRefreshSubMethods: complete (skipped validation)");
        } catch (Exception e) {
            System.err.println("[agent] invokeRefreshSubMethods error: " + e.getClass().getSimpleName() + ": " + e.getMessage());
            e.printStackTrace(System.err);
        }
    }

    /**
     * Refresh engine state without the synchronized t() wrapper.
     * Calls the same sub-methods as t() but unsynchronized, avoiding deadlock.
     */
    private void refreshEngineStateUnsynchronized() {
        invokeRefreshSubMethods();
    }

    /**
     * Waits for any pending background engine operation to complete.
     * Must NOT run on the FX thread (would deadlock if the bg op needs FX).
     * Accepts optional "timeoutMs" param (default 3000).
     */
    public String waitForEngine(String params) {
        JsonObject paramsObject = parseParams(params);
        CountDownLatch latch = engineOpLatch;
        if (latch == null) {
            return "{\"waited\":false,\"reason\":\"no pending operation\"}";
        }
        int timeoutMs = getInt(paramsObject, "timeoutMs", 3000);
        try {
            boolean done = latch.await(timeoutMs, java.util.concurrent.TimeUnit.MILLISECONDS);
            if (done) {
                engineOpLatch = null;
                // Wait for FX event queue to drain (process any Platform.runLater tasks
                // queued by the bg thread's engine operation, like tree rebuilds)
                CountDownLatch fxLatch = new CountDownLatch(1);
                javafx.application.Platform.runLater(fxLatch::countDown);
                fxLatch.await(5000, java.util.concurrent.TimeUnit.MILLISECONDS);
                return "{\"waited\":true}";
            } else {
                // Interrupt the stuck thread to allow it to terminate
                engineOpLatch = null;
                return "{\"waited\":false,\"reason\":\"timeout\"}";
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            return "{\"waited\":false,\"reason\":\"interrupted\"}";
        }
    }

    private Method findGetNumChangesMethod(Object parent, Object dataEntry) {
        // engine.b(BaseSelectionParent, SelectionEntry, int) → int
        for (Method m : engineClass.getDeclaredMethods()) {
            if (m.getName().equals("b") && m.getParameterCount() == 3
                    && m.getReturnType() == int.class) {
                Class<?>[] pts = m.getParameterTypes();
                if (pts[2] == int.class
                        && pts[0].isAssignableFrom(parent.getClass())
                        && pts[1].isAssignableFrom(dataEntry.getClass())) {
                    m.setAccessible(true);
                    return m;
                }
            }
        }
        return null;
    }

    private Method findAddForceMethod(Object gameSystem, Object catalogue, Object forceEntry) {
        Class<?> cls = engineClass;
        while (cls != null && cls != Object.class) {
            for (Method method : cls.getDeclaredMethods()) {
                if (!method.getName().equals("b") || method.getParameterCount() != 6) {
                    continue;
                }
                Class<?>[] pts = method.getParameterTypes();
                if (pts[0].isAssignableFrom(gameSystem.getClass())
                        && pts[1].isAssignableFrom(catalogue.getClass())
                        && Map.class.isAssignableFrom(pts[2])
                        && pts[3].isAssignableFrom(forceEntry.getClass())
                        && List.class.isAssignableFrom(pts[4])
                        && List.class.isAssignableFrom(pts[5])) {
                    method.setAccessible(true);
                    return method;
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    // engine.b(parentForce, gameSystem, catalogue, linkedCats, forceEntry, favourites, errors) — 7 params
    private Method findAddChildForceMethod(Object parentForce, Object gameSystem, Object catalogue, Object forceEntry) {
        Class<?> cls = engineClass;
        while (cls != null && cls != Object.class) {
            for (Method method : cls.getDeclaredMethods()) {
                if (!method.getName().equals("b") || method.getParameterCount() != 7) {
                    continue;
                }
                Class<?>[] pts = method.getParameterTypes();
                if (pts[0].isAssignableFrom(parentForce.getClass())
                        && pts[1].isAssignableFrom(gameSystem.getClass())
                        && pts[2].isAssignableFrom(catalogue.getClass())
                        && Map.class.isAssignableFrom(pts[3])
                        && pts[4].isAssignableFrom(forceEntry.getClass())
                        && List.class.isAssignableFrom(pts[5])
                        && List.class.isAssignableFrom(pts[6])) {
                    method.setAccessible(true);
                    return method;
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private Method findRemoveForceMethod(Object force) {
        Class<?> cls = engineClass;
        while (cls != null && cls != Object.class) {
            for (Method method : cls.getDeclaredMethods()) {
                if (!method.getName().equals("g") || method.getParameterCount() != 1) {
                    continue;
                }
                if (method.getParameterTypes()[0].isAssignableFrom(force.getClass())) {
                    method.setAccessible(true);
                    return method;
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    /**
     * Resets the engine's "loading" flag (field 'b' on engine class 'f') to false.
     * After bg thread engine ops, the controller's observer re-enters the engine
     * and leaves the loading flag stuck at true, blocking subsequent selectEntry calls.
     */
    private void resetEngineLoadingFlag() {
        try {
            java.lang.reflect.Field loadingField = engineClass.getDeclaredField("b");
            loadingField.setAccessible(true);
            loadingField.setBoolean(engineInstance, false);
        } catch (Exception ex) {
            // Field might be named differently or not accessible; try setter method
            try {
                Method setter = null;
                for (Method m : engineClass.getDeclaredMethods()) {
                    if (m.getName().equals("b") && m.getParameterCount() == 1
                            && m.getParameterTypes()[0] == boolean.class) {
                        setter = m;
                        break;
                    }
                }
                if (setter != null) {
                    setter.setAccessible(true);
                    setter.invoke(engineInstance, false);
                }
            } catch (Exception ex2) {
                System.err.println("[agent] resetEngineLoadingFlag: failed: " + ex2.getMessage());
            }
        }
    }

    private Method findSelectEntryMethod(Object parent, Object dataEntry) {
        // engine.b(BaseSelectionParent, SelectionEntry) → List (returns list of created selections)
        // NOT void! The actual selectEntry returns a java.util.List.
        Method best = null;
        for (Method m : engineClass.getDeclaredMethods()) {
            if (m.getName().equals("b") && m.getParameterCount() == 2
                    && m.getReturnType() != void.class
                    && m.getReturnType() != int.class
                    && m.getReturnType() != boolean.class) {
                Class<?>[] pts = m.getParameterTypes();
                if (pts[0].isAssignableFrom(parent.getClass())
                        && pts[1].isAssignableFrom(dataEntry.getClass())) {
                    if (best == null || best.getParameterTypes()[0].isAssignableFrom(pts[0])) {
                        m.setAccessible(true);
                        best = m;
                    }
                }
            }
        }
        return best;
    }

    private Method findDeselectEntryMethod(Object selection) {
        // engine.m(Selection) → List (returns deselected selections) — may be on superclass
        Class<?> cls = engineClass;
        while (cls != null && cls != Object.class) {
            for (Method m : cls.getDeclaredMethods()) {
                if (m.getName().equals("m") && m.getParameterCount() == 1) {
                    Class<?>[] pts = m.getParameterTypes();
                    if (pts[0].isAssignableFrom(selection.getClass())) {
                        m.setAccessible(true);
                        return m;
                    }
                }
            }
            cls = cls.getSuperclass();
        }
        return null;
    }

    private Method findSetNumSelectionsMethod(Object parent, Object dataEntry) {
        // Find engine method a(BaseSelectionParent, SelectionEntry, int) → void
        Method setNumSel = findMethod(engineClass, "a",
                new Class<?>[] { parent.getClass(), dataEntry.getClass(), int.class }, void.class);
        if (setNumSel != null) {
            return setNumSel;
        }
        // Broader search — parent/entry might be subclasses
        for (Method m : engineClass.getDeclaredMethods()) {
            if (m.getName().equals("a") && m.getParameterCount() == 3
                    && m.getReturnType() == void.class) {
                Class<?>[] pts = m.getParameterTypes();
                if (pts[2] == int.class
                        && pts[0].isAssignableFrom(parent.getClass())
                        && pts[1].isAssignableFrom(dataEntry.getClass())) {
                    m.setAccessible(true);
                    return m;
                }
            }
        }
        return null;
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
        StringBuilder sb = new StringBuilder("{\"patched\":").append(patched).append(",\"log\":[");
        for (int i = 0; i < log.size(); i++) {
            if (i > 0) sb.append(",");
            sb.append("\"").append(escapeJson(log.get(i))).append("\"");
        }
        sb.append("]}");
        return sb.toString();
    }

    private static String jsonStr(String value) {
        if (value == null) return "null";
        return "\"" + escapeJson(value) + "\"";
    }

    private static String escapeJson(String s) {
        if (s == null) return "";
        return s.replace("\\", "\\\\").replace("\"", "\\\"")
                .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t");
    }
}
