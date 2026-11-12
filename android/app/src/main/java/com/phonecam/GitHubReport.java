package com.phonecam;

import java.net.URLEncoder;
import java.io.UnsupportedEncodingException;

/** Builds a public issue draft from explicitly selected fields, never preferences or diagnostic logs. */
public final class GitHubReport {
    public static final String DESTINATION = "https://github.com/cyclessomething/streamsthetic/issues/new";
    public static final int MAX_URL_LENGTH = 7500;

    private GitHubReport() {}

    public static final class Draft {
        public final String url;
        public final boolean shortened;
        public final String fullBody;
        private Draft(String url, boolean shortened, String fullBody) {
            this.url = url; this.shortened = shortened; this.fullBody = fullBody;
        }
    }

    private static String limit(String value, int count) {
        String text = value == null ? "" : value.trim();
        int points = text.codePointCount(0, text.length());
        return points <= count ? text : text.substring(0, text.offsetByCodePoints(0, count));
    }

    private static String field(String value) {
        return limit(value, 60).replaceAll("[\\p{Cntrl}\\r\\n]+", " ");
    }

    private static String encode(String value) {
        try { return URLEncoder.encode(value, "UTF-8"); }
        catch (UnsupportedEncodingException impossible) { throw new AssertionError(impossible); }
    }

    public static String url(String description, String appVersion, String androidVersion, String model, String mode) {
        return create(description, appVersion, androidVersion, model, mode).url;
    }

    public static Draft create(String description, String appVersion, String androidVersion, String model, String mode) {
        String connection = "tether".equals(mode) ? "USB tethering" : "wifi".equals(mode) ? "Wi-Fi"
            : "usb".equals(mode) ? "USB debugging" : "Not selected";
        String details = "\n\n## App and device\nApp version: " + field(appVersion)
            + "\nAndroid: " + field(androidVersion) + "\nPhone model: " + field(model)
            + "\nConnection: " + connection
            + "\n\nPlease review this public report before submitting. Do not include pairing codes, addresses or other private details.";
        String original = description == null ? "" : description;
        String fullBody = "## What happened\n" + original + details;
        String prefix = DESTINATION + "?title=" + encode("Android: problem report") + "&body=";
        String result = prefix + encode(fullBody);
        if (result.length() <= MAX_URL_LENGTH) return new Draft(result, false, fullBody);
        String notice = "\n\n[Description shortened for the browser link. The full summary is saved in the Android app: use Share saved GitHub summary in Report a problem to retrieve it.]";
        StringBuilder text = new StringBuilder();
        int used = (prefix + encode("## What happened\n" + notice + details)).length();
        for (int offset = 0; offset < original.length();) {
            int point = original.codePointAt(offset);
            String next = new String(Character.toChars(point));
            int size = encode(next).length();
            if (used + size > MAX_URL_LENGTH) break;
            text.append(next); used += size; offset += Character.charCount(point);
        }
        return new Draft(prefix + encode("## What happened\n" + text + notice + details), true, fullBody);
    }
}
