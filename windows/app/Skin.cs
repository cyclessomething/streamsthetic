using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhoneCam;

/// <summary>
/// Attached properties the themes (Studio, Creator, Gamer) use, so one layout and one set of control templates can look
/// different per theme without code changes (docs/DESIGN_PATTERNS.md "Themes"):
/// <list type="bullet">
/// <item><b>Upper</b>: show a TextBlock's text in CAPITALS (Studio, Gamer) or as typed (Creator). The XAML and the code
/// always write natural text ("Frame rate"); the theme decides how it is displayed.</item>
/// <item><b>SectionBrush</b>, <b>Glyph</b>: the color and icon of a panel; chips inside a panel inherit the color.</item>
/// </list>
/// </summary>
public static class Skin
{
    // ---- text case -------------------------------------------------------------------------------------------

    public static readonly DependencyProperty UpperProperty = DependencyProperty.RegisterAttached(
        "Upper", typeof(bool), typeof(Skin), new PropertyMetadata(false, OnUpperChanged));

    public static bool GetUpper(DependencyObject d) => (bool)d.GetValue(UpperProperty);
    public static void SetUpper(DependencyObject d, bool value) => d.SetValue(UpperProperty, value);

    private sealed class CaseState
    {
        public string Original = "";
        public string Shown = "";
        public bool Applying;
    }

    private static readonly ConditionalWeakTable<TextBlock, CaseState> States = new();
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private static void OnUpperChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        if (!States.TryGetValue(block, out _))
        {
            States.Add(block, new CaseState { Original = block.Text, Shown = block.Text });
            // text set later (code, bindings) is re-cased too
            TextDescriptor.AddValueChanged(block, (_, _) => Apply(block));
        }
        Apply(block);
    }

    private static void Apply(TextBlock block)
    {
        if (!States.TryGetValue(block, out var state) || state.Applying) return;
        var current = block.Text ?? "";
        if (current != state.Shown) state.Original = current; // someone set new text: that is the new original
        var wanted = GetUpper(block) ? state.Original.ToUpperInvariant() : state.Original;
        state.Shown = wanted;
        if (current == wanted) return;
        state.Applying = true;
        // SetCurrentValue, not an assignment: assigning would REPLACE a binding, and later changes of the bound text
        // (the dropdown's shown choice, the Start/Stop label) would never reach the screen again
        try { block.SetCurrentValue(TextBlock.TextProperty, wanted); }
        finally { state.Applying = false; }
    }

    // ---- panel color and icon -------------------------------------------------------------------------------

    public static readonly DependencyProperty SectionBrushProperty = DependencyProperty.RegisterAttached(
        "SectionBrush", typeof(Brush), typeof(Skin),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static Brush? GetSectionBrush(DependencyObject d) => (Brush?)d.GetValue(SectionBrushProperty);
    public static void SetSectionBrush(DependencyObject d, Brush? value) => d.SetValue(SectionBrushProperty, value);

    // fill of one panel (themes can tint each panel differently; others use the same fill everywhere)
    public static readonly DependencyProperty PanelFillProperty = DependencyProperty.RegisterAttached(
        "PanelFill", typeof(Brush), typeof(Skin), new PropertyMetadata(null));

    public static Brush? GetPanelFill(DependencyObject d) => (Brush?)d.GetValue(PanelFillProperty);
    public static void SetPanelFill(DependencyObject d, Brush? value) => d.SetValue(PanelFillProperty, value);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Skin), new PropertyMetadata(""));

    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string value) => d.SetValue(GlyphProperty, value);
}
