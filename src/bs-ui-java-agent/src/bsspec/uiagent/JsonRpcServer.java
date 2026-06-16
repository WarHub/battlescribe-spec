package bsspec.uiagent;

import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.*;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutionException;

import javafx.application.Platform;

/**
 * Minimal JSON-RPC 2.0 server over TCP. Accepts one connection at a time,
 * reads newline-delimited JSON-RPC requests, dispatches to
 * {@link SceneGraphCommands}, and writes responses.
 *
 * <p>Protocol: each message is a single JSON line terminated by {@code \n}.
 */
public class JsonRpcServer {

    /**
     * Scene graph inspection/automation commands must run on the JavaFX thread.
     * Everything else defaults to a background thread so newly added engine/diagnostic
     * methods do not deadlock when someone forgets to update dispatch logic.
     */
    private static final Set<String> FX_THREAD_METHODS = Collections.unmodifiableSet(new HashSet<String>(Arrays.asList(
            "dumpTree",
            "getWindows",
            "findNode",
            "clickNode",
            "fireButton",
            "findNodeByText",
            "setNodeText",
            "getComboBoxItems",
            "selectComboBoxItem",
            "selectTreeItem",
            "clickTreeItem",
            "clickTreeCellButton",
            "pressKey",
            "setSpinnerValue",
            "captureScreenshot",
            "getUiState",
            "startRecording",
            "stopRecording",
            "clickControlByLabel",
            "setSpinnerValueByLabel"
    )));

    private final ServerSocket serverSocket;
    private final SceneGraphCommands commands;
    private final RosterActions rosterActions;
    private final DataEditorActions dataEditorActions;

    public JsonRpcServer(int port, EngineAccessor engineAccessor) throws IOException {
        this.serverSocket = new ServerSocket(port, 1, java.net.InetAddress.getLoopbackAddress());
        this.commands = new SceneGraphCommands(engineAccessor);
        this.rosterActions = new RosterActions(engineAccessor);
        this.dataEditorActions = new DataEditorActions(engineAccessor);
    }

    public int getPort() {
        return serverSocket.getLocalPort();
    }

    public void startAsync() {
        Thread serverThread = new Thread(this::acceptLoop, "bs-ui-agent-server");
        serverThread.setDaemon(true);
        serverThread.start();
    }

    private void acceptLoop() {
        while (!serverSocket.isClosed()) {
            try {
                Socket client = serverSocket.accept();
                handleClient(client);
            } catch (IOException e) {
                if (!serverSocket.isClosed()) {
                    System.err.println("[bs-ui-agent] Accept error: " + e.getMessage());
                }
            }
        }
    }

    private void handleClient(Socket client) {
        try (client;
             BufferedReader reader = new BufferedReader(
                     new InputStreamReader(client.getInputStream(), StandardCharsets.UTF_8));
             BufferedWriter writer = new BufferedWriter(
                     new OutputStreamWriter(client.getOutputStream(), StandardCharsets.UTF_8))) {

            String line;
            while ((line = reader.readLine()) != null) {
                String response = processRequest(line);
                writer.write(response);
                writer.newLine();
                writer.flush();
            }
        } catch (IOException e) {
            System.err.println("[bs-ui-agent] Client error: " + e.getMessage());
        }
    }

    private String processRequest(String json) {
        JsonObject request;
        try {
            request = new JsonParser().parse(json).getAsJsonObject();
        } catch (RuntimeException e) {
            return errorResponse(null, -32700, "Parse error: " + e.getMessage());
        }

        String method = request.has("method") ? request.get("method").getAsString() : null;
        String id = request.has("id") ? request.get("id").toString() : null;
        JsonElement paramsElement = request.get("params");
        String params = paramsElement != null ? paramsElement.toString() : "{}";

        if (method == null) {
            return errorResponse(id, -32600, "Invalid Request: missing 'method'");
        }

        try {
            String result;
            if (FX_THREAD_METHODS.contains(method)) {
                result = executeOnFxThread(() -> commands.dispatch(method, params));
            } else if (method.startsWith("gamedata")) {
                // High-level gamedata (Data Editor) actions run on background thread
                result = dataEditorActions.dispatch(method, params);
            } else if (method.startsWith("roster")) {
                // High-level roster (Roster Editor) actions run on background thread
                result = rosterActions.dispatch(method, params);
            } else {
                result = commands.dispatch(method, params);
            }
            return successResponse(id, result);
        } catch (Throwable e) {
            return errorResponse(id, -32603, e.getClass().getSimpleName() + ": " + e.getMessage());
        }
    }

    /**
     * Executes a task on the JavaFX Application Thread with a 60s timeout.
     * This is part of the timeout architecture:
     * - .NET CallTimeout (30s) > FX dispatch (60s) > engine op wait (15s)
     * The FX timeout is intentionally longer than CallTimeout so the .NET side
     * times out first with a clearer error message in normal scenarios.
     */
    private String executeOnFxThread(java.util.concurrent.Callable<String> task) throws Exception {
        if (Platform.isFxApplicationThread()) {
            return task.call();
        }

        CompletableFuture<String> future = new CompletableFuture<>();
        Platform.runLater(() -> {
            try {
                future.complete(task.call());
            } catch (Exception e) {
                future.completeExceptionally(e);
            }
        });

        try {
            return future.get(60, java.util.concurrent.TimeUnit.SECONDS);
        } catch (java.util.concurrent.TimeoutException e) {
            throw new RuntimeException("FX thread did not respond within 60s (likely blocked/deadlocked)");
        } catch (ExecutionException e) {
            Throwable cause = e.getCause();
            if (cause instanceof Exception) {
                throw (Exception) cause;
            }
            throw e;
        }
    }

    static String successResponse(String id, String result) {
        JsonObject response = new JsonObject();
        response.addProperty("jsonrpc", "2.0");
        response.add("id", parseJsonValue(id));
        response.add("result", parseJsonValue(result));
        return response.toString();
    }

    static String errorResponse(String id, int code, String message) {
        JsonObject response = new JsonObject();
        response.addProperty("jsonrpc", "2.0");
        response.add("id", parseJsonValue(id));

        JsonObject error = new JsonObject();
        error.addProperty("code", code);
        error.addProperty("message", message);
        response.add("error", error);
        return response.toString();
    }

    private static JsonElement parseJsonValue(String json) {
        if (json == null) {
            return JsonNull.INSTANCE;
        }
        return new JsonParser().parse(json);
    }
}
