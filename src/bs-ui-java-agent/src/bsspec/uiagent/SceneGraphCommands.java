package bsspec.uiagent;

import javafx.application.Platform;
import javafx.collections.ObservableList;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.Scene;
import javafx.scene.control.*;
import javafx.scene.input.KeyCode;
import javafx.scene.input.KeyEvent;
import javafx.scene.input.MouseButton;
import javafx.scene.input.MouseEvent;
import javafx.scene.text.Text;
import javafx.stage.Stage;
import javafx.stage.Window;

import java.util.ArrayList;
import java.util.List;

/**
 * Scene graph operations dispatched by the JSON-RPC server.
 * All methods run on the JavaFX Application Thread.
 */
public class SceneGraphCommands {

    private final EngineAccessor engineAccessor;

    public SceneGraphCommands(EngineAccessor engineAccessor) {
        this.engineAccessor = engineAccessor;
    }

    public String dispatch(String method, String params) {
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
                return engineAccessor.inspectClass(extractStr(params, "className"));
            case "findEngine":
                return engineAccessor.findEngine();
            case "getRosterState":
                return engineAccessor.getRosterState();
            case "getValidationErrors":
                return engineAccessor.getValidationErrors();
            case "readStaticFields":
                return engineAccessor.readStaticFields(extractStr(params, "className"));
            case "dumpNodeProperties":
                return dumpNodeProperties(params);
            default:
                throw new IllegalArgumentException("Unknown method: " + method);
        }
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

    private String dumpTree(String params) {
        int maxDepth = extractInt(params, "maxDepth", 10);
        String windowTitle = extractStr(params, "windowTitle");

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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");

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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        return nodeToJson(node);
    }

    private String clickNode(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");
        boolean doubleClick = "true".equals(extractStr(params, "doubleClick"));
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            return "null";
        }
        return jsonString(extractTextContent(node));
    }

    private String findAllNodes(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");

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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String async = extractStr(params, "async");
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof javafx.scene.control.ButtonBase)) {
            throw new IllegalArgumentException("Node is not a ButtonBase: " + node.getClass().getSimpleName());
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
        String text = extractStr(params, "text");
        String nodeType = extractStr(params, "nodeType");
        String windowTitle = extractStr(params, "windowTitle");

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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");

        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof TextInputControl)) {
            throw new IllegalArgumentException("Node is not a text input: " + node.getClass().getSimpleName());
        }
        ((TextInputControl) node).setText(text != null ? text : "");
        return "{\"set\":true}";
    }

    private String getComboBoxItems(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");
        int index = extractInt(params, "index", -1);
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        int maxDepth = extractInt(params, "maxDepth", 3);
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");
        int index = extractInt(params, "index", -1);
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
    private String expandTreeItem(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        String text = extractStr(params, "text");
        boolean doubleClick = "true".equals(extractStr(params, "doubleClick"));
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
        String keyName = extractStr(params, "key");
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");

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
                false, false, false, false));
        target.fireEvent(new KeyEvent(
                KeyEvent.KEY_RELEASED, "", "", keyCode,
                false, false, false, false));

        return "{\"pressed\":true,\"key\":\"" + keyCode.getName() + "\"}";
    }

    /**
     * Get the current value of a Spinner control.
     */
    @SuppressWarnings("unchecked")
    private String getSpinnerValue(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
        int steps = extractInt(params, "steps", 0);
        int value = extractInt(params, "value", -1);
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
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "windowTitle");
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

    // --- Minimal JSON/param helpers ---

    private static String extractStr(String json, String key) {
        String pattern = "\"" + key + "\"";
        int idx = json.indexOf(pattern);
        if (idx < 0) {
            return null;
        }
        int colon = json.indexOf(':', idx + pattern.length());
        if (colon < 0) {
            return null;
        }
        int start = colon + 1;
        while (start < json.length() && json.charAt(start) == ' ') {
            start++;
        }
        if (start >= json.length() || json.charAt(start) != '"') {
            return null;
        }
        int end = json.indexOf('"', start + 1);
        return end > start ? json.substring(start + 1, end) : null;
    }

    private static int extractInt(String json, String key, int defaultValue) {
        String val = extractStr(json, key);
        if (val != null) {
            try {
                return Integer.parseInt(val);
            } catch (NumberFormatException e) {
                // fall through
            }
        }

        String raw = extractNumberToken(json, key);
        if (raw != null) {
            try {
                return Integer.parseInt(raw);
            } catch (NumberFormatException e) {
                // fall through
            }
        }
        return defaultValue;
    }


    private static String extractNumberToken(String json, String key) {
        String pattern = "\"" + key + "\"";
        int idx = json.indexOf(pattern);
        if (idx < 0) {
            return null;
        }
        int colon = json.indexOf(':', idx + pattern.length());
        if (colon < 0) {
            return null;
        }
        int start = colon + 1;
        while (start < json.length() && json.charAt(start) == ' ') {
            start++;
        }
        int end = start;
        while (end < json.length()) {
            char ch = json.charAt(end);
            if ((ch >= '0' && ch <= '9') || ch == '-' || ch == '+' || ch == '.') {
                end++;
                continue;
            }
            break;
        }
        return end > start ? json.substring(start, end) : null;
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
