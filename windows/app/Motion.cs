using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PhoneCam;

/// <summary>Small shared animations: things slide/fade in instead of snapping (docs/DESIGN_PATTERNS.md, Motion).</summary>
public static class Motion
{
    private static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades an element in while it rises a few pixels from below.</summary>
    public static void RiseIn(FrameworkElement element, double fromY = 10, int ms = 260, int delayMs = 0)
    {
        if (element.RenderTransform is not TranslateTransform move || move.IsFrozen)
        {
            move = new TranslateTransform();
            element.RenderTransform = move;
        }
        var delay = TimeSpan.FromMilliseconds(delayMs);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = delay, EasingFunction = Out });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = delay, EasingFunction = Out });
    }

    /// <summary>Shows or hides an element; showing it rises in. (Hiding is immediate: the layout below it must not hang.)</summary>
    public static void Reveal(FrameworkElement element, bool show)
    {
        var wasVisible = element.Visibility == Visibility.Visible;
        element.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !wasVisible && element.IsLoaded) RiseIn(element, 8, 240);
    }

    /// <summary>Whatever tab is selected rises in when the selection changes.</summary>
    public static void AnimateTabs(TabControl tabs)
    {
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.OriginalSource != tabs) return; // not a selection inside a ComboBox in the tab
            if (tabs.SelectedItem is TabItem { Content: FrameworkElement content } && tabs.IsLoaded) RiseIn(content, 10, 240);
        };
    }
}
