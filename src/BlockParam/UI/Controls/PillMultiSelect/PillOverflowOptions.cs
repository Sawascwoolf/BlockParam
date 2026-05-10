using BlockParam.Localization;

namespace BlockParam.UI.Controls.PillMultiSelect;

/// <summary>
/// Three independent thresholds that govern how the trigger summary
/// degrades as the selection grows. Each is nullable — null means the
/// behavior never triggers. <see cref="PillOverflowFormatter"/> applies
/// abbreviation FIRST, then the "+N more" collapse, so both can fire on
/// the same selection (e.g. 8 long DB names → switch to DB-numbers AND
/// hide the trailing few behind "+3 more").
/// </summary>
public class PillOverflowOptions
{
    private string? _plusMoreFormat;

    /// <summary>
    /// Switch from full <see cref="PillRowViewModel.Display"/>
    /// to short <see cref="PillRowViewModel.Abbreviation"/>
    /// when the selection has more than this many entries. Null disables
    /// the count-based switch.
    /// </summary>
    public int? AbbreviateAfterEntries { get; set; }

    /// <summary>
    /// Switch from full Display to short Abbreviation when the joined
    /// full-display string would exceed this many characters. Null
    /// disables the width-based switch. Whichever of <see cref="AbbreviateAfterEntries"/>
    /// or this triggers first wins; they OR together.
    /// </summary>
    public int? AbbreviateAfterChars { get; set; }

    /// <summary>
    /// After the abbreviation decision is made, show only the first N
    /// items and append "+M more" for the rest. Null disables the
    /// collapse, in which case the full (possibly-abbreviated) list is
    /// rendered.
    /// </summary>
    public int? CollapseAfterEntries { get; set; }

    /// <summary>
    /// Format string for the suffix appended once <see cref="CollapseAfterEntries"/>
    /// trips. Must contain a single <c>{0}</c> placeholder for the hidden-
    /// item count. Defaults to the localized "+{0} more" pulled from
    /// BlockParam's resx; host apps not shipping that resx can set this
    /// directly (e.g. <c>"and {0} others"</c>) to avoid the dependency.
    /// </summary>
    public string PlusMoreFormat
    {
        get => _plusMoreFormat ?? Res.Get("PillMultiSelect_PlusMore");
        set => _plusMoreFormat = value;
    }

    /// <summary>
    /// Sensible defaults for the data-block use case: switch to DB-numbers
    /// once 4 are selected OR the joined names exceed 30 chars, and
    /// collapse to "+N more" past 5 visible items.
    /// </summary>
    public static PillOverflowOptions DataBlockDefault() => new()
    {
        AbbreviateAfterEntries = 4,
        AbbreviateAfterChars = 30,
        CollapseAfterEntries = 5,
    };
}
