package bsspec.uiagent;

import java.io.*;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
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

    private final ServerSocket serverSocket;
    private final SceneGraphCommands commands;

    public JsonRpcServer(int port, EngineAccessor engineAccessor) throws IOException {
        this.serverSocket = new ServerSocket(port, 1, java.net.InetAddress.getLoopbackAddress());
        this.commands = new SceneGraphCommands(engineAccessor);
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
        // Minimal JSON-RPC 2.0 parsing (no external dependencies)
        String id = extractString(json, "id");
        String method = extractString(json, "method");
        String params = extractObject(json, "params");

        if (method == null) {
            return errorResponse(id, -32600, "Invalid Request: missing 'method'");
        }

        try {
            String result;
            // Commands that must NOT run on the FX thread:
            // - patchSupporterPass: uses Instrumentation.retransformClasses
            // - waitForEngine: blocks until bg engine op completes; FX must stay free
            // - threadDump: diagnostic; must work even when FX thread is frozen
            // - rebuildCatalogueTree: schedules FX work internally; must run off-FX to avoid deadlock
            if ("patchSupporterPass".equals(method) || "waitForEngine".equals(method)
                    || "threadDump".equals(method) || "rebuildCatalogueTree".equals(method)
                    || "addForceViaEngine".equals(method) || "removeForceViaEngine".equals(method)
                    || "deselectEntryViaEngine".equals(method)
                    || "setRosterName".equals(method)) {
                result = commands.dispatch(method, params);
            } else {
                // Execute on JavaFX Application Thread and wait for result
                result = executeOnFxThread(() -> commands.dispatch(method, params));
            }
            return successResponse(id, result);
        } catch (Throwable e) {
            return errorResponse(id, -32603, e.getClass().getSimpleName() + ": " + e.getMessage());
        }
    }

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

    // --- Minimal JSON helpers (no external dependencies) ---

    private static String extractString(String json, String key) {
        String pattern = "\"" + key + "\"";
        int keyIdx = json.indexOf(pattern);
        if (keyIdx < 0) {
            return null;
        }
        int colonIdx = json.indexOf(':', keyIdx + pattern.length());
        if (colonIdx < 0) {
            return null;
        }
        // Find the value - could be string or number
        int start = colonIdx + 1;
        while (start < json.length() && json.charAt(start) == ' ') {
            start++;
        }
        if (start >= json.length()) {
            return null;
        }
        if (json.charAt(start) == '"') {
            int end = json.indexOf('"', start + 1);
            return end > start ? json.substring(start + 1, end) : null;
        }
        // Number or other literal
        int end = start;
        while (end < json.length() && json.charAt(end) != ',' && json.charAt(end) != '}') {
            end++;
        }
        return json.substring(start, end).trim();
    }

    private static String extractObject(String json, String key) {
        String pattern = "\"" + key + "\"";
        int keyIdx = json.indexOf(pattern);
        if (keyIdx < 0) {
            return "{}";
        }
        int colonIdx = json.indexOf(':', keyIdx + pattern.length());
        if (colonIdx < 0) {
            return "{}";
        }
        int start = colonIdx + 1;
        while (start < json.length() && json.charAt(start) == ' ') {
            start++;
        }
        if (start >= json.length() || json.charAt(start) != '{') {
            return "{}";
        }
        // Find matching closing brace
        int depth = 0;
        for (int i = start; i < json.length(); i++) {
            if (json.charAt(i) == '{') {
                depth++;
            } else if (json.charAt(i) == '}') {
                depth--;
                if (depth == 0) {
                    return json.substring(start, i + 1);
                }
            }
        }
        return "{}";
    }

    static String successResponse(String id, String result) {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + formatId(id) + ",\"result\":" + result + "}";
    }

    static String errorResponse(String id, int code, String message) {
        String escapedMsg = message.replace("\\", "\\\\").replace("\"", "\\\"");
        return "{\"jsonrpc\":\"2.0\",\"id\":" + formatId(id)
                + ",\"error\":{\"code\":" + code + ",\"message\":\"" + escapedMsg + "\"}}";
    }

    private static String formatId(String id) {
        if (id == null) {
            return "null";
        }
        // If it looks like a number, return as-is
        try {
            Long.parseLong(id);
            return id;
        } catch (NumberFormatException e) {
            return "\"" + id + "\"";
        }
    }
}
