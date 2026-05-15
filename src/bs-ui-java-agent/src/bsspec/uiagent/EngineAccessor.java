package bsspec.uiagent;

import java.lang.instrument.Instrumentation;
import java.lang.reflect.Array;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
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
    private Class<?> engineClass;
    private Class<?> rosterClass;
    private Method getRosterMethod;

    public EngineAccessor(Instrumentation instrumentation) {
        this.instrumentation = instrumentation;
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
                        Object eng = readEngineFromController(controller, controllerClass);
                        if (eng != null) {
                            engineInstance = eng;
                            cacheRosterAccess();
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
                Object eng = readEngineFromController(controller, controllerClass);
                if (eng != null) {
                    engineInstance = eng;
                    cacheRosterAccess();
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

        // Selections
        Object selList = callListGetter(force, "getSelections");
        sb.append(",\"selections\":").append(serializeSelectionList(selList));

        // Child forces
        Object childForces = callListGetter(force, "getForces");
        sb.append(",\"childForces\":").append(serializeForceList(childForces));

        sb.append("}");
        return sb.toString();
    }

    private String serializeSelectionList(Object list) throws Exception {
        if (list == null) return "[]";
        StringBuilder sb = new StringBuilder("[");
        int size = (int) list.getClass().getMethod("size").invoke(list);
        for (int i = 0; i < size; i++) {
            if (i > 0) sb.append(",");
            Object sel = list.getClass().getMethod("get", int.class).invoke(list, i);
            sb.append(serializeSelection(sel));
        }
        sb.append("]");
        return sb.toString();
    }

    private String serializeSelection(Object sel) throws Exception {
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"id\":").append(jsonStr(callGetter(sel, "getId")));
        sb.append(",\"name\":").append(jsonStr(callGetter(sel, "getName")));
        sb.append(",\"entryId\":").append(jsonStr(callGetter(sel, "getEntryId")));

        // Number (getNumber returns int)
        try {
            Method m = sel.getClass().getMethod("getNumber");
            sb.append(",\"number\":").append(m.invoke(sel));
        } catch (Exception e) {
            sb.append(",\"number\":1");
        }

        // Hidden
        try {
            Method m = sel.getClass().getMethod("isHidden");
            sb.append(",\"hidden\":").append(m.invoke(sel));
        } catch (Exception e) {
            sb.append(",\"hidden\":false");
        }

        // Costs
        sb.append(",\"costs\":").append(serializeCostList(callListGetter(sel, "getCosts")));

        // Child selections
        Object children = callListGetter(sel, "getSelections");
        sb.append(",\"children\":").append(serializeSelectionList(children));

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

    private Object getForceContext(Object force) throws Exception {
        Method method = findMethod(engineClass, "e", new Class<?>[] { force.getClass() }, Object.class);
        if (method == null) {
            return null;
        }
        return method.invoke(engineInstance, force);
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
        for (Object error : toJavaList(errors)) {
            if (!first[0]) {
                sb.append(",");
            }
            first[0] = false;
            sb.append("{");
            sb.append("\"message\":").append(jsonStr(extractValidationMessage(error)));
            sb.append(",\"ownerType\":").append(jsonStr(ownerType));
            sb.append(",\"ownerId\":").append(jsonStr(callGetter(element, "getId")));
            String ownerEntryId = callGetter(element, "getEntryId");
            if (ownerEntryId != null) {
                sb.append(",\"ownerEntryId\":").append(jsonStr(ownerEntryId));
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
