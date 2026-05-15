package bsspec.uiagent;

import javafx.application.Platform;
import javafx.collections.ObservableList;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.Scene;
import javafx.scene.control.*;
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
            // Engine access commands
            case "listBsClasses":
                return engineAccessor.listBsClasses();
            case "inspectClass":
                return engineAccessor.inspectClass(extractStr(params, "className"));
            case "findEngine":
                return engineAccessor.findEngine();
            case "getRosterState":
                return engineAccessor.getRosterState();
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
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }

        var bounds = node.localToScreen(node.getBoundsInLocal());
        double x = bounds.getMinX() + bounds.getWidth() / 2;
        double y = bounds.getMinY() + bounds.getHeight() / 2;

        // Fire a mouse click event
        node.fireEvent(new MouseEvent(
                MouseEvent.MOUSE_CLICKED,
                bounds.getWidth() / 2, bounds.getHeight() / 2,
                x, y,
                MouseButton.PRIMARY, 1,
                false, false, false, false,
                true, false, false,
                true, false, false, null));

        return "{\"clicked\":true,\"x\":" + x + ",\"y\":" + y + "}";
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
        Node node = resolveNode(selector, windowTitle);
        if (node == null) {
            throw new IllegalArgumentException("Node not found: " + selector);
        }
        if (!(node instanceof javafx.scene.control.ButtonBase)) {
            throw new IllegalArgumentException("Node is not a ButtonBase: " + node.getClass().getSimpleName());
        }
        ((javafx.scene.control.ButtonBase) node).fire();
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

    private String dumpNodeProperties(String params) {
        String selector = extractStr(params, "selector");
        String windowTitle = extractStr(params, "window");
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
        // Try non-string number
        String pattern = "\"" + key + "\"";
        int idx = json.indexOf(pattern);
        if (idx < 0) {
            return defaultValue;
        }
        int colon = json.indexOf(':', idx + pattern.length());
        if (colon < 0) {
            return defaultValue;
        }
        int start = colon + 1;
        while (start < json.length() && json.charAt(start) == ' ') {
            start++;
        }
        int end = start;
        while (end < json.length() && Character.isDigit(json.charAt(end))) {
            end++;
        }
        if (end > start) {
            try {
                return Integer.parseInt(json.substring(start, end));
            } catch (NumberFormatException e) {
                // fall through
            }
        }
        return defaultValue;
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
