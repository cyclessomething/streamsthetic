package com.phonecam

/**
 * The phone app's themes: the same five looks and values as the PC app's token files (the Theme folder of windows/app,
 * docs/DESIGN_PATTERNS.md "Themes"): Studio, Creator, Gamer (always dark), Brutal, Dot, each in light and dark.
 */
class Look(
    val paper: Int, val panel: Int, val stroke: Int, val ink: Int, val ink2: Int, val ink3: Int,
    val accent: Int, val onAccent: Int, val link: Int, val chipFill: Int, val chipStroke: Int,
    val primary: Int, val onPrimary: Int, val headerBlock: Int, val onHeader: Int,
    val good: Int, val idle: Int, val onStatus: Int, val sections: IntArray, val sectionInk: Int,
    val panelRadius: Int, val panelBorder: Int, val chipRadius: Int, val chipBorder: Int, val buttonRadius: Int,
    val shadow: Boolean, val upper: Boolean, val bar: Boolean,
    val labelFont: Int, val titleFont: Int, val chipFont: Int, val wordmarkFont: Int,
    // optional (Prism): a tinted fill per panel, and a colour per chip row (Lens, Resolution, Frame rate, Quality, Focus, White balance, Anti-flicker, Stabilization, Theme, Appearance)
    val panels: IntArray? = null, val hues: IntArray? = null,
)

private fun c(hex: String) = 0xFF000000.toInt() or hex.toInt(16)
private fun a(hex: String) = hex.toLong(16).toInt() // with alpha: AARRGGBB
private fun sections(vararg hex: String) = IntArray(5) { c(hex[if (hex.size == 1) 0 else it]) }

const val DEFAULT_SKIN = "Studio"
val SKINS = listOf("Studio", "Creator", "Gamer", "Brutal", "Dot", "Prism", "Contrast")

/** [dark] is ignored for Gamer, which is always dark. */
fun lookFor(skin: String, dark: Boolean): Look = when (skin) {
    "Creator" -> if (dark) creatorDark else creatorLight
    "Gamer" -> gamer
    "Brutal" -> if (dark) brutalDark else brutalLight
    "Dot" -> if (dark) dotDark else dotLight
    "Prism" -> if (dark) prismDark else prismLight
    "Contrast" -> if (dark) contrastDark else contrastLight
    else -> if (dark) studioDark else studioLight
}

private val studioLight = Look(
    paper = c("F4F2EC"), panel = c("FCFBF8"), stroke = c("E2DED6"), ink = c("1B1D1F"), ink2 = c("6E6B67"), ink3 = c("A9A59F"),
    accent = c("FE5921"), onAccent = c("1B1D1F"), link = c("C23C13"), chipFill = a("00FFFFFF"), chipStroke = c("E2DED6"),
    primary = c("FE5921"), onPrimary = c("1B1D1F"), headerBlock = c("FE5921"), onHeader = c("1B1D1F"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("FE5921"), sectionInk = c("1B1D1F"),
    panelRadius = 6, panelBorder = 1, chipRadius = 4, chipBorder = 1, buttonRadius = 6, shadow = false, upper = true, bar = true,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.plex_mono_semibold, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)
private val studioDark = Look(
    paper = c("0F1011"), panel = c("17191B"), stroke = c("2A2C2F"), ink = c("F1F1F1"), ink2 = c("9C9A97"), ink3 = c("5E5C59"),
    accent = c("FE5921"), onAccent = c("1B1D1F"), link = c("FF7A4D"), chipFill = a("00FFFFFF"), chipStroke = c("2A2C2F"),
    primary = c("FE5921"), onPrimary = c("1B1D1F"), headerBlock = c("FE5921"), onHeader = c("1B1D1F"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("FE5921"), sectionInk = c("1B1D1F"),
    panelRadius = 6, panelBorder = 1, chipRadius = 4, chipBorder = 1, buttonRadius = 6, shadow = false, upper = true, bar = true,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.plex_mono_semibold, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)
private val creatorLight = Look(
    paper = c("F2F2F4"), panel = c("FFFFFF"), stroke = a("00FFFFFF"), ink = c("1C1C1E"), ink2 = c("5F6368"), ink3 = c("A1A1A6"),
    accent = c("0F7A3A"), onAccent = c("FFFFFF"), link = c("0F7A3A"), chipFill = c("EEEEF1"), chipStroke = a("00FFFFFF"),
    primary = c("0F7A3A"), onPrimary = c("FFFFFF"), headerBlock = c("FFFFFF"), onHeader = c("1C1C1E"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"),
    sections = sections("1A6FE0", "0B76A8", "0F7A3A", "B45309", "6D3FD8"), sectionInk = c("FFFFFF"),
    panelRadius = 20, panelBorder = 0, chipRadius = 18, chipBorder = 0, buttonRadius = 26, shadow = true, upper = false, bar = false,
    labelFont = R.font.archivo_regular, titleFont = R.font.archivo_bold, chipFont = R.font.archivo_regular, wordmarkFont = R.font.archivo_bold,
)
private val creatorDark = Look(
    paper = c("111214"), panel = c("1C1D20"), stroke = c("2A2C30"), ink = c("F2F2F4"), ink2 = c("A0A0A6"), ink3 = c("6B6B70"),
    accent = c("34C759"), onAccent = c("0B1F12"), link = c("34C759"), chipFill = c("26272B"), chipStroke = a("00FFFFFF"),
    primary = c("34C759"), onPrimary = c("0B1F12"), headerBlock = c("1C1D20"), onHeader = c("F2F2F4"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"),
    sections = sections("4C9AFF", "4CC2F0", "5BD67E", "FFB02E", "A78BFA"), sectionInk = c("0B0B0C"),
    panelRadius = 20, panelBorder = 1, chipRadius = 18, chipBorder = 0, buttonRadius = 26, shadow = false, upper = false, bar = false,
    labelFont = R.font.archivo_regular, titleFont = R.font.archivo_bold, chipFont = R.font.archivo_regular, wordmarkFont = R.font.archivo_bold,
)
private val gamer = Look(
    paper = c("0A0A0B"), panel = c("111113"), stroke = a("59FFE01B"), ink = c("F5F5F0"), ink2 = c("9C9A97"), ink3 = c("5E5C59"),
    accent = c("FFE01B"), onAccent = c("0A0A0B"), link = c("FFE01B"), chipFill = a("00FFFFFF"), chipStroke = a("99FFE01B"),
    primary = c("FFE01B"), onPrimary = c("0A0A0B"), headerBlock = c("FFE01B"), onHeader = c("0A0A0B"),
    good = c("FF5A3C"), idle = c("8A8A90"), onStatus = c("0A0A0B"), sections = sections("FFE01B"), sectionInk = c("0A0A0B"),
    panelRadius = 0, panelBorder = 1, chipRadius = 0, chipBorder = 1, buttonRadius = 0, shadow = false, upper = true, bar = true,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.chakra_petch_semibold, chipFont = R.font.chakra_petch_semibold, wordmarkFont = R.font.chakra_petch_bold,
)
private val brutalLight = Look(
    paper = c("E4E4E4"), panel = c("ECECEC"), stroke = c("1A1A1A"), ink = c("1A1A1A"), ink2 = c("4A4A4A"), ink3 = c("8A8A8A"),
    accent = c("D92D14"), onAccent = c("FFFFFF"), link = c("B8220C"), chipFill = a("00FFFFFF"), chipStroke = c("1A1A1A"),
    primary = c("1A1A1A"), onPrimary = c("FFFFFF"), headerBlock = c("1A1A1A"), onHeader = c("FFFFFF"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("D92D14"), sectionInk = c("FFFFFF"),
    panelRadius = 0, panelBorder = 2, chipRadius = 0, chipBorder = 2, buttonRadius = 0, shadow = false, upper = true, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.anton, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.anton,
)
private val brutalDark = Look(
    paper = c("111111"), panel = c("1A1A1A"), stroke = c("E8E8E8"), ink = c("F1F1F1"), ink2 = c("B0B0B0"), ink3 = c("707070"),
    accent = c("FF4A2B"), onAccent = c("0F0F0F"), link = c("FF4A2B"), chipFill = a("00FFFFFF"), chipStroke = c("E8E8E8"),
    primary = c("F1F1F1"), onPrimary = c("111111"), headerBlock = c("F1F1F1"), onHeader = c("111111"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("FF4A2B"), sectionInk = c("0F0F0F"),
    panelRadius = 0, panelBorder = 2, chipRadius = 0, chipBorder = 2, buttonRadius = 0, shadow = false, upper = true, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.anton, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.anton,
)
private val dotLight = Look(
    paper = c("F1F1F1"), panel = c("F7F7F5"), stroke = a("1F1B1D1F"), ink = c("1B1D1F"), ink2 = c("5A5A5A"), ink3 = c("9A9A9A"),
    accent = c("D71921"), onAccent = c("FFFFFF"), link = c("D71921"), chipFill = a("00FFFFFF"), chipStroke = a("661B1D1F"),
    primary = c("D71921"), onPrimary = c("FFFFFF"), headerBlock = c("D71921"), onHeader = c("FFFFFF"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("D71921"), sectionInk = c("FFFFFF"),
    panelRadius = 14, panelBorder = 1, chipRadius = 18, chipBorder = 1, buttonRadius = 26, shadow = false, upper = false, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.doto, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)
private val dotDark = Look(
    paper = c("1B1D1F"), panel = c("232527"), stroke = a("33F1F1F1"), ink = c("F1F1F1"), ink2 = c("A9A9A9"), ink3 = c("6E6E6E"),
    accent = c("FF4B55"), onAccent = c("1B1D1F"), link = c("FF4B55"), chipFill = a("00FFFFFF"), chipStroke = a("66F1F1F1"),
    primary = c("FF4B55"), onPrimary = c("1B1D1F"), headerBlock = c("FF4B55"), onHeader = c("1B1D1F"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("FF4B55"), sectionInk = c("1B1D1F"),
    panelRadius = 14, panelBorder = 1, chipRadius = 18, chipBorder = 1, buttonRadius = 26, shadow = false, upper = false, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.doto, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)

// Prism: white, grey and a rare red (Nothing-phone style) in the soft raised (neumorphic) design language of the owner's
// Metropulse image. Chosen things are charcoal (light) or silver (dark); red only on small accents (links, the header slash).
private val prismLight = Look(
    paper = c("ECEDEF"), panel = c("F6F6F7"), stroke = a("00FFFFFF"), ink = c("1C1C1F"), ink2 = c("55585F"), ink3 = c("9A9DA4"),
    accent = c("D71921"), onAccent = c("FFFFFF"), link = c("C4141B"), chipFill = c("EFEFF1"), chipStroke = a("00FFFFFF"),
    primary = c("2A2A2E"), onPrimary = c("FFFFFF"), headerBlock = c("F6F6F7"), onHeader = c("1C1C1F"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"),
    sections = sections("2A2A2E"), sectionInk = c("FFFFFF"),
    panelRadius = 34, panelBorder = 0, chipRadius = 18, chipBorder = 0, buttonRadius = 30, shadow = true, upper = true, bar = false,
    labelFont = R.font.michroma, titleFont = R.font.michroma, chipFont = R.font.archivo_regular, wordmarkFont = R.font.michroma,
)
private val prismDark = Look(
    paper = c("1A1A1C"), panel = c("242427"), stroke = a("00FFFFFF"), ink = c("F2F2F3"), ink2 = c("B0B2B8"), ink3 = c("77797F"),
    accent = c("FF4750"), onAccent = c("1A1A1C"), link = c("FF5961"), chipFill = c("2E2E32"), chipStroke = a("00FFFFFF"),
    primary = c("E6E6E8"), onPrimary = c("1A1A1C"), headerBlock = c("2E2E32"), onHeader = c("F2F2F3"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"),
    sections = sections("E6E6E8"), sectionInk = c("1A1A1C"),
    panelRadius = 34, panelBorder = 0, chipRadius = 18, chipBorder = 0, buttonRadius = 30, shadow = true, upper = true, bar = false,
    labelFont = R.font.michroma, titleFont = R.font.michroma, chipFont = R.font.archivo_regular, wordmarkFont = R.font.michroma,
)
// Contrast (accessibility): black on white / white on black, thick borders, one strong accent. Same values as the PC theme.
private val contrastLight = Look(
    paper = c("FFFFFF"), panel = c("FFFFFF"), stroke = c("000000"), ink = c("000000"), ink2 = c("262626"), ink3 = c("595959"),
    accent = c("0000CC"), onAccent = c("FFFFFF"), link = c("0000CC"), chipFill = c("FFFFFF"), chipStroke = c("000000"),
    primary = c("000000"), onPrimary = c("FFFFFF"), headerBlock = c("000000"), onHeader = c("FFFFFF"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("000000"), sectionInk = c("FFFFFF"),
    panelRadius = 4, panelBorder = 3, chipRadius = 4, chipBorder = 2, buttonRadius = 4, shadow = false, upper = true, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.plex_mono_semibold, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)
private val contrastDark = Look(
    paper = c("000000"), panel = c("000000"), stroke = c("FFFFFF"), ink = c("FFFFFF"), ink2 = c("E6E6E6"), ink3 = c("BDBDBD"),
    accent = c("FFD400"), onAccent = c("000000"), link = c("FFD400"), chipFill = c("000000"), chipStroke = c("FFFFFF"),
    primary = c("FFD400"), onPrimary = c("000000"), headerBlock = c("FFD400"), onHeader = c("000000"),
    good = c("0F7A3A"), idle = c("6B6B6B"), onStatus = c("FFFFFF"), sections = sections("FFD400"), sectionInk = c("000000"),
    panelRadius = 4, panelBorder = 3, chipRadius = 4, chipBorder = 2, buttonRadius = 4, shadow = false, upper = true, bar = false,
    labelFont = R.font.plex_mono_medium, titleFont = R.font.plex_mono_semibold, chipFont = R.font.plex_mono_medium, wordmarkFont = R.font.doto,
)