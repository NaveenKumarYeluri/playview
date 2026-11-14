package com.playview.streamer;

import android.content.Context;

import java.io.File;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/**
 * Writes every uncaught exception to filesDir/crash.txt so a crash can be
 * diagnosed even on ROMs (this phone's included) that filter app logs out of
 * logcat. MainActivity shows the last crash in its UI; with a debuggable
 * build, <code>adb run-as</code> can also pull the file directly.
 */
final class CrashCatcher {

    private static final SimpleDateFormat TS = new SimpleDateFormat(
            "yyyy-MM-dd HH:mm:ss", Locale.US);

    private CrashCatcher() { }

    static void install(Context context) {
        final File file = new File(context.getFilesDir(), "crash.txt");
        Thread.setDefaultUncaughtExceptionHandler((thread, throwable) -> {
            try {
                StringWriter sw = new StringWriter();
                throwable.printStackTrace(new PrintWriter(sw));
                String text = TS.format(new Date()) + "  " + thread.getName() + "\n"
                        + sw + "\n\n";
                // Append (file grows slowly; each entry is self-contained).
                PrintWriter out = new PrintWriter(new java.io.FileWriter(file, true));
                out.print(text);
                out.close();
            } catch (Throwable ignored) { }
            // Chain to the default handler so the system still shows
            // "app has stopped" and the process dies normally.
            Thread.UncaughtExceptionHandler def =
                    Thread.getDefaultUncaughtExceptionHandler();
            if (def != null) def.uncaughtException(thread, throwable);
        });
    }

    /** The last crash, or null. */
    static String lastCrash(Context context) {
        try {
            File file = new File(context.getFilesDir(), "crash.txt");
            if (!file.exists()) return null;
            byte[] all = new byte[(int) file.length()];
            java.io.FileInputStream in = new java.io.FileInputStream(file);
            int n = in.read(all);
            in.close();
            if (n <= 0) return null;
            String text = new String(all, 0, n, "UTF-8");
            int cut = text.lastIndexOf("yyyy-", 0);
            String[] parts = text.split("(?=\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}  )");
            return parts[parts.length - 1].trim();
        } catch (Throwable t) {
            return "could not read crash.txt: " + t;
        }
    }
}
