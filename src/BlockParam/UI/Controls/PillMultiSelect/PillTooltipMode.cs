namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Controls which built-in tooltip strategy the <see cref="PillMultiSelect"/>
/// trigger pill uses. Set via the <c>TooltipMode</c> DependencyProperty for
/// XAML hosts; code-only hosts that need full custom logic use the
/// <c>TooltipFormatter</c> CLR escape hatch instead (which overrides this DP
/// when both are set).
/// </summary>
public enum PillTooltipMode
{
    /// <summary>
    /// No tooltip is shown on the trigger pill. Default.
    /// </summary>
    None,

    /// <summary>
    /// Shows one full display name per line. Useful when the trigger
    /// collapses or abbreviates the visible label and the user needs to
    /// recover the full names on hover.
    /// </summary>
    FullNames,

    /// <summary>
    /// Shows "<c>Abbrev — Display</c>" per line. Useful when the trigger
    /// shows only abbreviations and the user also wants the expanded mapping.
    /// </summary>
    AbbrevAndFullNames,
}
