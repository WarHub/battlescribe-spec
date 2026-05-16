package bsspec.uiagent;

import javafx.event.EventHandler;
import javafx.scene.Node;
import javafx.scene.Scene;
import javafx.scene.control.Button;
import javafx.scene.control.CheckBox;
import javafx.scene.control.ComboBox;
import javafx.scene.control.Spinner;
import javafx.scene.control.TreeCell;
import javafx.scene.input.MouseButton;
import javafx.scene.input.MouseEvent;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * Records user interactions with the BattleScribe Roster Editor UI
 * and maps them to high-level actions.
 */
public class ActionRecorder {

    private static ActionRecorder instance;

    private final List<RecordedAction> actions = Collections.synchronizedList(new ArrayList<RecordedAction>());
    private Scene attachedScene;
    private EventHandler<MouseEvent> clickHandler;
    private boolean recording;

    public static ActionRecorder getInstance() {
        if (instance == null) {
            instance = new ActionRecorder();
        }
        return instance;
    }

    public void startRecording(Scene scene) {
        if (recording) {
            stopRecording();
        }

        actions.clear();
        attachedScene = scene;
        recording = true;

        clickHandler = new EventHandler<MouseEvent>() {
            @Override
            public void handle(MouseEvent event) {
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
            }
        };

        scene.addEventFilter(MouseEvent.MOUSE_CLICKED, clickHandler);
    }

    public String stopRecording() {
        recording = false;
        if (attachedScene != null && clickHandler != null) {
            attachedScene.removeEventFilter(MouseEvent.MOUSE_CLICKED, clickHandler);
        }
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

            if (current instanceof ComboBox) {
                ComboBox<?> combo = (ComboBox<?>) current;
                Object value = combo.getValue();
                return new RecordedAction(
                        "comboBoxSelect",
                        "value", value != null ? value.toString() : null,
                        "id", combo.getId());
            }

            if (current instanceof Spinner) {
                Spinner<?> spinner = (Spinner<?>) current;
                Object value = spinner.getValue();
                return new RecordedAction(
                        "spinnerChange",
                        "value", value != null ? value.toString() : null);
            }

            if (current instanceof CheckBox) {
                CheckBox checkBox = (CheckBox) current;
                return new RecordedAction(
                        "checkBoxToggle",
                        "text", checkBox.getText(),
                        "selected", String.valueOf(checkBox.isSelected()));
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
