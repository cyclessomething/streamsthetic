package com.phonecam

import android.content.Context
import android.content.res.Configuration
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

/**
 * PhoneCam's themed building blocks for the phone screen, the same system as the PC app (docs/DESIGN_PATTERNS.md
 * "Implemented system" and "Themes"): numbered panels, chips instead of dropdowns, one primary button. Everything
 * visual (colours, corner radii, borders, fonts, text case) comes from the active [Look] (Looks.kt), chosen by the
 * saved theme and, except for Gamer (always dark), by the phone's light/dark setting.
 */
class Ui(private val context: Context, skin: String) {
    val skinName = if (skin in SKINS) skin else DEFAULT_SKIN
    val look: Look = lookFor(
        skinName,
        dark = skinName == "Gamer" || (context.resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES,
    )

    fun dp(value: Int) = (value * context.resources.displayMetrics.density).toInt()

    /** Colours by their old resource names, so screens keep saying R.color.ink / R.color.accent. */
    fun color(id: Int): Int = when (id) {
        R.color.paper -> look.paper
        R.color.panel -> look.panel
        R.color.hairline -> look.stroke
        R.color.ink -> look.ink
        R.color.ink2 -> look.ink2
        R.color.ink3 -> look.ink3
        R.color.accent -> look.accent
        R.color.on_accent -> look.onAccent
        R.color.good -> look.good
        else -> context.getColor(id)
    }

    private fun font(id: Int): Typeface = context.resources.getFont(id)
    val labelFont: Typeface by lazy { font(look.labelFont) }
    val titleFont: Typeface by lazy { font(look.titleFont) }
    val chipFont: Typeface by lazy { font(look.chipFont) }
    val wordmarkFont: Typeface by lazy { font(look.wordmarkFont) }
    val sans: Typeface by lazy { font(R.font.archivo_regular) }
    val sansBold: Typeface by lazy { font(R.font.archivo_bold) }
    val mono: Typeface by lazy { font(R.font.plex_mono_medium) }

    /** Text as the theme shows it: capitals (Studio, Gamer, Brutal) or as written (Creator, Dot). */
    fun cased(text: String) = if (look.upper) text.uppercase() else text

    fun box(fill: Int, stroke: Int = 0, radiusDp: Int = 6, strokeDp: Int = 1) = GradientDrawable().apply {
        setColor(fill)
        cornerRadius = dp(radiusDp).toFloat()
        if (stroke != 0 && strokeDp > 0) setStroke(dp(strokeDp), stroke)
    }

    /** Row names, units, keys. */
    fun label(text: String, size: Float = 11f, tint: Int = R.color.ink2) = TextView(context).apply {
        this.text = cased(text)
        typeface = labelFont
        textSize = size
        letterSpacing = if (look.upper) 0.08f else 0f
        setTextColor(color(tint))
    }

    /**
     * The little (i) beside a setting's name and the plain-language explanation it opens. Tap (i): the explanation fades in
     * under the row; tap again to close. Returns the button and the card; the caller puts the card under the row.
     */
    fun info(explanation: String): Pair<View, View> {
        val card = body(explanation, 12.5f, R.color.ink).apply {
            visibility = View.GONE
            setPadding(dp(12), dp(10), dp(12), dp(10))
            background = box(look.paper, look.stroke, 10, 1)
            setLineSpacing(0f, 1.1f)
        }
        fun ring(open: Boolean) = android.graphics.drawable.InsetDrawable(GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(if (open) look.primary else 0)
            setStroke(dp(1), if (open) look.primary else look.ink2)
        }, dp(5))
        val button = TextView(context).apply {
            text = "i"
            typeface = sansBold
            textSize = 11f
            gravity = Gravity.CENTER
            setTextColor(look.ink2)
            background = ring(false)
            contentDescription = "Show setting help"
            isClickable = true
            isFocusable = true
        }
        button.setOnClickListener {
            val open = card.visibility != View.VISIBLE
            button.background = ring(open)
            button.setTextColor(if (open) look.onPrimary else look.ink2)
            if (open) {
                card.alpha = 0f
                card.translationY = -dp(6).toFloat()
                card.visibility = View.VISIBLE
                card.animate().alpha(1f).translationY(0f).setDuration(220).start()
            } else {
                card.animate().alpha(0f).setDuration(150).withEndAction { card.visibility = View.GONE }.start()
            }
        }
        return button to card
    }

    fun body(text: String = "", size: Float = 14f, tint: Int = R.color.ink2, bold: Boolean = false) = TextView(context).apply {
        this.text = text
        typeface = if (bold) sansBold else sans
        textSize = size
        setTextColor(color(tint))
    }

    /** A numbered panel in the section's colour: accent bar (or a coloured dot in Creator), title, number, then [content]. */
    fun panel(section: Int, number: String, title: String, vararg content: View) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        background = box(look.panels?.getOrNull(section) ?: look.panel, look.stroke, look.panelRadius, look.panelBorder)
        if (look.shadow) elevation = dp(4).toFloat()
        setPadding(dp(16), dp(14), dp(16), dp(16))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(16) }
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            val tint = look.sections[section.coerceIn(0, 4)]
            if (look.bar) {
                addView(View(context).apply { setBackgroundColor(tint) }, LinearLayout.LayoutParams(dp(3), dp(13)).apply { rightMargin = dp(9) })
            } else if (skinName == "Creator" || skinName == "Prism") {
                addView(View(context).apply { background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(tint) } },
                    LinearLayout.LayoutParams(dp(12), dp(12)).apply { rightMargin = dp(9) })
            }
            addView(TextView(context).apply {
                text = cased(title)
                typeface = titleFont
                textSize = if (skinName == "Creator" || skinName == "Prism") 16f else 13f
                setTextColor(look.ink)
            }, LinearLayout.LayoutParams(0, -2, 1f))
            addView(label(number, 11f, R.color.ink3))
        }, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(12) })
        content.forEach { addView(it) }
    }

    /** One choice out of a few, all visible: the chosen chip is filled with its panel's colour. Wraps after 4 (3 when there are 5). */
    inner class ChipRow(labels: List<String>, private val section: Int = 0, private val hue: Int = -1) : LinearLayout(context) {
        private val chips = labels.map { text ->
            TextView(context).apply {
                this.text = cased(text)
                typeface = chipFont
                textSize = 12f
                gravity = Gravity.CENTER
                minWidth = dp(48)
                isSingleLine = true
                setPadding(dp(12), dp(8), dp(12), dp(8))
                isClickable = true
                isFocusable = true
                lightUp(this, look.hues?.getOrNull(hue) ?: look.sections[section.coerceIn(0, 4)], look.chipRadius, lift = false)
            }
        }
        /** Four or more choices are shown as a dropdown (one tap target, list opens under it); two or three stay as chips. */
        val isDropdown = labels.size > 3
        private val choiceLabels = labels
        private val fieldText = TextView(context).apply {
            typeface = chipFont
            textSize = 13f
            isSingleLine = true
            setTextColor(look.ink)
        }
        private val field = LinearLayout(context).apply {
            orientation = HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = box(look.chipFill, look.chipStroke, look.chipRadius, look.chipBorder)
            setPadding(dp(14), dp(11), dp(12), dp(11))
            isClickable = true
            isFocusable = true
            addView(fieldText, LayoutParams(0, -2, 1f))
            addView(android.widget.ImageView(context).apply { setImageResource(R.drawable.ic_chevron); setColorFilter(look.ink2) }, LayoutParams(dp(20), dp(20)))
        }
        var selectedItemPosition = -1
            private set
        /** Called for a chip the user tapped (not for setSelection). */
        var onSelected: ((Int) -> Unit)? = null

        init {
            orientation = VERTICAL
            layoutParams = LayoutParams(-1, -2).apply { bottomMargin = dp(6) }
            if (isDropdown) {
                addView(field, LayoutParams(-1, -2))
                lightUp(field, look.sections[section.coerceIn(0, 4)], look.chipRadius, lift = false)
                field.setOnClickListener { if (isEnabled) openList() }
            } else chips.chunked(if (chips.size > 4) 3 else 4).forEach { group ->
                val row = LinearLayout(context).apply { orientation = HORIZONTAL }
                group.forEach { chip ->
                    val i = chips.indexOf(chip)
                    chip.setOnClickListener {
                        if (!isEnabled) return@setOnClickListener
                        setSelection(i)
                        onSelected?.invoke(i)
                    }
                    row.addView(chip, LayoutParams(-2, -2).apply { rightMargin = dp(6) })
                }
                addView(row, LayoutParams(-2, -2).apply { bottomMargin = dp(6) })
            }
            setSelection(-1)
        }

        private fun openList() {
            val popup = android.widget.ListPopupWindow(context)
            popup.anchorView = field
            popup.width = field.width
            popup.isModal = true
            popup.setBackgroundDrawable(box(look.panel, (look.ink3 and 0x00FFFFFF) or 0x66000000, 14, 1))
            popup.setAdapter(object : android.widget.BaseAdapter() {
                override fun getCount() = choiceLabels.size
                override fun getItem(position: Int) = choiceLabels[position]
                override fun getItemId(position: Int) = position.toLong()
                override fun getView(position: Int, convertView: View?, parent: android.view.ViewGroup): View {
                    val on = position == selectedItemPosition
                    return TextView(context).apply {
                        text = cased(choiceLabels[position]) + if (on) "   ●" else ""
                        typeface = chipFont
                        textSize = 13.5f
                        setTextColor(if (on) look.accent else look.ink)
                        setPadding(dp(16), dp(13), dp(16), dp(13))
                    }
                }
            })
            popup.setOnItemClickListener { _, _, position, _ ->
                setSelection(position)
                onSelected?.invoke(position)
                popup.dismiss()
            }
            popup.show()
        }

        fun setSelection(position: Int) {
            selectedItemPosition = position
            fieldText.text = cased(choiceLabels.getOrNull(position) ?: "")
            if (isDropdown) return
            chips.forEachIndexed { i, chip ->
                val on = i == position
                chip.background = if (on) box(look.hues?.getOrNull(hue) ?: look.sections[section.coerceIn(0, 4)], radiusDp = look.chipRadius)
                else box(look.chipFill, look.chipStroke, look.chipRadius, look.chipBorder)
                chip.setTextColor(if (on) look.sectionInk else look.ink)
                // chosen chips stay lit: a soft glow in the row's colour; unchosen ones have none
                val tint = look.hues?.getOrNull(hue) ?: look.sections[section.coerceIn(0, 4)]
                chip.elevation = if (on) dp(6).toFloat() else 0f
                if (android.os.Build.VERSION.SDK_INT >= 28) { chip.outlineSpotShadowColor = tint; chip.outlineAmbientShadowColor = tint }
            }
        }

        override fun setEnabled(enabled: Boolean) {
            super.setEnabled(enabled)
            alpha = if (enabled) 1f else 0.45f
        }
    }

    /**
     * Makes a view feel pressed and lit: on touch a white veil fades in over it, it dips a little, and (Android 9+) a
     * [glow]-coloured shadow lifts under it, like a lamp coming on. All of it fades away when the finger lifts.
     */
    fun lightUp(view: View, glow: Int, radiusDp: Int, lift: Boolean = true) {
        val veil = GradientDrawable().apply { setColor(0xFFFFFFFF.toInt()); cornerRadius = dp(radiusDp).toFloat(); alpha = 0 }
        view.foreground = veil
        if (android.os.Build.VERSION.SDK_INT >= 28) { view.outlineSpotShadowColor = glow; view.outlineAmbientShadowColor = glow }
        var baseElevation = view.elevation
        fun to(on: Boolean) {
            if (on) baseElevation = view.elevation
            val anim = android.animation.ValueAnimator.ofFloat(if (on) 0f else 1f, if (on) 1f else 0f).apply {
                duration = if (on) 70 else 380
                addUpdateListener {
                    val t = it.animatedValue as Float
                    veil.alpha = (t * 90).toInt()
                    view.scaleX = 1f - 0.03f * t
                    view.scaleY = 1f - 0.03f * t
                    if (lift) view.elevation = baseElevation + dp(14) * t
                }
            }
            anim.start()
        }
        view.setOnTouchListener { _, e ->
            when (e.actionMasked) {
                android.view.MotionEvent.ACTION_DOWN -> to(true)
                android.view.MotionEvent.ACTION_UP, android.view.MotionEvent.ACTION_CANCEL -> to(false)
            }
            false
        }
    }
    /** The one main action. */
    fun primaryButton(text: String, onClick: () -> Unit) = TextView(context).apply {
        this.text = cased(text)
        typeface = chipFont
        textSize = 14f
        gravity = Gravity.CENTER
        setTextColor(look.onPrimary)
        background = box(look.primary, radiusDp = look.buttonRadius)
        setPadding(0, dp(14), 0, dp(14))
        isClickable = true
        isFocusable = true
        setOnClickListener { onClick() }
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(8) }
        lightUp(this, look.primary, look.buttonRadius)
    }

    /** The primary button while streaming: red "Stop" (white text); back to the normal look when [stop] is false. */
    fun setStopState(button: TextView, stop: Boolean, text: String) {
        val red = 0xFFD32F2F.toInt()
        button.text = cased(text)
        button.setTextColor(if (stop) 0xFFFFFFFF.toInt() else look.onPrimary)
        button.background = box(if (stop) red else look.primary, radiusDp = look.buttonRadius)
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            val glow = if (stop) red else look.primary
            button.outlineSpotShadowColor = glow
            button.outlineAmbientShadowColor = glow
        }
    }

    /** A big tappable card: line illustration on top, title, one-line description (the connection chooser). */
    fun modeCard(illustration: Int, title: String, description: String, onClick: () -> Unit) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        gravity = Gravity.CENTER_HORIZONTAL
        background = box(look.panel, look.stroke, look.panelRadius, look.panelBorder)
        if (look.shadow) elevation = dp(4).toFloat()
        setPadding(dp(18), dp(18), dp(18), dp(18))
        isClickable = true
        isFocusable = true
        contentDescription = "$title. $description"
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(14) }
        addView(android.widget.ImageView(context).apply { setImageResource(illustration); setColorFilter(look.ink) }, LinearLayout.LayoutParams(dp(180), dp(112)))
        addView(TextView(context).apply {
            text = cased(title)
            typeface = titleFont
            textSize = 15f
            gravity = Gravity.CENTER
            setTextColor(look.ink)
        }, LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(8) })
        addView(body(description, 13f).apply { gravity = Gravity.CENTER }, LinearLayout.LayoutParams(-2, -2).apply { topMargin = dp(4) })
        setOnClickListener { onClick() }
        lightUp(this, look.sections[0], look.panelRadius.coerceAtMost(24), lift = true)
    }
    /** Settings uses the existing panel shape with a compact heading and no panel numbers. */
    fun settingsSection(vararg controls: View) = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        background = box(look.panel, look.stroke, look.panelRadius, look.panelBorder)
        if (look.shadow) elevation = dp(4).toFloat()
        setPadding(dp(16), dp(14), dp(16), dp(16))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(14) }
        controls.forEachIndexed { index, control ->
            addView(control, LinearLayout.LayoutParams(-1, -2).apply { if (index > 0) topMargin = dp(8) })
        }
    }

    fun settingsCategory(title: String, description: String, onClick: () -> Unit) = settingsSection().apply {
        minimumHeight = dp(64)
        gravity = Gravity.CENTER_VERTICAL
        isClickable = true
        isFocusable = true
        contentDescription = "$title. $description. Open settings category."
        addView(body(cased(title), 15f, R.color.ink, bold = true).apply { typeface = titleFont })
        addView(body(description, 12f), LinearLayout.LayoutParams(-1, -2).apply { topMargin = dp(4) })
        setOnClickListener { onClick() }
        lightUp(this, look.sections[4], look.panelRadius, lift = false)
    }

    /** A quiet button: outlined. */
    fun ghostButton(text: String, onClick: () -> Unit) = TextView(context).apply {
        this.text = cased(text)
        typeface = chipFont
        textSize = 12.5f
        gravity = Gravity.CENTER
        setTextColor(look.ink)
        background = box(look.chipFill, look.chipStroke, look.chipRadius, look.chipBorder)
        setPadding(dp(12), dp(11), dp(12), dp(11))
        isClickable = true
        isFocusable = true
        setOnClickListener { if (isEnabled) onClick() }
        lightUp(this, look.sections[0], look.chipRadius)
    }

    /** A rounded "bubble" button: obvious to spot and press (Change connection, Show pairing code), outlined in the main colour. */
    fun pillButton(text: String, onClick: () -> Unit) = TextView(context).apply {
        this.text = cased(text)
        typeface = chipFont
        textSize = 12.5f
        gravity = Gravity.CENTER
        setTextColor(look.ink)
        background = box(look.chipFill, look.primary, 40, 2)
        setPadding(dp(18), dp(11), dp(18), dp(11))
        isClickable = true
        isFocusable = true
        setOnClickListener { onClick() }
        lightUp(this, look.primary, 40)
    }

    /** Header: block with the wordmark, status chip at the right. */
    fun header(status: TextView) = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        background = box(look.headerBlock, look.stroke, look.panelRadius.coerceAtMost(14), look.panelBorder)
        setPadding(dp(18), dp(16), dp(14), dp(16))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(14) }
        // the wordmark shrinks to fit (large text / icon sizes in Settings, Accessibility), never overlaps the status tag
        addView(TextView(context).apply {
            text = "STREAMSTHETIC" // always capitals, in every theme
            typeface = wordmarkFont
            isSingleLine = true
            setTextColor(look.onHeader)
            val max = if (skinName == "Brutal") 24 else 22
            val saved = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
            if (saved.getInt("TextScale", 0) == 0 && saved.getInt("IconScale", 0) == 0) textSize = max.toFloat()
            else setAutoSizeTextTypeUniformWithConfiguration(8, max, 1, android.util.TypedValue.COMPLEX_UNIT_SP)
        }, LinearLayout.LayoutParams(0, -2, 1f).apply { rightMargin = dp(8) })
        addView(status, LinearLayout.LayoutParams(-2, -2))
    }

    /** Small filled status tag (Live / Stopped). */
    fun statusChip() = TextView(context).apply {
        typeface = chipFont
        textSize = 11f
        setPadding(dp(8), dp(3), dp(8), dp(3))
    }

    fun setStatusChip(chip: TextView, live: Boolean) {
        chip.text = cased(if (live) "● Live" else "Stopped")
        chip.setTextColor(look.onStatus)
        chip.background = box(if (live) look.good else look.idle, radiusDp = if (skinName == "Creator" || skinName == "Prism") 12 else 3)
    }

    /**
     * Bottom tab bar: icon over a short label per tab; the chosen tab is tinted in its section colour. [items] are
     * (icon resource, label) pairs; [onChosen] gets the tab index.
     */
    inner class TabBar(items: List<Pair<Int, String>>, private val onChosen: (Int) -> Unit) : LinearLayout(context) {
        private val icons = ArrayList<android.widget.ImageView>()
        private val captions = ArrayList<TextView>()

        init {
            orientation = HORIZONTAL
            background = box(look.panel, 0, 0)
            elevation = dp(10).toFloat()
            setPadding(0, dp(8), 0, dp(8))
            items.forEachIndexed { i, (icon, text) ->
                val cell = LinearLayout(context).apply {
                    orientation = VERTICAL
                    gravity = Gravity.CENTER
                    isClickable = true
                    isFocusable = true
                    contentDescription = text
                    setOnClickListener { onChosen(i) }
                    lightUp(this, look.sections[i.coerceIn(0, 4)], 16)
                }
                val image = android.widget.ImageView(context).apply { setImageResource(icon) }
                val caption = label(text, 9.5f)
                cell.addView(image, LayoutParams(dp(26), dp(26)))
                cell.addView(caption, LayoutParams(-2, -2).apply { topMargin = dp(3) })
                addView(cell, LayoutParams(0, -2, 1f))
                icons.add(image)
                captions.add(caption)
            }
        }

        fun select(index: Int) {
            for (i in icons.indices) {
                val on = i == index
                icons[i].setColorFilter(if (on) look.sections[i.coerceIn(0, 4)] else look.ink2)
                captions[i].setTextColor(if (on) look.ink else look.ink2)
            }
        }
    }
}

/** Slider track in two colours: [ok] up to the recommended point, [warn] beyond it, with a thin tick at that point. */
class DimTrack(private val ok: Int, private val warn: Int, private val back: Int, private val recommended: Float, private val thickness: Float, private val tick: Int) : android.graphics.drawable.Drawable() {
    private val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)

    override fun onLevelChange(level: Int): Boolean { invalidateSelf(); return true }

    override fun draw(canvas: android.graphics.Canvas) {
        val b = bounds
        val cy = b.exactCenterY()
        val half = thickness / 2
        val left = b.left.toFloat()
        val right = b.right.toFloat()
        val width = right - left
        val reached = left + width * (level / 10000f)
        val limit = left + width * recommended
        fun bar(from: Float, to: Float, color: Int) {
            if (to <= from) return
            paint.color = color
            canvas.drawRoundRect(from, cy - half, to, cy + half, half, half, paint)
        }
        bar(left, right, back)
        bar(left, minOf(reached, limit), ok)
        bar(limit, reached, warn)
        paint.color = tick
        canvas.drawRect(limit - 1.5f, cy - half * 2, limit + 1.5f, cy + half * 2, paint)
    }

    override fun setAlpha(alpha: Int) { paint.alpha = alpha }
    override fun setColorFilter(filter: android.graphics.ColorFilter?) { paint.colorFilter = filter }
    @Deprecated("Deprecated in Java")
    override fun getOpacity() = android.graphics.PixelFormat.TRANSLUCENT
    override fun getIntrinsicHeight() = thickness.toInt()
}
