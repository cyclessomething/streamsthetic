package com.phonecam

import android.animation.AnimatorSet
import android.animation.ObjectAnimator
import android.content.Context
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The Wi-Fi pairing pop-up: a dimmed backdrop and a card that rises in with a soft overshoot, the six code digits
 * popping in one after another, the three steps, a "Close" button and a close cross. Tapping the backdrop, the cross
 * or the system back button closes it (reverse animation).
 */
class PairingSheet(context: Context, private val ui: Ui) : FrameLayout(context) {
    private val card = LinearLayout(context)
    private val digits = ArrayList<TextView>()
    private var closing = false

    val isOpen get() = visibility == VISIBLE && !closing

    init {
        visibility = GONE
        isClickable = true // swallows touches so nothing behind the pop-up reacts
        setBackgroundColor(0xB3000000.toInt())
        setOnClickListener { close() }
        val look = ui.look
        card.apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            background = ui.box(look.panel, look.stroke, look.panelRadius.coerceAtLeast(14), look.panelBorder)
            elevation = ui.dp(12).toFloat()
            setPadding(ui.dp(22), ui.dp(20), ui.dp(22), ui.dp(20))
            isClickable = true // taps on the card must not reach the backdrop
        }
        addView(card, LayoutParams(-1, -2, Gravity.CENTER).apply { setMargins(ui.dp(22), 0, ui.dp(22), 0) })
    }

    /** Fills the card for [code] and plays the opening animation. */
    fun open(code: String, newCode: (() -> String)? = null, scan: (() -> Unit)? = null) {
        val look = ui.look
        closing = false
        card.removeAllViews()
        digits.clear()

        val top = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL }
        top.addView(ui.body("Pair with your PC", 18f, R.color.ink, bold = true), LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(TextView(context).apply {
            text = "✕"
            textSize = 18f
            gravity = Gravity.CENTER
            setTextColor(look.ink2)
            contentDescription = "Close"
            isClickable = true
            setOnClickListener { close() }
            ui.lightUp(this, look.ink2, 20, lift = false)
        }, LinearLayout.LayoutParams(ui.dp(48), ui.dp(48)))
        card.addView(top, LinearLayout.LayoutParams(-1, -2))

        card.addView(ui.body("Type this code into Streamsthetic on your PC.", 13f), LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(2); bottomMargin = ui.dp(16) })

        val row = LinearLayout(context).apply { gravity = Gravity.CENTER }
        code.forEachIndexed { i, c ->
            if (i == 3) row.addView(View(context), LinearLayout.LayoutParams(ui.dp(10), 1))
            val digit = TextView(context).apply {
                text = c.toString()
                typeface = ui.mono
                textSize = 30f
                setAutoSizeTextTypeUniformWithConfiguration(18, 30, 1, android.util.TypedValue.COMPLEX_UNIT_SP)
                gravity = Gravity.CENTER
                setTextColor(look.onPrimary)
                background = ui.box(look.primary, 0, look.chipRadius.coerceAtLeast(8), 0)
                alpha = 0f
            }
            digits.add(digit)
            row.addView(digit, LinearLayout.LayoutParams(0, ui.dp(56), 1f).apply { setMargins(ui.dp(3), 0, ui.dp(3), 0) })
        }
        row.contentDescription = "Pairing code ${code.toCharArray().joinToString(" ")}"
        card.addView(row, LinearLayout.LayoutParams(-1, -2))

        card.addView(ui.body(
            "1. Connect the phone to Wi-Fi on the same local network as the PC. The PC can use Ethernet.\n2. In Streamsthetic on the PC, choose Wi-Fi and enter this pairing code.\n3. Press Start on the phone. If the PC does not connect automatically, press Start there.\nThe code is saved until you replace it or reset the app.",
            12.5f), LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(18); bottomMargin = ui.dp(16) })

        if (scan != null) {
            card.addView(ui.primaryButton("Scan the QR code on the PC") { close(); scan() }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(8) })
            card.addView(ui.ghostButton("Close") { close() })
        } else card.addView(ui.primaryButton("Close") { close() })
        if (newCode != null) {
            card.addView(ui.ghostButton("Generate new code") { open(newCode()) })
            card.addView(ui.body("Generating a new code replaces this one. Update the code on the PC to reconnect.", 12f, R.color.ink2),
                LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(6) })
        }

        visibility = VISIBLE
        alpha = 0f
        card.scaleX = 0.86f; card.scaleY = 0.86f; card.translationY = ui.dp(36).toFloat(); card.alpha = 0f
        val intro = AnimatorSet().apply {
            playTogether(
                ObjectAnimator.ofFloat(this@PairingSheet, ALPHA, 0f, 1f).setDuration(220),
                ObjectAnimator.ofFloat(card, SCALE_X, 0.86f, 1f).setDuration(380),
                ObjectAnimator.ofFloat(card, SCALE_Y, 0.86f, 1f).setDuration(380),
                ObjectAnimator.ofFloat(card, TRANSLATION_Y, ui.dp(36).toFloat(), 0f).setDuration(380),
                ObjectAnimator.ofFloat(card, ALPHA, 0f, 1f).setDuration(240),
            )
            interpolator = OvershootInterpolator(0.9f)
        }
        intro.start()
        digits.forEachIndexed { i, d ->
            d.scaleX = 0.4f; d.scaleY = 0.4f
            d.animate().alpha(1f).scaleX(1f).scaleY(1f).setStartDelay(180L + 70L * i).setDuration(260)
                .setInterpolator(OvershootInterpolator(2.2f)).start()
        }
    }

    fun close() {
        if (visibility != VISIBLE || closing) return
        closing = true
        AnimatorSet().apply {
            playTogether(
                ObjectAnimator.ofFloat(this@PairingSheet, ALPHA, 1f, 0f),
                ObjectAnimator.ofFloat(card, SCALE_X, 1f, 0.92f),
                ObjectAnimator.ofFloat(card, SCALE_Y, 1f, 0.92f),
                ObjectAnimator.ofFloat(card, TRANSLATION_Y, 0f, ui.dp(24).toFloat()),
            )
            duration = 200
            interpolator = DecelerateInterpolator()
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) { visibility = GONE; closing = false }
            })
            start()
        }
    }
}
