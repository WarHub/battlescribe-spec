package bsspec.uiagent;

import com.google.gson.JsonElement;
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
        StringBuilder sb = new StringBuilder("{\"threads\":[");
        boolean first = true;
        for (java.util.Map.Entry<Thread, StackTraceElement[]> entry : Thread.getAllStackTraces().entrySet()) {
            Thread t = entry.getKey();
            StackTraceElement[] stack = entry.getValue();
            if (!first) sb.append(",");
            first = false;
            sb.append("{\"name\":\"").append(jsonEscape(t.getName()))
              .append("\",\"state\":\"").append(t.getState())
              .append("\",\"stack\":[");
            for (int i = 0; i < Math.min(stack.length, 15); i++) {
                if (i > 0) sb.append(",");
                sb.append("\"").append(jsonEscape(stack[i].toString())).append("\"");
            }
            sb.append("]}");
        }
        sb.append("]}");
        return sb.toString();
    }

    private String jsonEscape(String s) {
        return s.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n").replace("\r", "\\r");
    }

    private String getWindows() {
        StringBuilder sb = new StringBuilder("[");
        boolean first = true;
        for (Window w : Window.getWindows()) {
            if (!first) {
                sb.append(",");
            }
            first = false;
            sb.append("{");
            sb.append("\"type\":\"").append(w.getClass().getSimpleName()).append("\"");
            if (w instanceof Stage) {
                Stage s = (Stage) w;
                sb.append(",\"title\":").append(jsonString(s.getTitle()));
                sb.append(",\"showing\":").append(s.isShowing());
            }
            sb.append(",\"width\":").append(w.getWidth());
            sb.append(",\"height\":").append(w.getHeight());
            sb.append("}");
        }
        sb.append("]");
        return sb.toString();
    }

    private String captureScreenshot(String params) {
        JsonObject paramsObject = parseParams(params);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return "{\"error\":\"No scene found\"}";
        }

        WritableImage image = scene.snapshot(null);
        // Convert WritableImage to PNG using reflection to access SwingFXUtils
        // (javafx.swing module may not be in JRE, so we use reflection)
        try {
            Class<?> swingFxClass = Class.forName("javafx.embed.swing.SwingFXUtils");
            java.lang.reflect.Method fromFXImage = swingFxClass.getMethod("fromFXImage",
                javafx.scene.image.Image.class, java.awt.image.BufferedImage.class);
            Object buffered = fromFXImage.invoke(null, image, null);
            if (buffered == null) {
                return "{\"error\":\"Failed to capture scene\"}";
            }
            try (ByteArrayOutputStream baos = new ByteArrayOutputStream()) {
                if (!ImageIO.write((BufferedImage) buffered, "png", baos)) {
                    throw new IOException("No PNG writer available");
                }
                String base64 = Base64.getEncoder().encodeToString(baos.toByteArray());
                return "{\"png\":" + jsonString(base64)
                    + ",\"width\":" + (int) image.getWidth()
                    + ",\"height\":" + (int) image.getHeight() + "}";
            }
        } catch (ClassNotFoundException e) {
            return "{\"error\":\"javafx.swing module not available for screenshots\"}";
        } catch (Exception e) {
            return "{\"error\":" + jsonString("Screenshot failed: " + e.getMessage()) + "}";
        }
    }

    private String startRecording(String params) {
        JsonObject paramsObject = parseParams(params);
        Scene scene = findScene(getString(paramsObject, "windowTitle", null));
        if (scene == null) {
            return "{\"error\":\"No scene found\"}";
        }

        ActionRecorder.getInstance().startRecording(scene);
        return "{\"status\":\"recording\"}";
    }

    private String stopRecording(String params) {
        JsonObject paramsObject = parseParams(params);
        String actions = ActionRecorder.getInstance().stopRecording();
        return "{\"actions\":" + actions + "}";
    }

    private String getRecordedActions(String params) {
        JsonObject paramsObject = parseParams(params);
        String actions = ActionRecorder.getInstance().getActionsJson();
        return "{\"recording\":" + ActionRecorder.getInstance().isRecording() + ",\"actions\":" + actions + "}";
    }

    private String getUiState(String params) {
        JsonObject paramsObject = parseParams(params);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Scene scene = findScene(windowTitle != null ? windowTitle : "Roster Editor");
        if (scene == null && windowTitle != null) {
            scene = findScene(windowTitle);
        }
        if (scene == null) {
            return "{\"rosterName\":null,\"forces\":[],\"costs\":[]}";
        }

        TreeView<Object> rosterTree = findRosterTree(scene);
        Map<String, String> costs = new LinkedHashMap<String, String>();
        collectCosts(scene.getRoot(), costs);

        StringBuilder sb = new StringBuilder("{");
        sb.append("\"rosterName\":").append(jsonString(readRosterName(scene)));
        sb.append(",\"forces\":[");
        appendVisibleForces(rosterTree, sb);
        sb.append("],\"costs\":[");
        appendCosts(costs, sb);
        sb.append("]}");
        return sb.toString();
    }

    private String dumpTree(String params) {
        JsonObject paramsObject = parseParams(params);
        int maxDepth = getInt(paramsObject, "maxDepth", 10);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return "{\"error\":\"No scene found\"}";
        }

        StringBuilder sb = new StringBuilder();
        sb.append("{\"windowTitle\":").append(jsonString(getWindowTitle(scene)));
        sb.append(",\"tree\":");
        dumpNode(scene.getRoot(), sb, 0, maxDepth);
        sb.append("}");
        return sb.toString();
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

        return nodeToJson(node);
    }

    private String getNodeInfo(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        return nodeToJson(node);
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
        double x = bounds.getMinX() + bounds.getWidth() / 2;
        double y = bounds.getMinY() + bounds.getHeight() / 2;

        // Fire mouse events: press, release, click (with correct clickCount)
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

        return "{\"clicked\":true,\"doubleClick\":" + doubleClick + ",\"x\":" + x + ",\"y\":" + y + "}";
    }

    private String getChildren(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "[]";
        }
        if (!(node instanceof Parent)) {
            return "[]";
        }

        StringBuilder sb = new StringBuilder("[");
        boolean first = true;
        for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
            if (!first) {
                sb.append(",");
            }
            first = false;
            sb.append(nodeToJson(child));
        }
        sb.append("]");
        return sb.toString();
    }

    private String getNodeText(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        return jsonString(extractTextContent(node));
    }

    private String findAllNodes(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);

        if (selector == null) {
            throw new IllegalArgumentException("Missing 'selector' param");
        }

        Scene scene = findScene(windowTitle);
        if (scene == null) {
            return "[]";
        }

        var nodes = scene.getRoot().lookupAll(selector);
        StringBuilder sb = new StringBuilder("[");
        boolean first = true;
        for (Node node : nodes) {
            if (!first) {
                sb.append(",");
            }
            first = false;
            sb.append(nodeToJson(node));
        }
        sb.append("]");
        return sb.toString();
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
        if ("true".equals(async)) {
            // Fire asynchronously — schedule on FX thread so this call returns immediately.
            // This is needed when the button opens a modal dialog (showAndWait).
            Platform.runLater(() -> button.fire());
            return "{\"fired\":true,\"async\":true}";
        }
        button.fire();
        return "{\"fired\":true}";
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
        return nodeToJson(found);
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
        return "{\"set\":true}";
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
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"selectedIndex\":").append(combo.getSelectionModel().getSelectedIndex());
        Object selected = combo.getSelectionModel().getSelectedItem();
        sb.append(",\"selectedText\":").append(jsonString(selected != null ? selected.toString() : null));
        sb.append(",\"items\":[");
        for (int i = 0; i < combo.getItems().size(); i++) {
            if (i > 0) sb.append(",");
            Object item = combo.getItems().get(i);
            sb.append("{\"index\":").append(i);
            sb.append(",\"text\":").append(jsonString(item != null ? item.toString() : null));
            sb.append("}");
        }
        sb.append("]}");
        return sb.toString();
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
        return "{\"selectedIndex\":" + combo.getSelectionModel().getSelectedIndex()
                + ",\"selectedText\":" + jsonString(selected != null ? selected.toString() : null) + "}";
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
        if (root == null) return "{\"root\":null}";
        StringBuilder sb = new StringBuilder("{\"root\":");
        serializeTreeItem(root, sb, 0, maxDepth);
        sb.append(",\"showRoot\":").append(tree.isShowRoot());
        sb.append("}");
        return sb.toString();
    }

    private void serializeTreeItem(TreeItem<Object> item, StringBuilder sb, int depth, int maxDepth) {
        sb.append("{");
        Object val = item.getValue();
        sb.append("\"text\":").append(jsonString(val != null ? val.toString() : null));
        sb.append(",\"expanded\":").append(item.isExpanded());
        sb.append(",\"leaf\":").append(item.isLeaf());
        if (depth < maxDepth && !item.getChildren().isEmpty()) {
            sb.append(",\"children\":[");
            for (int i = 0; i < item.getChildren().size(); i++) {
                if (i > 0) sb.append(",");
                serializeTreeItem(item.getChildren().get(i), sb, depth + 1, maxDepth);
            }
            sb.append("]");
        } else if (!item.getChildren().isEmpty()) {
            sb.append(",\"childCount\":").append(item.getChildren().size());
        }
        sb.append("}");
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
                return "{\"selected\":false,\"error\":\"Item not found: " + text + "\"}";
            }
        }
        TreeItem<Object> sel = tree.getSelectionModel().getSelectedItem();
        return "{\"selected\":true,\"selectedText\":"
                + jsonString(sel != null && sel.getValue() != null ? sel.getValue().toString() : null) + "}";
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
        return "{\"cleared\":true}";
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
            return "{\"expanded\":false,\"error\":\"Item not found: " + text + "\"}";
        }
        item.setExpanded(true);
        return "{\"expanded\":true,\"text\":"
                + jsonString(item.getValue() != null ? item.getValue().toString() : null) + "}";
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
            return "{\"clicked\":false,\"error\":\"Item not found: " + text + "\"}";
        }

        // Select the item first (this also scrolls to it)
        int itemIndex = tree.getRow(item);
        tree.getSelectionModel().select(item);
        tree.scrollTo(itemIndex);

        // Find the visible cell rendering this item by scanning VirtualFlow cells
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
            // Fallback: fire on the tree itself at the item's position
            cellNode = tree;
        }

        var bounds = cellNode.localToScreen(cellNode.getBoundsInLocal());
        if (bounds == null) {
            return "{\"clicked\":false,\"error\":\"Node bounds not available (window not visible?)\"}";
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

        return "{\"clicked\":true,\"doubleClick\":" + doubleClick
                + ",\"text\":" + jsonString(item.getValue() != null ? item.getValue().toString() : null)
                + ",\"cellFound\":" + (cellNode != tree)
                + "}";
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

        return "{\"pressed\":true,\"key\":\"" + keyCode.getName() + "\"}";
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
        return "{\"value\":" + (value != null ? value.toString() : "null")
                + ",\"editable\":" + spinner.isEditable() + "}";
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
        return "{\"value\":" + (newValue != null ? newValue.toString() : "null") + "}";
    }

    private String dumpNodeProperties(String params) {
        JsonObject paramsObject = parseParams(params);
        String selector = getString(paramsObject, "selector", null);
        String windowTitle = getString(paramsObject, "windowTitle", null);
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            // If no selector, dump root node properties
            Scene scene = findScene(windowTitle);
            if (scene == null) return "{\"error\":\"no scene\"}";
            node = scene.getRoot();
        }
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"nodeType\":\"").append(node.getClass().getName()).append("\"");
        sb.append(",\"id\":").append(jsonString(node.getId()));
        // Node properties map
        sb.append(",\"properties\":{");
        boolean first = true;
        for (Object key : node.getProperties().keySet()) {
            if (!first) sb.append(",");
            first = false;
            Object val = node.getProperties().get(key);
            sb.append(jsonString(String.valueOf(key))).append(":");
            if (val == null) {
                sb.append("null");
            } else {
                sb.append("{\"type\":\"").append(val.getClass().getName()).append("\"");
                sb.append(",\"toString\":").append(jsonString(val.toString())).append("}");
            }
        }
        sb.append("}");
        // userData
        Object ud = node.getUserData();
        if (ud != null) {
            sb.append(",\"userData\":{\"type\":\"").append(ud.getClass().getName()).append("\"");
            sb.append(",\"toString\":").append(jsonString(ud.toString())).append("}");
        }
        sb.append("}");
        return sb.toString();
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
        String controlType = getString(paramsObject, "controlType", null); // optional filter

        Scene scene = findScene(windowTitle);
        if (scene == null) return "{\"error\":\"no scene\"}";

        // Find all Labels in the scene
        for (Node labelNode : scene.getRoot().lookupAll(".label")) {
            if (!(labelNode instanceof Label)) continue;
            Label label = (Label) labelNode;
            String labelText = label.getText();
            if (labelText == null || !labelText.contains(text)) continue;

            // Found a matching label. Look at its parent (usually an HBox) for sibling controls.
            Parent parent = label.getParent();
            if (parent == null) continue;

            for (Node sibling : parent.getChildrenUnmodifiable()) {
                if (sibling == label) continue;
                if (sibling instanceof Spinner) {
                    if (controlType != null && !controlType.equals("spinner")) continue;
                    Spinner<?> spinner = (Spinner<?>) sibling;
                    Object val = spinner.getValue();
                    return "{\"found\":true,\"controlType\":\"spinner\",\"labelText\":" + jsonString(labelText) +
                            ",\"value\":" + (val != null ? val.toString() : "null") +
                            ",\"parentClass\":\"" + parent.getClass().getSimpleName() + "\"}";
                }
                if (sibling instanceof Button) {
                    if (controlType != null && !controlType.equals("button")) continue;
                    return "{\"found\":true,\"controlType\":\"button\",\"labelText\":" + jsonString(labelText) +
                            ",\"parentClass\":\"" + parent.getClass().getSimpleName() + "\"}";
                }
            }

            // Also check: the label itself might BE a CheckBox (CheckBox extends ButtonBase which shows text)
            if (parent instanceof CheckBox) {
                // Actually CheckBox IS a Labeled with text
            }
        }

        // Also check CheckBoxes directly (they have text built-in, no separate label)
        for (Node cbNode : scene.getRoot().lookupAll(".check-box")) {
            if (!(cbNode instanceof CheckBox)) continue;
            CheckBox cb = (CheckBox) cbNode;
            String cbText = cb.getText();
            if (cbText != null && cbText.contains(text)) {
                if (controlType != null && !controlType.equals("checkbox")) continue;
                return "{\"found\":true,\"controlType\":\"checkbox\",\"labelText\":" + jsonString(cbText) +
                        ",\"selected\":" + cb.isSelected() + "}";
            }
        }

        return "{\"found\":false,\"searchedText\":" + jsonString(text) + "}";
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
        String action = getString(paramsObject, "action", null); // "increment" or "decrement", default increment

        Scene scene = findScene(windowTitle);
        if (scene == null) return "{\"error\":\"no scene\"}";

        // Try spinners first (in HBox with Label)
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
                    String act = decrement ? "decrement" : "increment";
                    return "{\"clicked\":true,\"controlType\":\"spinner\",\"action\":\"" + act + "\"" +
                            ",\"labelText\":" + jsonString(labelText) + "}";
                }
                if (sibling instanceof Button) {
                    Button button = (Button) sibling;
                    Platform.runLater(() -> button.fire());
                    return "{\"clicked\":true,\"controlType\":\"button\",\"action\":\"fire\"" +
                            ",\"labelText\":" + jsonString(labelText) + "}";
                }
            }
        }

        // Try CheckBoxes (text is built-in)
        for (Node cbNode : scene.getRoot().lookupAll(".check-box")) {
            if (!(cbNode instanceof CheckBox)) continue;
            CheckBox cb = (CheckBox) cbNode;
            String cbText = cb.getText();
            if (cbText != null && cbText.contains(text)) {
                Platform.runLater(() -> cb.fire());
                return "{\"clicked\":true,\"controlType\":\"checkbox\",\"action\":\"toggle\"" +
                        ",\"labelText\":" + jsonString(cbText) + "}";
            }
        }

        return "{\"clicked\":false,\"error\":\"Control not found for text: " + text + "\"}";
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
            return "{\"error\":\"Missing or invalid 'value' parameter.\"}";
        }

        Scene scene = findScene(windowTitle);
        if (scene == null) return "{\"error\":\"no scene\"}";

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
                    if (currentInt == value) {
                        return "{\"set\":true,\"controlType\":\"spinner\",\"labelText\":" + jsonString(labelText) +
                                ",\"previousValue\":" + currentInt + ",\"value\":" + value + ",\"noChange\":true}";
                    }
                    // Set value directly (we're already on FX thread) to trigger change listeners synchronously
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
                    return "{\"set\":true,\"controlType\":\"spinner\",\"labelText\":" + jsonString(labelText) +
                            ",\"previousValue\":" + currentInt + ",\"value\":" + value +
                            ",\"elapsedMs\":" + elapsedMs + "}";
                }
            }
        }

        return "{\"set\":false,\"error\":\"Spinner not found for label text: " + text + "\"}";
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

    private void appendVisibleForces(TreeView<Object> tree, StringBuilder sb) {
        if (tree == null) {
            return;
        }
        TreeItem<Object> root = tree.getRoot();
        if (root == null || root.getChildren().isEmpty()) {
            return;
        }
        boolean firstForce = true;
        for (TreeItem<Object> forceItem : root.getChildren()) {
            if (forceItem == null) {
                continue;
            }
            if (!firstForce) {
                sb.append(",");
            }
            firstForce = false;
            sb.append("{\"name\":").append(jsonString(treeItemText(forceItem)));
            sb.append(",\"selections\":[");
            boolean[] firstSelection = new boolean[] { true };
            appendVisibleSelections(forceItem, sb, firstSelection);
            sb.append("]}");
        }
    }

    private void appendVisibleSelections(TreeItem<Object> parent, StringBuilder sb, boolean[] firstSelection) {
        if (parent == null || !parent.isExpanded()) {
            return;
        }
        for (TreeItem<Object> child : parent.getChildren()) {
            if (child == null) {
                continue;
            }
            if (!firstSelection[0]) {
                sb.append(",");
            }
            firstSelection[0] = false;
            appendSelectionWithChildren(child, sb);
        }
    }

    private void appendSelectionWithChildren(TreeItem<Object> item, StringBuilder sb) {
        NameCount parsed = splitNameAndCount(treeItemText(item));
        sb.append("{\"name\":").append(jsonString(parsed.name));
        sb.append(",\"count\":").append(jsonString(parsed.count));
        sb.append(",\"children\":[");
        if (item.isExpanded() && !item.getChildren().isEmpty()) {
            boolean[] first = new boolean[] { true };
            for (TreeItem<Object> child : item.getChildren()) {
                if (child == null) {
                    continue;
                }
                if (!first[0]) {
                    sb.append(",");
                }
                first[0] = false;
                appendSelectionWithChildren(child, sb);
            }
        }
        sb.append("]}");
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

    private void appendCosts(Map<String, String> costs, StringBuilder sb) {
        boolean first = true;
        for (Map.Entry<String, String> entry : costs.entrySet()) {
            if (!first) {
                sb.append(",");
            }
            first = false;
            sb.append("{\"name\":").append(jsonString(entry.getKey()));
            sb.append(",\"value\":").append(jsonString(entry.getValue()));
            sb.append("}");
        }
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

    private void dumpNode(Node node, StringBuilder sb, int depth, int maxDepth) {
        sb.append("{");
        sb.append("\"type\":\"").append(node.getClass().getSimpleName()).append("\"");
        String id = node.getId();
        if (id != null && !id.isEmpty()) {
            sb.append(",\"id\":").append(jsonString(id));
        }
        var styleClasses = node.getStyleClass();
        if (!styleClasses.isEmpty()) {
            sb.append(",\"styleClasses\":[");
            for (int i = 0; i < styleClasses.size(); i++) {
                if (i > 0) {
                    sb.append(",");
                }
                sb.append(jsonString(styleClasses.get(i)));
            }
            sb.append("]");
        }
        String text = extractTextContent(node);
        if (text != null) {
            sb.append(",\"text\":").append(jsonString(text));
        }
        sb.append(",\"visible\":").append(node.isVisible());
        sb.append(",\"disabled\":").append(node.isDisabled());

        if (depth < maxDepth && node instanceof Parent) {
            ObservableList<Node> children = ((Parent) node).getChildrenUnmodifiable();
            if (!children.isEmpty()) {
                sb.append(",\"children\":[");
                for (int i = 0; i < children.size(); i++) {
                    if (i > 0) {
                        sb.append(",");
                    }
                    dumpNode(children.get(i), sb, depth + 1, maxDepth);
                }
                sb.append("]");
            }
        }
        sb.append("}");
    }

    private String nodeToJson(Node node) {
        StringBuilder sb = new StringBuilder("{");
        sb.append("\"type\":\"").append(node.getClass().getSimpleName()).append("\"");
        String id = node.getId();
        if (id != null && !id.isEmpty()) {
            sb.append(",\"id\":").append(jsonString(id));
        }
        var styleClasses = node.getStyleClass();
        if (!styleClasses.isEmpty()) {
            sb.append(",\"styleClasses\":[");
            for (int i = 0; i < styleClasses.size(); i++) {
                if (i > 0) {
                    sb.append(",");
                }
                sb.append(jsonString(styleClasses.get(i)));
            }
            sb.append("]");
        }
        String text = extractTextContent(node);
        if (text != null) {
            sb.append(",\"text\":").append(jsonString(text));
        }
        sb.append(",\"visible\":").append(node.isVisible());
        sb.append(",\"disabled\":").append(node.isDisabled());
        sb.append("}");
        return sb.toString();
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


    private static String jsonString(String value) {
        if (value == null) {
            return "null";
        }
        StringBuilder sb = new StringBuilder("\"");
        for (char c : value.toCharArray()) {
            switch (c) {
                case '"': sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\n': sb.append("\\n"); break;
                case '\r': sb.append("\\r"); break;
                case '\t': sb.append("\\t"); break;
                default: sb.append(c);
            }
        }
        sb.append("\"");
        return sb.toString();
    }
}
