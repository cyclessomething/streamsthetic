package com.phonecam

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * "Check for updates", the phone twin of the PC's UpdateChecker: asks GitHub for the published releases and says
 * whether a newer version exists. It never downloads or installs anything by itself and sends nothing about the user
 * (a plain GET). Uses the release list because GitHub's "latest" skips pre-releases (the betas).
 */
object UpdateCheck {
    const val FEED = "https://api.github.com/repos/cyclessomething/streamsthetic/releases?per_page=10"

    data class Result(val newer: Boolean, val message: String, val url: String?)

    fun isNewer(latest: String, current: String): Boolean = UpdateVersion.isNewer(latest, current)

    /** The newest published release in the feed: the object itself, or the first non-draft in a list. */
    fun newest(body: String): JSONObject? {
        val text = body.trim()
        if (text.startsWith("{")) return JSONObject(text)
        val list = JSONArray(text)
        for (i in 0 until list.length()) {
            val release = list.getJSONObject(i)
            if (!release.optBoolean("draft", false)) return release
        }
        return null
    }

    /** Blocking: call off the main thread. */
    fun check(current: String): Result = try {
        val connection = (URL(FEED).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 10_000
            setRequestProperty("User-Agent", "Streamsthetic-update-check")
            setRequestProperty("Accept", "application/vnd.github+json")
        }
        val body = try { connection.inputStream.bufferedReader().use { it.readText() } } finally { connection.disconnect() }
        val release = newest(body)
        if (release == null) Result(false, "No releases have been published yet.", null)
        else {
            val tag = release.optString("tag_name")
            if (isNewer(tag, current)) Result(true, "Version ${tag.trimStart('v')} is available (you have $current).", release.optString("html_url").ifEmpty { null })
            else Result(false, "You have the latest version ($current).", null)
        }
    } catch (e: Exception) {
        Result(false, "Could not check for updates. Try again later and check your internet connection.", null)
    }
}
