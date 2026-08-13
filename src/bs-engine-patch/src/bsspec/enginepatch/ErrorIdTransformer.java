package bsspec.enginepatch;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;

/**
 * UI lane application point: rewrites the engine classes as the BattleScribe JVM loads them, using
 * the exact same {@link ErrorIdPatcher#transform(byte[])} the offline build step uses. Registered
 * from the existing {@code bsspec.uiagent.BsUiAgent#premain} (no second {@code -javaagent}); ASM is
 * shaded into {@code bs-ui-java-agent.jar} so the transformer can run inside the app JVM.
 *
 * <p>The transformer is the only thing that differs between the two lanes; the transform itself is
 * shared, so the two BattleScribe engines cannot diverge on what the field contains.
 */
public final class ErrorIdTransformer implements ClassFileTransformer {

    @Override
    public byte[] transform(
            ClassLoader loader,
            String className,
            Class<?> classBeingRedefined,
            ProtectionDomain protectionDomain,
            byte[] classfileBuffer) {
        // className is the internal name ('net/battlescribe/engine/a/f'); gate cheaply so the vast
        // majority of loaded classes cost one string compare.
        if (className == null || !ErrorIdPatcher.handles(className)) {
            return null; // null == "no transformation", the contract's cheap path
        }
        try {
            byte[] out = ErrorIdPatcher.transform(classfileBuffer);
            System.out.println("[bs-ui-agent] error-id transform applied to " + className
                    + " (" + classfileBuffer.length + " -> " + out.length + " bytes)");
            return out;
        } catch (Throwable t) {
            // A transform failure must NOT be swallowed into a silently-unpatched engine: the agent
            // then reports validation errors with no id and the harness cannot tell why. Fail the
            // load loudly. (EngineAccessor also fails fast if the field is absent at read time.)
            System.err.println("[bs-ui-agent] FATAL: error-id transform failed for " + className
                    + " -- validation attribution would be unavailable: " + t);
            throw new IllegalStateException("error-id transform failed for " + className, t);
        }
    }
}
