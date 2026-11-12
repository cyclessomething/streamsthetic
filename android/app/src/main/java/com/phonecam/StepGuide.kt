package com.phonecam

import android.content.Context
import android.view.Gravity
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

/** One page of a [StepGuide]: what to do in plain words, an optional button that takes the user there, an optional check that tells the guide the step is already done. */
class GuideStep(
    val title: String,
    val body: String,
    val button: String? = null,
    val action: (() -> Unit)? = null,
    val done: (() -> Boolean)? = null,
)

/**
 * A step-by-step pop-up for people who are not technical: one instruction per page, a big button for the thing to tap,
 * Back / Next, progress dots, and a last page that says whether it worked ([ready]). Pages with a [GuideStep.done] check
 * move on by themselves when the phone shows the step is finished. [help] opens a web search for the user's own phone
 * model, for phones whose Settings look different.
 */
class StepGuide(
    context: Context,
    private val ui: Ui,
    private val steps: List<GuideStep>,
    private val readyTitle: String,
    private val readyBody: String,
    private val notReadyTitle: String,
    private val notReadyBody: String,
    private val ready: () -> Boolean,
    private val help: () -> Unit,
    private val onDone: () -> Unit,
) : FrameLayout(context) {
    private val card = LinearLayout(context)
    private var closing = false
    private var index = 0
    private var lastReady = false
    private var armed = false // the shown step was not finished when it appeared, so finishing it moves on (going Back to a finished step must stay put)
    private val total get() = steps.size + 1

    val isOpen get() = visibility == VISIBLE && !closing

    init {
        visibility = GONE
        isClickable = true
        setBackgroundColor(0xB3000000.toInt())
        val look = ui.look
        card.apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            background = ui.box(look.panel, look.stroke, look.panelRadius.coerceAtLeast(14), look.panelBorder)
            elevation = ui.dp(12).toFloat()
            setPadding(ui.dp(22), ui.dp(20), ui.dp(22), ui.dp(20))
            isClickable = true
        }
        addView(card, LayoutParams(-1, -2, Gravity.CENTER).apply { setMargins(ui.dp(22), 0, ui.dp(22), 0) })
    }

    fun open() {
        index = 0
        skipFinished()
        closing = false
        render()
        visibility = VISIBLE
        removeCallbacks(poll)
        postDelayed(poll, 1000)
        alpha = 0f
        card.scaleX = 0.88f; card.scaleY = 0.88f
        animate().alpha(1f).setDuration(220).start()
        card.animate().scaleX(1f).scaleY(1f).setDuration(340).setInterpolator(OvershootInterpolator(0.9f)).start()
    }

    /** Steps the phone already shows as done are not asked again. */
    private fun skipFinished() {
        while (index < steps.size && steps[index].done?.invoke() == true) index++
    }

    /** Runs when the app comes back to the front and every second while open: finished steps move on by themselves. */
    fun refresh() {
        if (!isOpen) return
        if (index < steps.size) {
            if (armed && steps[index].done?.invoke() == true) { index++; skipFinished(); render() }
        } else if (ready() != lastReady) render()
    }

    private val poll = object : Runnable {
        override fun run() {
            refresh()
            if (isOpen) postDelayed(this, 1000)
        }
    }

    fun close() {
        if (visibility != VISIBLE || closing) return
        closing = true
        animate().alpha(0f).setDuration(200).setInterpolator(DecelerateInterpolator())
            .withEndAction { visibility = GONE; closing = false }.start()
    }

    private fun render() {
        val look = ui.look
        card.removeAllViews()
        val last = index >= steps.size
        val ok = last && ready()
        armed = !last && steps[index].done?.invoke() != true
        lastReady = ok

        val top = LinearLayout(context).apply { gravity = Gravity.CENTER_VERTICAL }
        top.addView(ui.body("Step ${index + 1} of $total", 12f, R.color.ink3), LinearLayout.LayoutParams(0, -2, 1f))
        top.addView(TextView(context).apply {
            text = "✕"; textSize = 18f; gravity = Gravity.CENTER; setTextColor(look.ink2)
            contentDescription = "Close"; isClickable = true
            setOnClickListener { close() }
            ui.lightUp(this, look.ink2, 20, lift = false)
        }, LinearLayout.LayoutParams(ui.dp(40), ui.dp(40)))
        card.addView(top, LinearLayout.LayoutParams(-1, -2))

        val dots = LinearLayout(context).apply { gravity = Gravity.CENTER }
        for (i in 0 until total) {
            dots.addView(View(context).apply {
                background = ui.box(if (i <= index) look.primary else look.stroke, 0, 20, 0)
            }, LinearLayout.LayoutParams(ui.dp(if (i == index) 22 else 8), ui.dp(8)).apply { setMargins(ui.dp(3), 0, ui.dp(3), 0) })
        }
        card.addView(dots, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(4); bottomMargin = ui.dp(16) })

        val step = steps.getOrNull(index)
        val title = if (step != null) step.title else if (ok) readyTitle else notReadyTitle
        val body = if (step != null) step.body else if (ok) readyBody else notReadyBody
        card.addView(ui.body(title, 20f, R.color.ink, bold = true), LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(8) })
        card.addView(ui.body(body, 14f), LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(18) })

        if (last) {
            card.addView(ui.primaryButton(if (ok) "Done" else "Back") { if (ok) { close(); onDone() } else { index = steps.size - 1; render() } })
        } else {
            if (step!!.button != null) {
                card.addView(ui.primaryButton(step.button) { step.action?.invoke() }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = ui.dp(8) })
                card.addView(ui.ghostButton("Next") { index++; render() })
            } else {
                card.addView(ui.primaryButton("Next") { index++; render() })
            }
            card.addView(ui.ghostButton("Find instructions for this phone") { help() }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(6) })
        }
        // the "not on yet" last page already has Back as its one button: a second Back would only repeat it
        if (index > 0 && !(last && !ok)) card.addView(ui.ghostButton("Back") { index--; render() }, LinearLayout.LayoutParams(-1, -2).apply { topMargin = ui.dp(6) })
    }
}
