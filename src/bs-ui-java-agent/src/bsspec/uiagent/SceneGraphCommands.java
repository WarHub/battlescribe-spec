package bsspec.uiagent;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import javafx.application.Platform;
import javafx.collections.ObservableList;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.Scene;
import javafx.scene.control.*;
import javafx.scene.image.WritableImage;
import javafx.scene.input.KeyCode;
import javafx.scene.input.KeyEvent;
import javafx.scene.input.MouseButton;
import javafx.scene.input.MouseEvent;
import javafx.scene.text.Text;
import javafx.stage.Stage;
import javafx.stage.Window;

import javax.imageio.ImageIO;
import java.awt.image.BufferedImage;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.util.ArrayList;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Scene graph operations dispatched by the JSON-RPC server.
 * All methods run on the JavaFX Application Thread.
 */
public class SceneGraphCommands {

    private static final Pattern TITLE_ROSTER_NAME_PATTERN = Pattern.compile("^.* - (.+?)(?: \\([^)]*\\))?$");
    private static final Pattern LEADING_COUNT_PATTERN = Pattern.compile("^\\s*(\\d+)\\s*[x×]\\s*(.+?)\\s*$");
    private static final Pattern TRAILING_COUNT_PATTERN = Pattern.compile("^\\s*(.+?)\\s*[x×]\\s*(\\d+)\\s*$");
    private static final Pattern LEADING_NUMBER_PATTERN = Pattern.compile("^\\s*(\\d+)\\s+(.+?)\\s*$");
    private static final Pattern COST_INLINE_PATTERN = Pattern.compile("(?i)^\\s*([A-Za-z][A-Za-z0-9 %/_-]{0,20})\\s*[:=]\\s*(-?\\d+(?:\\.\\d+)?)\\s*$");
    private static final Pattern COST_SUFFIX_PATTERN = Pattern.compile("(?i)^\\s*(-?\\d+(?:\\.\\d+)?)\\s*([A-Za-z][A-Za-z0-9 %/_-]{0,20})\\s*$");
    private static final Pattern NUMERIC_VALUE_PATTERN = Pattern.compile("^-?\\d+(?:\\.\\d+)?$");

    private final EngineAccessor engineAccessor;

    public SceneGraphCommands(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
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

    private static boolean getBoolean(JsonObject params, String key, boolean defaultValue) {
        JsonElement value = params.get(key);
        return value != null && !value.isJsonNull() ? value.getAsBoolean() : defaultValue;
    }

    private static JsonElement parseJsonValue(String json) {
        if (json == null) {
            return JsonNull.INSTANCE;
        }
        return new JsonParser().parse(json);
    }

    private static void addDynamicProperty(JsonObject obj, String key, Object value) {
        if (value == null) {
            obj.add(key, JsonNull.INSTANCE);
        } else if (value instanceof Number) {
            obj.addProperty(key, (Number) value);
        } else if (value instanceof Boolean) {
            obj.addProperty(key, (Boolean) value);
        } else if (value instanceof Character) {
            obj.addProperty(key, (Character) value);
        } else {
            obj.addProperty(key, value.toString());
        }
    }

    private static String jsonError(String message) {
        JsonObject response = new JsonObject();
        response.addProperty("error", message);
        return response.toString();
    }

    private static String jsonBooleanResult(String key, boolean value) {
        JsonObject response = new JsonObject();
        response.addProperty(key, value);
        return response.toString();
    }

    public String dispatch(String method, String params) {
        JsonObject paramsObject = parseParams(params);
        switch (method) {
            case "ping":
                return "\"pong\"";
            case "dumpTree":
                return dumpTree(params);
            case "getWindows":
                return getWindows();
            case "findNode":
                return findNode(params);
            case "findAllNodes":
                return findAllNodes(params);
            case "getNodeInfo":
                return getNodeInfo(params);
            case "clickNode":
                return clickNode(params);
            case "fireButton":
                return fireButton(params);
            case "getChildren":
                return getChildren(params);
            case "getNodeText":
                return getNodeText(params);
            case "findNodeByText":
                return findNodeByText(params);
            case "setNodeText":
                return setNodeText(params);
            case "getComboBoxItems":
                return getComboBoxItems(params);
            case "selectComboBoxItem":
                return selectComboBoxItem(params);
            case "getTreeItems":
                return getTreeItems(params);
            case "selectTreeItem":
                return selectTreeItem(params);
            case "clearTreeSelection":
                return clearTreeSelection(params);
            case "expandTreeItem":
                return expandTreeItem(params);
            case "clickTreeItem":
                return clickTreeItem(params);
            case "pressKey":
                return pressKey(params);
            case "getSpinnerValue":
                return getSpinnerValue(params);
            case "setSpinnerValue":
                return setSpinnerValue(params);
            // Engine access commands
            case "listBsClasses":
                return engineAccessor.listBsClasses();
            case "inspectClass":
                return engineAccessor.inspectClass(getString(paramsObject, "className", null));
            case "findEngine":
                return engineAccessor.findEngine();
            case "getRosterState":
                return engineAccessor.getRosterState();
            case "exportRosterXml":
                return engineAccessor.exportRosterXml();
            case "captureScreenshot":
                return captureScreenshot(params);
            case "getUiState":
                return getUiState(params);
            case "startRecording":
                return startRecording(params);
            case "stopRecording":
                return stopRecording(params);
            case "getRecordedActions":
                return getRecordedActions(params);
            case "setRosterName":
                return engineAccessor.setRosterName(params);
            case "getValidationErrors":
                return engineAccessor.getValidationErrors();
            case "readStaticFields":
                return engineAccessor.readStaticFields(getString(paramsObject, "className", null));
            case "dumpNodeProperties":
                return dumpNodeProperties(params);
            case "findControlByLabel":
                return findControlByLabel(params);
            case "clickControlByLabel":
                return clickControlByLabel(params);
            case "setSpinnerValueByLabel":
                return setSpinnerValueByLabel(params);
            case "patchSupporterPass":
                return engineAccessor.patchSupporterPass();
            case "setCategoryCustomNotes":
                return engineAccessor.setCategoryCustomNotes(params);
            case "addForceViaEngine":
                return engineAccessor.addForceViaEngine(params);
            case "removeForceViaEngine":
                return engineAccessor.removeForceViaEngine(params);
            case "selectEntryViaEngine":
                return engineAccessor.selectEntryViaEngine(params);
            case "deselectEntryViaEngine":
                return engineAccessor.deselectEntryViaEngine(params);
            case "setSelectionCount":
                return engineAccessor.setSelectionCount(params);
            case "setCostLimit":
                return engineAccessor.setCostLimit(params);
            case "rebuildCatalogueTree":
                return engineAccessor.rebuildCatalogueTree(params);
            case "waitForEngine":
                return engineAccessor.waitForEngine(params);
            case "threadDump":
                return threadDump();
            default:
                throw new IllegalArgumentException("Unknown method: " + method);
        }
    }

    private String threadDump() {
        JsonObject response = new JsonObject();
        JsonArray threads = new JsonArray();
        for (java.util.Map.Entry<Thread, StackTraceElement[]> entry : Thread.getAllStackTraces().entrySet()) {
            Thread t = entry.getKey();
            StackTraceElement[] stack = entry.getValue();
            JsonObject thread = new JsonObject();
            thread.addProperty("name", t.getName());
            thread.addProperty("state", t.getState().toString());
            JsonArray frames = new JsonArray();
            for (int i = 0; i < Math.min(stack.length, 15); i++) {
                frames.add(stack[i].toString());
            }
            thread.add("stack", frames);
            threads.add(thread);
        }
        response.add("threads", threads);
        return response.toString();
    }

    private String getWindows() {
        JsonArray windows = new JsonArray();
        for (Window w : Window.getWindows()) {
            JsonObject item = new JsonObject();
            item.addProperty("type", w.getClass().getSimpleName());
            if (w instanceof Stage) {
                Stage s = (Stage) w;
                item.addProperty("title", s.getTitle());
                item.addProperty("showing", s.isShowing());
            }
            item.addProperty("width", w.getWidth());
            item.addProperty("height", w.getHeight());
            windows.add(item);
        }
        return windows.toString();
    }

    private String captureScreenshot(String params) {
        JsonObject paramsObject = parseParams(params);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return jsonError("No scene found");
        }

        WritableImage image = scene.snapshot(null);
        try {
            Class<?> swingFxClass = Class.forName("javafx.embed.swing.SwingFXUtils");
            java.lang.reflect.Method fromFXImage = swingFxClass.getMethod("fromFXImage",
                javafx.scene.image.Image.class, java.awt.image.BufferedImage.class);
            Object buffered = fromFXImage.invoke(null, image, null);
            if (buffered == null) {
                return jsonError("Failed to capture scene");
            }
            try (ByteArrayOutputStream baos = new ByteArrayOutputStream()) {
                if (!ImageIO.write((BufferedImage) buffered, "png", baos)) {
                    throw new IOException("No PNG writer available");
                }
                String base64 = Base64.getEncoder().encodeToString(baos.toByteArray());
                JsonObject response = new JsonObject();
                response.addProperty("png", base64);
                response.addProperty("width", (int) image.getWidth());
                response.addProperty("height", (int) image.getHeight());
                return response.toString();
            }
        } catch (ClassNotFoundException e) {
            return jsonError("javafx.swing module not available for screenshots");
        } catch (Exception e) {
            return jsonError("Screenshot failed: " + e.getMessage());
        }
    }

    private String startRecording(String params) {
        JsonObject paramsObject = parseParams(params);
        Scene scene = findScene(getString(paramsObject, "windowTitle", null));
        if (scene == null) {
            return jsonError("No scene found");
        }

        ActionRecorder.getInstance().startRecording(scene);
        JsonObject response = new JsonObject();
        response.addProperty("status", "recording");
        return response.toString();
    }

    private String stopRecording(String params) {
        JsonObject paramsObject = parseParams(params);
        String actions = ActionRecorder.getInstance().stopRecording();
        JsonObject response = new JsonObject();
        response.add("actions", parseJsonValue(actions));
        return response.toString();
    }

    private String getRecordedActions(String params) {
        JsonObject paramsObject = parseParams(params);
        String actions = ActionRecorder.getInstance().getActionsJson();
        JsonObject response = new JsonObject();
        response.addProperty("recording", ActionRecorder.getInstance().isRecording());
        response.add("actions", parseJsonValue(actions));
        return response.toString();
    }

    private String getUiState(String params) {
        JsonObject paramsObject = parseParams(params);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Scene scene = findScene(windowTitle != null ? windowTitle : "Roster Editor");
        if (scene == null && windowTitle != null) {
            scene = findScene(windowTitle);
        }

        JsonObject response = new JsonObject();
        if (scene == null) {
            response.add("rosterName", JsonNull.INSTANCE);
            response.add("forces", new JsonArray());
            response.add("costs", new JsonArray());
            return response.toString();
        }

        TreeView<Object> rosterTree = findRosterTree(scene);
        Map<String, String> costs = new LinkedHashMap<String, String>();
        collectCosts(scene.getRoot(), costs);

        response.addProperty("rosterName", readRosterName(scene));
        response.add("forces", getVisibleForces(rosterTree));
        response.add("costs", costsToJson(costs));
        return response.toString();
    }

    private String dumpTree(String params) {
        JsonObject paramsObject = parseParams(params);
        int maxDepth = getInt(paramsObject, "maxDepth", 10);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return jsonError("No scene found");
        }

        JsonObject response = new JsonObject();
        response.addProperty("windowTitle", getWindowTitle(scene));
        response.add("tree", dumpNode(scene.getRoot(), 0, maxDepth));
        return response.toString();
    }

    private String findNode(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        if (selector == null) {
            throw new IllegalArgumentException("Missing 'selector' param");
        }

        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return "null";
        }

        Node node = scene.getRoot().lookup(selector);
        if (node == null) {
            return "null";
        }

        return nodeToJsonObject(node).toString();
    }

    private String getNodeInfo(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        return nodeToJsonObject(node).toString();
    }

    private String clickNode(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);
        boolean doubleClick = getBoolean(paramsObject, "doubleClick", false);
        int clickCount = doubleClick ? 2 : 1;

        Node node;
        if (text != null) {
            Scene scene = findScene(windowTitle);
            if (scene == null) {
                throw new IllegalArgumentException("Window not found: " + windowTitle);
            }
            node = findNodeByTextRecursive(scene.getRoot(), text, null);
        } else {
            node = resolveNode(selector, windowTitle);
        }
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + (text != null ? "text=" + text : selector));
        }

        var bounds = node.localToScreen(node.getBoundsInLocal());
        if (bounds == null) {
            return jsonError("Node not visible on screen (localToScreen returned null)");
        }
        double x = bounds.getMinX() + bounds.getWidth() / 2;
        double y = bounds.getMinY() + bounds.getHeight() / 2;

        node.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_PRESSED,
                bounds.getWidth() / 2, bounds.getHeight() / 2,
                x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false,
                true, false, false, null));
        node.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_RELEASED,
                bounds.getWidth() / 2, bounds.getHeight() / 2,
                x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false,
                true, false, false, null));
        node.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_CLICKED,
                bounds.getWidth() / 2, bounds.getHeight() / 2,
                x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false,
                true, false, false, null));

        JsonObject response = new JsonObject();
        response.addProperty("clicked", true);
        response.addProperty("doubleClick", doubleClick);
        response.addProperty("x", x);
        response.addProperty("y", y);
        return response.toString();
    }

    private String getChildren(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        JsonArray children = new JsonArray();
        if (node == null || !(node instanceof Parent)) {
            return children.toString();
        }

        for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
            children.add(nodeToJsonObject(child));
        }
        return children.toString();
    }

    private String getNodeText(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        String text = extractTextContent(node);
        return text == null ? "null" : new com.google.gson.JsonPrimitive(text).toString();
    }

    private String findAllNodes(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        if (selector == null) {
            throw new IllegalArgumentException("Missing 'selector' param");
        }

        Scene scene = findScene(windowTitle);
        JsonArray nodesJson = new JsonArray();
        if (scene == null) {
            return nodesJson.toString();
        }

        var nodes = scene.getRoot().lookupAll(selector);
        for (Node node : nodes) {
            nodesJson.add(nodeToJsonObject(node));
        }
        return nodesJson.toString();
    }

    private String fireButton(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String async = getString(paramsObject, "async", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof javafx.scene.control.ButtonBase)) {
            Node parent = node.getParent();
            boolean found = false;
            for (int i = 0; i < 5 && parent != null; i++) {
                if (parent instanceof javafx.scene.control.ButtonBase) {
                    node = parent;
                    found = true;
                    break;
                }
                parent = parent.getParent();
            }
            if (!found) {
                throw new IllegalArgumentException("Node is not a ButtonBase: " + node.getClass().getSimpleName());
            }
        }
        javafx.scene.control.ButtonBase button = (javafx.scene.control.ButtonBase) node;
        JsonObject response = new JsonObject();
        response.addProperty("fired", true);
        if ("true".equals(async)) {
            Platform.runLater(() -> button.fire());
            response.addProperty("async", true);
            return response.toString();
        }
        button.fire();
        return response.toString();
    }

    private String findNodeByText(String params) {
        JsonObject paramsObject = parseParams(params);
        String text = getString(paramsObject, "text", null);
        String nodeType = getString(paramsObject, "nodeType", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        if (text == null) {
            throw new IllegalArgumentException("Missing 'text' param");
        }

        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return "null";
        }

        Node found = findNodeByTextRecursive(scene.getRoot(), text, nodeType);
        if (found == null) {
            return "null";
        }
        return nodeToJsonObject(found).toString();
    }

    private Node findNodeByTextRecursive(Node node, String text, String nodeType) {
        String nodeText = extractTextContent(node);
        if (nodeText != null && nodeText.contains(text)) {
            if (nodeType == null || node.getClass().getSimpleName().equals(nodeType)) {
                return node;
            }
        }
        if (node instanceof Parent) {
            for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
                Node found = findNodeByTextRecursive(child, text, nodeType);
                if (found != null) {
                    return found;
                }
            }
        }
        return null;
    }

    private String setNodeText(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);

        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof TextInputControl)) {
            throw new IllegalArgumentException("Node is not a text input: " + node.getClass().getSimpleName());
        }
        TextInputControl textInput = (TextInputControl) node;
        textInput.setText(text != null ? text : "");
        // Fire a synthetic KEY_RELEASED event so that onKeyReleased handlers are triggered.
        // BattleScribe's CustomiseSelectionWindowController uses onKeyReleased to persist changes.
        javafx.scene.input.KeyEvent keyEvent = new javafx.scene.input.KeyEvent(
            javafx.scene.input.KeyEvent.KEY_RELEASED,
            "", "", javafx.scene.input.KeyCode.UNDEFINED,
            false, false, false, false);
        textInput.fireEvent(keyEvent);
        return jsonBooleanResult("set", true);
    }

    private String getComboBoxItems(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("ComboBox not found: " + selector);
        }
        if (!(node instanceof ComboBox)) {
            throw new IllegalArgumentException("Node is not a ComboBox: " + node.getClass().getSimpleName());
        }
        @SuppressWarnings("unchecked")
        ComboBox<Object> combo = (ComboBox<Object>) node;
        JsonObject response = new JsonObject();
        response.addProperty("selectedIndex", combo.getSelectionModel().getSelectedIndex());
        Object selected = combo.getSelectionModel().getSelectedItem();
        response.addProperty("selectedText", selected != null ? selected.toString() : null);
        JsonArray items = new JsonArray();
        for (int i = 0; i < combo.getItems().size(); i++) {
            Object item = combo.getItems().get(i);
            JsonObject option = new JsonObject();
            option.addProperty("index", i);
            option.addProperty("text", item != null ? item.toString() : null);
            items.add(option);
        }
        response.add("items", items);
        return response.toString();
    }

    private String selectComboBoxItem(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);
        int index = getInt(paramsObject, "index", -1);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("ComboBox not found: " + selector);
        }
        if (!(node instanceof ComboBox)) {
            throw new IllegalArgumentException("Node is not a ComboBox: " + node.getClass().getSimpleName());
        }
        @SuppressWarnings("unchecked")
        ComboBox<Object> combo = (ComboBox<Object>) node;
        if (index >= 0) {
            combo.getSelectionModel().select(index);
        } else if (text != null) {
            for (int i = 0; i < combo.getItems().size(); i++) {
                Object item = combo.getItems().get(i);
                if (item != null && item.toString().contains(text)) {
                    combo.getSelectionModel().select(i);
                    break;
                }
            }
        }
        Object selected = combo.getSelectionModel().getSelectedItem();
        JsonObject response = new JsonObject();
        response.addProperty("selectedIndex", combo.getSelectionModel().getSelectedIndex());
        response.addProperty("selectedText", selected != null ? selected.toString() : null);
        return response.toString();
    }

    @SuppressWarnings("unchecked")
    private String getTreeItems(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        int maxDepth = getInt(paramsObject, "maxDepth", 3);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("TreeView not found: " + selector);
        }
        if (!(node instanceof TreeView)) {
            throw new IllegalArgumentException("Node is not a TreeView: " + node.getClass().getSimpleName());
        }
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> root = tree.getRoot();
        JsonObject response = new JsonObject();
        response.add("root", root != null ? serializeTreeItem(root, 0, maxDepth) : JsonNull.INSTANCE);
        response.addProperty("showRoot", tree.isShowRoot());
        return response.toString();
    }

    private JsonObject serializeTreeItem(TreeItem<Object> item, int depth, int maxDepth) {
        JsonObject result = new JsonObject();
        Object val = item.getValue();
        result.addProperty("text", val != null ? val.toString() : null);
        result.addProperty("expanded", item.isExpanded());
        result.addProperty("leaf", item.isLeaf());
        if (depth < maxDepth && !item.getChildren().isEmpty()) {
            JsonArray children = new JsonArray();
            for (TreeItem<Object> child : item.getChildren()) {
                children.add(serializeTreeItem(child, depth + 1, maxDepth));
            }
            result.add("children", children);
        } else if (!item.getChildren().isEmpty()) {
            result.addProperty("childCount", item.getChildren().size());
        }
        return result;
    }

    @SuppressWarnings("unchecked")
    private String selectTreeItem(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);
        int index = getInt(paramsObject, "index", -1);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("TreeView not found: " + selector);
        }
        if (!(node instanceof TreeView)) {
            throw new IllegalArgumentException("Node is not a TreeView: " + node.getClass().getSimpleName());
        }
        TreeView<Object> tree = (TreeView<Object>) node;
        if (index >= 0) {
            tree.getSelectionModel().select(index);
        } else if (text != null) {
            TreeItem<Object> found = findTreeItemByText(tree.getRoot(), text);
            if (found != null) {
                tree.getSelectionModel().select(found);
            } else {
                JsonObject response = new JsonObject();
                response.addProperty("selected", false);
                response.addProperty("error", "Item not found: " + text);
                return response.toString();
            }
        }
        TreeItem<Object> sel = tree.getSelectionModel().getSelectedItem();
        JsonObject response = new JsonObject();
        response.addProperty("selected", true);
        response.addProperty("selectedText", sel != null && sel.getValue() != null ? sel.getValue().toString() : null);
        return response.toString();
    }

    @SuppressWarnings("unchecked")
    private String clearTreeSelection(String params) {
        JsonObject paramsObject = parseParams(params);
        String treeId = getString(paramsObject, "treeId", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(treeId, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("TreeView not found: " + treeId);
        }
        if (!(node instanceof TreeView)) {
            throw new IllegalArgumentException("Node is not a TreeView: " + node.getClass().getSimpleName());
        }
        TreeView<Object> tree = (TreeView<Object>) node;
        tree.getSelectionModel().clearSelection();
        return jsonBooleanResult("cleared", true);
    }

    @SuppressWarnings("unchecked")
    private String expandTreeItem(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("TreeView not found: " + selector);
        }
        if (!(node instanceof TreeView)) {
            throw new IllegalArgumentException("Node is not a TreeView: " + node.getClass().getSimpleName());
        }
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), text);
        if (item == null) {
            JsonObject response = new JsonObject();
            response.addProperty("expanded", false);
            response.addProperty("error", "Item not found: " + text);
            return response.toString();
        }
        item.setExpanded(true);
        JsonObject response = new JsonObject();
        response.addProperty("expanded", true);
        response.addProperty("text", item.getValue() != null ? item.getValue().toString() : null);
        return response.toString();
    }

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
     * Click (or double-click) a tree item by text. This selects the item first,
     * scrolls it into view, then fires mouse events on the visible cell.
     * For catalogue entries, double-click triggers "select entry" (add to roster).
     */
    @SuppressWarnings("unchecked")
    private String clickTreeItem(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String text = getString(paramsObject, "text", null);
        boolean doubleClick = getBoolean(paramsObject, "doubleClick", false);
        int clickCount = doubleClick ? 2 : 1;

        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("TreeView not found: " + selector);
        }
        if (!(node instanceof TreeView)) {
            throw new IllegalArgumentException("Node is not a TreeView: " + node.getClass().getSimpleName());
        }
        TreeView<Object> tree = (TreeView<Object>) node;
        TreeItem<Object> item = findTreeItemByText(tree.getRoot(), text);
        if (item == null) {
            JsonObject response = new JsonObject();
            response.addProperty("clicked", false);
            response.addProperty("error", "Item not found: " + text);
            return response.toString();
        }

        int itemIndex = tree.getRow(item);
        tree.getSelectionModel().select(item);
        tree.scrollTo(itemIndex);

        Node cellNode = null;
        for (Node child : tree.lookupAll(".tree-cell")) {
            if (child instanceof javafx.scene.control.TreeCell) {
                javafx.scene.control.TreeCell<?> cell = (javafx.scene.control.TreeCell<?>) child;
                if (cell.getTreeItem() == item && !cell.isEmpty()) {
                    cellNode = cell;
                    break;
                }
            }
        }

        if (cellNode == null) {
            cellNode = tree;
        }

        var bounds = cellNode.localToScreen(cellNode.getBoundsInLocal());
        if (bounds == null) {
            JsonObject response = new JsonObject();
            response.addProperty("clicked", false);
            response.addProperty("error", "Node bounds not available (window not visible?)");
            return response.toString();
        }
        double x = bounds.getMinX() + bounds.getWidth() / 2;
        double y = bounds.getMinY() + bounds.getHeight() / 2;
        double localX = bounds.getWidth() / 2;
        double localY = bounds.getHeight() / 2;

        cellNode.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_PRESSED, localX, localY, x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false, true, false, false, null));
        cellNode.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_RELEASED, localX, localY, x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false, true, false, false, null));
        cellNode.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_CLICKED, localX, localY, x, y,
                MouseButton.PRIMARY, clickCount,
                false, false, false, false,
                true, false, false, true, false, false, null));

        JsonObject response = new JsonObject();
        response.addProperty("clicked", true);
        response.addProperty("doubleClick", doubleClick);
        response.addProperty("text", item.getValue() != null ? item.getValue().toString() : null);
        response.addProperty("cellFound", cellNode != tree);
        return response.toString();
    }

    /**
     * Press a key on the currently focused node or a specified node.
     * key: KeyCode name (e.g., "DELETE", "ENTER", "ESCAPE")
     */
    private String pressKey(String params) {
        JsonObject paramsObject = parseParams(params);
        String keyName = getString(paramsObject, "key", null);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        boolean ctrl = getBoolean(paramsObject, "ctrl", false);
        boolean alt = getBoolean(paramsObject, "alt", false);
        boolean shift = getBoolean(paramsObject, "shift", false);
        boolean meta = getBoolean(paramsObject, "meta", false);

        KeyCode keyCode;
        try {
            keyCode = KeyCode.valueOf(keyName.toUpperCase());
        } catch (IllegalArgumentException e) {
            throw new IllegalArgumentException("Unknown key: " + keyName);
        }

        Node target;
        if (selector != null) {
            target = resolveNode(selector, windowTitle);
            if (target == null) {
                throw new IllegalArgumentException("Node not found: " + selector);
            }
        } else {
            Scene scene = findScene(windowTitle);
            if (scene == null) {
                throw new IllegalArgumentException("No scene found");
            }
            target = scene.getFocusOwner();
            if (target == null) {
                target = scene.getRoot();
            }
        }

        target.fireEvent(new KeyEvent(
                KeyEvent.KEY_PRESSED, "", "", keyCode,
                shift, ctrl, alt, meta));
        target.fireEvent(new KeyEvent(
                KeyEvent.KEY_RELEASED, "", "", keyCode,
                shift, ctrl, alt, meta));

        JsonObject response = new JsonObject();
        response.addProperty("pressed", true);
        response.addProperty("key", keyCode.getName());
        return response.toString();
    }

    /**
     * Get the current value of a Spinner control.
     */
    @SuppressWarnings("unchecked")
    private String getSpinnerValue(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof Spinner)) {
            throw new IllegalArgumentException("Node is not a Spinner: " + node.getClass().getSimpleName());
        }
        Spinner<?> spinner = (Spinner<?>) node;
        Object value = spinner.getValue();
        JsonObject response = new JsonObject();
        addDynamicProperty(response, "value", value);
        response.addProperty("editable", spinner.isEditable());
        return response.toString();
    }

    /**
     * Set the value of a Spinner control by incrementing/decrementing or setting directly.
     * steps: number of steps to increment (positive) or decrement (negative)
     * value: direct integer value to set (alternative to steps)
     */
    @SuppressWarnings("unchecked")
    private String setSpinnerValue(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        int steps = getInt(paramsObject, "steps", 0);
        int value = getInt(paramsObject, "value", -1);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof Spinner)) {
            throw new IllegalArgumentException("Node is not a Spinner: " + node.getClass().getSimpleName());
        }
        Spinner<Object> spinner = (Spinner<Object>) node;
        if (value >= 0) {
            // Try direct value factory set
            SpinnerValueFactory<Object> factory = spinner.getValueFactory();
            if (factory.getClass().getSimpleName().equals("IntegerSpinnerValueFactory")) {
                try {
                    var setValueMethod = factory.getClass().getMethod("setValue", Object.class);
                    setValueMethod.invoke(factory, value);
                } catch (Exception e) {
                    factory.setValue((Object) Integer.valueOf(value));
                }
            } else {
                factory.setValue((Object) Integer.valueOf(value));
            }
        } else if (steps != 0) {
            spinner.getValueFactory().increment(steps);
        }
        Object newValue = spinner.getValue();
        JsonObject response = new JsonObject();
        addDynamicProperty(response, "value", newValue);
        return response.toString();
    }

    private String dumpNodeProperties(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            Scene scene = findScene(windowTitle);
            if (scene == null) return jsonError("no scene");
            node = scene.getRoot();
        }

        JsonObject response = new JsonObject();
        response.addProperty("nodeType", node.getClass().getName());
        response.addProperty("id", node.getId());

        JsonObject properties = new JsonObject();
        for (Object key : node.getProperties().keySet()) {
            Object val = node.getProperties().get(key);
            if (val == null) {
                properties.add(String.valueOf(key), JsonNull.INSTANCE);
            } else {
                JsonObject property = new JsonObject();
                property.addProperty("type", val.getClass().getName());
                property.addProperty("toString", val.toString());
                properties.add(String.valueOf(key), property);
            }
        }
        response.add("properties", properties);

        Object ud = node.getUserData();
        if (ud != null) {
            JsonObject userData = new JsonObject();
            userData.addProperty("type", ud.getClass().getName());
            userData.addProperty("toString", ud.toString());
            response.add("userData", userData);
        }
        return response.toString();
    }

    /**
     * Find a control (Spinner, CheckBox, or Button) in the scene by looking for it adjacent
     * to a Label whose text contains the specified text. Used for edit panel child entries.
     * Params: text (label text to match), windowTitle (optional), controlType (optional: spinner, checkbox, button)
     * Returns: JSON with found control info (type, index, value if applicable)
     */
    private String findControlByLabel(String params) {
        JsonObject paramsObject = parseParams(params);
        String text = getString(paramsObject, "text", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String controlType = getString(paramsObject, "controlType", null);

        Scene scene = findScene(windowTitle);
        if (scene == null) return jsonError("no scene");

        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String labelText = label.getText();
            if (labelText == null || !labelText.contains(text)) continue;

            Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    if (controlType != null && !controlType.equals("spinner")) continue;
                    Spinner<?> spinner = (Spinner<?>) sibling;
                    Object val = spinner.getValue();
                    JsonObject response = new JsonObject();
                    response.addProperty("found", true);
                    response.addProperty("controlType", "spinner");
                    response.addProperty("labelText", labelText);
                    addDynamicProperty(response, "value", val);
                    response.addProperty("parentClass", parent.getClass().getSimpleName());
                    return response.toString();
                }
                if (sibling instanceof Button) {
                    if (controlType != null && !controlType.equals("button")) continue;
                    JsonObject response = new JsonObject();
                    response.addProperty("found", true);
                    response.addProperty("controlType", "button");
                    response.addProperty("labelText", labelText);
                    response.addProperty("parentClass", parent.getClass().getSimpleName());
                    return response.toString();
                }
            }
        }

        for (Node cbNode : scene.getRoot().lookupAll(".check-box")) {
            if (!(cbNode instanceof CheckBox)) continue;
            CheckBox cb = (CheckBox) cbNode;
            String cbText = cb.getText();
            if (cbText != null && cbText.contains(text)) {
                if (controlType != null && !controlType.equals("checkbox")) continue;
                JsonObject response = new JsonObject();
                response.addProperty("found", true);
                response.addProperty("controlType", "checkbox");
                response.addProperty("labelText", cbText);
                response.addProperty("selected", cb.isSelected());
                return response.toString();
            }
        }

        JsonObject response = new JsonObject();
        response.addProperty("found", false);
        response.addProperty("searchedText", text);
        return response.toString();
    }

    /**
     * Click a control found by its sibling label text. Used for adding child entries.
     * For Spinners: increments by 1 step. For CheckBoxes: toggles. For Buttons: fires.
     * The actual interaction is scheduled via Platform.runLater to avoid deadlocks
     * when the change triggers BS engine operations on the FX thread.
     * Params: text (label text to match), windowTitle (optional), controlType (optional)
     */
    private String clickControlByLabel(String params) {
        JsonObject paramsObject = parseParams(params);
        String text = getString(paramsObject, "text", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        String action = getString(paramsObject, "action", null);

        Scene scene = findScene(windowTitle);
        if (scene == null) return jsonError("no scene");

        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String labelText = label.getText();
            if (labelText == null || !labelText.contains(text)) continue;

            Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    @SuppressWarnings("unchecked")
                    Spinner<Object> spinner = (Spinner<Object>) sibling;
                    boolean decrement = "decrement".equals(action);
                    if (decrement) {
                        Platform.runLater(() -> spinner.getValueFactory().decrement(1));
                    } else {
                        Platform.runLater(() -> spinner.getValueFactory().increment(1));
                    }
                    JsonObject response = new JsonObject();
                    response.addProperty("clicked", true);
                    response.addProperty("controlType", "spinner");
                    response.addProperty("action", decrement ? "decrement" : "increment");
                    response.addProperty("labelText", labelText);
                    return response.toString();
                }
                if (sibling instanceof Button) {
                    Button button = (Button) sibling;
                    Platform.runLater(() -> button.fire());
                    JsonObject response = new JsonObject();
                    response.addProperty("clicked", true);
                    response.addProperty("controlType", "button");
                    response.addProperty("action", "fire");
                    response.addProperty("labelText", labelText);
                    return response.toString();
                }
            }
        }

        for (Node cbNode : scene.getRoot().lookupAll(".check-box")) {
            if (!(cbNode instanceof CheckBox)) continue;
            CheckBox cb = (CheckBox) cbNode;
            String cbText = cb.getText();
            if (cbText != null && cbText.contains(text)) {
                Platform.runLater(() -> cb.fire());
                JsonObject response = new JsonObject();
                response.addProperty("clicked", true);
                response.addProperty("controlType", "checkbox");
                response.addProperty("action", "toggle");
                response.addProperty("labelText", cbText);
                return response.toString();
            }
        }

        JsonObject response = new JsonObject();
        response.addProperty("clicked", false);
        response.addProperty("error", "Control not found for text: " + text);
        return response.toString();
    }

    /**
     * Set a Spinner's value by its sibling label text. Used for setSelectionCount via parent's edit panel.
     * Finds the Spinner adjacent to the matching Label, then sets its value via Platform.runLater.
     * Params: text (label text to match), value (integer target), windowTitle (optional)
     */
    @SuppressWarnings("unchecked")
    private String setSpinnerValueByLabel(String params) {
        JsonObject paramsObject = parseParams(params);
        String text = getString(paramsObject, "text", null);
        int value = getInt(paramsObject, "value", -1);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        if (value < 0) {
            return jsonError("Missing or invalid 'value' parameter.");
        }

        Scene scene = findScene(windowTitle);
        if (scene == null) return jsonError("no scene");

        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String labelText = label.getText();
            if (labelText == null || !labelText.contains(text)) continue;

            Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    Spinner<Object> spinner = (Spinner<Object>) sibling;
                    Object currentVal = spinner.getValue();
                    int currentInt = (currentVal instanceof Number) ? ((Number) currentVal).intValue() : 0;
                    JsonObject response = new JsonObject();
                    response.addProperty("set", true);
                    response.addProperty("controlType", "spinner");
                    response.addProperty("labelText", labelText);
                    response.addProperty("previousValue", currentInt);
                    response.addProperty("value", value);
                    if (currentInt == value) {
                        response.addProperty("noChange", true);
                        return response.toString();
                    }
                    int delta = value - currentInt;
                    long startMs = System.currentTimeMillis();
                    SpinnerValueFactory<Object> factory = spinner.getValueFactory();
                    if (delta > 0) {
                        for (int i = 0; i < delta; i++) factory.increment(1);
                    } else {
                        for (int i = 0; i < -delta; i++) factory.decrement(1);
                    }
                    long elapsedMs = System.currentTimeMillis() - startMs;
                    System.err.println("[bs-ui-agent] setSpinnerValueByLabel: increment took " + elapsedMs + "ms");
                    response.addProperty("elapsedMs", elapsedMs);
                    return response.toString();
                }
            }
        }

        JsonObject response = new JsonObject();
        response.addProperty("set", false);
        response.addProperty("error", "Spinner not found for label text: " + text);
        return response.toString();
    }

    private String readRosterName(Scene scene) {
        String fromTitle = extractRosterNameFromTitle(getWindowTitle(scene));
        if (fromTitle != null) {
            return fromTitle;
        }
        return findRosterNameInScene(scene.getRoot());
    }

    private String extractRosterNameFromTitle(String title) {
        if (title == null) {
            return null;
        }
        Matcher matcher = TITLE_ROSTER_NAME_PATTERN.matcher(title.trim());
        if (!matcher.matches()) {
            return null;
        }
        String candidate = matcher.group(1);
        if (candidate == null) {
            return null;
        }
        candidate = candidate.trim();
        if (candidate.isEmpty() || candidate.startsWith("Roster Editor")) {
            return null;
        }
        return candidate;
    }

    private String findRosterNameInScene(Node node) {
        if (node == null || !node.isVisible()) {
            return null;
        }
        String id = node.getId();
        String text = extractTextContent(node);
        if (text != null) {
            String trimmed = text.trim();
            if (!trimmed.isEmpty() && id != null) {
                String lowerId = id.toLowerCase();
                if ((lowerId.contains("roster") || lowerId.contains("name"))
                        && !trimmed.equalsIgnoreCase("Roster Editor")) {
                    return trimmed;
                }
            }
        }
        if (node instanceof Parent) {
            for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
                String childName = findRosterNameInScene(child);
                if (childName != null) {
                    return childName;
                }
            }
        }
        return null;
    }

    @SuppressWarnings("unchecked")
    private TreeView<Object> findRosterTree(Scene scene) {
        if (scene == null) {
            return null;
        }
        for (String selector : new String[] { "#treeRoster", "#treeForces" }) {
            Node node = scene.getRoot().lookup(selector);
            if (node instanceof TreeView && node.isVisible()) {
                return (TreeView<Object>) node;
            }
        }
        return findFirstVisibleTree(scene.getRoot());
    }

    @SuppressWarnings("unchecked")
    private TreeView<Object> findFirstVisibleTree(Node node) {
        if (node == null || !node.isVisible()) {
            return null;
        }
        if (node instanceof TreeView) {
            return (TreeView<Object>) node;
        }
        if (node instanceof Parent) {
            for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
                TreeView<Object> tree = findFirstVisibleTree(child);
                if (tree != null) {
                    return tree;
                }
            }
        }
        return null;
    }

    private JsonArray getVisibleForces(TreeView<Object> tree) {
        JsonArray forces = new JsonArray();
        if (tree == null) {
            return forces;
        }
        TreeItem<Object> root = tree.getRoot();
        if (root == null || root.getChildren().isEmpty()) {
            return forces;
        }
        for (TreeItem<Object> forceItem : root.getChildren()) {
            if (forceItem == null) {
                continue;
            }
            JsonObject force = new JsonObject();
            force.addProperty("name", treeItemText(forceItem));
            force.add("selections", getVisibleSelections(forceItem));
            forces.add(force);
        }
        return forces;
    }

    private JsonArray getVisibleSelections(TreeItem<Object> parent) {
        JsonArray selections = new JsonArray();
        if (parent == null || !parent.isExpanded()) {
            return selections;
        }
        for (TreeItem<Object> child : parent.getChildren()) {
            if (child != null) {
                selections.add(selectionToJson(child));
            }
        }
        return selections;
    }

    private JsonObject selectionToJson(TreeItem<Object> item) {
        NameCount parsed = splitNameAndCount(treeItemText(item));
        JsonObject selection = new JsonObject();
        selection.addProperty("name", parsed.name);
        selection.addProperty("count", parsed.count);
        JsonArray children = new JsonArray();
        if (item.isExpanded() && !item.getChildren().isEmpty()) {
            for (TreeItem<Object> child : item.getChildren()) {
                if (child != null) {
                    children.add(selectionToJson(child));
                }
            }
        }
        selection.add("children", children);
        return selection;
    }

    private String treeItemText(TreeItem<Object> item) {
        if (item == null || item.getValue() == null) {
            return null;
        }
        return item.getValue().toString();
    }

    private NameCount splitNameAndCount(String text) {
        if (text == null) {
            return new NameCount(null, "1");
        }
        String trimmed = text.trim();
        Matcher leadingCount = LEADING_COUNT_PATTERN.matcher(trimmed);
        if (leadingCount.matches()) {
            return new NameCount(leadingCount.group(2).trim(), leadingCount.group(1));
        }
        Matcher trailingCount = TRAILING_COUNT_PATTERN.matcher(trimmed);
        if (trailingCount.matches()) {
            return new NameCount(trailingCount.group(1).trim(), trailingCount.group(2));
        }
        Matcher leadingNumber = LEADING_NUMBER_PATTERN.matcher(trimmed);
        if (leadingNumber.matches()) {
            return new NameCount(leadingNumber.group(2).trim(), leadingNumber.group(1));
        }
        return new NameCount(trimmed, "1");
    }

    private void collectCosts(Node node, Map<String, String> costs) {
        if (node == null || !node.isVisible()) {
            return;
        }
        String text = extractTextContent(node);
        if (text != null) {
            addCostFromText(text, costs);
        }
        if (node instanceof Parent) {
            Parent parent = (Parent) node;
            collectSiblingCosts(parent, costs);
            for (Node child : parent.getChildrenUnmodifiable()) {
                collectCosts(child, costs);
            }
        }
    }

    private void collectSiblingCosts(Parent parent, Map<String, String> costs) {
        List<Node> children = parent.getChildrenUnmodifiable();
        for (int i = 0; i < children.size(); i++) {
            Node first = children.get(i);
            if (first == null || !first.isVisible()) {
                continue;
            }
            String firstText = normalizedNodeText(first);
            if (firstText == null) {
                continue;
            }
            String costName = normalizeCostName(firstText);
            if (costName != null) {
                for (int j = i + 1; j < children.size(); j++) {
                    Node second = children.get(j);
                    if (second == null || !second.isVisible()) {
                        continue;
                    }
                    String secondText = normalizedNodeText(second);
                    if (secondText == null) {
                        continue;
                    }
                    String numericValue = extractNumericValue(secondText);
                    if (numericValue != null) {
                        costs.put(costName, numericValue);
                        break;
                    }
                }
            }
        }
    }

    private void addCostFromText(String rawText, Map<String, String> costs) {
        String text = rawText.trim();
        if (text.isEmpty()) {
            return;
        }

        Matcher inlineMatcher = COST_INLINE_PATTERN.matcher(text);
        if (inlineMatcher.matches()) {
            String costName = normalizeCostName(inlineMatcher.group(1));
            if (costName != null) {
                costs.put(costName, inlineMatcher.group(2));
            }
            return;
        }

        Matcher suffixMatcher = COST_SUFFIX_PATTERN.matcher(text);
        if (suffixMatcher.matches()) {
            String costName = normalizeCostName(suffixMatcher.group(2));
            if (costName != null) {
                costs.put(costName, suffixMatcher.group(1));
            }
        }
    }

    private String normalizedNodeText(Node node) {
        String text = extractTextContent(node);
        if (text == null) {
            return null;
        }
        text = text.trim();
        return text.isEmpty() ? null : text;
    }

    private String normalizeCostName(String rawName) {
        if (rawName == null) {
            return null;
        }
        String normalized = rawName.trim().toLowerCase();
        if (normalized.endsWith(":")) {
            normalized = normalized.substring(0, normalized.length() - 1).trim();
        }
        if (normalized.equals("pt") || normalized.equals("pts") || normalized.equals("point") || normalized.equals("points")) {
            return "pts";
        }
        if (normalized.equals("power") || normalized.equals("power level")) {
            return "power";
        }
        if (normalized.equals("pl")) {
            return "pl";
        }
        if (normalized.equals("cp")) {
            return "cp";
        }
        if (normalized.equals("ep")) {
            return "ep";
        }
        if (normalized.equals("vp")) {
            return "vp";
        }
        return null;
    }

    private String extractNumericValue(String text) {
        if (text == null) {
            return null;
        }
        String trimmed = text.trim();
        return NUMERIC_VALUE_PATTERN.matcher(trimmed).matches() ? trimmed : null;
    }

    private JsonArray costsToJson(Map<String, String> costs) {
        JsonArray result = new JsonArray();
        for (Map.Entry<String, String> entry : costs.entrySet()) {
            JsonObject item = new JsonObject();
            item.addProperty("name", entry.getKey());
            item.addProperty("value", entry.getValue());
            result.add(item);
        }
        return result;
    }

    private static final class NameCount {
        private final String name;
        private final String count;

        private NameCount(String name, String count) {
            this.name = name;
            this.count = count;
        }
    }

    // --- Helpers ---

    private Node resolveNode(String selector, String windowTitle) {
        if (selector == null) {
            return null;
        }
        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return null;
        }
        return scene.getRoot().lookup(selector);
    }

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
        // Fallback: any window with a scene
        for (Window w : Window.getWindows()) {
            if (w.getScene() != null) {
                return w.getScene();
            }
        }
        return null;
    }

    private String getWindowTitle(Scene scene) {
        Window w = scene.getWindow();
        if (w instanceof Stage) {
            return ((Stage) w).getTitle();
        }
        return null;
    }

    private JsonObject dumpNode(Node node, int depth, int maxDepth) {
        JsonObject result = nodeToJsonObject(node);
        if (depth < maxDepth && node instanceof Parent) {
            ObservableList<Node> children = ((Parent) node).getChildrenUnmodifiable();
            if (!children.isEmpty()) {
                JsonArray childrenJson = new JsonArray();
                for (Node child : children) {
                    childrenJson.add(dumpNode(child, depth + 1, maxDepth));
                }
                result.add("children", childrenJson);
            }
        }
        return result;
    }

    private JsonObject nodeToJsonObject(Node node) {
        JsonObject result = new JsonObject();
        result.addProperty("type", node.getClass().getSimpleName());
        String id = node.getId();
        if (id != null && !id.isEmpty()) {
            result.addProperty("id", id);
        }
        var styleClasses = node.getStyleClass();
        if (!styleClasses.isEmpty()) {
            JsonArray styles = new JsonArray();
            for (String styleClass : styleClasses) {
                styles.add(styleClass);
            }
            result.add("styleClasses", styles);
        }
        String text = extractTextContent(node);
        if (text != null) {
            result.addProperty("text", text);
        }
        result.addProperty("visible", node.isVisible());
        result.addProperty("disabled", node.isDisabled());
        return result;
    }

    private static String extractTextContent(Node node) {
        if (node instanceof Labeled) {
            return ((Labeled) node).getText();
        }
        if (node instanceof Text) {
            return ((Text) node).getText();
        }
        if (node instanceof TextInputControl) {
            return ((TextInputControl) node).getText();
        }
        return null;
    }


}
