package com.phonecam;

/** Version comparison for "Check for updates" (plain Java so the desktop behaviour checks can run it). Same rule as the PC app. */
public final class UpdateVersion {
    private UpdateVersion() {}

    /** true when {@code latest} ("v0.2.6-beta.1") is newer than {@code current} ("0.2.5"); suffixes after the numbers are ignored. */
    public static boolean isNewer(String latest, String current) {
        int[] a = parts(latest), b = parts(current);
        for (int i = 0; i < Math.max(a.length, b.length); i++) {
            int x = i < a.length ? a[i] : 0, y = i < b.length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static int[] parts(String text) {
        String t = text == null ? "" : text.trim();
        while (t.startsWith("v") || t.startsWith("V")) t = t.substring(1);
        int end = 0;
        while (end < t.length() && (Character.isDigit(t.charAt(end)) || t.charAt(end) == '.')) end++;
        String[] pieces = t.substring(0, end).split("\\.");
        int[] out = new int[pieces.length];
        for (int i = 0; i < pieces.length; i++) {
            try { out[i] = pieces[i].isEmpty() ? 0 : Integer.parseInt(pieces[i]); } catch (NumberFormatException e) { out[i] = 0; }
        }
        return out;
    }
}
