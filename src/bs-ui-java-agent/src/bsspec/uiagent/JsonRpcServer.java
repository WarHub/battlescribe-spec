package bsspec.uiagent;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

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
            // Commands that must NOT run on the FX thread:
            // - patchSupporterPass: uses Instrumentation.retransformClasses
            // - waitForEngine: blocks until bg engine op completes; FX must stay free
            // - threadDump: diagnostic; must work even when FX thread is frozen
            // - rebuildCatalogueTree: schedules FX work internally; must run off-FX to avoid deadlock
            if ("patchSupporterPass".equals(method) || "waitForEngine".equals(method)
                    || "threadDump".equals(method) || "rebuildCatalogueTree".equals(method)
                    || "addForceViaEngine".equals(method) || "removeForceViaEngine".equals(method)
                    || "deselectEntryViaEngine".equals(method)
                    || "setRosterName".equals(method) || "exportRosterXml".equals(method)) {
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

    static String successResponse(String id, String result) {
        return "{\"jsonrpc\":\"2.0\",\"id\":" + formatId(id) + ",\"result\":" + result + "}";
    }

    static String errorResponse(String id, int code, String message) {
        String escapedMsg = message.replace("\\", "\\\\").replace("\"", "\\\"");
        return "{\"jsonrpc\":\"2.0\",\"id\":" + formatId(id)
                + ",\"error\":{\"code\":" + code + ",\"message\":\"" + escapedMsg + "\"}}";
    }

    private static String formatId(String id) {
        return id == null ? "null" : id;
    }
}
