package com.phonecam

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.TotalCaptureResult
import android.hardware.camera2.params.OutputConfiguration
import android.hardware.camera2.params.SessionConfiguration
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.net.LocalServerSocket
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.SystemClock
import android.system.Os
import android.system.OsConstants
import android.util.Log
import android.util.Range
import android.view.Surface
import java.io.Closeable
import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.security.SecureRandom
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Captures the camera with Camera2, hardware-encodes it with MediaCodec and serves a framed
 * stream on the abstract unix socket "phonecam" (reach it from the PC with
 * `adb forward tcp:5555 localabstract:phonecam`).
 *
 * Wire format, big-endian. Every packet from the phone starts with a 28-byte header:
 *   u32 magic "PCM1" | u8 type | u8 flags | u16 reserved | u32 payload length | i64 ptsUs | i64 sendUs
 * type 1 = codec config (SPS/PPS), type 2 = frame (flags bit0 = keyframe), type 3 = clock reply,
 * type 4 = current settings (ASCII "cam=0;zoom=1.00;w=1920;h=1080;fps=60;mbps=20", sent on connect and after changes).
 * ptsUs is the camera capture timestamp and sendUs the moment the encoder output was ready, both on
 * the phone clock returned by nowUs(). For a clock reply, ptsUs is the phone clock at receipt and
 * sendUs echoes the token from the request.
 * The PC may send 9-byte clock requests: 'T' + i64 token, and setting changes: 'S' + u16 length + the same
 * key=value text (any subset of the keys); they apply while streaming, see applyLive().
 *
 * With the tcp=<port> extra the same stream is also served over TCP (Wi-Fi or USB tethering).
 *
 * Start parameters come from intent extras: cam (String), w, h, fps, mbps (Int), codec ("h264"|"hevc").
 */
class CameraStreamService : Service() {

    private var cameraThread: HandlerThread? = null
    private var cameraHandler: Handler? = null
    private var cameraDevice: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var codec: MediaCodec? = null
    private var inputSurface: Surface? = null
    private var server: LocalServerSocket? = null
    private var encoderThread: Thread? = null
    private var acceptThread: Thread? = null

    @Volatile private var running = false
    private var tcpServer: ServerSocket? = null
    private var tcpAcceptThread: Thread? = null
    @Volatile private var discoverySocket: DatagramSocket? = null

    private class Client(val input: InputStream, val output: OutputStream, private val socket: Closeable, val label: String) {
        fun close() {
            try { socket.close() } catch (_: IOException) {}
        }
    }

    @Volatile private var client: Client? = null
    @Volatile private var configBytes: ByteArray? = null
    private val writeLock = Any()

    private var cameraId = "0"
    private var isp = ""
    private var requestTemplate = "record"
    private var streamUseCase = ""
    private var exposureUs = 0
    // Picture controls (the PC app's Picture panel), applied to the running session and kept in prefs.
    private var ev = 0                 // exposure compensation in camera steps (evStep EV each)
    private var focus = "auto"         // auto | lock
    private var wb = "auto"            // see WHITE_BALANCE
    private var flicker = "auto"       // see ANTI_FLICKER
    private var stab = 0               // 1 = video stabilization (crops a little, can add latency)
    private var mirror = 0             // 1 = the PC flips the picture left-right (the phone only remembers and reports it)
    private var orient = "auto"        // auto = picture follows how the phone is held; manual = turned by buttons
    private var manualRot = 0          // manual mode: the turn the user chose (degrees clockwise)
    private var rot = 0                // degrees clockwise the PC turns the picture to keep it upright
    // how the phone is held, degrees clockwise from upright portrait (OrientationEventListener); 270 = the
    // landscape position the camera's frames are already upright in (back camera), so that is the start value
    @Volatile private var deviceOrientation = 270
    // where the sensor last said the phone is (any position); deviceOrientation is the one in use under "orient"
    @Volatile private var sensedOrientation = 270
    private var sensorOrientation = 90
    private var frontFacing = false
    private var evRange: Range<Int>? = null
    private var evStep = 0f
    @Volatile private var lastFocusDistance: Float? = null
    private var lockedFocusDistance: Float? = null
    private var iso = 800
    private var zoomRatio = 1f
    private var zoomRange: Range<Float>? = null
    private var fps = 60
    private var width = 1920
    private var height = 1080
    private var mbps = 20
    private var hevc = false
    private var tcpPort = 0
    private var linkMode = "" // "tether" (USB connection), "wifi" or "" (USB debugging), chosen on the phone or sent by the PC app
    /** Encoder tuning knobs from the start extras (lat, prio, oprate, ...), reused when the encoder is rebuilt. */
    private var tuning = Bundle()
    private var reopenCount = 0
    private var wifiLocks: List<WifiManager.WifiLock> = emptyList()
    /** Bumped whenever the camera is closed on purpose, so callbacks from the old device/session are ignored. */
    private var cameraGeneration = 0
    private var rebuildEncoderOnReopen = false
    private val reopen = Runnable {
        if (rebuildEncoderOnReopen && running) {
            rebuildEncoderOnReopen = false
            Log.w(TAG, "rebuilding the encoder after a camera error")
            stopEncoder()
            client?.close(); client = null // the PC reconnects and starts from the new encoder's keyframe
            startEncoder()
        }
        openCamera()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // the microphone type may only be declared when the permission is already granted (Settings, Microphone asks for it)
        val serviceType = ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA or
            (if (micAllowed()) ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE else 0)
        if (intent?.action == ACTION_STOP) { // the Stop button in the notification
            stopSelf()
            return START_NOT_STICKY
        }
        startForeground(NOTIFICATION_ID, buildNotification(), serviceType)
        val extras = intent?.extras ?: Bundle.EMPTY
        // already streaming on the same transport: change the settings in place, keeping the connection
        if (running && extras.getInt("tcp", 0) == tcpPort) {
            requestApply(extras)
            return START_NOT_STICKY
        }
        if (running) stopStreaming()
        try {
            startStreaming(intent?.extras ?: Bundle.EMPTY)
        } catch (e: Exception) {
            // never take the whole app down: show the problem and leave the service stopped
            Log.e(TAG, "could not start streaming: $e")
            stopStreaming()
            status = "Could not start streaming: ${e.message}"
            stopSelf()
        }
        return START_NOT_STICKY
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
    }

    override fun onDestroy() {
        instance = null
        stopStreaming()
        super.onDestroy()
    }

    private fun buildNotification(): Notification {
        val mgr = getSystemService(NotificationManager::class.java)
        mgr.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Streamsthetic streaming", NotificationManager.IMPORTANCE_LOW)
        )
        val open = android.app.PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            android.app.PendingIntent.FLAG_IMMUTABLE)
        val stop = android.app.PendingIntent.getService(this, 1, Intent(this, CameraStreamService::class.java).setAction(ACTION_STOP),
            android.app.PendingIntent.FLAG_IMMUTABLE)
        // always visible in the shade: a reminder that the camera is on, with a Stop button right there
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Streamsthetic is streaming")
            .setContentText("The camera is on. Tap Stop when you are done.")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .setOngoing(true)
            .build()
    }

    // ---- turns itself off when nobody uses it (Settings, Power): no PC connected, or no app using the camera ----

    @Volatile private var appIdle = false          // the PC says: no app has the camera open
    private var idleSinceMs = SystemClock.elapsedRealtime()
    private val idleWatch = object : Runnable {
        override fun run() {
            if (!running) return
            val minutes = getSharedPreferences("settings", MODE_PRIVATE).getInt("AutoOffMinutes", 30)
            val idle = client == null || appIdle
            val now = SystemClock.elapsedRealtime()
            if (!idle) idleSinceMs = now
            else if (minutes > 0 && now - idleSinceMs >= minutes * 60_000L) {
                Log.i(TAG, "idle for $minutes minutes: turning off")
                notifyAutoOff(minutes)
                stopSelf()
                return
            }
            mainHandler.postDelayed(this, 30_000)
        }
    }

    private fun notifyAutoOff(minutes: Int) {
        val mgr = getSystemService(NotificationManager::class.java)
        mgr.createNotificationChannel(NotificationChannel("info", "Streamsthetic messages", NotificationManager.IMPORTANCE_DEFAULT))
        val open = android.app.PendingIntent.getActivity(this, 2, Intent(this, MainActivity::class.java), android.app.PendingIntent.FLAG_IMMUTABLE)
        mgr.notify(2, Notification.Builder(this, "info")
            .setContentTitle("Streaming stopped automatically")
            .setContentText("The camera was idle for $minutes ${if (minutes == 1) "minute" else "minutes"}, so it was switched off to save battery. Tap to open.")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentIntent(open)
            .setAutoCancel(true)
            .build())
    }

    /**
     * Phone clock in microseconds, CLOCK_MONOTONIC. Measured on the S24 Ultra: encoder output
     * presentationTimeUs is on this clock even though the camera reports timestampSource REALTIME,
     * so elapsedRealtime (CLOCK_BOOTTIME) was off by the time the phone had spent suspended.
     */
    private fun nowUs(): Long = System.nanoTime() / 1000

    // ---- phone microphone (roadmap "Other"): 48 kHz mono 16-bit PCM in 20 ms packets (TYPE_AUDIO), played by the receiver ----

    private fun micAllowed() = checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED
    private fun micWanted() = micAllowed() && getSharedPreferences("settings", MODE_PRIVATE).getBoolean("Mic", false)

    private var micThread: Thread? = null

    /** Sends the microphone while the stream runs and "Microphone" is on (checked every packet, so the switch works live). */
    @android.annotation.SuppressLint("MissingPermission") // micWanted() checks the permission before every start
    private fun startMicThread() {
        if (micThread?.isAlive == true) return
        micThread = Thread({
            val rate = 48000
            val chunk = rate / 50 // 20 ms
            var record: android.media.AudioRecord? = null
            val buffer = ByteArray(chunk * 2)
            try {
                while (running) {
                    if (!micWanted()) {
                        record?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
                        record = null
                        Thread.sleep(300)
                        continue
                    }
                    if (record == null) {
                        val min = android.media.AudioRecord.getMinBufferSize(rate, android.media.AudioFormat.CHANNEL_IN_MONO, android.media.AudioFormat.ENCODING_PCM_16BIT)
                        record = android.media.AudioRecord(android.media.MediaRecorder.AudioSource.MIC, rate,
                            android.media.AudioFormat.CHANNEL_IN_MONO, android.media.AudioFormat.ENCODING_PCM_16BIT, maxOf(min, chunk * 8))
                        if (record.state != android.media.AudioRecord.STATE_INITIALIZED) {
                            Log.w(TAG, "microphone could not start")
                            record.release(); record = null
                            Thread.sleep(2000)
                            continue
                        }
                        record.startRecording()
                        Log.i(TAG, "microphone on")
                    }
                    var got = 0
                    while (got < buffer.size && running) {
                        val n = record.read(buffer, got, buffer.size - got)
                        if (n <= 0) break
                        got += n
                    }
                    if (got == buffer.size) send(TYPE_AUDIO, 0, nowUs(), nowUs(), buffer.copyOf())
                }
            } catch (e: Exception) {
                Log.w(TAG, "microphone stopped: $e")
            } finally {
                record?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
            }
        }, "microphone").also { it.isDaemon = true; it.start() }
    }

    private fun startStreaming(p: Bundle) {
        cameraId = p.getString("cam", "0")
        width = p.getInt("w", 3840)
        height = p.getInt("h", 2160)
        fps = p.getInt("fps", 60)
        mbps = p.getInt("mbps", 40)
        hevc = p.getString("codec", "h264") == "hevc"
        tuning = Bundle(p)

        Log.i(TAG, "start cam=$cameraId ${width}x$height@$fps ${mbps}Mbps ${if (hevc) "HEVC" else "H.264"}")

        running = true
        reopenCount = 0
        appIdle = false
        idleSinceMs = SystemClock.elapsedRealtime()
        mainHandler.removeCallbacks(idleWatch)
        mainHandler.postDelayed(idleWatch, 30_000)
        startMicThread()

        cameraThread = HandlerThread("camera").also { it.start() }
        cameraHandler = Handler(cameraThread!!.looper)

        startEncoder()
        loadPictureControls()
        // the phone screen's Orientation choice comes with Start
        p.getString("orient")?.takeIf { it in ORIENTATIONS && it != orient }?.let { orient = it; savePictureControls() }
        // A transport restart can resume auto mode after the sensor tracked turns in manual mode.
        if (orient == "auto") deviceOrientation = sensedOrientation
        isp = p.getString("isp", "")
        requestTemplate = p.getString("tmpl", "record")
        streamUseCase = p.getString("usecase", "")
        exposureUs = p.getInt("exp_us", 0)
        iso = p.getInt("iso", 800)
        // zoom=<ratio> on the back camera switches lenses: 0.6 ultra-wide, 1 main, 3 and 5 telephoto (S24 Ultra)
        zoomRatio = p.getFloat("zoom", 1f)

        server = bindLocalServer()
        acceptThread = Thread({ acceptLoop() }, "accept").also { it.start() }
        // tcp=<port> also listens on the network (Wi-Fi, USB tethering). Clients must send the pairing
        // code shown on the phone, and connections arriving over mobile data are refused.
        tcpPort = p.getInt("tcp", 0)
        linkMode = p.getString("link", "") ?: ""
        if (tcpPort > 0) {
            pairingCode = persistentPairingCode()
            tcpServer = ServerSocket(tcpPort)
            val addresses = localLinkAddresses()
            addresses.forEach { Log.i(TAG, "tcp listening on $it port $tcpPort") }
            Log.i(TAG, "pairing code $pairingCode")
            networkInfo = "Wi-Fi/tether: ${addresses.joinToString { it.substringBefore(' ') }} port $tcpPort, code $pairingCode"
            tcpAcceptThread = Thread({ tcpAcceptLoop() }, "tcp-accept").also { it.start() }
            Thread({ discoveryLoop(tcpPort) }, "discovery").start()
            acquireWifiLocks()
            if (linkMode == "tether") watchUsbCable() // the USB connection ends when the cable comes out
        } else {
            networkInfo = "USB only"
            watchUsbCable()
        }
        publishSettings()
        mainHandler.post { watchOrientation() }
        watchHeat()
        openCamera()
    }

    /**
     * The code is made once and kept, so a PC that paired before reconnects by itself after the stream or the
     * phone restarts. "New code" in a later version would simply delete the stored value.
     */
    private fun persistentPairingCode(): String = storedPairingCode(this)

    /**
     * Wi-Fi power save batches packets, which showed up as transport stalls of 100-260 ms over Wi-Fi (p99 116 ms at
     * 4K60 60 Mbps) and froze the picture. Same approach as Moonlight's Android client: a low-latency lock (active
     * while the app is visible) plus a high-performance lock (screen off / app in background).
     */
    private fun acquireWifiLocks() {
        val wifi = applicationContext.getSystemService(WifiManager::class.java) ?: return
        wifiLocks = listOf(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, @Suppress("DEPRECATION") WifiManager.WIFI_MODE_FULL_HIGH_PERF)
            .map { mode -> wifi.createWifiLock(mode, "PhoneCam:stream").apply { setReferenceCounted(false); acquire() } }
        Log.i(TAG, "wifi locks held: low latency + high performance")
    }

    private fun releaseWifiLocks() {
        wifiLocks.forEach { if (it.isHeld) it.release() }
        wifiLocks = emptyList()
    }

    private val mainHandler = Handler(android.os.Looper.getMainLooper())
    private var usbReceiverRegistered = false
    private var usbSeen = false
    private val stopForUnplug = Runnable {
        if (!running || (tcpPort != 0 && linkMode != "tether")) return@Runnable
        Log.i(TAG, "USB cable unplugged (${if (tcpPort != 0) "USB connection" else "USB debugging"}): stopping")
        stopStreaming()
        status = "Stopped: the USB cable was unplugged"
        stopSelf()
    }

    /**
     * USB-only mode streams nowhere without the cable, so unplugging stops it (frees the camera, saves battery).
     * A 2 s grace period ignores a wiggled cable. USB_STATE is the system's sticky USB broadcast ("connected").
     */
    private val usbState = object : android.content.BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            mainHandler.removeCallbacks(stopForUnplug)
            val connected = intent.getBooleanExtra("connected", true)
            if (connected) usbSeen = true
            // the USB connection may be started before the cable is plugged in: only a cable that was seen and then pulled stops it
            if (!connected && (tcpPort == 0 || usbSeen)) mainHandler.postDelayed(stopForUnplug, 2000)
        }
    }

    private fun watchUsbCable() {
        usbSeen = false
        val filter = android.content.IntentFilter("android.hardware.usb.action.USB_STATE")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) registerReceiver(usbState, filter, RECEIVER_NOT_EXPORTED)
        else registerReceiver(usbState, filter)
        usbReceiverRegistered = true
    }

    /** Thread-safe entry for setting changes from the phone UI or the PC; applied in order on the camera thread. */
    fun requestApply(p: Bundle) {
        val copy = Bundle(p)
        cameraHandler?.post { applyLive(copy) }
    }

    /**
     * Applies new settings without stopping the service: zoom on the running session (instant), another camera
     * by reopening only the camera, bitrate on the running encoder, and size/frame rate by rebuilding the
     * encoder. Unknown keys are ignored and unsupported values refused, so a PC can only pick valid settings.
     */
    private fun applyLive(p: Bundle) {
        if (!running) return
        val newCam = p.getString("cam", cameraId)
        val newZoom = if (p.containsKey("zoom")) p.getFloat("zoom") else zoomRatio
        val newW = p.getInt("w", width)
        val newH = p.getInt("h", height)
        val newFps = p.getInt("fps", fps)
        val newMbps = p.getInt("mbps", mbps)
        val newEv = p.getInt("ev", ev)
        var newFocus = p.getString("focus", focus)
        val newWb = p.getString("wb", wb)
        val newFlicker = p.getString("flicker", flicker)
        val newStab = p.getInt("stab", stab)
        // auto-rotate on/off and manual turns: only a different turn, no camera work (the PC turns the picture)
        val newOrient = p.getString("orient", orient)
        val newManualRot = p.getInt("mrot", if (newOrient == "manual" && orient != "manual") rot else manualRot)
        if (newOrient != orient || newManualRot != manualRot) {
            if (newOrient !in ORIENTATIONS || newManualRot !in setOf(0, 90, 180, 270)) {
                Log.w(TAG, "refused orient $newOrient mrot $newManualRot")
                publishSettings()
            } else {
                // switching to manual keeps the picture where it is (manual starts from the current turn);
                // back to auto follows where the phone really is
                if (newOrient == "auto") deviceOrientation = sensedOrientation
                orient = newOrient
                manualRot = newManualRot
                savePictureControls()
                updateRotation(if (orient == "auto") "auto-rotate on" else "turned by hand", always = true)
            }
            if (p.keySet().all { it == "orient" || it == "mrot" }) return
        }
        // mirror: only a flag for the PC (it flips the picture while scaling); no camera work
        val newMirror = p.getInt("mirror", mirror)
        if (newMirror != mirror) {
            if (newMirror in 0..1) { mirror = newMirror; savePictureControls() } else Log.w(TAG, "refused mirror $newMirror")
            publishSettings()
            if (p.keySet().all { it == "mirror" }) return
        }
        val mgr = getSystemService(Context.CAMERA_SERVICE) as CameraManager
        if (newCam !in mgr.cameraIdList || "${newW}x$newH" !in SIZES || newFps !in FRAME_RATES || newMbps !in 2..100 ||
            newZoom !in 0.5f..10f || newEv !in -24..24 || newFocus !in setOf("auto", "lock") || newWb !in WHITE_BALANCE ||
            newFlicker !in ANTI_FLICKER || newStab !in 0..1) {
            Log.w(TAG, "refused settings cam=$newCam ${newW}x$newH@$newFps ${newMbps}Mbps zoom $newZoom ev $newEv " +
                "focus $newFocus wb $newWb flicker $newFlicker stab $newStab")
            publishSettings()
            return
        }
        val camChanged = newCam != cameraId
        if (newCam != cameraId) newFocus = "auto" // a locked distance belongs to the old lens
        val zoomChanged = newZoom != zoomRatio
        // Stabilization is a session-level setting. Changing it on the running session, or even rebuilding just the
        // camera session, hung Samsung's camera HAL on the S24 Ultra ("Can't idle device", fatal error 4, ~9 s stall).
        // So it takes the same full restart as a new resolution: camera closed, encoder rebuilt, viewer reconnects.
        val encoderChanged = newW != width || newH != height || newFps != fps || newStab != stab
        val bitrateChanged = newMbps != mbps
        val pictureChanged = newEv != ev || newFocus != focus || newWb != wb || newFlicker != flicker || newStab != stab
        if (!camChanged && !zoomChanged && !encoderChanged && !bitrateChanged && !pictureChanged) {
            publishSettings()
            return
        }
        Log.i(TAG, "apply live cam=$newCam zoom $newZoom ${newW}x$newH@$newFps ${newMbps}Mbps ev $newEv focus $newFocus " +
            "wb $newWb flicker $newFlicker stab $newStab")
        if (newFocus == "lock" && focus != "lock") {
            // lock where autofocus is right now
            lockedFocusDistance = lastFocusDistance
            if (lockedFocusDistance == null) newFocus = "auto"
        }
        cameraId = newCam
        zoomRatio = newZoom
        width = newW; height = newH; fps = newFps; mbps = newMbps
        ev = newEv; focus = newFocus; wb = newWb; flicker = newFlicker; stab = newStab
        if (pictureChanged) savePictureControls()
        // publish before the slow part (encoder rebuild ~0.5 s) so the phone UI never shows the old values
        publishSettings()
        when {
            encoderChanged -> {
                // A new size needs a new encoder and a new decoder on the PC: close the camera session, rebuild
                // the encoder and drop the viewer, which reconnects straight away and starts from the new keyframe.
                closeCamera()
                stopEncoder()
                client?.close(); client = null
                startEncoder()
                openCamera()
            }
            camChanged -> {
                closeCamera()
                openCamera()
                if (bitrateChanged) setBitrate()
            }
            else -> {
                if (bitrateChanged) setBitrate()
                if (zoomChanged || pictureChanged) updateRepeatingRequest()
            }
        }
    }

    private fun savePictureControls() {
        getSharedPreferences("settings", MODE_PRIVATE).edit()
            .putInt("ctl_ev", ev).putString("ctl_wb", wb).putString("ctl_flicker", flicker).putInt("ctl_stab", stab)
            .putString("ctl_orient", orient).putInt("ctl_mrot", manualRot).putInt("ctl_mirror", mirror)
            .apply() // focus lock is not kept: the distance only makes sense for the scene it was taken in
    }

    // --- staying upright -------------------------------------------------------------------------------------
    // The camera's frames are fixed to the sensor, so only one sideways position looked right. The phone's
    // orientation sensor tells how it is held; the PC turns the picture by "rot" degrees clockwise (its video
    // processor does it while scaling, no extra work or delay here). "orient": auto = follows the phone;
    // manual = the user turns the picture with buttons ("mrot") and moving the phone changes nothing.

    /** Turn (clockwise) that makes this camera's frames upright with the phone held at [device] degrees. */
    private fun rotationFor(device: Int): Int =
        if (frontFacing) (sensorOrientation - device + 360) % 360 else (sensorOrientation + device) % 360

    private var orientationListener: android.view.OrientationEventListener? = null
    private var orientationDebouncer: OrientationDebouncer? = null

    private fun watchOrientation() {
        if (!running) return
        orientationListener?.disable()
        orientationDebouncer?.stop()
        val debouncer = OrientationDebouncer(sensedOrientation, object : OrientationDebouncer.Scheduler {
            override fun schedule(action: Runnable, delayMillis: Long) { mainHandler.postDelayed(action, delayMillis) }
            override fun cancel(action: Runnable) { mainHandler.removeCallbacks(action) }
        }) { snapped ->
            if (!running) return@OrientationDebouncer
            sensedOrientation = snapped // still tracked in manual mode, ready when auto-rotate is enabled again
            cameraHandler?.post {
                if (running && orient == "auto") {
                    deviceOrientation = snapped
                    updateRotation("phone turned to $snapped")
                }
            }
        }
        orientationDebouncer = debouncer
        orientationListener = object : android.view.OrientationEventListener(this) {
            override fun onOrientationChanged(degrees: Int) {
                debouncer.onReading(degrees)
            }
        }.also { if (it.canDetectOrientation()) it.enable() else Log.w(TAG, "no orientation sensor: picture stays as the camera sees it") }
    }

    private fun updateRotation(why: String, always: Boolean = false) {
        val newRot = if (orient == "manual") manualRot else rotationFor(deviceOrientation)
        if (newRot == rot && !always) return
        rot = newRot
        Log.i(TAG, "upright: $why, picture turned $rot degrees (orient $orient)")
        publishSettings()
    }

    private fun loadPictureControls() {
        val prefs = getSharedPreferences("settings", MODE_PRIVATE)
        ev = prefs.getInt("ctl_ev", 0).coerceIn(-24, 24)
        wb = prefs.getString("ctl_wb", "auto")!!.takeIf { it in WHITE_BALANCE } ?: "auto"
        flicker = prefs.getString("ctl_flicker", "auto")!!.takeIf { it in ANTI_FLICKER } ?: "auto"
        stab = prefs.getInt("ctl_stab", 0).coerceIn(0, 1)
        mirror = prefs.getInt("ctl_mirror", 0).coerceIn(0, 1)
        orient = prefs.getString("ctl_orient", "auto")!!.takeIf { it in ORIENTATIONS } ?: "auto"
        manualRot = prefs.getInt("ctl_mrot", 0).takeIf { it in setOf(0, 90, 180, 270) } ?: 0
        if (orient == "manual") rot = manualRot
        focus = "auto"
    }

    private fun setBitrate() {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, mbps * 1_000_000) })
        } catch (e: IllegalStateException) {
            Log.w(TAG, "bitrate change failed: $e")
        }
    }

    // ---- heat: when Android reports the phone as warm, lower the quality by itself (found: a phone streaming 4K60 at 60 Mbps over a weak link got hot) ----
    private var thermalListener: Any? = null
    private var lastHeatStepMs = 0L
    @Volatile private var heatStatus = 0

    private fun watchHeat() {
        if (Build.VERSION.SDK_INT < 29) return
        val pm = getSystemService(android.os.PowerManager::class.java)
        val listener = android.os.PowerManager.OnThermalStatusChangedListener { s -> mainHandler.post { onHeat(s) } }
        pm.addThermalStatusListener(listener)
        thermalListener = listener
        heatStatus = pm.currentThermalStatus
        heatNote = ""
    }

    private fun unwatchHeat() {
        mainHandler.removeCallbacks(heatRestore)
        heatOriginal = null
        if (Build.VERSION.SDK_INT >= 29) (thermalListener as? android.os.PowerManager.OnThermalStatusChangedListener)?.let {
            getSystemService(android.os.PowerManager::class.java).removeThermalStatusListener(it)
        }
        thermalListener = null
        heatNote = ""
    }

    private fun heatAutoOn() = getSharedPreferences("settings", MODE_PRIVATE).getBoolean("HeatAuto", true)

    private var heatOriginal: Bundle? = null // what was streaming before the first cooling step
    private val heatRestore = Runnable {
        val o = heatOriginal ?: return@Runnable
        if (!running || heatStatus >= 2) return@Runnable
        heatOriginal = null
        heatNote = ""
        Log.i(TAG, "heat is gone: back to the settings before the cooling")
        requestApply(o)
    }

    /**
     * Thermal status: 0 none, 1 light, 2 moderate, 3 severe, 4+ critical. From moderate on: one step cooler at most every 30 s
     * (detail, size, frame rate); severe: straight to 1080p 30. When the phone has been cool for two minutes the first settings come
     * back. Switch: Settings > Power on the phone, or the PC app (Settings > App).
     */
    private fun onHeat(statusNow: Int) {
        heatStatus = statusNow
        if (running) publishSettings() // the PC app shows the heat state too
        mainHandler.removeCallbacks(heatRestore)
        if (!running) return
        if (!heatAutoOn()) { heatNote = if (statusNow >= 2) "The phone is warm. Automatic cooling is switched off in Settings." else ""; return }
        if (statusNow < 2) {
            if (heatOriginal != null) mainHandler.postDelayed(heatRestore, 120_000) // calm for two minutes, then back up (never in a hurry: it must not swing)
            return
        }
        val now = SystemClock.elapsedRealtime()
        if (now - lastHeatStepMs < 30_000 && statusNow < 3) return // severe heat is never made to wait
        lastHeatStepMs = now
        if (heatOriginal == null) heatOriginal = Bundle().apply { putInt("w", width); putInt("h", height); putInt("fps", fps); putInt("mbps", mbps) }
        val p = Bundle()
        val what: String
        if (statusNow >= 3) { val n = minOf(mbps, 20); p.putInt("w", 1920); p.putInt("h", 1080); p.putInt("fps", 30); p.putInt("mbps", n); what = "1080p, 30 fps and $n Mbps" }
        else if (mbps > 20) { val n = if (mbps > 40) 40 else 20; p.putInt("mbps", n); what = "$n Mbps" }
        else if (height > 1080) { p.putInt("w", 1920); p.putInt("h", 1080); what = "1080p" }
        else if (fps > 30) { p.putInt("fps", 30); what = "30 fps" }
        else { heatNote = "The phone is warm. Stop streaming and let it cool down."; return }
        heatNote = "Video settings reduced to $what because the phone is warm. Your settings will be restored after it cools down."
        Log.i(TAG, "heat status $statusNow: lowering to $what")
        requestApply(p)
    }

    private fun settingsText() =
        "cam=$cameraId;zoom=${"%.2f".format(java.util.Locale.ROOT, zoomRatio)};w=$width;h=$height;fps=$fps;mbps=$mbps" +
            ";ev=$ev;evmin=${evRange?.lower ?: 0};evmax=${evRange?.upper ?: 0};evstep=${"%.3f".format(java.util.Locale.ROOT, evStep)}" +
            ";focus=$focus;wb=$wb;flicker=$flicker;stab=$stab;mirror=$mirror;orient=$orient;rot=$rot;heat=$heatStatus;heatauto=${if (heatAutoOn()) 1 else 0}" +
            // zoom range of the open camera (back camera 0.6-10x moves between lenses by itself; front is digital)
            ";zmin=${"%.2f".format(java.util.Locale.ROOT, zoomRange?.lower ?: 1f)};zmax=${"%.2f".format(java.util.Locale.ROOT, zoomRange?.upper ?: 1f)}"

    /** Tells the phone UI and the connected PC what is streaming now. */
    private fun publishSettings() {
        currentSettings = Bundle().apply {
            putString("cam", cameraId); putFloat("zoom", zoomRatio)
            putInt("w", width); putInt("h", height); putInt("fps", fps); putInt("mbps", mbps)
            putString("orient", orient); putInt("rot", rot)
        }
        status = "Streaming ${width}x$height @ $fps fps, $mbps Mbps"
        send(TYPE_SETTINGS, 0, 0, nowUs(), settingsText().toByteArray(Charsets.US_ASCII))
    }

    private fun startEncoder() {
        val p = tuning
        // Tuning knobs, all optional launch extras so one build can run every experiment:
        //   lat=0|1 (KEY_LATENCY), prio=0|1, oprate=<fps hint>, br=cbr|vbr, profile=base|main|high,
        //   bframes=<n>, qlat=1 (Qualcomm low-latency vendor key), ifi=<seconds>
        val mime = if (hevc) MediaFormat.MIMETYPE_VIDEO_HEVC else MediaFormat.MIMETYPE_VIDEO_AVC
        val format = MediaFormat.createVideoFormat(mime, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, mbps * 1_000_000)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, p.getInt("ifi", 1))
            setInteger(MediaFormat.KEY_BITRATE_MODE,
                if (p.getString("br", "cbr") == "vbr") MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR
                else MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR)
            setInteger(MediaFormat.KEY_PRIORITY, p.getInt("prio", 0))
            setInteger(MediaFormat.KEY_LATENCY, p.getInt("lat", 1))
            // every keyframe carries SPS/PPS so a client can join mid-stream
            setInteger(MediaFormat.KEY_PREPEND_HEADER_TO_SYNC_FRAMES, 1)
            // 120 measured ~3.4 ms lower capture->encoded at 1080p60 on the S24 Ultra (3 rounds each); 0 disables
            p.getInt("oprate", 120).takeIf { it > 0 }?.let { setInteger(MediaFormat.KEY_OPERATING_RATE, it) }
            if (p.containsKey("bframes")) setInteger(MediaFormat.KEY_MAX_B_FRAMES, p.getInt("bframes"))
            if (!hevc) when (p.getString("profile", "")) {
                "base" -> setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline)
                "main" -> setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileMain)
                "high" -> setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileHigh)
            }
            if (p.getInt("qlat", 0) == 1) setInteger("vendor.qti-ext-enc-low-latency.enable", 1)
        }
        val enc = MediaCodec.createEncoderByType(mime)
        enc.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        Log.i(TAG, "encoder ${enc.name} in=${enc.inputFormat}")
        inputSurface = enc.createInputSurface()
        enc.start()
        configBytes = null
        codec = enc
        encoderThread = Thread({ drainLoop(enc) }, "encoder").also { it.start() }
    }

    private fun stopEncoder() {
        val enc = codec ?: return
        codec = null
        try { enc.stop() } catch (_: Exception) {}
        encoderThread?.join(500)
        encoderThread = null
        try { enc.release() } catch (_: Exception) {}
        inputSurface?.release(); inputSurface = null
        configBytes = null
    }

    private fun acceptLoop() {
        while (running) {
            try {
                val s = server?.accept() ?: return
                attachClient(Client(s.inputStream, s.outputStream, s, "adb"))
            } catch (e: IOException) {
                if (running) Log.w(TAG, "accept failed: $e")
                return
            }
        }
    }

    private fun tcpAcceptLoop() {
        while (running) {
            val s = try {
                tcpServer?.accept() ?: return
            } catch (e: IOException) {
                if (running) Log.w(TAG, "tcp accept failed: $e")
                return
            }
            // vet each connection on its own thread so a slow client cannot block new ones
            Thread({ vetTcpClient(s) }, "tcp-vet").start()
        }
    }

    private val wrongTries = ArrayList<Long>()
    @Volatile private var lockedUntilMs = 0L

    /** A PCM1 packet of type 6 (no payload): "the pairing code was refused". The PC shows a clear message. */
    private fun sendReject(s: Socket) {
        try {
            val buf = ByteBuffer.allocate(HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
                .putInt(MAGIC).put(TYPE_REJECT.toByte()).put(0).putShort(0).putInt(0).putLong(0).putLong(0)
            s.getOutputStream().write(buf.array())
            s.getOutputStream().flush()
        } catch (_: IOException) { /* the PC already left */ }
    }

    /** Accepts only local-link connections (Wi-Fi, USB tethering) that send the pairing code first. */
    private fun vetTcpClient(s: Socket) {
        try {
            val iface = NetworkInterface.getByInetAddress(s.localAddress)?.name ?: ""
            if (isMobileDataInterface(iface)) {
                Log.w(TAG, "rejected tcp ${s.inetAddress.hostAddress}: arrived on mobile data ($iface)")
                s.close()
                return
            }
            s.soTimeout = 5000
            val input = DataInputStream(s.getInputStream())
            val hello = ByteArray(7)
            input.readFully(hello)
            val code = pairingCode
            val now = SystemClock.elapsedRealtime()
            // too many wrong codes in a row: ignore every try for a short while (a 6-digit code must not be guessable by trying them all)
            val lockedOut = now < lockedUntilMs
            // a USB cable is a private point-to-point link: whoever is plugged in needs no code. Wi-Fi always does.
            val cable = isCableInterface(iface) && hello[0] == 'P'.code.toByte()
            if (!cable && (lockedOut || code == null || hello[0] != 'P'.code.toByte() || String(hello, 1, 6, Charsets.US_ASCII) != code)) {
                Log.w(TAG, "rejected tcp ${s.inetAddress.hostAddress}: wrong pairing code")
                if (!lockedOut) {
                    wrongTries.removeAll { now - it > 60_000 }
                    wrongTries.add(now)
                    if (wrongTries.size >= 5) { lockedUntilMs = now + 20_000; wrongTries.clear() }
                }
                sendReject(s) // tells the PC "that code was refused" instead of leaving it waiting
                s.close()
                return
            }
            wrongTries.clear()
            s.soTimeout = 0
            s.tcpNoDelay = true
            attachClient(Client(s.getInputStream(), s.getOutputStream(), s, "tcp ${s.inetAddress.hostAddress} ($iface)"))
        } catch (e: IOException) {
            Log.w(TAG, "tcp client dropped during pairing: $e")
            try { s.close() } catch (_: IOException) {}
        }
    }

    /**
     * Answers "PHONECAM?" queries broadcast by the PC on UDP 5557 with "PHONECAM1|<model>|<tcp port>", so the PC
     * finds the phone without typing an IP. The PC asks and the phone replies, so the PC needs no inbound firewall
     * rule. The reply reveals no secret: connecting still needs the pairing code.
     */
    private fun discoveryLoop(tcpPort: Int) {
        try {
            val socket = DatagramSocket(null).apply {
                reuseAddress = true
                broadcast = true
                bind(InetSocketAddress(DISCOVERY_PORT))
            }
            discoverySocket = socket
            // A random installation identity keeps two phones of the same model distinct, including after DHCP changes.
            val identityPrefs = getSharedPreferences("discovery", MODE_PRIVATE)
            val identity = identityPrefs.getString("installationId", null) ?: java.util.UUID.randomUUID().toString().also {
                identityPrefs.edit().putString("installationId", it).apply()
            }
            val model = Build.MODEL.replace('|', ' ').replace('\n', ' ').replace('\r', ' ').take(120)
            val buffer = ByteArray(64)
            while (running) {
                val query = DatagramPacket(buffer, buffer.size)
                socket.receive(query)
                val request = String(query.data, 0, query.length, Charsets.US_ASCII)
                val response = when (request) {
                    "PHONECAM2?" -> "PHONECAM2|$identity|$model|$tcpPort"
                    "PHONECAM?" -> "PHONECAM1|$model|$tcpPort"
                    else -> continue
                }
                val reply = response.toByteArray(Charsets.US_ASCII)
                socket.send(DatagramPacket(reply, reply.size, query.address, query.port))
                Log.i(TAG, "answered discovery from ${query.address.hostAddress}")
            }
        } catch (e: IOException) {
            if (running) Log.w(TAG, "discovery stopped: $e")
        }
    }

    /** The network the phone makes when USB tethering is on (Android names it rndis0, usb0 or ncm0). */
    private fun isCableInterface(name: String) = name.startsWith("rndis") || name.startsWith("usb") || name.startsWith("ncm")

    private fun isMobileDataInterface(name: String) =
        name.startsWith("rmnet") || name.startsWith("ccmni") || name.startsWith("v4-") || name.startsWith("dummy")

    /** Wi-Fi / tethering addresses a PC can reach, e.g. "192.168.0.112 (wlan0)". */
    private fun localLinkAddresses(): List<String> {
        val out = mutableListOf<String>()
        try {
            for (iface in NetworkInterface.getNetworkInterfaces()) {
                if (!iface.isUp || iface.isLoopback || isMobileDataInterface(iface.name)) continue
                for (addr in iface.inetAddresses) {
                    if (addr is Inet4Address) out += "${addr.hostAddress} (${iface.name})"
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "could not list network interfaces: $e")
        }
        return out
    }

    /** One viewer at a time; a new connection replaces the old one. */
    private fun attachClient(c: Client) {
        Log.i(TAG, "client connected (${c.label})")
        client?.close()
        client = c
        send(TYPE_SETTINGS, 0, 0, nowUs(), settingsText().toByteArray(Charsets.US_ASCII))
        configBytes?.let { send(TYPE_CONFIG, 0, 0, nowUs(), it) }
        requestKeyFrame()
        Thread({ clientReader(c) }, "client-reader").start()
    }

    /** The abstract socket can stay taken for a moment after a previous stream stopped; retry briefly. */
    private fun bindLocalServer(): LocalServerSocket {
        var last: IOException? = null
        repeat(10) {
            try {
                return LocalServerSocket(SOCKET_NAME)
            } catch (e: IOException) {
                last = e
                Thread.sleep(100)
            }
        }
        throw last ?: IOException("could not bind $SOCKET_NAME")
    }

    /**
     * Reads what the PC sends: clock requests ('T' + i64 token), answered so the PC can estimate the phone/PC
     * clock offset, and setting changes ('S' + u16 length + ASCII "key=value;key=value", keys cam, zoom, w, h,
     * fps, mbps). Only an attached client (adb, or TCP after the pairing code) gets here.
     */
    private fun clientReader(c: Client) {
        try {
            val input = DataInputStream(c.input)
            while (running && client === c) {
                when (input.readByte()) {
                    'T'.code.toByte() -> send(TYPE_CLOCK, 0, nowUs(), input.readLong(), null)
                    'S'.code.toByte() -> {
                        val length = input.readUnsignedShort()
                        if (length > 512) throw IOException("settings message too long ($length)")
                        val text = ByteArray(length).also { input.readFully(it) }.toString(Charsets.US_ASCII)
                        Log.i(TAG, "settings from PC (${c.label}): $text")
                        // "idle=1/0": whether any app is using the camera (not a camera setting)
                        Regex("(?:^|;)idle=([01])").find(text)?.let { appIdle = it.groupValues[1] == "1" }
                        // "heatauto=1/0": the PC app's switch for cooling down by itself (the same setting as on the phone)
                        Regex("(?:^|;)heatauto=([01])").find(text)?.let {
                            getSharedPreferences("settings", MODE_PRIVATE).edit().putBoolean("HeatAuto", it.groupValues[1] == "1").apply()
                            publishSettings()
                        }
                        val rest = text.split(';').filter { it.isNotEmpty() && !it.startsWith("idle=") && !it.startsWith("heatauto=") }.joinToString(";")
                        if (rest.isNotEmpty()) requestApply(parseSettings(rest))
                    }
                }
            }
        } catch (_: IOException) {
        }
    }

    private fun parseSettings(text: String): Bundle {
        val b = Bundle()
        for (pair in text.split(';')) {
            val key = pair.substringBefore('=', "").trim()
            val value = pair.substringAfter('=', "").trim()
            when (key) {
                "cam" -> b.putString("cam", value)
                "zoom" -> value.toFloatOrNull()?.let { b.putFloat("zoom", it) }
                "w", "h", "fps", "mbps", "ev", "stab", "mrot", "mirror" -> value.toIntOrNull()?.let { b.putInt(key, it) }
                "focus", "wb", "flicker", "orient" -> b.putString(key, value)
            }
        }
        return b
    }

    private fun requestKeyFrame() {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (e: IllegalStateException) {
            Log.w(TAG, "keyframe request failed: $e")
        }
    }

    private fun send(type: Int, flags: Int, ptsUs: Long, sendUs: Long, payload: ByteArray?) {
        val c = client ?: return
        val len = payload?.size ?: 0
        val buf = ByteBuffer.allocate(HEADER_SIZE + len).order(ByteOrder.BIG_ENDIAN)
            .putInt(MAGIC).put(type.toByte()).put(flags.toByte()).putShort(0)
            .putInt(len).putLong(ptsUs).putLong(sendUs)
        if (payload != null) buf.put(payload)
        synchronized(writeLock) {
            try {
                c.output.write(buf.array())
            } catch (e: IOException) {
                Log.i(TAG, "client dropped (${c.label}): $e")
                c.close()
                if (client === c) client = null
            }
        }
    }

    private fun drainLoop(enc: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        var frames = 0
        var bytes = 0L
        var encMsSum = 0.0
        var encMsMax = 0.0
        var clockChecked = false
        var lastLog = SystemClock.elapsedRealtime()
        while (running && codec === enc) {
            val idx = try {
                enc.dequeueOutputBuffer(info, 10_000)
            } catch (e: IllegalStateException) {
                return
            }
            if (idx < 0) continue
            val outUs = nowUs()
            val buf = enc.getOutputBuffer(idx)
            if (buf != null && info.size > 0) {
                val data = ByteArray(info.size)
                buf.position(info.offset)
                buf.get(data)
                if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                    configBytes = data
                    send(TYPE_CONFIG, 0, 0, outUs, data)
                } else {
                    frames++
                    bytes += data.size
                    val latencyMs = (outUs - info.presentationTimeUs) / 1000.0
                    if (!clockChecked) {
                        clockChecked = true
                        val boottimeMs = (SystemClock.elapsedRealtimeNanos() / 1000 - info.presentationTimeUs) / 1000.0
                        Log.i(TAG, "clock check: monotonic-pts=%.1f ms, boottime-pts=%.1f ms (first should be small and positive)"
                            .format(latencyMs, boottimeMs))
                    }
                    encMsSum += latencyMs
                    if (latencyMs > encMsMax) encMsMax = latencyMs
                    val key = if (info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0) 1 else 0
                    send(TYPE_FRAME, key, info.presentationTimeUs, outUs, data)
                }
            }
            enc.releaseOutputBuffer(idx, false)

            val now = SystemClock.elapsedRealtime()
            if (now - lastLog >= 1000) {
                val secs = (now - lastLog) / 1000.0
                Log.i(TAG, "encoder %.1f fps %.1f Mbps client=%s capture->encoded avg %.1f ms max %.1f ms".format(
                    frames / secs, bytes * 8 / secs / 1e6, client != null,
                    if (frames > 0) encMsSum / frames else 0.0, encMsMax))
                frames = 0; bytes = 0; encMsSum = 0.0; encMsMax = 0.0; lastLog = now
            }
        }
    }

    /** Closes the camera on purpose (settings change, stop); callbacks still in flight for it are ignored. */
    private fun closeCamera() {
        cameraGeneration++
        cameraHandler?.removeCallbacks(reopen)
        try { session?.close() } catch (_: Exception) {}
        try { cameraDevice?.close() } catch (_: Exception) {}
        session = null
        cameraDevice = null
    }

    private fun openCamera() {
        if (!running) return
        val mgr = getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val id = cameraId
        val generation = cameraGeneration
        try {
            mgr.openCamera(id, object : CameraDevice.StateCallback() {
                override fun onOpened(device: CameraDevice) {
                    if (generation != cameraGeneration || !running) {
                        device.close() // settings changed while this camera was opening
                        return
                    }
                    val chars = mgr.getCameraCharacteristics(id)
                    zoomRange = chars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE)
                    evRange = chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE)
                    evStep = chars.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_STEP)?.toFloat() ?: 0f
                    evRange?.let { ev = ev.coerceIn(it.lower, it.upper) }
                    sensorOrientation = chars.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 90
                    frontFacing = chars.get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_FRONT
                    rot = if (orient == "manual") manualRot else rotationFor(deviceOrientation)
                    publishSettings() // the PC learns this camera's brightness range and how to turn it upright
                    Log.i(TAG, "CAMERA OPENED id=$id zoom $zoomRatio (range $zoomRange)")
                    cameraDevice = device
                    createSession(device)
                }

                override fun onDisconnected(device: CameraDevice) {
                    device.close()
                    if (generation != cameraGeneration) return
                    Log.w(TAG, "CAMERA DISCONNECTED id=$id (evicted?)")
                    cameraDevice = null
                    scheduleReopen()
                }

                override fun onError(device: CameraDevice, error: Int) {
                    device.close()
                    if (generation != cameraGeneration) return
                    Log.e(TAG, "CAMERA ERROR id=$id code=$error")
                    cameraDevice = null
                    // a fatal camera error can leave the encoder's input surface broken (seen on the S24 Ultra: no
                    // frames and failing keyframe requests after the HAL hung), so rebuild the encoder on reopen too
                    if (error == ERROR_CAMERA_DEVICE || error == ERROR_CAMERA_SERVICE) rebuildEncoderOnReopen = true
                    scheduleReopen()
                }
            }, cameraHandler)
        } catch (e: SecurityException) {
            Log.e(TAG, "no camera permission: $e")
        } catch (e: Exception) {
            Log.e(TAG, "openCamera failed: $e")
            scheduleReopen()
        }
    }

    private fun scheduleReopen() {
        if (!running) return
        reopenCount++
        Log.w(TAG, "reopen attempt #$reopenCount in 1s")
        cameraHandler?.removeCallbacks(reopen)
        cameraHandler?.postDelayed(reopen, 1000)
    }

    /** The capture request for the current settings (zoom, exposure, frame rate) on the given device. */
    private fun buildRequest(device: CameraDevice, surface: Surface): CaptureRequest {
        val template = if (requestTemplate == "preview") CameraDevice.TEMPLATE_PREVIEW else CameraDevice.TEMPLATE_RECORD
        return device.createCaptureRequest(template).apply {
            addTarget(surface)
            set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(fps, fps))
            zoomRange?.let { set(CaptureRequest.CONTROL_ZOOM_RATIO, zoomRatio.coerceIn(it.lower, it.upper)) }
            // exp_us=<n> fixes exposure (AE off); iso=<n> sets the matching gain
            if (exposureUs > 0) {
                set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_OFF)
                set(CaptureRequest.SENSOR_EXPOSURE_TIME, exposureUs * 1000L)
                set(CaptureRequest.SENSOR_SENSITIVITY, iso)
                set(CaptureRequest.SENSOR_FRAME_DURATION, 1_000_000_000L / fps)
            }
            set(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
                if (stab == 1) CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_ON else CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
            // picture controls
            if (exposureUs == 0) set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, ev)
            set(CaptureRequest.CONTROL_AWB_MODE, WHITE_BALANCE.getValue(wb))
            set(CaptureRequest.CONTROL_AE_ANTIBANDING_MODE, ANTI_FLICKER.getValue(flicker))
            val lockAt = lockedFocusDistance
            if (focus == "lock" && lockAt != null) {
                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_OFF)
                set(CaptureRequest.LENS_FOCUS_DISTANCE, lockAt)
            } else {
                set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
            }
            // isp=fast|off trades image processing for latency (noise reduction + edge enhancement)
            when (isp) {
                "fast" -> {
                    set(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_FAST)
                    set(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_FAST)
                }
                "off" -> {
                    set(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_OFF)
                    set(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_OFF)
                }
            }
        }.build()
    }

    /** Zoom (lens) changes on the running session: no new session, the stream continues. */
    private fun updateRepeatingRequest() {
        val s = session ?: return
        val device = cameraDevice ?: return
        val surface = inputSurface ?: return
        try {
            s.setRepeatingRequest(buildRequest(device, surface), CameraStats(), cameraHandler)
        } catch (e: Exception) {
            Log.w(TAG, "zoom change failed: $e")
        }
    }

    private fun createSession(device: CameraDevice) {
        val surface = inputSurface ?: return
        val output = OutputConfiguration(surface)
        // usecase=preview|video_call|record tells the HAL what the stream is for (Android 13+)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val useCase = when (streamUseCase) {
                "preview" -> CameraMetadata.SCALER_AVAILABLE_STREAM_USE_CASES_PREVIEW
                "video_call" -> CameraMetadata.SCALER_AVAILABLE_STREAM_USE_CASES_VIDEO_CALL
                "record" -> CameraMetadata.SCALER_AVAILABLE_STREAM_USE_CASES_VIDEO_RECORD
                else -> null
            }
            useCase?.let { output.setStreamUseCase(it.toLong()) }
        }
        val generation = cameraGeneration
        val cfg = SessionConfiguration(
            SessionConfiguration.SESSION_REGULAR,
            listOf(output),
            { r -> cameraHandler?.post(r) },
            object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(s: CameraCaptureSession) {
                    if (generation != cameraGeneration) {
                        s.close()
                        return
                    }
                    session = s
                    s.setRepeatingRequest(buildRequest(device, surface), CameraStats(), cameraHandler)
                    Log.i(TAG, "SESSION STREAMING cam=$cameraId ${width}x$height@$fps template=$requestTemplate " +
                        "usecase=${streamUseCase.ifEmpty { "default" }} exposure=${if (exposureUs > 0) "${exposureUs}us iso $iso" else "auto"}")
                }

                override fun onConfigureFailed(s: CameraCaptureSession) {
                    Log.e(TAG, "SESSION CONFIGURE FAILED")
                }
            }
        )
        // the HAL configures the stream for these settings once (fps range, stabilization) instead of reconfiguring
        // when the first repeating request arrives
        try {
            cfg.sessionParameters = buildRequest(device, surface)
        } catch (e: Exception) {
            Log.w(TAG, "session parameters not set: $e")
        }
        try {
            device.createCaptureSession(cfg)
        } catch (e: Exception) {
            Log.e(TAG, "createCaptureSession failed: $e")
        }
    }

    /**
     * Logs once a second what the camera actually did: real exposure time, how long after exposure
     * start the capture result arrived, and the HAL pipeline depth.
     */
    private inner class CameraStats : CameraCaptureSession.CaptureCallback() {
        private var n = 0
        private var resultMsSum = 0.0
        private var exposureMsSum = 0.0
        private var depthMax = 0
        private var lastLog = 0L

        override fun onCaptureCompleted(s: CameraCaptureSession, r: CaptureRequest, result: TotalCaptureResult) {
            val ts = result.get(CaptureResult.SENSOR_TIMESTAMP) ?: return
            // the sensor timestamp may be on either clock; the right one gives a small positive delta
            val boot = SystemClock.elapsedRealtimeNanos() - ts
            val mono = System.nanoTime() - ts
            val delta = if (boot in 0..2_000_000_000L) boot else mono
            n++
            resultMsSum += delta / 1e6
            exposureMsSum += (result.get(CaptureResult.SENSOR_EXPOSURE_TIME) ?: 0L) / 1e6
            result.get(CaptureResult.LENS_FOCUS_DISTANCE)?.let { lastFocusDistance = it }
            depthMax = maxOf(depthMax, (result.get(CaptureResult.REQUEST_PIPELINE_DEPTH) ?: 0).toInt())
            val now = SystemClock.elapsedRealtime()
            if (lastLog == 0L) lastLog = now
            if (now - lastLog >= 1000 && n > 0) {
                Log.i(TAG, "camera result latency avg %.1f ms exposure avg %.2f ms pipelineDepth max %d"
                    .format(resultMsSum / n, exposureMsSum / n, depthMax))
                n = 0; resultMsSum = 0.0; exposureMsSum = 0.0; depthMax = 0; lastLog = now
            }
        }
    }

    private fun stopStreaming() {
        running = false
        unwatchHeat()
        closeCamera()
        client?.close(); client = null
        // close() alone does not wake a thread blocked in accept(), which keeps the name taken and makes the
        // next start fail with "address in use"; shutting the socket down releases it immediately
        server?.let { s ->
            try { Os.shutdown(s.fileDescriptor, OsConstants.SHUT_RDWR) } catch (_: Exception) {}
            try { s.close() } catch (_: Exception) {}
        }
        server = null
        acceptThread?.join(500)
        try { tcpServer?.close() } catch (_: Exception) {}
        tcpServer = null
        tcpAcceptThread?.join(500)
        discoverySocket?.close()
        discoverySocket = null
        releaseWifiLocks()
        if (usbReceiverRegistered) {
            unregisterReceiver(usbState)
            usbReceiverRegistered = false
        }
        mainHandler.removeCallbacks(stopForUnplug)
        mainHandler.removeCallbacks(idleWatch)
        mainHandler.post {
            orientationDebouncer?.stop(); orientationDebouncer = null
            orientationListener?.disable(); orientationListener = null
        }
        pairingCode = null
        networkInfo = ""
        status = "Stopped"
        currentSettings = null
        tcpPort = 0
        stopEncoder()
        cameraThread?.quitSafely(); cameraThread = null
        cameraHandler = null
        Log.i(TAG, "stopped")
    }

    companion object {
        /** Read by MainActivity to show what the service is doing (same process). */
        @Volatile var status: String = "Stopped"
        @Volatile var networkInfo: String = ""
        /** A plain-words note when the phone lowered the quality because it got warm (shown above the Stop button). */
        @Volatile var heatNote: String = ""
        @Volatile var pairingCode: String? = null
        /** What is streaming now (cam, zoom, w, h, fps, mbps), null when stopped. */
        @Volatile var currentSettings: Bundle? = null
        /** The running service, for the phone UI to apply setting changes in place. */
        @Volatile var instance: CameraStreamService? = null

        private const val TAG = "PhoneCam"
        private const val PREF_PAIRING_CODE = "pairing_code"

        /** A fresh code (Pairing pop-up, "New code"): PCs that paired before must enter the new one. */
        fun newPairingCode(context: android.content.Context): String {
            val code = "%06d".format(SecureRandom().nextInt(1_000_000))
            context.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE).edit().putString(PREF_PAIRING_CODE, code).apply()
            if (pairingCode != null) pairingCode = code // a running stream uses it from now on
            return code
        }

        /** Adopts a code that came from the PC (QR scan): the phone then accepts that PC without typing. */
        fun setPairingCode(context: android.content.Context, code: String) {
            context.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE).edit().putString(PREF_PAIRING_CODE, code).apply()
            if (pairingCode != null) pairingCode = code
        }

        /** The pairing code (made once, kept); the app shows it in the Wi-Fi pop-up before any stream has started. */
        fun storedPairingCode(context: android.content.Context): String {
            val prefs = context.getSharedPreferences("settings", android.content.Context.MODE_PRIVATE)
            prefs.getString(PREF_PAIRING_CODE, null)?.takeIf { it.matches(Regex("\\d{6}")) }?.let { return it }
            val code = "%06d".format(SecureRandom().nextInt(1_000_000))
            prefs.edit().putString(PREF_PAIRING_CODE, code).apply()
            return code
        }
        private val SIZES = setOf("1280x720", "1920x1080", "3840x2160")
        private val FRAME_RATES = setOf(30, 60)
        private val ORIENTATIONS = setOf("auto", "manual")
        private val WHITE_BALANCE = mapOf(
            "auto" to CaptureRequest.CONTROL_AWB_MODE_AUTO,
            "daylight" to CaptureRequest.CONTROL_AWB_MODE_DAYLIGHT,
            "cloudy" to CaptureRequest.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT,
            "tungsten" to CaptureRequest.CONTROL_AWB_MODE_INCANDESCENT,
            "fluorescent" to CaptureRequest.CONTROL_AWB_MODE_FLUORESCENT,
        )
        // 50 Hz for UK/Europe mains lighting, 60 Hz for US
        private val ANTI_FLICKER = mapOf(
            "auto" to CaptureRequest.CONTROL_AE_ANTIBANDING_MODE_AUTO,
            "50" to CaptureRequest.CONTROL_AE_ANTIBANDING_MODE_50HZ,
            "60" to CaptureRequest.CONTROL_AE_ANTIBANDING_MODE_60HZ,
            "off" to CaptureRequest.CONTROL_AE_ANTIBANDING_MODE_OFF,
        )
        private const val SOCKET_NAME = "phonecam"
        private const val CHANNEL_ID = "stream"
        private const val NOTIFICATION_ID = 1
        private const val ACTION_STOP = "com.phonecam.STOP"

        private const val MAGIC = 0x50434D31 // "PCM1"
        private const val HEADER_SIZE = 28
        private const val TYPE_CONFIG = 1
        private const val TYPE_FRAME = 2
        private const val TYPE_CLOCK = 3
        private const val TYPE_SETTINGS = 4
        private const val TYPE_AUDIO = 5
        private const val TYPE_REJECT = 6
        private const val DISCOVERY_PORT = 5557
    }
}
