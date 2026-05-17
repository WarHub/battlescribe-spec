package bsspec.uiagent;

import javafx.beans.value.ChangeListener;
import javafx.collections.ListChangeListener;
import javafx.event.EventHandler;
import javafx.scene.Node;
import javafx.scene.Parent;
import javafx.scene.Scene;
import javafx.scene.control.Button;
import javafx.scene.control.CheckBox;
import javafx.scene.control.ComboBox;
import javafx.scene.control.Spinner;
import javafx.scene.control.TreeCell;
import javafx.scene.control.TreeView;
import javafx.scene.input.MouseButton;
import javafx.scene.input.MouseEvent;

import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Records user interactions with the BattleScribe Roster Editor UI
 * and maps them to high-level actions using value-change listeners
 * on controls (not just mouse clicks).
 */
public class ActionRecorder {

    private static final ActionRecorder instance = new ActionRecorder();

    private final List<RecordedAction> actions = Collections.synchronizedList(new ArrayList<RecordedAction>());
    private Scene attachedScene;
    private boolean recording;

    // Listeners to detach on stop
    private EventHandler<MouseEvent> clickHandler;
    private final Set<Runnable> detachCallbacks = new HashSet<>();

    public static ActionRecorder getInstance() {
        return instance;
    }

    public void startRecording(Scene scene) {
        if (recording) {
            stopRecording();
        }

        actions.clear();
        attachedScene = scene;
        recording = true;

        // Mouse click handler for buttons and tree items
        clickHandler = event -> {
            if (event.getButton() != MouseButton.PRIMARY) {
                return;
            }
            Object rawTarget = event.getTarget();
            if (!(rawTarget instanceof Node)) {
                return;
            }
            RecordedAction action = analyzeClick((Node) rawTarget);
            if (action != null) {
                actions.add(action);
            }
        };
        scene.addEventFilter(MouseEvent.MOUSE_CLICKED, clickHandler);

        // Attach value-change listeners to existing controls in the scene
        attachControlListeners(scene.getRoot());

        // Listen for new nodes added to the scene graph
        ListChangeListener<Node> childListener = change -> {
            while (change.next()) {
                if (change.wasAdded()) {
                    for (Node added : change.getAddedSubList()) {
                        attachControlListeners(added);
                    }
                }
            }
        };
        if (scene.getRoot() instanceof Parent) {
            ((Parent) scene.getRoot()).getChildrenUnmodifiable().addListener(childListener);
            detachCallbacks.add(() ->
                ((Parent) scene.getRoot()).getChildrenUnmodifiable().removeListener(childListener));
        }
    }

    private void attachControlListeners(Node node) {
        if (node == null || !recording) {
            return;
        }

        if (node instanceof ComboBox) {
            ComboBox<?> combo = (ComboBox<?>) node;
            ChangeListener<Object> listener = (obs, oldVal, newVal) -> {
                if (recording && newVal != null) {
                    actions.add(new RecordedAction(
                        "comboBoxSelect",
                        "value", newVal.toString(),
                        "id", combo.getId()));
                }
            };
            combo.valueProperty().addListener(listener);
            detachCallbacks.add(() -> combo.valueProperty().removeListener(listener));
        } else if (node instanceof Spinner) {
            Spinner<?> spinner = (Spinner<?>) node;
            ChangeListener<Object> listener = (obs, oldVal, newVal) -> {
                if (recording && newVal != null) {
                    actions.add(new RecordedAction(
                        "spinnerChange",
                        "value", newVal.toString(),
                        "oldValue", oldVal != null ? oldVal.toString() : null));
                }
            };
            spinner.valueProperty().addListener(listener);
            detachCallbacks.add(() -> spinner.valueProperty().removeListener(listener));
        } else if (node instanceof CheckBox) {
            CheckBox checkBox = (CheckBox) node;
            ChangeListener<Boolean> listener = (obs, oldVal, newVal) -> {
                if (recording) {
                    actions.add(new RecordedAction(
                        "checkBoxToggle",
                        "text", checkBox.getText(),
                        "selected", String.valueOf(newVal)));
                }
            };
            checkBox.selectedProperty().addListener(listener);
            detachCallbacks.add(() -> checkBox.selectedProperty().removeListener(listener));
        }

        // Recurse into children
        if (node instanceof Parent) {
            for (Node child : ((Parent) node).getChildrenUnmodifiable()) {
                attachControlListeners(child);
            }
        }
    }

    public String stopRecording() {
        recording = false;
        if (attachedScene != null && clickHandler != null) {
            attachedScene.removeEventFilter(MouseEvent.MOUSE_CLICKED, clickHandler);
        }
        // Detach all value-change listeners
        for (Runnable detach : detachCallbacks) {
            try {
                detach.run();
            } catch (Exception e) {
                // best effort
            }
        }
        detachCallbacks.clear();
        attachedScene = null;
        clickHandler = null;
        return getActionsJson();
    }

    public String getActionsJson() {
        StringBuilder sb = new StringBuilder();
        sb.append("[");
        synchronized (actions) {
            for (int i = 0; i < actions.size(); i++) {
                if (i > 0) {
                    sb.append(",");
                }
                sb.append(actions.get(i).toJson());
            }
        }
        sb.append("]");
        return sb.toString();
    }

    public boolean isRecording() {
        return recording;
    }

    private RecordedAction analyzeClick(Node target) {
        Node current = target;
        while (current != null) {
            if (current instanceof TreeCell) {
                TreeCell<?> cell = (TreeCell<?>) current;
                Object item = cell.getItem();
                return new RecordedAction(
                        "treeItemClick",
                        "text", cell.getText(),
                        "item", item != null ? item.toString() : null);
            }

            if (current instanceof Button) {
                Button button = (Button) current;
                return new RecordedAction(
                        "buttonClick",
                        "text", button.getText(),
                        "id", button.getId());
            }

            current = current.getParent();
        }
        return null;
    }

    /**
     * Represents a recorded UI action.
     */
    public static class RecordedAction {
        private final String type;
        private final String[][] properties;
        private final long timestamp;

        RecordedAction(String type, String... keyValues) {
            this.type = type;
            this.timestamp = System.currentTimeMillis();
            this.properties = new String[keyValues.length / 2][2];
            for (int i = 0; i < keyValues.length; i += 2) {
                this.properties[i / 2] = new String[] { keyValues[i], keyValues[i + 1] };
            }
        }

        String toJson() {
            StringBuilder sb = new StringBuilder();
            sb.append("{\"type\":\"").append(jsonEscape(type)).append("\"");
            sb.append(",\"timestamp\":").append(timestamp);
            for (String[] property : properties) {
                if (property[1] != null) {
                    sb.append(",\"").append(jsonEscape(property[0])).append("\":\"")
                      .append(jsonEscape(property[1])).append("\"");
                }
            }
            sb.append("}");
            return sb.toString();
        }

        private static String jsonEscape(String value) {
            return value
                    .replace("\\", "\\\\")
                    .replace("\"", "\\\"")
                    .replace("\n", "\\n")
                    .replace("\r", "\\r");
        }
    }
}
