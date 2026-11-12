package com.phonecam

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.PowerManager
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.view.WindowManager
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView

class MainActivity : Activity() {

    private class Choice(val label: String, val apply: (Bundle) -> Unit) {
        override fun toString() = label
    }

    // Choices shown in the app; each one fills in the same launch extras the adb scripts use.
    private val resolutions = listOf(
        Choice("1080p") { it.putInt("w", 1920); it.putInt("h", 1080) },
        Choice("720p") { it.putInt("w", 1280); it.putInt("h", 720) },
        Choice("4K") { it.putInt("w", 3840); it.putInt("h", 2160) },
    )
    private val frameRates = listOf(
        Choice("60") { it.putInt("fps", 60) },
        Choice("30") { it.putInt("fps", 30) },
    )
    private val bitrates = listOf(
        Choice("20") { it.putInt("mbps", 20) },
        Choice("10") { it.putInt("mbps", 10) },
        Choice("40") { it.putInt("mbps", 40) },
        Choice("60") { it.putInt("mbps", 60) },
    )
    private val lenses = listOf(
        Choice("Main 1x") { it.putString("cam", "0"); it.putFloat("zoom", 1f) },
        Choice("Wide 0.6x") { it.putString("cam", "0"); it.putFloat("zoom", 0.6f) },
        Choice("Tele 3x") { it.putString("cam", "0"); it.putFloat("zoom", 3f) },
        Choice("Tele 5x") { it.putString("cam", "0"); it.putFloat("zoom", 5f) },
        Choice("Front") { it.putString("cam", "1"); it.putFloat("zoom", 1f) },
    )

    private lateinit var statusView: TextView
    // Auto-rotate on: the picture follows how the phone is held. Off: the two buttons turn it, a quarter per press.
    // Only one of the two is usable at a time (the buttons are greyed out while auto-rotate is on).
    private lateinit var autoRotate: android.widget.Switch
    private lateinit var rotationHint: TextView
    private lateinit var turnLeft: TextView
    private lateinit var turnRight: TextView
    private var syncingRotate = false
    private val pickers = mutableMapOf<String, Ui.ChipRow>()
    private val ui by lazy { Ui(this, prefs.getString("Skin", DEFAULT_SKIN) ?: DEFAULT_SKIN) }
    private lateinit var titleView: TextView
    private lateinit var statusChip: TextView
    private lateinit var pages: List<View>
    private lateinit var tabBar: Ui.TabBar
    private lateinit var settingsPages: List<android.widget.ScrollView>
    private var settingsBackCallback: android.window.OnBackInvokedCallback? = null
    private var settingsCategory = 0
    private var settingsScroll = IntArray(5)
    private lateinit var chooser: LinearLayout
    private lateinit var modeView: LinearLayout
    private lateinit var modeTitle: TextView
    private lateinit var modeSteps: TextView
    private lateinit var backLink: TextView
    private lateinit var startStop: TextView
    private lateinit var healthNote: TextView
    private lateinit var pairingSheet: PairingSheet
    private lateinit var codeLink: TextView
    private lateinit var scanLink: TextView
    private lateinit var tetherLink: TextView
    private lateinit var tetherGuide: StepGuide
    private lateinit var debugGuide: StepGuide
    private lateinit var modeArt: android.widget.ImageView
    private val handler = Handler(Looper.getMainLooper())
    private var dimSliderHeld = false
    private var chooserShown: Boolean? = null
    private var lastLocalChange = 0L
    private val shownPositions = mutableMapOf<String, Int>()
    private val refresh = object : Runnable {
        override fun run() {
            // the USB connection needs no code, so its line never mentions one; Wi-Fi keeps it
            val net = CameraStreamService.networkInfo.let {
                if (sessionMode == "tether") it.replace(Regex(", code [0-9]+"), "").replace("Wi-Fi/tether", "USB connection")
                else it.replace("Wi-Fi/tether", "Wi-Fi")
            }
            statusView.text = CameraStreamService.status + if (net.isNotEmpty()) "\n$net" else ""
            // settings may have been changed from the PC: show what is really streaming
            // (not right after a change made here, which the service may not have applied yet)
            if (SystemClock.uptimeMillis() - lastLocalChange > 3000) CameraStreamService.currentSettings?.let { current ->
                for ((name, spinner) in pickers) {
                    val index = matchingChoice(choicesFor(name), current)
                    if (index >= 0 && index != spinner.selectedItemPosition) {
                        shownPositions[name] = index // not a user choice: the listener must not send it back
                        spinner.setSelection(index)
                    }
                }
                syncingRotate = true
                autoRotate.isChecked = current.getString("orient") != "manual"
                syncingRotate = false
            }
            val streaming = CameraStreamService.currentSettings != null
            autoRotate.isEnabled = streaming
            setTurnEnabled(streaming && !autoRotate.isChecked)
            titleView.text = if (streaming) "Stream status" else "Ready"
            ui.setStatusChip(statusChip, streaming)
            renderStream(streaming)
            updateHealth(streaming)
            checkWifi()
            handler.postDelayed(this, 1000)
        }
    }

    /**
     * Accessibility sizes (Settings, Accessibility): the app's own text size and icon/button size on top of the phone's
     * settings. Text = fontScale; icons and buttons = density, because everything here is measured in dp. The new
     * Configuration must start from the base one, or the phone's own large-text setting would be lost.
     */
    override fun attachBaseContext(newBase: android.content.Context) {
        val saved = newBase.getSharedPreferences("settings", MODE_PRIVATE)
        val text = TEXT_SCALES[saved.getInt("TextScale", 0).coerceIn(0, TEXT_SCALES.lastIndex)]
        val icons = ICON_SCALES[saved.getInt("IconScale", 0).coerceIn(0, ICON_SCALES.lastIndex)]
        if (text == 1f && icons == 1f) { super.attachBaseContext(newBase); return }
        val config = android.content.res.Configuration(newBase.resources.configuration)
        config.fontScale = config.fontScale * text
        config.densityDpi = (config.densityDpi * icons).toInt()
        super.attachBaseContext(newBase.createConfigurationContext(config))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // instrument-panel screen (docs/DESIGN_PATTERNS.md): header block, status, numbered panels, actions, footer
        statusView = ui.body(size = 13f).apply { typeface = ui.mono }
        titleView = ui.body("Stopped", 22f, R.color.ink, bold = true)
        statusChip = ui.statusChip()
        // four pages (built once so the refresh below keeps every control in sync), switched by the bottom tab bar
        fun page(vararg views: View, fill: Boolean = false) = android.widget.ScrollView(this).apply {
            isVerticalScrollBarEnabled = false // no grey scroll handle on the right while scrolling
            overScrollMode = View.OVER_SCROLL_NEVER
            isFillViewport = fill // the Stream tab uses the whole height: Start sits at the bottom, in thumb reach
            addView(LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(ui.dp(16), ui.dp(4), ui.dp(16), ui.dp(20))
                views.forEach { if (fill) addView(it, LinearLayout.LayoutParams(-1, -1)) else addView(it) } // not-fill pages keep each panel's own margin (the gap between panels)
            })
        }
        pages = listOf(
            page(buildStreamPage(), fill = true),
            page(ui.panel(1, "01", "Phone camera",
                picker("Lens", "Lens", lenses),
                picker("Resolution", "Resolution", resolutions),
                picker("Frame rate", "Frame rate", frameRates),
                picker("Quality", "Bitrate", bitrates))),
            page(ui.panel(2, "02", "Orientation", rotationControls())),
            buildSettingsPage(savedInstanceState),
        )
        tabBar = ui.TabBar(listOf(
            R.drawable.ic_tab_stream to "Stream", R.drawable.ic_tab_camera to "Camera",
            R.drawable.ic_tab_rotate to "Rotate", R.drawable.ic_tab_settings to "Settings",
        )) { showTab(it) }
        val body = android.widget.FrameLayout(this).apply { pages.forEach { addView(it) } }
        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(ui.color(R.color.paper))
            fitsSystemWindows = true // Android 15 draws apps under the status bar
            addView(ui.header(statusChip), LinearLayout.LayoutParams(-1, -2).apply { setMargins(ui.dp(16), ui.dp(12), ui.dp(16), 0) })
            addView(body, LinearLayout.LayoutParams(-1, 0, 1f))
            addView(tabBar, LinearLayout.LayoutParams(-1, -2))
        }
        // a fresh start (the app was swiped away, or the activity was closed) opens the Stream tab; a redraw (theme, size) keeps the tab you were on
        showTab(if (savedInstanceState == null) 0 else prefs.getInt("Tab", 0))
        pairingSheet = PairingSheet(this, ui)
        val redraw = { renderStream(CameraStreamService.currentSettings != null) }
        tetherGuide = StepGuide(this, ui, listOf(
            GuideStep("Plug in the cable", "Connect your phone to your PC with a USB data cable. A charge-only cable will not work."),
            GuideStep("Turn on USB tethering", "Open tethering settings below and turn on USB tethering, then return to Streamsthetic.\n\nIf the page is unavailable, look for USB tethering under Connections or Network in your phone settings. Menu names vary by phone.",
                "Open tethering settings", ::openTetherSettings, ::cableLinkUp),
        ), "USB tethering is on", "Your phone is ready. Now open Streamsthetic on your PC, choose USB connection, then press Start here.",
            "USB tethering not detected", "Check that the USB data cable is connected and USB tethering is enabled in your phone settings. Detection may take a few seconds.",
            ::cableLinkUp, { openHelp("turn on USB tethering") }, redraw)
        debugGuide = StepGuide(this, ui, listOf(
            GuideStep("Unlock Developer options", "Open About phone below. Find Build number (under Software information on Samsung phones) and tap it seven times to enable Developer options. Android may ask for your screen lock.",
                "Open About phone", { openSettingsPage(android.provider.Settings.ACTION_DEVICE_INFO_SETTINGS) }, ::developerOptionsOn),
            GuideStep("Turn on USB debugging", "Tap the button below to open Developer options. Find USB debugging and tap its switch so it turns on. Confirm if your phone asks.\n\nIf Developer options is switched off at the top of that page, switch it on first.",
                "Open Developer options", { openSettingsPage(android.provider.Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS) }, ::usbDebuggingOn),
        ), "USB debugging is on", "Plug your phone into your PC with a USB cable. If the phone asks \"Allow USB debugging?\", tick Always allow and tap Allow. Then open Streamsthetic on your PC, choose USB debugging, and press Start.",
            "USB debugging is off", "Go back, open Developer options and turn on USB debugging.",
            ::usbDebuggingOn, { openHelp("turn on USB debugging") }, redraw)
        // the pop-ups sit over everything (header, pages, tab bar)
        setContentView(android.widget.FrameLayout(this).apply {
            setBackgroundColor(ui.color(R.color.paper))
            addView(layout, android.widget.FrameLayout.LayoutParams(-1, -1))
            addView(pairingSheet, android.widget.FrameLayout.LayoutParams(-1, -1))
            addView(tetherGuide, android.widget.FrameLayout.LayoutParams(-1, -1))
            addView(debugGuide, android.widget.FrameLayout.LayoutParams(-1, -1))
        })
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // a redraw (new Appearance, screen rotation) recreates the screen with the same launch intent: only a
        // fresh launch may start the stream, or the redraw would restart it
        if (savedInstanceState == null) {
            if (intent?.action == Intent.ACTION_VIEW) adoptPairingLink(intent.dataString)
            handleLaunch(intent)
            if (intent?.extras == null) welcomePermissions() // not when started by the PC app or a script
        }
    }

    /**
     * The Stream tab. Every visit starts with the choice "how do you connect?" (USB connection or Wi-Fi). Picking one opens that
     * connection's screen: what to do, the status, and ONE button: Start, which turns into a red Stop while streaming.
     */
    private fun buildStreamPage(): View {
        chooser = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(ui.body("How do you want to connect?", 20f, R.color.ink, bold = true), LinearLayout.LayoutParams(-2, -2).apply { bottomMargin = ui.dp(4) })
            addView(ui.body("Choose a connection method. You can change it later.", 13f), LinearLayout.LayoutParams(-2, -2).apply { bottomMargin = ui.dp(16) })
            addView(ui.modeCard(R.drawable.ill_usb, "USB connection", "Recommended. Plug your phone into the PC.") { chooseMode("tether") })
            addView(ui.modeCard(R.drawable.ill_wifi, "Wi-Fi", "No cable. Connect to your PC over Wi-Fi.") { chooseMode("wifi") })
            // for people who know what they are doing: USB debugging, hidden until asked for
            val advancedBox = LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                visibility = if (prefs.getBoolean("Advanced", false)) View.VISIBLE else View.GONE
                addView(ui.modeCard(R.drawable.ill_usb, "USB debugging", "For advanced users. Needs Developer options on the phone.") { chooseMode("usb") })
            }
            lateinit var advancedLink: TextView
            advancedLink = ui.pillButton(if (advancedBox.visibility == View.VISIBLE) "Advanced  ▴" else "Advanced  ▾") {
                val open = advancedBox.visibility != View.VISIBLE
                advancedBox.visibility = if (open) View.VISIBLE else View.GONE
                advancedLink.text = if (open) "Advanced  ▴" else "Advanced  ▾"
                prefs.edit().putBoolean("Advanced", open).apply()
            }
            addView(advancedLink, LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(8); bottomMargin = ui.dp(8) })
            addView(advancedBox)
        }
        backLink = ui.pillButton("‹  Change connection") { sessionMode = null; renderStream(CameraStreamService.currentSettings != null) }
        scanLink = ui.pillButton("▣  Scan QR code from the PC") { scanPairingQr() }
        codeLink = ui.pillButton("#  Show pairing code (if you cannot scan)") { showPairingCode() }
        tetherLink = ui.pillButton("Show the setup guide") { if (sessionMode == "usb") debugGuide.open() else tetherGuide.open() }
        modeTitle = ui.body("", 20f, R.color.ink, bold = true)
        modeSteps = ui.body("", 13.5f)
        startStop = ui.primaryButton("Start") { onStartStop() }
        healthNote = ui.body("", 13f, R.color.ink, bold = true).apply {
            visibility = View.GONE
            setPadding(ui.dp(14), ui.dp(10), ui.dp(14), ui.dp(10))
            background = ui.box(0x33D32F2F, 0x99D32F2F.toInt(), 12, 1)
        }
        modeView = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(backLink, LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(4); bottomMargin = ui.dp(14) })
            addView(modeTitle)
            addView(modeSteps, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(6); bottomMargin = ui.dp(4) })
            addView(tetherLink, LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(8) })
            addView(scanLink, LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(8) })
            addView(codeLink, LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(8); bottomMargin = ui.dp(12) })
            addView(titleView)
            addView(statusView, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(2); bottomMargin = ui.dp(16) })
            // the middle of the screen: the connection's picture (fills the free height; Start / Stop stay at the bottom, in thumb reach)
            modeArt = android.widget.ImageView(context).apply { setColorFilter(ui.look.ink); alpha = 0.9f; adjustViewBounds = true }
            addView(android.widget.FrameLayout(context).apply {
                addView(modeArt, android.widget.FrameLayout.LayoutParams(-2, ui.dp(180), android.view.Gravity.CENTER))
            }, LinearLayout.LayoutParams(-1, 0, 1f))
            addView(healthNote, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(12) })
            addView(startStop)
            addView(ui.label("Streamsthetic  ·  phone to Windows camera  //////", 10f, R.color.ink3).apply { setPadding(0, ui.dp(18), 0, 0) })
        }
        val holder = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; addView(chooser); addView(modeView, LinearLayout.LayoutParams(-1, 0, 1f)) }
        // already streaming (the PC started it, or the screen was redrawn): go straight to that connection's screen
        if (sessionMode == null && CameraStreamService.currentSettings != null) sessionMode = prefs.getString("Mode", "tether")
        renderStream(CameraStreamService.currentSettings != null)
        return holder
    }

    /**
     * Heat and battery warning while streaming (roadmap "Polish" 8): a note above the Stop button when the phone's thermal
     * state or battery temperature is high, or the battery is low and not charging. And the screen is dimmed while
     * streaming (Settings, Look: "Dim the screen while streaming", on by default) to save battery and heat.
     */
    private fun updateHealth(streaming: Boolean) {
        var message: String? = null
        if (streaming) {
            val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val temp = (battery?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10.0
            val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, 100) ?: 100
            val charging = (battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0
            val thermal = if (Build.VERSION.SDK_INT >= 29) getSystemService(PowerManager::class.java).currentThermalStatus else 0
            message = when {
                thermal >= 3 || temp >= 45 -> "The phone is very hot. Press Stop and let it cool down, or lower the resolution and frame rate."
                thermal >= 2 || temp >= 42 -> "The phone is getting warm. Lower the resolution or frame rate to reduce processing load."
                level <= 15 && !charging -> "Battery is low ($level%). Plug the phone into a charger or the PC."
                else -> CameraStreamService.heatNote.ifEmpty { null } // the quality was lowered by itself because the phone got warm
            }
        }
        healthNote.text = message ?: ""
        healthNote.visibility = if (message != null) View.VISIBLE else View.GONE
        val dim = streaming && prefs.getBoolean("Dim", true)
        val params = window.attributes
        val wanted = if (dim) prefs.getInt("DimLevel", 3).coerceIn(DIM_MIN, DIM_MAX) / 100f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        if (params.screenBrightness != wanted && !(dimSliderHeld)) { params.screenBrightness = wanted; window.attributes = params }
    }

    /** A labelled on/off row with a short description (Switch tinted like the Auto-rotate one). */
    private fun toggleRow(caption: String, hint: String, key: String, default: Boolean): View {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = android.view.Gravity.CENTER_VERTICAL }
        val explain = explanations[key]?.let { ui.info(it) }
        val title = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            clipChildren = false
            addView(captionWithInfo(caption, explain?.first), LinearLayout.LayoutParams(-2, -2)) // wrap: a vertical layout's default would squeeze it to the hint's width
            addView(ui.body(hint, 12f, R.color.ink2), LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(2) })
        }
        val states = arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf())
        val toggle = android.widget.Switch(this).apply {
            isChecked = prefs.getBoolean(key, default)
            thumbTintList = android.content.res.ColorStateList(states, intArrayOf(ui.color(R.color.accent), 0xFFBDBDBD.toInt()))
            trackTintList = android.content.res.ColorStateList(states, intArrayOf((ui.color(R.color.accent) and 0x00FFFFFF) or 0x80000000.toInt(), 0x80BDBDBD.toInt()))
            setOnCheckedChangeListener { _, on -> prefs.edit().putBoolean(key, on).apply() }
        }
        row.addView(title, LinearLayout.LayoutParams(0, -2, 1f))
        row.addView(toggle)
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(row)
            explain?.let { addView(it.second, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(8) }) }
            addView(View(context).apply { setBackgroundColor((ui.look.ink3 and 0x00FFFFFF) or 0x40000000) },
                LinearLayout.LayoutParams(-1, 1).apply { topMargin = ui.dp(8); bottomMargin = ui.dp(10) })
        }
    }
    private fun chooseMode(mode: String) {
        sessionMode = mode
        prefs.edit().putString("Mode", mode).apply()
        renderStream(CameraStreamService.currentSettings != null)
        // Wi-Fi needs the pairing code typed on the PC: show it right away, in a pop-up.
        // The cable needs no code, only the USB switch: the guide walks through it (skipped when it is already on).
        if (mode == "wifi") showPairingCode()
        else if (mode == "tether" && !cableLinkUp()) tetherGuide.open()
        else if (mode == "usb" && !usbDebuggingOn()) debugGuide.open()
    }

    /** Opens the phone's page with the USB switch; older phones fall back to the general network settings. */
    private fun openTetherSettings() {
        try { startActivity(Intent().setClassName("com.android.settings", "com.android.settings.TetherSettings")) }
        catch (_: Exception) { startActivity(Intent(android.provider.Settings.ACTION_WIRELESS_SETTINGS)) }
    }

    private fun openSettingsPage(action: String) {
        try { startActivity(Intent(action)) } catch (_: Exception) { startActivity(Intent(android.provider.Settings.ACTION_SETTINGS)) }
    }

    /** Opens a web search for this phone's make and model: the way to find the right menu on any phone. */
    private fun openHelp(what: String) {
        val query = android.net.Uri.encode("how to $what ${Build.MANUFACTURER} ${Build.MODEL}")
        try { startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse("https://www.google.com/search?q=$query"))) } catch (_: Exception) {}
    }

    /** True once Developer options is unlocked (Build number tapped 7 times). */
    private fun developerOptionsOn(): Boolean =
        android.provider.Settings.Global.getInt(contentResolver, android.provider.Settings.Global.DEVELOPMENT_SETTINGS_ENABLED, 0) == 1

    /** True while USB debugging is switched on in Developer options. */
    private fun usbDebuggingOn(): Boolean =
        android.provider.Settings.Global.getInt(contentResolver, android.provider.Settings.Global.ADB_ENABLED, 0) == 1

    /** True while the phone's USB link is switched on (Android shows it as a network named rndis0, usb0 or ncm0). */
    private fun cableLinkUp(): Boolean = try {
        java.net.NetworkInterface.getNetworkInterfaces().asSequence()
            .any { it.isUp && (it.name.startsWith("rndis") || it.name.startsWith("usb") || it.name.startsWith("ncm")) && it.inetAddresses.hasMoreElements() }
    } catch (_: Exception) { false }

    /** Public GitHub draft uses only selected fields; full diagnostic sharing is a separate action. */
    private fun showReportDialog() {
        val crashFile = java.io.File(filesDir, PhoneCamApp.CRASH_FILE)
        val savedSummary = java.io.File(filesDir, "github-report-summary.txt")
        val look = ui.look
        val note = android.widget.EditText(this).apply {
            hint = "What happened? What were you doing?"
            setHintTextColor(look.ink3)
            setTextColor(look.ink)
            typeface = ui.sans
            textSize = 14f
            minLines = 3
            gravity = android.view.Gravity.TOP
            background = ui.box(look.paper, look.stroke, 10, 1)
            setPadding(ui.dp(12), ui.dp(10), ui.dp(12), ui.dp(10))
        }
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = ui.box(look.panel, look.stroke, look.panelRadius.coerceAtLeast(14), look.panelBorder)
            setPadding(ui.dp(20), ui.dp(18), ui.dp(20), ui.dp(16))
            addView(ui.body("Report a problem", 18f, R.color.ink, bold = true))
            addView(ui.body("Open a draft on GitHub with your description, app version, Android version, phone model and connection type. Sign in to GitHub, review the draft and submit it yourself. Reports are public: leave out pairing codes, addresses and private details. If the link needs a shorter description, the full summary is saved privately on this phone. Use Share saved GitHub summary to retrieve it; a later shortened report replaces that saved summary.", 12.5f),
                LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(4); bottomMargin = ui.dp(12) })
            addView(note, LinearLayout.LayoutParams(-1, -2))
        }
        val dialog = android.app.AlertDialog.Builder(this).setView(android.widget.ScrollView(this).apply { addView(card) }).create()
        dialog.window?.setBackgroundDrawable(android.graphics.drawable.InsetDrawable(android.graphics.drawable.ColorDrawable(0), ui.dp(14)))
        val savedSummaryButton = ui.ghostButton("Share saved GitHub summary") {
            try {
                val text = android.util.AtomicFile(savedSummary).openRead().bufferedReader().use { it.readText() }
                startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_SUBJECT, "Streamsthetic GitHub summary")
                    putExtra(Intent.EXTRA_TEXT, text)
                }, "Share full summary with"))
            } catch (_: Exception) {
                showMessage("Summary unavailable", "The saved summary could not be opened or shared. Your current description is still in this dialog.", "OK") {}
            }
        }.apply { visibility = if (savedSummary.exists()) View.VISIBLE else View.GONE }
        card.addView(ui.primaryButton("Continue on GitHub") {
            val version = try { packageManager.getPackageInfo(packageName, 0).versionName } catch (_: Exception) { "Unknown" }
            val draft = GitHubReport.create(note.text.toString(), version, Build.VERSION.RELEASE, Build.MODEL,
                sessionMode ?: prefs.getString("Mode", null))
            if (draft.shortened) {
                val file = android.util.AtomicFile(savedSummary)
                var stream: java.io.FileOutputStream? = null
                try {
                    stream = file.startWrite()
                    stream.write(draft.fullBody.toByteArray(Charsets.UTF_8))
                    file.finishWrite(stream)
                    savedSummaryButton.visibility = View.VISIBLE
                } catch (_: Exception) {
                    file.failWrite(stream)
                    showMessage("Could not save the full summary", "GitHub was not opened because the link would shorten your description. Your text is still here. Shorten it yourself or try again after freeing space on the phone.", "OK") {}
                    return@primaryButton
                }
            }
            try {
                startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(draft.url)))
            } catch (_: android.content.ActivityNotFoundException) {
                showMessage("No browser available", "Install or enable a browser, then try again. Your description is still in this dialog.", "OK") {}
            }
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(14) })
        card.addView(savedSummaryButton)
        card.addView(ui.body("Optional diagnostic sharing includes settings, recent logs, connection details and any saved crash. These may contain private information. Review the report before sharing it; do not post it publicly without checking it.", 12.5f),
            LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(12) })
        card.addView(ui.ghostButton("Share diagnostic report") {
            val text = buildReport(note.text.toString().trim(), crashFile)
            try {
                startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_SUBJECT, "Streamsthetic diagnostic report")
                    putExtra(Intent.EXTRA_TEXT, text)
                }, "Share diagnostic report with"))
            } catch (_: android.content.ActivityNotFoundException) {
                showMessage("No sharing app available", "Install or enable an app that can share text, then try again.", "OK") {}
            }
        }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(14) })
        card.addView(ui.ghostButton("Cancel") { dialog.dismiss() })
        dialog.show()
    }

    private fun buildReport(userText: String, crashFile: java.io.File): String {
        val log = try {
            val proc = Runtime.getRuntime().exec(arrayOf("logcat", "-d", "-v", "time", "-t", "1200"))
            proc.inputStream.bufferedReader().readText()
        } catch (e: Exception) { "(log not available: $e)" }
        val settings = prefs.all.entries.filter { it.key != "pairing_code" }.joinToString("\n") { "${it.key} = ${it.value}" }
        val version = try { packageManager.getPackageInfo(packageName, 0).versionName } catch (e: Exception) { "?" }
        val text = buildString {
            appendLine("# Streamsthetic problem report (phone)")
            appendLine("App version: $version")
            appendLine("Phone: ${Build.MANUFACTURER} ${Build.MODEL}, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
            appendLine("Streaming now: ${CameraStreamService.currentSettings != null}")
            // heat and power right now: a hot phone slows its video chip, which shows up on the PC as a delay spike
            val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val thermalNow = if (Build.VERSION.SDK_INT >= 29) getSystemService(PowerManager::class.java).currentThermalStatus else -1
            appendLine("Heat: thermal status $thermalNow (0 normal, 1 light, 2 moderate, 3 severe, 4+ critical), battery ${(battery?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10.0} C, " +
                "level ${battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)}%, charging ${(battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0}, " +
                "battery saver ${getSystemService(PowerManager::class.java).isPowerSaveMode}")
            CameraStreamService.currentSettings?.let { appendLine("Stream: ${it.keySet().filter { k -> k in setOf("w", "h", "fps", "mbps") }.joinToString(", ") { k -> "$k=${it.get(k)}" }}") }
            appendLine()
            appendLine("## Problem description")
            appendLine(if (userText.isEmpty()) "(No description provided)" else userText)
            appendLine()
            appendLine("## Settings")
            appendLine(settings)
            if (crashFile.exists()) {
                appendLine()
                appendLine("## Last crash")
                appendLine(crashFile.readText())
            }
            appendLine()
            appendLine("## Recent log")
            appendLine(log)
        }
        // the share sheet has a size limit: keep the newest part
        return if (text.length > 120_000) text.take(2000) + "\n...(older log cut)...\n" + text.takeLast(115_000) else text
    }

    private val permissionRefreshers = mutableListOf<() -> Unit>()

    /**
     * One permission as a row: its name, why, a tag (Needed / Recommended / Optional) and a button. Tapping asks Android;
     * once Android will not ask again (it was refused twice) the button opens the app's own settings page instead.
     * [permission] null = the battery exemption (not a runtime permission).
     */
    private fun permissionRow(name: String, why: String, permission: String?, tag: String): View {
        if (permission == Manifest.permission.POST_NOTIFICATIONS && Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return View(this).apply { visibility = View.GONE }
        val title = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                addView(ui.label(name, 11f, R.color.ink))
                addView(ui.label("  " + tag, 10f, R.color.ink3))
            }, LinearLayout.LayoutParams(-2, -2))
            addView(ui.body(why, 12f, R.color.ink2), LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(2) })
        }
        val button = ui.ghostButton("Allow") {}
        fun granted(): Boolean = if (permission == null) {
            getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        } else checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
        fun refresh() {
            val ok = granted()
            button.text = ui.cased(if (ok) "Allowed" else "Allow")
            button.isEnabled = !ok
            button.alpha = if (ok) 0.55f else 1f
        }
        button.setOnClickListener {
            when {
                permission == null -> startActivity(Intent(android.provider.Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, android.net.Uri.parse("package:$packageName")))
                prefs.getBoolean("Asked_$permission", false) && !shouldShowRequestPermissionRationale(permission) ->
                    startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
                else -> { prefs.edit().putBoolean("Asked_$permission", true).apply(); requestPermissions(arrayOf(permission), 3) }
            }
        }
        refresh()
        permissionRefreshers.add(::refresh)
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = android.view.Gravity.CENTER_VERTICAL
            addView(title, LinearLayout.LayoutParams(0, -2, 1f))
            addView(button, LinearLayout.LayoutParams(-2, -2).apply { leftMargin = ui.dp(10) })
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, ui.dp(10), 0, 0)
            addView(row)
            addView(View(context).apply { setBackgroundColor((ui.look.ink3 and 0x00FFFFFF) or 0x40000000) },
                LinearLayout.LayoutParams(-1, 1).apply { topMargin = ui.dp(10) })
        }
    }

    /** First start: say what the app will ask for and why, then ask for the camera and notifications together. */
    private fun welcomePermissions() {
        if (prefs.getBoolean("PermissionsIntro", false)) return
        prefs.edit().putBoolean("PermissionsIntro", true).apply()
        val wanted = listOf(Manifest.permission.CAMERA) +
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) listOf(Manifest.permission.POST_NOTIFICATIONS) else emptyList()
        if (wanted.all { checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }) return
        showMessage("Before you start",
            "Choose permissions for Streamsthetic:\n\n" +
                "Camera: lets your PC use this phone as a webcam.\n" +
                (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) "Notifications (optional): shows when the camera is on, with a Stop button.\n" else "") +
                "\nStreamsthetic sends video directly to the connected PC. It does not record video or upload it to a cloud service. You can change these any time in Settings, Permissions.", "Continue") {
            requestPermissions(wanted.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }.toTypedArray(), 3)
        }
    }

    /** Turn off by itself when nobody uses the camera for a while (default 30 minutes). */
    private fun autoOffPicker(): View {
        val names = listOf("Off", "10 min", "30 min", "1 hour")
        val minutes = intArrayOf(0, 10, 30, 60)
        val chips = ui.ChipRow(names, 1, 4)
        chips.setSelection(SavedChoice.indexOrDefault(minutes, prefs.getInt("AutoOffMinutes", 30), 2))
        chips.onSelected = { position -> prefs.edit().putInt("AutoOffMinutes", minutes[position]).apply() }
        return labelled("Auto-off", chips)
    }

    /** Send this phone's microphone to the PC (played by the Streamsthetic receiver on the output you pick there). */
    private fun micRow(): View {
        val row = toggleRow("Send the microphone to the PC", "Send microphone audio while streaming", "Mic", false)
        // the switch is inside the row: find it and add the permission step
        fun findSwitch(v: View): android.widget.Switch? = if (v is android.widget.Switch) v else (v as? android.view.ViewGroup)?.let { g -> (0 until g.childCount).firstNotNullOfOrNull { findSwitch(g.getChildAt(it)) } }
        findSwitch(row)?.setOnCheckedChangeListener { _, on ->
            prefs.edit().putBoolean("Mic", on).apply()
            if (on && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                showMessage("Allow the microphone", "Android will ask to let Streamsthetic use the microphone. Audio is sent to the connected PC only while streaming with the microphone enabled.", "Continue") {
                    requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 2)
                }
            } else if (on && CameraStreamService.currentSettings != null) {
                showMessage("Restart the stream", "Stop and restart the stream to apply the microphone setting.", "OK") {}
            }
        }
        return row
    }

    private var offWifiWarned = false

    /** True when the phone's current network is Wi-Fi or Ethernet (not mobile data and not offline). */
    private fun onLocalNetwork(): Boolean {
        val cm = getSystemService(android.net.ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_ETHERNET)
    }

    /**
     * Wi-Fi mode only: when the phone is no longer on Wi-Fi (it switched to mobile data, or has no connection) say so once.
     * Streamsthetic never sends video over mobile data, so the stream cannot reach the PC until Wi-Fi is back.
     */
    private fun checkWifi() {
        if (sessionMode != "wifi") { offWifiWarned = false; return }
        if (onLocalNetwork()) { offWifiWarned = false; return }
        if (offWifiWarned) return
        offWifiWarned = true
        showMessage("Not connected to Wi-Fi",
            "Connect this phone to Wi-Fi on the same local network as your PC. The PC can use Wi-Fi or Ethernet. Wi-Fi streaming does not use mobile data.", "OK") {}
    }

    private fun showPairingCode() = pairingSheet.open(CameraStreamService.storedPairingCode(this), { CameraStreamService.newPairingCode(this) }, ::scanPairingQr)

    /** Opens Google's code scanner; the PC app shows the QR code in its Wi-Fi panel. */
    private fun scanPairingQr() {
        val options = com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(com.google.mlkit.vision.barcode.common.Barcode.FORMAT_QR_CODE).build()
        com.google.mlkit.vision.codescanner.GmsBarcodeScanning.getClient(this, options).startScan()
            .addOnSuccessListener { adoptPairingLink(it.rawValue) }
            .addOnFailureListener { showMessage("Could not scan", "The scanner did not open. Check that your phone has Google Play services, or type the code shown here on the PC instead.", "OK") {} }
    }

    /** A pairing link ("streamsthetic://pair?code=123456", from the QR code): adopt its code and say so. */
    private fun adoptPairingLink(link: String?) {
        val code = link?.let { android.net.Uri.parse(it) }?.takeIf { it.scheme == "streamsthetic" && it.host == "pair" }?.getQueryParameter("code")
        if (code == null || !code.matches(Regex("[0-9]{6}"))) {
            showMessage("Not a Streamsthetic code", "That QR code is not from the Streamsthetic app on your PC. Open Streamsthetic on the PC, choose Wi-Fi, and scan the QR code it shows.", "OK") {}
            return
        }
        CameraStreamService.setPairingCode(this, code)
        sessionMode = "wifi"
        prefs.edit().putString("Mode", "wifi").apply()
        renderStream(CameraStreamService.currentSettings != null)
        showMessage("Pairing code saved", "Press Start here. If Streamsthetic on your PC is waiting for this phone, it connects automatically. Otherwise, press Start on the PC. The code is saved for next time.", "OK") {}
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        when {
            pairingSheet.isOpen -> pairingSheet.close()
            tetherGuide.isOpen -> tetherGuide.close()
            debugGuide.isOpen -> debugGuide.close()
            pages[3].visibility == View.VISIBLE && settingsCategory != 0 -> showSettingsCategory(0)
            else -> @Suppress("DEPRECATION") super.onBackPressed()
        }
    }

    private fun renderStream(streaming: Boolean) {
        if (!::chooser.isInitialized) return
        val mode = sessionMode
        val showChooser = mode == null
        val incoming = if (showChooser) chooser else modeView
        val outgoing = if (showChooser) modeView else chooser
        outgoing.visibility = View.GONE
        incoming.visibility = View.VISIBLE
        if (chooserShown != null && chooserShown != showChooser) {
            // the two screens slide/fade into each other instead of snapping
            incoming.alpha = 0f
            incoming.translationX = ui.dp(if (showChooser) -24 else 24).toFloat()
            incoming.animate().alpha(1f).translationX(0f).setDuration(260).setInterpolator(android.view.animation.DecelerateInterpolator()).start()
        }
        chooserShown = showChooser
        if (mode != null) {
            modeTitle.text = ui.cased(when (mode) { "wifi" -> "Wi-Fi"; "tether" -> "USB connection"; else -> "USB debugging" })
            modeArt.setImageResource(if (mode == "wifi") R.drawable.ill_wifi else R.drawable.ill_usb)
            tetherLink.visibility = if (mode == "tether" || mode == "usb") View.VISIBLE else View.GONE
            modeSteps.text = if (mode == "tether")
                "1. Connect this phone to your PC with a USB data cable.\n2. Turn on USB tethering. Use the setup guide if needed.\n3. Open Streamsthetic on the PC and choose USB connection.\n4. Press Start on this phone. If the PC is not waiting for a connection, press Start there too."
            else if (mode == "wifi")
                "1. Connect this phone to Wi-Fi on the same local network as your PC. The PC can use Ethernet.\n2. Open Streamsthetic on the PC and choose Wi-Fi.\n3. Scan the PC's QR code using the button below.\n4. Press Start on this phone. If the PC is not waiting for a connection, press Start there too."
            else
                "1. Turn on USB debugging. Use the setup guide if needed.\n2. Plug this phone into your PC with a USB cable.\n3. Open Streamsthetic on the PC, choose USB debugging, and press Start."
            codeLink.visibility = if (mode == "wifi") View.VISIBLE else View.GONE
            scanLink.visibility = if (mode == "wifi") View.VISIBLE else View.GONE
            backLink.visibility = if (streaming) View.INVISIBLE else View.VISIBLE
            ui.setStopState(startStop, streaming, if (streaming) "Stop" else "Start")
        }
    }

    private fun onStartStop() {
        if (CameraStreamService.currentSettings != null) {
            stopService(Intent(this, CameraStreamService::class.java))
        } else if (sessionMode == "tether" && !cableLinkUp()) {
            // the USB connection starts only with the cable in and USB tethering on: say so, and offer the guide
            showMessage("Turn on USB tethering first",
                "Connect the phone to your PC with a USB data cable, then turn on USB tethering. Open the setup guide for instructions.",
                "Open setup guide") { tetherGuide.open() }
        } else {
            start(chosenSettings().apply { if (sessionMode == "wifi" || sessionMode == "tether") { putInt("tcp", TCP_PORT); putString("link", sessionMode) } })
        }
    }
    private fun buildSettingsPage(savedState: Bundle?): View {
        settingsCategory = (savedState?.getInt("settingsCategory") ?: 0).coerceIn(0, 4)
        settingsScroll = savedState?.getIntArray("settingsScroll")?.takeIf { it.size == 5 } ?: IntArray(5)
        fun page(title: String, vararg controls: View, category: Boolean = true) = android.widget.ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            addView(LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(ui.dp(16), ui.dp(4), ui.dp(16), ui.dp(20))
                if (category) addView(ui.ghostButton("Back to Settings") { showSettingsCategory(0) }.apply {
                    minHeight = ui.dp(48)
                    contentDescription = "Back to Settings overview"
                }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(12) })
                addView(ui.body(ui.cased(title), 18f, R.color.ink, bold = true).apply { typeface = ui.titleFont },
                    LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(12) })
                controls.forEach { addView(it) }
            })
        }
        settingsPages = listOf(
            page("Settings",
                ui.settingsCategory("Appearance", "Theme, text and control sizes") { showSettingsCategory(1) },
                ui.settingsCategory("Streaming & power", "Audio, brightness and power use") { showSettingsCategory(2) },
                ui.settingsCategory("Permissions", "Camera, audio and app access") { showSettingsCategory(3) },
                ui.settingsCategory("Help & requirements", "Setup requirements and reports") { showSettingsCategory(4) },
                category = false),
            page("Appearance", ui.settingsSection(
                skinPicker(), appearancePicker(),
                scalePicker("Text size", "TextScale", listOf("Normal", "Large", "Larger", "Largest")),
                scalePicker("Icon size", "IconScale", listOf("Normal", "Large", "Larger")))),
            page("Streaming & power", ui.settingsSection(
                micRow(), dimRows(),
                toggleRow("Cool down automatically", "Reduces video settings when the phone gets warm", "HeatAuto", true),
                autoOffPicker())),
            page("Permissions", ui.settingsSection(
                ui.body("What Streamsthetic can use on this phone. Only the camera is required.", 13f),
                permissionRow("Camera", "Allows video capture for your PC", Manifest.permission.CAMERA, "Required"),
                permissionRow("Notifications", "Shows that the camera is on, with a Stop button", Manifest.permission.POST_NOTIFICATIONS, "Recommended"),
                permissionRow("Microphone", "Required only when sending microphone audio", Manifest.permission.RECORD_AUDIO, "Optional"),
                permissionRow("Unrestricted battery", "Helps prevent battery restrictions from interrupting streams", null, "Optional"))),
            page("Help & requirements", ui.settingsSection(
                ui.body("Requirements", 15f, R.color.ink, bold = true),
                ui.body(explanations.getValue("Requirements"), 13f)),
                ui.settingsSection(
                    ui.body("Report a problem", 15f, R.color.ink, bold = true),
                    ui.body("Open a public GitHub issue draft, then review and submit it yourself. Diagnostic sharing is optional and may include private connection details; review it before sharing.", 13f),
                    ui.ghostButton("Report a problem") { showReportDialog() })),
        )
        val container = android.widget.FrameLayout(this).apply {
            settingsPages.forEach { addView(it, android.widget.FrameLayout.LayoutParams(-1, -1)) }
        }
        showSettingsCategory(settingsCategory, rememberScroll = false)
        return container
    }

    private fun showSettingsCategory(category: Int, rememberScroll: Boolean = true) {
        if (rememberScroll && settingsPages[settingsCategory].isShown) settingsScroll[settingsCategory] = settingsPages[settingsCategory].scrollY
        settingsCategory = category.coerceIn(0, settingsPages.lastIndex)
        settingsPages.forEachIndexed { index, page -> page.visibility = if (index == settingsCategory) View.VISIBLE else View.GONE }
        updateSettingsBackCallback()
        val selected = settingsCategory
        settingsPages[selected].post {
            if (settingsCategory == selected && settingsPages[selected].isShown) settingsPages[selected].scrollTo(0, settingsScroll[selected])
        }
    }

    private fun updateSettingsBackCallback() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        val needed = ::pages.isInitialized && pages[3].visibility == View.VISIBLE && settingsCategory != 0
        if (needed && settingsBackCallback == null) {
            settingsBackCallback = android.window.OnBackInvokedCallback { showSettingsCategory(0) }.also {
                onBackInvokedDispatcher.registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, it)
            }
        } else if (!needed) {
            settingsBackCallback?.let { onBackInvokedDispatcher.unregisterOnBackInvokedCallback(it) }
            settingsBackCallback = null
        }
    }

    override fun onDestroy() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            settingsBackCallback?.let { onBackInvokedDispatcher.unregisterOnBackInvokedCallback(it) }
        }
        settingsBackCallback = null
        super.onDestroy()
    }

    override fun onSaveInstanceState(outState: Bundle) {
        if (settingsPages[settingsCategory].isShown) settingsScroll[settingsCategory] = settingsPages[settingsCategory].scrollY
        outState.putInt("settingsCategory", settingsCategory)
        outState.putIntArray("settingsScroll", settingsScroll)
        super.onSaveInstanceState(outState)
    }

    private fun showTab(index: Int) {
        if (settingsPages[settingsCategory].isShown) settingsScroll[settingsCategory] = settingsPages[settingsCategory].scrollY
        pages.forEachIndexed { i, p ->
            val show = i == index
            if (show && p.visibility != View.VISIBLE) { p.alpha = 0f; p.animate().alpha(1f).setDuration(200).start() }
            p.visibility = if (show) View.VISIBLE else View.GONE
        }
        if (index == 3) showSettingsCategory(settingsCategory, rememberScroll = false)
        updateSettingsBackCallback()
        tabBar.select(index)
        prefs.edit().putInt("Tab", index).apply()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (intent.action == Intent.ACTION_VIEW) adoptPairingLink(intent.dataString)
        handleLaunch(intent)
    }

    /**
     * Launched from the PC app ("autostart": start with the settings remembered here, unless already streaming) or
     * from adb scripts and tests (explicit settings: start straight away with them).
     */
    private fun handleLaunch(intent: Intent) {
        val extras = intent.extras ?: return
        // started from outside (PC app, scripts): show that connection's screen, not the chooser
        sessionMode = if (extras.containsKey("tcp")) (prefs.getString("Mode", "wifi")?.takeIf { it == "tether" } ?: "wifi") else "usb" // started over the cable by the PC (debugging route)
        if (extras.getBoolean("autostart", false)) {
            if (CameraStreamService.currentSettings == null) start(chosenSettings())
        } else {
            start(extras)
        }
    }

    override fun onResume() {
        super.onResume()
        permissionRefreshers.forEach { it() }
        if (::tetherGuide.isInitialized) handler.postDelayed({ tetherGuide.refresh(); debugGuide.refresh() }, 800) // the switch takes a moment to show up
        handler.post(refresh)
    }

    override fun onPause() {
        handler.removeCallbacks(refresh)
        super.onPause()
    }

    private fun rotationControls(): LinearLayout {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        autoRotate = android.widget.Switch(this).apply {
            text = "Auto-rotate"
            typeface = ui.sans
            textSize = 15f
            isChecked = true
            setTextColor(ui.color(R.color.ink))
            // the default switch was nearly invisible in this theme: accent when on, grey when off
            val states = arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf())
            thumbTintList = android.content.res.ColorStateList(states, intArrayOf(ui.color(R.color.accent), 0xFFBDBDBD.toInt()))
            trackTintList = android.content.res.ColorStateList(states, intArrayOf((ui.color(R.color.accent) and 0x00FFFFFF) or 0x80000000.toInt(), 0x80BDBDBD.toInt()))
            setOnCheckedChangeListener { _, on ->
                if (syncingRotate) return@setOnCheckedChangeListener
                lastLocalChange = SystemClock.uptimeMillis()
                setTurnEnabled(!on && CameraStreamService.currentSettings != null)
                CameraStreamService.instance?.requestApply(Bundle().apply { putString("orient", if (on) "auto" else "manual") })
            }
        }
        val (rotateInfo, rotateCard) = ui.info(explanations.getValue("Auto-rotate"))
        box.addView(LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = android.view.Gravity.CENTER_VERTICAL
            addView(autoRotate, LinearLayout.LayoutParams(0, -2, 1f))
            addView(rotateInfo, LinearLayout.LayoutParams(ui.dp(30), ui.dp(30)).apply { leftMargin = ui.dp(10) })
        }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(10) })
        box.addView(rotateCard, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(10) })
        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        turnLeft = ui.ghostButton("⟲  Left") { turn(-90) }
        turnRight = ui.ghostButton("Right  ⟳") { turn(90) }
        buttons.addView(turnLeft, LinearLayout.LayoutParams(0, -2, 1f).apply { rightMargin = ui.dp(6) })
        buttons.addView(turnRight, LinearLayout.LayoutParams(0, -2, 1f))
        box.addView(buttons)
        rotationHint = ui.body("", 12f, R.color.ink2)
        box.addView(rotationHint, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(10) })
        updateRotationHint()
        return box
    }

    /** Left/Right only while auto-rotate is off (greyed out otherwise). */
    private fun updateRotationHint() {
        if (!::rotationHint.isInitialized) return
        rotationHint.text = when {
            CameraStreamService.currentSettings == null -> "Start streaming to adjust rotation."
            autoRotate.isChecked -> "Follows the phone after you hold it steady for a second."
            else -> "Use Left or Right to rotate 90°."
        }
    }

    private fun setTurnEnabled(enabled: Boolean) {
        updateRotationHint()
        for (b in listOf(turnLeft, turnRight)) {
            b.isEnabled = enabled
            b.alpha = if (enabled) 1f else 0.4f
        }
    }

    /**
     * Light or dark for this app only (System = follow the phone). Android 12+ keeps the choice itself and redraws
     * the screen; on Android 11 the app always follows the phone, so the choice is not offered there.
     * Not part of the stream settings, so it is kept out of [pickers].
     */
    /** The look of the app (Studio, Creator, Gamer, Brutal, Dot): saved, and the screen is redrawn in it. Not a stream setting. */
    private fun skinPicker(): View {
        val chips = ui.ChipRow(SKINS, 4, 8)
        chips.setSelection(SKINS.indexOf(ui.skinName).coerceAtLeast(0))
        chips.onSelected = { position ->
            if (SKINS[position] != ui.skinName) {
                prefs.edit().putString("Skin", SKINS[position]).apply()
                recreate() // the streaming service is not part of this screen: it keeps running
            }
        }
        return labelled("Theme", chips)
    }

    private fun appearancePicker(): View {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return ui.body("Follows the phone's light or dark setting.")
        val names = listOf("System", "Light", "Dark")
        val chips = ui.ChipRow(names, 4, 9)
        // Gamer is always dark: the choice does not apply
        if (ui.skinName == "Gamer") { chips.isEnabled = false; return LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; addView(labelled("Appearance", chips)); addView(ui.body("The Gamer theme uses dark appearance.", 13f)) } }
        chips.setSelection(prefs.getInt("Appearance", 0).coerceIn(0, names.size - 1))
        chips.onSelected = { position ->
            if (position != prefs.getInt("Appearance", 0)) {
                prefs.edit().putInt("Appearance", position).apply()
                val mode = when (position) {
                    1 -> android.app.UiModeManager.MODE_NIGHT_NO
                    2 -> android.app.UiModeManager.MODE_NIGHT_YES
                    else -> android.app.UiModeManager.MODE_NIGHT_AUTO
                }
                // redraws this screen in the new colours; streaming is not affected (it runs in the service)
                getSystemService(android.app.UiModeManager::class.java).setApplicationNightMode(mode)
            }
        }
        return labelled("Appearance", chips)
    }

    /** Text size / icon size: saved, and the screen is redrawn at the new size (streaming keeps running in its service). */
    private fun scalePicker(caption: String, key: String, names: List<String>): View {
        val chips = ui.ChipRow(names, 3, 10)
        chips.setSelection(prefs.getInt(key, 0).coerceIn(0, names.size - 1))
        chips.onSelected = { position ->
            if (position != prefs.getInt(key, 0)) {
                prefs.edit().putInt(key, position).apply()
                recreate()
            }
        }
        return labelled(caption, chips)
    }

    /**
     * How dark the screen gets while streaming (2-60 % brightness). While the slider is held the screen shows that
     * brightness, so you see what you are choosing; it goes back to normal when you let go.
     */
    /** The Dim switch and, only while it is on, the Dim level slider under it (fades in / out when the switch changes). */
    private fun dimRows(): View {
        val toggle = toggleRow("Dim the screen while streaming", "Saves battery and keeps the phone cooler", "Dim", true)
        val level = dimSlider()
        level.visibility = if (prefs.getBoolean("Dim", true)) View.VISIBLE else View.GONE
        fun findSwitch(v: View): android.widget.Switch? = if (v is android.widget.Switch) v else (v as? android.view.ViewGroup)?.let { g -> (0 until g.childCount).firstNotNullOfOrNull { findSwitch(g.getChildAt(it)) } }
        findSwitch(toggle)?.setOnCheckedChangeListener { _, on ->
            prefs.edit().putBoolean("Dim", on).apply()
            if (on) {
                level.alpha = 0f
                level.visibility = View.VISIBLE
                level.animate().alpha(1f).setDuration(200).start()
            } else {
                level.animate().alpha(0f).setDuration(150).withEndAction { level.visibility = View.GONE }.start()
            }
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(toggle)
            addView(level)
        }
    }

    private fun dimSlider(): View {
        val value = ui.body("", 13f, R.color.ink, bold = true)
        val warn = 0xFFD32F2F.toInt()
        val note = ui.body("", 12f, R.color.ink2)
        var barRef: android.widget.SeekBar? = null
        fun show(level: Int) {
            value.text = "$level%"
            val past = level > DIM_RECOMMENDED
            value.setTextColor(if (past) warn else ui.color(R.color.ink))
            note.text = if (past) "Higher brightness uses more power and can increase heat." else "Use $DIM_RECOMMENDED% or less to reduce power use. The red section marks higher brightness."
            note.setTextColor(if (past) warn else ui.look.ink3)
            barRef?.thumbTintList = android.content.res.ColorStateList.valueOf(if (past) warn else ui.color(R.color.accent))
        }
        val bar = android.widget.SeekBar(this).apply {
            max = DIM_MAX - DIM_MIN
            progress = prefs.getInt("DimLevel", 3).coerceIn(DIM_MIN, DIM_MAX) - DIM_MIN
            // two-colour track: orange up to the recommended level, red beyond it, a tick marks the limit
            progressDrawable = DimTrack(ui.color(R.color.accent), warn, (ui.look.ink3 and 0x00FFFFFF) or 0x55000000, (DIM_RECOMMENDED - DIM_MIN).toFloat() / (DIM_MAX - DIM_MIN), ui.dp(6).toFloat(), ui.look.ink)
            splitTrack = false
            progressDrawable.level = (progress * 10000L / max).toInt() // the new track starts at the saved level (not 0)
            thumbTintList = android.content.res.ColorStateList.valueOf(ui.color(R.color.accent))
            contentDescription = "Screen brightness while dimmed"
            setOnSeekBarChangeListener(object : android.widget.SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(b: android.widget.SeekBar, p: Int, fromUser: Boolean) {
                    show(p + DIM_MIN)
                    if (fromUser) setWindowBrightness((p + DIM_MIN) / 100f)
                }
                override fun onStartTrackingTouch(b: android.widget.SeekBar) { dimSliderHeld = true }
                override fun onStopTrackingTouch(b: android.widget.SeekBar) {
                    prefs.edit().putInt("DimLevel", b.progress + DIM_MIN).apply()
                    dimSliderHeld = false
                    setWindowBrightness(if (CameraStreamService.currentSettings != null && prefs.getBoolean("Dim", true)) (b.progress + DIM_MIN) / 100f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE)
                }
            })
        }
        barRef = bar
        show(bar.progress + DIM_MIN)
        val explain = ui.info(explanations.getValue("Dim level"))
        val head = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = android.view.Gravity.CENTER_VERTICAL
            addView(captionWithInfo("Dim level", explain.first), LinearLayout.LayoutParams(0, -2, 1f))
            addView(value)
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(head)
            addView(ui.body("Screen brightness while streaming with dimming on. Drag to preview.", 12f, R.color.ink2), LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(2) })
            addView(explain.second, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(8) })
            addView(bar, LinearLayout.LayoutParams(-1, ui.dp(40)).apply { topMargin = ui.dp(4) })
            addView(note)
        }
    }

    private fun setWindowBrightness(level: Float) {
        val params = window.attributes
        if (params.screenBrightness != level) { params.screenBrightness = level; window.attributes = params }
    }

    /** Manual mode: turn the picture a quarter left or right from where it is now. */
    private fun turn(by: Int) {
        val current = CameraStreamService.currentSettings ?: return
        val next = (current.getInt("rot", 0) + by + 360) % 360
        lastLocalChange = SystemClock.uptimeMillis()
        CameraStreamService.instance?.requestApply(Bundle().apply { putInt("mrot", next) })
    }

    /** A labelled row of chips that remembers its last choice between launches ([name] is also the saved key). */
    private fun picker(name: String, caption: String, choices: List<Choice>): LinearLayout {
        val chips = ui.ChipRow(choices.map { it.label }, 1, listOf("Lens", "Resolution", "Frame rate", "Quality").indexOf(name))
        val streaming = CameraStreamService.currentSettings?.let { matchingChoice(choices, it) } ?: -1
        chips.setSelection(if (streaming >= 0) streaming else prefs.getInt(name, 0).coerceIn(0, choices.size - 1))
        shownPositions[name] = chips.selectedItemPosition
        // while streaming, a new choice applies at once (no Start press); the connection to the PC stays up
        chips.onSelected = { position ->
            if (shownPositions[name] != position) {
                shownPositions[name] = position
                prefs.edit().putInt(name, position).apply()
                val current = CameraStreamService.currentSettings
                if (current != null && matchingChoice(listOf(choices[position]), current) != 0) {
                    lastLocalChange = SystemClock.uptimeMillis()
                    CameraStreamService.instance?.requestApply(Bundle().also { choices[position].apply(it) })
                }
            }
        }
        pickers[name] = chips
        return labelled(caption, chips)
    }

    /** Plain-language explanations behind each row's (i) (same wording idea as the PC app's tooltips). */
    private val explanations: Map<String, String> = mapOf(
        "Requirements" to "Phone: Android 11 or newer.\n\nPC: Windows 11.\n\nUSB: use a cable that carries data and turn on USB tethering. USB debugging is an optional alternative. A charge-only cable will not work.\n\nWi-Fi: connect the phone and PC to the same local network. A strong 5 GHz connection or a wired connection from the PC to the router can help.\n\nStart with 1080p. Higher resolutions and frame rates need more processing power and network bandwidth. If the picture stutters, lower the quality, frame rate or resolution.\n\nVideo travels directly between your phone and PC over the cable or local network. Streaming does not need an internet connection.",
        "Lens" to "Choose a camera or zoom level. Main 1x is a good starting point. Wide captures more of the scene, Tele magnifies distant subjects, and Front uses the selfie camera. Available lenses depend on your phone. Switching may briefly interrupt the picture.",
        "Resolution" to "How many pixels the video contains.\n\n1080p: a good starting point for streams and calls.\n\n4K: four times as many pixels as 1080p. It needs more processing power and bandwidth; performance depends on your phone, PC and connection. Try Bitrate at 40 to 60 Mbps.\n\n720p: fewer pixels to process and send. Try it if the picture stutters.\n\nChanging resolution briefly interrupts the picture.",
        "Frame rate" to "How many pictures are shown per second.\n\n60: smoother movement, but more work for the phone and PC. Performance depends on the devices, resolution and connection.\n\n30: fewer frames to process and more time to collect light, which can help in a dim room. Try 30 if 60 stutters.",
        "Bitrate" to "How much data the video uses, in megabits per second (Mbps). Higher values preserve fine detail and reduce blocky patches during movement, but need more bandwidth.\n\nStarting points:\n• 1080p: 20 Mbps\n• 4K: 40 to 60 Mbps\n\nUSB: use a data cable and a reliable port.\n\nWi-Fi: keep a strong connection to your router. A 5 GHz connection and a wired PC can help. Other devices and nearby networks can affect performance.\n\nYour internet plan does not set the speed between the phone and PC. An internet speed test does not measure this local connection.\n\nIf the picture stutters, lower Bitrate. You can also lower the frame rate or resolution.",
        "Theme" to "Changes the colors, shapes and fonts in this app. It does not affect your video.",
        "Appearance" to "Light, dark, or match your phone's setting. This only changes the app, not your video.",
        "HeatAuto" to "When Android reports elevated temperature, Streamsthetic reduces bitrate, resolution or frame rate to lower processing load. After two minutes at a lower thermal level, it restores your chosen settings. Turning this off disables these automatic adjustments; Android may still limit performance.",
        "Dim" to "While streaming, the screen dims so the phone stays cooler and uses less battery. The camera keeps working.",
        "Text size" to "Makes all text in this app larger, on top of your phone's own text size setting. The screen redraws right away and streaming is not interrupted.",
        "Icon size" to "Makes icons, buttons and spacing larger so they are easier to see and tap. The screen redraws right away and streaming is not interrupted.",
        "Dim level" to "Sets screen brightness while dimming is enabled, from 2% to 100%. Lower brightness reduces power use and heat. Start at 60% or less and adjust for readability. Drag the slider to preview the brightness.",
        "Mic" to "Sends microphone audio to your PC while streaming. Off by default.\n\nChoose speakers or headphones in the PC app to monitor audio. Streamsthetic does not create a Windows microphone device. To use the audio in another app, configure Windows audio routing, such as a virtual audio cable: select the cable's playback endpoint in Streamsthetic and its recording endpoint in the receiving app.\n\nRestart the stream after changing this setting.",
        "Auto-off" to "Stops streaming after the selected idle period to save battery. The phone is idle when no PC is connected or the PC reports that no app is using the Streamsthetic camera. Choose Off to disable the timer. A notification confirms an automatic stop if notifications are allowed.",
        "Auto-rotate" to "Automatically follows the phone's orientation after you hold it steady for about a second. When off, use Left or Right to rotate the picture by 90°. Flat or diagonal positions do not trigger a turn.",
    )
    private val hints = mapOf(
        "Lens" to "Which phone camera to use", "Resolution" to "How sharp the picture is", "Frame rate" to "Pictures per second (fps)",
        "Bitrate" to "Video data rate (Mbps)",
    )

    /**
     * A setting row, laid out like Swivel's option lists: bold title with a short description under it at the left, the
     * control at the right (a dropdown for 4+ choices; 2-3 chips go under the title), a thin divider below.
     */
    /** A row's name with its (i) button right after it. */
    private fun captionWithInfo(caption: String, info: View?): View = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        clipChildren = false
        gravity = android.view.Gravity.CENTER_VERTICAL
        addView(ui.label(caption, 11f, R.color.ink))
        info?.let { addView(it, LinearLayout.LayoutParams(ui.dp(30), ui.dp(30)).apply { leftMargin = ui.dp(2); topMargin = -ui.dp(4); bottomMargin = -ui.dp(4) }) }
    }

    private fun labelled(caption: String, chips: Ui.ChipRow): LinearLayout {
        val explain = explanations[caption]?.let { ui.info(it) }
        val title = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            clipChildren = false
            addView(captionWithInfo(caption, explain?.first), LinearLayout.LayoutParams(-2, -2)) // wrap: a vertical layout's default would squeeze it to the hint's width
            hints[caption]?.let { addView(ui.body(it, 12f, R.color.ink2), LinearLayout.LayoutParams(-2, -2).apply { topMargin = ui.dp(2) }) }
        }
        val row = LinearLayout(this).apply {
            // bigger text/icons (Settings, Accessibility): the dropdown goes under the name instead of squeezing it
            val roomy = prefs.getInt("TextScale", 0) == 0 && prefs.getInt("IconScale", 0) == 0
            if (chips.isDropdown && roomy) {
                orientation = LinearLayout.HORIZONTAL
                gravity = android.view.Gravity.CENTER_VERTICAL
                addView(title, LinearLayout.LayoutParams(0, -2, 1f))
                addView(chips, LinearLayout.LayoutParams(ui.dp(168), -2))
            } else {
                orientation = LinearLayout.VERTICAL
                addView(title, LinearLayout.LayoutParams(-2, -2).apply { bottomMargin = ui.dp(8) })
                addView(chips)
            }
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(row)
            explain?.let { addView(it.second, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(8) }) }
            addView(View(context).apply { setBackgroundColor((ui.look.ink3 and 0x00FFFFFF) or 0x40000000) },
                LinearLayout.LayoutParams(-1, 1).apply { topMargin = ui.dp(8); bottomMargin = ui.dp(10) })
        }
    }
    private fun choicesFor(name: String) = when (name) {
        "Lens" -> lenses
        "Resolution" -> resolutions
        "Frame rate" -> frameRates
        else -> bitrates
    }

    /** Index of the choice whose values all equal the streaming settings, or -1. */
    private fun matchingChoice(choices: List<Choice>, current: Bundle): Int = choices.indexOfFirst { choice ->
        val values = Bundle().also { choice.apply(it) }
        values.keySet().all { key ->
            when (key) {
                "zoom" -> kotlin.math.abs(values.getFloat(key) - current.getFloat(key, -1f)) < 0.01f
                "cam" -> values.getString(key) == current.getString(key)
                else -> values.getInt(key) == current.getInt(key, -1)
            }
        }
    }

    private val prefs by lazy { getSharedPreferences("settings", MODE_PRIVATE) }

    private fun chosenSettings(): Bundle {
        val settings = Bundle()
        val edit = prefs.edit()
        for ((label, spinner) in pickers) {
            choicesFor(label)[spinner.selectedItemPosition.coerceAtLeast(0)].apply(settings)
            edit.putInt(label, spinner.selectedItemPosition)
        }
        edit.apply()
        return settings
    }

    private var pendingStart: Bundle? = null

    private fun start(settings: Bundle) {
        // POST_NOTIFICATIONS only exists on Android 13+; asking for it earlier can never be granted
        val wanted = listOf(Manifest.permission.CAMERA) +
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) listOf(Manifest.permission.POST_NOTIFICATIONS) else emptyList()
        val needed = wanted.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (needed.isEmpty()) startStreaming(settings) else {
            pendingStart = settings
            if (prefs.getBoolean("PermissionExplained", false)) requestPermissions(needed.toTypedArray(), 1)
            else showMessage("Allow the camera",
                "Android will ask to allow the camera" +
                    (if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) " and optional notifications, which show when the camera is on" else "") +
                    ". Streamsthetic sends video directly to the connected PC. It does not record video or upload it to a cloud service.", "Continue") {
                prefs.edit().putBoolean("PermissionExplained", true).apply()
                requestPermissions(needed.toTypedArray(), 1)
            }
        }
    }

    /** A small pop-up card in the app's look: a title, a few words, one main button and Cancel. */
    private fun showMessage(title: String, text: String, button: String, onButton: () -> Unit) {
        val look = ui.look
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = ui.box(look.panel, look.stroke, look.panelRadius.coerceAtLeast(14), look.panelBorder)
            setPadding(ui.dp(20), ui.dp(18), ui.dp(20), ui.dp(16))
            addView(ui.body(title, 18f, R.color.ink, bold = true))
            addView(ui.body(text, 13.5f), LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(6); bottomMargin = ui.dp(14) })
        }
        val dialog = android.app.AlertDialog.Builder(this).setView(card).create()
        dialog.window?.setBackgroundDrawable(android.graphics.drawable.InsetDrawable(android.graphics.drawable.ColorDrawable(0), ui.dp(14)))
        card.addView(ui.primaryButton(button) { dialog.dismiss(); onButton() })
        card.addView(ui.ghostButton("Cancel") { dialog.dismiss(); pendingStart = null })
        dialog.show()
    }

    override fun onRequestPermissionsResult(code: Int, perms: Array<out String>, results: IntArray) {
        if (code == 3) { // Settings, Permissions (or the first-start question)
            permissionRefreshers.forEach { it() }
            return
        }
        if (code == 2) { // the microphone question from Settings
            val granted = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
            if (!granted) prefs.edit().putBoolean("Mic", false).apply()
            recreate() // shows the switch as it really is
            return
        }
        val settings = pendingStart ?: return
        pendingStart = null
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) startStreaming(settings)
        else showMessage("Camera access is off",
            "Streamsthetic needs the camera to send video to your PC. Open the app settings, choose Permissions, then Camera, and allow it.", "Open settings") {
            startActivity(Intent(android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS, android.net.Uri.parse("package:$packageName")))
        }
    }

    // The service must be started while this activity is visible: Android only lets
    // a foreground service keep camera access if it was started from the foreground.
    private fun startStreaming(settings: Bundle) {
        val defaults = Bundle().apply {
            putInt("w", 1920); putInt("h", 1080); putInt("fps", 60); putInt("mbps", 20)
        }
        defaults.putAll(settings)
        startForegroundService(Intent(this, CameraStreamService::class.java).putExtras(defaults))
    }

    companion object {
        private const val TCP_PORT = 5556
        private val TEXT_SCALES = floatArrayOf(1f, 1.2f, 1.4f, 1.7f)
        private val ICON_SCALES = floatArrayOf(1f, 1.2f, 1.4f)
        private const val DIM_MIN = 2
        private const val DIM_MAX = 100
        private const val DIM_RECOMMENDED = 60 // beyond this the bar turns red: brighter than advised (battery, heat)
        /** The connection picked on the Stream tab this time the app runs (null = still to choose). Survives a screen redraw. */
        private var sessionMode: String? = null
    }
}
