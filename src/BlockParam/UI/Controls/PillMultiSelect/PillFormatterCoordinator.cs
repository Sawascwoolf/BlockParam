using System;
using System.Collections.Generic;
using System.Linq;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Owns the precedence logic between the DP-driven formatters
/// (<see cref="PillMultiSelect.OverflowOptions"/>,
/// <see cref="PillMultiSelect.TooltipMode"/>) and the CLR escape-hatch
/// formatters (<c>DisplayFormatter</c>, <c>TooltipFormatter</c>).
/// </summary>
/// <remarks>
/// Precedence rules (highest wins):
/// <list type="number">
/// <item><description>
///   CLR <c>DisplayFormatter</c> set by the host overrides
///   <c>OverflowOptions</c> DP. Cleared by setting <c>DisplayFormatter = null</c>,
///   which re-evaluates the DP.
/// </description></item>
/// <item><description>
///   CLR <c>TooltipFormatter</c> set by the host overrides
///   <c>TooltipMode</c> DP. Cleared by setting <c>TooltipFormatter = null</c>,
///   which re-evaluates the DP.
/// </description></item>
/// </list>
/// These rules are intentional: code hosts that reach for the Func escape
/// hatches typically have a reason (custom sorting, contextual phrasing)
/// that the DP can't express. The DP is a convenience; the Func is the
/// authoritative override.
/// </remarks>
internal sealed class PillFormatterCoordinator
{
    private readonly PillMultiSelectInternalState _state;
    private readonly PillItemSource _itemSource;

    // Tracks whether the host has set a custom CLR formatter so DP changes
    // don't silently overwrite it.
    private bool _customDisplayFormatterSet;
    private bool _customTooltipFormatterSet;

    // The latest DP values — re-applied when the CLR override is cleared.
    private PillOverflowOptions? _overflowOptions;
    private PillTooltipMode _tooltipMode = PillTooltipMode.None;

    internal PillFormatterCoordinator(
        PillMultiSelectInternalState state,
        PillItemSource itemSource)
    {
        _state = state;
        _itemSource = itemSource;
    }

    // ── OverflowOptions DP (sets DisplayFormatter) ────────────────────────────

    /// <summary>
    /// Called from the UserControl's <c>OverflowOptions</c> DP changed callback.
    /// No-ops when a CLR <c>DisplayFormatter</c> has already been installed —
    /// the host-set formatter takes precedence.
    /// </summary>
    internal void OnOverflowOptionsChanged(PillOverflowOptions? options)
    {
        _overflowOptions = options;
        if (!_customDisplayFormatterSet)
            ApplyOverflowOptionsToState(options);
    }

    // ── TooltipMode DP ────────────────────────────────────────────────────────

    /// <summary>
    /// Called from the UserControl's <c>TooltipMode</c> DP changed callback.
    /// No-ops when a CLR <c>TooltipFormatter</c> has already been installed.
    /// </summary>
    internal void OnTooltipModeChanged(PillTooltipMode mode)
    {
        _tooltipMode = mode;
        if (!_customTooltipFormatterSet)
            ApplyTooltipModeToState(mode);
    }

    // ── CLR DisplayFormatter escape hatch ─────────────────────────────────────

    private Func<IReadOnlyList<object>, string>? _publicDisplayFormatter;

    /// <summary>
    /// Custom trigger-summary formatter. Receives the source items (not wrapper
    /// rows) of the currently selected entries. Return value replaces the entire
    /// trigger summary text.
    /// <para>
    /// <b>Precedence</b>: overrides <see cref="PillMultiSelect.OverflowOptions"/>
    /// when both are set. Setting this back to <c>null</c> clears the override
    /// and re-activates any <c>OverflowOptions</c> DP value.
    /// </para>
    /// </summary>
    internal Func<IReadOnlyList<object>, string>? DisplayFormatter
    {
        get => _publicDisplayFormatter;
        set
        {
            _publicDisplayFormatter = value;
            _customDisplayFormatterSet = value != null;

            if (value != null)
            {
                // Wrap: project rows → source items, then invoke host delegate.
                _state.DisplayFormatter = rows =>
                    value(rows.Select(r => r.Source).ToList());
            }
            else
            {
                // Host cleared the override — re-apply the DP value.
                ApplyOverflowOptionsToState(_overflowOptions);
            }
        }
    }

    // ── CLR TooltipFormatter escape hatch ─────────────────────────────────────

    private Func<IReadOnlyList<object>, string?>? _publicTooltipFormatter;

    /// <summary>
    /// Custom tooltip formatter. Receives the source items of selected entries.
    /// Return <c>null</c> to suppress the tooltip.
    /// <para>
    /// <b>Precedence</b>: overrides <see cref="PillMultiSelect.TooltipMode"/>
    /// when both are set. Setting this back to <c>null</c> clears the override
    /// and re-activates any <c>TooltipMode</c> DP value.
    /// </para>
    /// </summary>
    internal Func<IReadOnlyList<object>, string?>? TooltipFormatter
    {
        get => _publicTooltipFormatter;
        set
        {
            _publicTooltipFormatter = value;
            _customTooltipFormatterSet = value != null;

            if (value != null)
            {
                _state.TooltipFormatter = rows =>
                    value(rows.Select(r => r.Source).ToList());
            }
            else
            {
                ApplyTooltipModeToState(_tooltipMode);
            }
        }
    }

    // ── CLR DisplaySelector escape hatch ──────────────────────────────────────

    /// <summary>
    /// Per-item display-string override. Takes precedence over
    /// <see cref="PillMultiSelect.DisplayMemberPath"/>. Existing rows are
    /// re-resolved immediately when this is set.
    /// Falls back to <c>obj.ToString()</c> when the delegate is <c>null</c>
    /// and no member path is set.
    /// </summary>
    internal Func<object, string>? DisplaySelector
    {
        get => _itemSource.DisplayOverride;
        set => _itemSource.DisplayOverride = value;
    }

    // ── CLR AbbreviationSelector escape hatch ─────────────────────────────────

    /// <summary>
    /// Per-item abbreviation-string override. Takes precedence over
    /// <see cref="PillMultiSelect.AbbreviationMemberPath"/>. Existing rows
    /// are re-resolved immediately when this is set.
    /// Falls back to <see cref="DisplaySelector"/> (or Display) when <c>null</c>.
    /// </summary>
    internal Func<object, string>? AbbreviationSelector
    {
        get => _itemSource.AbbreviationOverride;
        set => _itemSource.AbbreviationOverride = value;
    }

    // ── CLR FilterPredicate escape hatch ──────────────────────────────────────

    private Func<object, string, bool>? _publicFilterPredicate;

    /// <summary>
    /// Custom search-filter predicate. Receives a source item and the current
    /// search text; return <c>true</c> to include the item in results.
    /// Overrides the default Display/Abbreviation contains-check when set.
    /// Setting back to <c>null</c> restores the default check.
    /// </summary>
    internal Func<object, string, bool>? FilterPredicate
    {
        get => _publicFilterPredicate;
        set
        {
            _publicFilterPredicate = value;
            _state.CustomFilter = value == null
                ? null
                : (row, text) => value(row.Source, text);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ApplyOverflowOptionsToState(PillOverflowOptions? options)
    {
        _state.DisplayFormatter = options != null
            ? selected => PillOverflowFormatter.FormatRows(selected, options)
            : (Func<IReadOnlyList<PillRowViewModel>, string>?)null;
    }

    private void ApplyTooltipModeToState(PillTooltipMode mode)
    {
        _state.TooltipFormatter = BuildTooltipFormatterFor(mode);
    }

    /// <summary>
    /// Returns the row-level formatter delegate that corresponds to
    /// <paramref name="mode"/>. Returns <c>null</c> for <see cref="PillTooltipMode.None"/>,
    /// which WPF renders as "no tooltip".
    /// </summary>
    private static Func<IReadOnlyList<PillRowViewModel>, string?>? BuildTooltipFormatterFor(
        PillTooltipMode mode) =>
        mode switch
        {
            PillTooltipMode.FullNames =>
                rows => PillTooltipFormatters.FullNamesRows(rows),
            PillTooltipMode.AbbrevAndFullNames =>
                rows => PillTooltipFormatters.AbbrevAndFullNamesRows(rows),
            _ => null,
        };
}
