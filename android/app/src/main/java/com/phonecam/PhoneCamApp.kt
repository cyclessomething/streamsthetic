package com.phonecam

import android.app.Application
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Remembers the last crash so the "Report a problem" screen can offer it ("Is this the one you want to report?").
 * The stack trace is written to a small file, then the normal crash handling carries on.
 */
class PhoneCamApp : Application() {
    override fun onCreate() {
        super.onCreate()
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
                File(filesDir, CRASH_FILE).writeText("$stamp  thread ${thread.name}\n${error.stackTraceToString()}")
            } catch (_: Exception) { /* the crash itself matters more than failing to record it */ }
            previous?.uncaughtException(thread, error)
        }
    }

    companion object {
        const val CRASH_FILE = "last_crash.txt"
    }
}
