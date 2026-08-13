package bsspec.enginepatch;

import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.FieldVisitor;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * Stops the BattleScribe engine discarding the constraint identity it computes for every validation
 * error, so the conformance harness can read an error's owner/entry/constraint structurally instead
 * of parsing it back out of the rendered message.
 *
 * <p><b>One transform, two application points.</b> {@link #transform(byte[])} is a pure
 * {@code byte[] -> byte[]} function called from both lanes so they cannot diverge:
 * <ul>
 *   <li>the in-process lane's build step ({@link PatchJarMain}) rewrites
 *       {@code BattleScribeEngine.jar} on disk before IKVM compiles it to a .NET assembly;
 *   <li>the UI lane's {@code java.lang.instrument.ClassFileTransformer}
 *       ({@link ErrorIdTransformer}) rewrites the same two classes as the BattleScribe JVM loads
 *       them.
 * </ul>
 *
 * <p><b>What it does, in engine terms</b> (measured from {@code net.battlescribe.engine.a.f},
 * BattleScribe 2.03.21). The engine builds an id {@code ownerId::entryId::constraintId} for every
 * error, registers it for dedup only when the entry is shared, then passes it as the second argument
 * of the private funnel {@code a.f.a(BaseRosterElement, String, String)} whose body never reads that
 * argument. This transform:
 * <ol>
 *   <li>adds a public field {@code bsspecErrorId} to the error class
 *       {@code net.battlescribe.engine.b.a}, and
 *   <li>in the funnel, immediately after the {@code new b.a(element, message)} constructor returns,
 *       stores the funnel's discarded id argument (local slot 2) into that field on the freshly
 *       constructed error.
 * </ol>
 *
 * <p><b>serialVersionUID.</b> {@code b.a implements Serializable} and declares no explicit
 * {@code serialVersionUID}, so adding a field would shift the default-computed value. The engine
 * persists rosters as XML (simple-xml, annotation-driven) and never Java-serializes {@code b.a},
 * but rather than rely on that this pins the class's PRE-transform default UID
 * ({@link #ERROR_CLASS_SERIAL_VERSION_UID}) as an explicit field, making the added field
 * UID-neutral by construction.
 */
public final class ErrorIdPatcher {

    /** Internal name of the validation-error class that gains the field. */
    public static final String ERROR_CLASS = "net/battlescribe/engine/b/a";
    /** Internal name of the engine class carrying the error funnel. */
    public static final String ENGINE_CLASS = "net/battlescribe/engine/a/f";
    /** The added field. Plain name, no {@code $}: IKVM must surface it as a clean .NET member. */
    public static final String FIELD_NAME = "bsspecErrorId";
    public static final String FIELD_DESC = "Ljava/lang/String;";

    /**
     * The default {@code serialVersionUID} of {@code net.battlescribe.engine.b.a} BEFORE this
     * transform, computed with {@code java.io.ObjectStreamClass.lookup(...).getSerialVersionUID()}
     * against the pristine BattleScribe 2.03.21 jar. Pinned as an explicit field during the
     * transform so adding {@code bsspecErrorId} does not change the class's serialization identity.
     * If the engine jar is ever repinned to a different BattleScribe build this constant must be
     * recomputed (the {@code EngineErrorFieldTests} guard fails loudly if the field vanishes, but a
     * changed UID is on the maintainer to re-measure).
     */
    public static final long ERROR_CLASS_SERIAL_VERSION_UID = -8003478985922043719L;

    private ErrorIdPatcher() {
    }

    /** Whether this class is one the patcher rewrites (cheap gate for the agent's hot path). */
    public static boolean handles(String internalName) {
        return ERROR_CLASS.equals(internalName) || ENGINE_CLASS.equals(internalName);
    }

    /**
     * Transforms one class. Returns the rewritten bytes for the two target classes, or the input
     * array unchanged (same reference) for anything else.
     */
    public static byte[] transform(byte[] original) {
        ClassReader reader = new ClassReader(original);
        String name = reader.getClassName();
        if (!handles(name)) {
            return original;
        }
        // COMPUTE_MAXS: the funnel injection grows the operand stack by two slots; let ASM
        // recompute max_stack rather than hand-maintain it. No stack-map frames change on this
        // Java-6 (v50) code, so COMPUTE_FRAMES -- which would need the full class hierarchy on the
        // classpath -- is not required.
        ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
        ClassVisitor visitor =
                ERROR_CLASS.equals(name) ? new AddFieldVisitor(writer) : new FunnelVisitor(writer);
        reader.accept(visitor, 0);
        return writer.toByteArray();
    }

    /**
     * Adds {@code public String bsspecErrorId;} and pins {@code serialVersionUID} on the error
     * class, each only if absent.
     */
    private static final class AddFieldVisitor extends ClassVisitor {
        private boolean fieldPresent;
        private boolean uidPresent;

        AddFieldVisitor(ClassVisitor next) {
            super(Opcodes.ASM9, next);
        }

        @Override
        public FieldVisitor visitField(
                int access, String name, String desc, String sig, Object value) {
            if (FIELD_NAME.equals(name)) {
                fieldPresent = true;
            }
            if ("serialVersionUID".equals(name)) {
                uidPresent = true;
            }
            return super.visitField(access, name, desc, sig, value);
        }

        @Override
        public void visitEnd() {
            if (!uidPresent) {
                // private static final long serialVersionUID = <pre-transform default>;
                FieldVisitor uid = super.visitField(
                        Opcodes.ACC_PRIVATE | Opcodes.ACC_STATIC | Opcodes.ACC_FINAL,
                        "serialVersionUID", "J", null,
                        Long.valueOf(ERROR_CLASS_SERIAL_VERSION_UID));
                if (uid != null) {
                    uid.visitEnd();
                }
            }
            if (!fieldPresent) {
                FieldVisitor fv = super.visitField(
                        Opcodes.ACC_PUBLIC, FIELD_NAME, FIELD_DESC, null, null);
                if (fv != null) {
                    fv.visitEnd();
                }
            }
            super.visitEnd();
        }
    }

    /**
     * In the funnel {@code a.f.a(BaseRosterElement, String, String)}, stores the discarded id
     * (local slot 2) onto the error object the method constructs.
     */
    private static final class FunnelVisitor extends ClassVisitor {
        FunnelVisitor(ClassVisitor next) {
            super(Opcodes.ASM9, next);
        }

        @Override
        public MethodVisitor visitMethod(
                int access, String name, String desc, String sig, String[] exceptions) {
            MethodVisitor base = super.visitMethod(access, name, desc, sig, exceptions);
            // The funnel is the ONLY method taking (BaseRosterElement, String, String). The three
            // engine call sites all route through it, so patching it alone covers every error.
            if ("a".equals(name)
                    && "(Lnet/battlescribe/model/roster/BaseRosterElement;Ljava/lang/String;Ljava/lang/String;)V"
                            .equals(desc)) {
                return new FunnelMethodVisitor(base);
            }
            return base;
        }
    }

    /**
     * Watches for the {@code invokespecial b.a.<init>(Object, String)} and, right after it returns
     * with the initialized error on the stack top, emits {@code dup; aload_2; putfield bsspecErrorId}.
     */
    private static final class FunnelMethodVisitor extends MethodVisitor {
        private boolean injected;

        FunnelMethodVisitor(MethodVisitor next) {
            super(Opcodes.ASM9, next);
        }

        @Override
        public void visitMethodInsn(
                int opcode, String owner, String name, String desc, boolean isInterface) {
            super.visitMethodInsn(opcode, owner, name, desc, isInterface);
            if (!injected
                    && opcode == Opcodes.INVOKESPECIAL
                    && ERROR_CLASS.equals(owner)
                    && "<init>".equals(name)
                    && "(Ljava/lang/Object;Ljava/lang/String;)V".equals(desc)) {
                // Stack after the ctor: [..., List, errorRef]. Set errorRef.bsspecErrorId = arg2.
                super.visitInsn(Opcodes.DUP);
                super.visitVarInsn(Opcodes.ALOAD, 2);
                super.visitFieldInsn(Opcodes.PUTFIELD, ERROR_CLASS, FIELD_NAME, FIELD_DESC);
                injected = true;
            }
        }
    }
}
