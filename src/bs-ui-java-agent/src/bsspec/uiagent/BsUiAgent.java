package bsspec.uiagent;

import bsspec.enginepatch.ErrorIdTransformer;
import java.lang.instrument.Instrumentation;
import java.io.IOException;

/**
 * Java agent entry point. Loaded via {@code -javaagent:bs-ui-java-agent.jar}.
 *
 * <p>Starts a JSON-RPC server on a TCP socket. The port is either specified via
 * agent args or chosen dynamically. The chosen port is printed to stdout as
 * {@code BSUI_AGENT_PORT=<port>} so the .NET client can discover it.
 */
public class BsUiAgent {

    public static void premain(String agentArgs, Instrumentation inst) {
        // Before the engine classes load: register the transform that makes every validation error
        // carry its constraint id (the SAME transform the in-process build bakes into the jar --
        // src/bs-engine-patch), so the UI lane reads attribution structurally instead of parsing it
        // out of the message. Registered here rather than via a second -javaagent so the two lanes
        // share one application point. The engine classes load lazily, well after premain.
        inst.addTransformer(new ErrorIdTransformer(), true);

        // Before anything else, and in particular before the FX toolkit exists: an exception the FX
        // thread never catches is how an action silently half-happens (see FxExceptionMonitor).
        FxExceptionMonitor.install();

        int requestedPort = 0; // 0 = dynamic
        if (agentArgs != null && !agentArgs.isEmpty()) {
            try {
                requestedPort = Integer.parseInt(agentArgs.trim());
            } catch (NumberFormatException e) {
                System.err.println("[bs-ui-agent] Invalid port argument: " + agentArgs);
            }
        }

        try {
            EngineAccessor engineAccessor = new EngineAccessor(inst);
            JsonRpcServer server = new JsonRpcServer(requestedPort, engineAccessor);
            int actualPort = server.getPort();
            System.out.println("BSUI_AGENT_PORT=" + actualPort);
            System.out.flush();
            server.startAsync();
            System.out.println("[bs-ui-agent] Listening on port " + actualPort);
        } catch (IOException e) {
            System.err.println("[bs-ui-agent] Failed to start server: " + e.getMessage());
            e.printStackTrace();
        }
    }
}
