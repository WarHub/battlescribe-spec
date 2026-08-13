package bsspec.enginepatch;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.Enumeration;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import java.util.jar.JarOutputStream;

/**
 * In-process lane application point: rewrite the two target classes inside a jar and write a new
 * jar, copying every other entry byte-for-byte. Invoked by the {@code PatchBattleScribeEngineJar}
 * MSBuild target before IKVM compiles the result.
 *
 * <p>Usage: {@code PatchJarMain <in.jar> <out.jar>}. Exits non-zero unless exactly the two expected
 * classes were rewritten, so a jar that no longer contains them (an engine upgrade that renamed the
 * obfuscated classes) fails the build loudly rather than silently shipping an unpatched engine.
 */
public final class PatchJarMain {

    public static void main(String[] args) throws Exception {
        if (args.length != 2) {
            System.err.println("usage: PatchJarMain <in.jar> <out.jar>");
            System.exit(2);
        }
        Path in = Paths.get(args[0]);
        Path out = Paths.get(args[1]);
        if (!Files.exists(in)) {
            System.err.println("PatchJarMain: input jar not found: " + in.toAbsolutePath());
            System.exit(3);
        }
        Path parent = out.toAbsolutePath().getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }

        int patched = 0;
        try (JarFile jar = new JarFile(in.toFile());
                JarOutputStream jos = new JarOutputStream(Files.newOutputStream(out))) {
            Enumeration<JarEntry> entries = jar.entries();
            while (entries.hasMoreElements()) {
                JarEntry entry = entries.nextElement();
                byte[] bytes;
                try (InputStream is = jar.getInputStream(entry)) {
                    bytes = readAll(is);
                }
                if (entry.getName().endsWith(".class")) {
                    byte[] transformed = ErrorIdPatcher.transform(bytes);
                    if (transformed != bytes) {
                        System.out.println("patched: " + entry.getName()
                                + " (" + bytes.length + " -> " + transformed.length + " bytes)");
                        bytes = transformed;
                        patched++;
                    }
                }
                // Preserve the original entry time so the output is byte-stable across rebuilds,
                // which keeps IKVM's content hash stable and its compile incremental.
                JarEntry copy = new JarEntry(entry.getName());
                copy.setTime(entry.getTime());
                jos.putNextEntry(copy);
                jos.write(bytes);
                jos.closeEntry();
            }
        }
        System.out.println("classes patched: " + patched);
        if (patched != 2) {
            System.err.println("PatchJarMain: expected to patch 2 classes ("
                    + ErrorIdPatcher.ERROR_CLASS + " and " + ErrorIdPatcher.ENGINE_CLASS
                    + ") but patched " + patched
                    + " -- the engine jar may have been repinned to a different BattleScribe build.");
            System.exit(1);
        }
    }

    private static byte[] readAll(InputStream is) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = is.read(buf)) != -1) {
            bos.write(buf, 0, n);
        }
        return bos.toByteArray();
    }

    private PatchJarMain() {
    }
}
