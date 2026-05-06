using System.Collections.Generic;

namespace BlockParam.UI;

/// <summary>
/// Per-PLC group of active-DB chips. Lifts the PLC name out of every chip
/// into a single dim header so long PLC names (e.g.
/// <c>CPU-LB-6-1_V26_01_13_SL_MM</c>) don't blow up each chip's width.
/// In single-PLC sessions <see cref="HasPlcHeader"/> is false and the
/// header label is hidden, leaving just a clean row of DB chips.
/// </summary>
public class ActiveDbChipGroupViewModel
{
    public ActiveDbChipGroupViewModel(
        string plcName,
        IReadOnlyList<ActiveDbChipViewModel> chips,
        bool showHeader)
    {
        PlcName = plcName;
        Chips = chips;
        HasPlcHeader = showHeader && !string.IsNullOrEmpty(plcName);
    }

    public string PlcName { get; }
    public bool HasPlcHeader { get; }
    public IReadOnlyList<ActiveDbChipViewModel> Chips { get; }
}
