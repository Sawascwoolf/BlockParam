using System.Windows;
using System.Windows.Controls;

namespace BlockParam.UI.Controls;

/// <summary>
/// Attached behavior that gives a <see cref="ListView"/>'s <see cref="GridView"/>
/// one "star" column that expands to fill whatever horizontal space the fixed
/// columns leave behind — mirroring the star-sizing that WPF
/// <see cref="Grid"/> columns support natively but <see cref="GridViewColumn"/>
/// does not (#9 / #84).
///
/// <para>
/// Usage in XAML:
/// <code>
///   &lt;GridViewColumn x:Name="CommentColumn" …/&gt;
///   …
///   &lt;ListView local:GridViewStarColumnBehavior.StarColumn="{x:Reference CommentColumn}"
///              local:GridViewStarColumnBehavior.FixedColumnsWidth="540"
///              local:GridViewStarColumnBehavior.MinStarWidth="160"
///              SizeChanged="…" /&gt;
/// </code>
/// Or, without a code-behind SizeChanged at all — the behavior subscribes to
/// the host ListView's <c>SizeChanged</c> internally and resizes whenever the
/// ListView width changes.
/// </para>
///
/// <para>
/// <b>Chrome constant:</b> <see cref="ChromeWidth"/> accounts for the
/// vertical scrollbar track + row padding. Keep it slightly conservative so
/// a horizontal scrollbar never appears when space is tight. Override via the
/// <c>ChromeWidth</c> attached property if the ListView uses non-default chrome.
/// </para>
/// </summary>
public static class GridViewStarColumnBehavior
{
    // ── Attached properties ──────────────────────────────────────────────────

    /// <summary>The <see cref="GridViewColumn"/> that receives the star width.</summary>
    public static readonly DependencyProperty StarColumnProperty =
        DependencyProperty.RegisterAttached(
            "StarColumn",
            typeof(GridViewColumn),
            typeof(GridViewStarColumnBehavior),
            new PropertyMetadata(null, OnParameterChanged));

    /// <summary>
    /// Sum of the fixed columns' widths (Name + DataType + StartValue in the
    /// member ListView). The star column gets:
    /// <c>Max(MinStarWidth, ListView.ActualWidth - FixedColumnsWidth - ChromeWidth)</c>.
    /// </summary>
    public static readonly DependencyProperty FixedColumnsWidthProperty =
        DependencyProperty.RegisterAttached(
            "FixedColumnsWidth",
            typeof(double),
            typeof(GridViewStarColumnBehavior),
            new PropertyMetadata(0d, OnParameterChanged));

    /// <summary>Minimum width the star column is allowed to reach (default 160 DIPs).</summary>
    public static readonly DependencyProperty MinStarWidthProperty =
        DependencyProperty.RegisterAttached(
            "MinStarWidth",
            typeof(double),
            typeof(GridViewStarColumnBehavior),
            new PropertyMetadata(160d, OnParameterChanged));

    /// <summary>
    /// Horizontal chrome to subtract (scrollbar track + row padding).
    /// Default 32 DIPs — same conservative constant the old code-behind used.
    /// </summary>
    public static readonly DependencyProperty ChromeWidthProperty =
        DependencyProperty.RegisterAttached(
            "ChromeWidth",
            typeof(double),
            typeof(GridViewStarColumnBehavior),
            new PropertyMetadata(32d, OnParameterChanged));

    // ── Accessors ────────────────────────────────────────────────────────────

    public static GridViewColumn? GetStarColumn(DependencyObject d) =>
        (GridViewColumn?)d.GetValue(StarColumnProperty);
    public static void SetStarColumn(DependencyObject d, GridViewColumn? value) =>
        d.SetValue(StarColumnProperty, value);

    public static double GetFixedColumnsWidth(DependencyObject d) =>
        (double)d.GetValue(FixedColumnsWidthProperty);
    public static void SetFixedColumnsWidth(DependencyObject d, double value) =>
        d.SetValue(FixedColumnsWidthProperty, value);

    public static double GetMinStarWidth(DependencyObject d) =>
        (double)d.GetValue(MinStarWidthProperty);
    public static void SetMinStarWidth(DependencyObject d, double value) =>
        d.SetValue(MinStarWidthProperty, value);

    public static double GetChromeWidth(DependencyObject d) =>
        (double)d.GetValue(ChromeWidthProperty);
    public static void SetChromeWidth(DependencyObject d, double value) =>
        d.SetValue(ChromeWidthProperty, value);

    // ── Change handler ───────────────────────────────────────────────────────

    private static void OnParameterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView lv) return;

        // Subscribe once; subsequent calls are no-ops because the lambda
        // captures the ListView by reference and reads its current attached
        // property values on every SizeChanged event.
        lv.SizeChanged -= OnListViewSizeChanged;
        lv.SizeChanged += OnListViewSizeChanged;

        // Apply immediately in case the parameter changed while the ListView
        // is already laid out (e.g. during a theme switch or a test harness).
        Apply(lv);
    }

    private static void OnListViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is ListView lv) Apply(lv);
    }

    private static void Apply(ListView lv)
    {
        var column = GetStarColumn(lv);
        if (column == null) return;

        var fixed_ = GetFixedColumnsWidth(lv);
        var chrome = GetChromeWidth(lv);
        var min = GetMinStarWidth(lv);

        var available = lv.ActualWidth - fixed_ - chrome;
        column.Width = System.Math.Max(min, available);
    }
}
