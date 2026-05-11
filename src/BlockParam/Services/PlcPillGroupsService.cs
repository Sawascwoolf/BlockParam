using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BlockParam.Models;
using BlockParam.UI;

namespace BlockParam.Services;

/// <summary>
/// Pure mapper: converts the current active-DB set into a list of
/// <see cref="PlcPillViewModel"/>s, one per distinct PLC that has at least
/// one active DB. Mirrors the per-PLC grouping logic from
/// <c>BulkChangeViewModel.RebuildActiveDbChips</c> but produces pills
/// instead of chip groups.
///
/// <para>
/// Anchor rule: the PLC that owns the first active DB (index 0, whose PLC
/// name comes from <paramref name="anchorPlcName"/>) is placed first.
/// All other PLCs appear in stable insertion order.
/// </para>
/// </summary>
public static class PlcPillGroupsService
{
    /// <summary>
    /// Builds the pill list for the current active set.
    /// </summary>
    /// <param name="activeDbs">The VM's current active-DB list.</param>
    /// <param name="anchorPlcName">
    ///     PLC name for the anchor (index 0). May be empty for single-PLC or
    ///     DevLauncher sessions — the anchor's pill will show an empty label.
    /// </param>
    /// <param name="loadDbsForPlc">
    ///     Async loader injected from <c>BulkChangeViewModel</c>. The pill calls
    ///     this on first open to populate its item list.
    /// </param>
    /// <returns>
    ///     One <see cref="PlcPillViewModel"/> per distinct PLC, in anchor-first
    ///     stable order.
    /// </returns>
    public static IReadOnlyList<PlcPillViewModel> Build(
        IReadOnlyList<ActiveDb> activeDbs,
        string anchorPlcName,
        Func<string, Task<IReadOnlyList<DataBlockListItem>>> loadDbsForPlc)
    {
        if (activeDbs.Count == 0)
            return Array.Empty<PlcPillViewModel>();

        // Group active DBs by PLC, preserving insertion order (anchor first).
        var orderedPlcs = new List<string>();
        var perPlc = new Dictionary<string, List<DataBlockListItem>>(StringComparer.Ordinal);

        for (int i = 0; i < activeDbs.Count; i++)
        {
            var db = activeDbs[i];
            // Index 0 reads PLC name from anchorPlcName — mirrors
            // BulkChangeViewModel.RebuildActiveDbChips lines 1102–1157.
            var plc = (i == 0 ? anchorPlcName : db.PlcName) ?? "";
            // DataBlockInfo.Number is int (never null for a parsed block).
            var item = new DataBlockListItem(
                new DataBlockSummary(
                    db.Info.Name,
                    folderPath: "",
                    plcName: plc,
                    number: db.Info.Number),
                isActive: true,
                isAnchor: i == 0);

            if (!perPlc.TryGetValue(plc, out var list))
            {
                list = new List<DataBlockListItem>();
                perPlc[plc] = list;
                orderedPlcs.Add(plc);
            }
            list.Add(item);
        }

        // Show the PLC label on each pill only when the project is multi-PLC
        // (anchorPlcName non-empty). Single-PLC sessions show an empty label
        // so the pill chrome stays clean.
        bool multiPlc = !string.IsNullOrEmpty(anchorPlcName);

        var pills = new List<PlcPillViewModel>(orderedPlcs.Count);
        for (int idx = 0; idx < orderedPlcs.Count; idx++)
        {
            var plc = orderedPlcs[idx];
            var activeItems = perPlc[plc];
            var pill = new PlcPillViewModel(
                plcName: plc,
                isAnchor: idx == 0,
                initialActiveItems: activeItems,
                loadDbs: loadDbsForPlc);

            pill.Label = multiPlc ? plc : "";
            pills.Add(pill);
        }

        return pills;
    }
}
