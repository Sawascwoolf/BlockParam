using System;
using System.Collections.Generic;
using System.Linq;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for <see cref="PillSelectionCoordinator"/> (#169).
/// Validates cascade ordering, re-entrancy guard, and no-op on equal
/// selections without going through the full ActiveSetViewModel.
/// </summary>
public class PillSelectionCoordinatorTests
{
    [Fact]
    public void RebuildPlcPills_ProducesOnePillPerPlc()
    {
        var h = new Harness(Db("DB1", "PLC1"), Db("DB2", "PLC2"));
        h.Coordinator.RebuildPlcPills();

        h.Coordinator.PlcPills.Should().HaveCount(2);
        h.Coordinator.PlcPills[0].PlcName.Should().Be("PLC1");
        h.Coordinator.PlcPills[1].PlcName.Should().Be("PLC2");
    }

    [Fact]
    public void RebuildPlcPills_UnsubscribesOldPillsBeforeRebuilding()
    {
        var h = new Harness(Db("DB1", "PLC1"));
        h.Coordinator.RebuildPlcPills();
        var oldPill = h.Coordinator.PlcPills[0];

        h.Coordinator.RebuildPlcPills();

        // The old pill's SelectionChanged should be unsubscribed — firing it
        // after rebuild must not trigger any add/remove on the active set.
        h.AddedSummaries.Clear();
        oldPill.SyncSelectedDbs(Array.Empty<DataBlockListItem>());
        h.AddedSummaries.Should().BeEmpty("old pill is unsubscribed after rebuild");
    }

    [Fact]
    public void OnPillSelectionChanged_Added_CallsAddActiveDbFromSummary()
    {
        var h = new Harness(Db("DB1", "PLC1"));
        h.Coordinator.RebuildPlcPills();

        var pill = h.Coordinator.PlcPills[0];
        var newSummary = new DataBlockSummary("DB2", "", plcName: "PLC1");
        var item = new DataBlockListItem(newSummary, false, false);
        pill.AvailableDbs.Add(item);
        pill.SelectedDbs.Add(item);

        h.AddedSummaries.Should().ContainSingle()
            .Which.Name.Should().Be("DB2");
    }

    [Fact]
    public void OnPillSelectionChanged_Removed_CallsRemoveActiveDb()
    {
        var db1 = Db("DB1", "PLC1");
        var db2 = Db("DB2", "PLC1");
        var h = new Harness(db1, db2);
        h.Coordinator.RebuildPlcPills();

        var pill = h.Coordinator.PlcPills[0];
        // Simulate removing the second selected item from the pill
        var toRemove = pill.SelectedDbs.Cast<DataBlockListItem>()
            .FirstOrDefault(i => i.Name == "DB2");
        if (toRemove != null)
            pill.SelectedDbs.Remove(toRemove);

        h.RemovedDbs.Should().ContainSingle()
            .Which.Info.Name.Should().Be("DB2");
    }

    [Fact]
    public void OnPillSelectionChanged_RefusesRemovalOfLastDb()
    {
        var h = new Harness(Db("DB1", "PLC1"));
        h.Coordinator.RebuildPlcPills();

        var pill = h.Coordinator.PlcPills[0];
        var item = pill.SelectedDbs.Cast<DataBlockListItem>().First();
        pill.SelectedDbs.Remove(item);

        h.RemovedDbs.Should().BeEmpty("last DB must not be removable");
        // Pill should be re-synced to still show the DB as selected
        pill.SelectedDbs.Should().NotBeEmpty("pill snaps back to active set");
    }

    [Fact]
    public void Guard_PreventsReEntrantCascade()
    {
        // When addActiveDbFromSummary triggers a rebuild (which re-syncs pills),
        // the coordinator must not re-enter OnPillSelectionChanged.
        int addCount = 0;
        var db1 = Db("DB1", "PLC1");
        var state = Snap(db1);
        PillSelectionCoordinator? coordinator = null;
        coordinator = new PillSelectionCoordinator(
            getState: () => state,
            getActiveStatusFor: s => (false, false),
            addActiveDbFromSummary: s =>
            {
                addCount++;
                coordinator!.RebuildPlcPills();
            },
            findActiveDb: s => null,
            removeActiveDb: db => { },
            hasDataBlockSwitcher: () => false,
            loadAvailableDataBlocks: _ => { },
            getAvailableDataBlocks: () => null,
            hasEnumerateDataBlocks: false,
            onDataBlockListItemToggled: null);

        coordinator.RebuildPlcPills();
        var pill = coordinator.PlcPills[0];
        var newItem = new DataBlockListItem(
            new DataBlockSummary("DB2", "", plcName: "PLC1"), false, false);
        pill.AvailableDbs.Add(newItem);
        pill.SelectedDbs.Add(newItem);

        addCount.Should().Be(1, "guard prevents re-entrant cascade");
    }

    [Fact]
    public void NoOpOnEqualSelection_DoesNotCallMutators()
    {
        var h = new Harness(Db("DB1", "PLC1"));
        h.Coordinator.RebuildPlcPills();

        var pill = h.Coordinator.PlcPills[0];
        // Adding an item that's already active should be a no-op
        var alreadyActive = new DataBlockListItem(
            new DataBlockSummary("DB1", "", plcName: "PLC1"), true, true);
        pill.AvailableDbs.Add(alreadyActive);
        pill.SelectedDbs.Add(alreadyActive);

        h.AddedSummaries.Should().BeEmpty("already-active item is a no-op");
    }

    // ---------- helpers ----------

    private static ActiveSetState Snap(params ActiveDb[] dbs)
        => new ActiveSetState(
            dbs,
            new Dictionary<string, StashedDbState>(),
            dbs.Length > 0 ? (dbs[0].PlcName ?? "") : "");

    private static ActiveDb Db(string name, string plc = "")
    {
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>());
        return new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plc);
    }

    private class Harness
    {
        private ActiveSetState _state;
        private readonly List<ActiveDb> _activeDbs;

        public List<DataBlockSummary> AddedSummaries { get; } = new();
        public List<ActiveDb> RemovedDbs { get; } = new();
        public PillSelectionCoordinator Coordinator { get; }

        public Harness(params ActiveDb[] dbs)
        {
            _activeDbs = dbs.ToList();
            _state = Snap(dbs);

            Coordinator = new PillSelectionCoordinator(
                getState: () => _state,
                getActiveStatusFor: s =>
                {
                    for (int i = 0; i < _state.Dbs.Count; i++)
                    {
                        var db = _state.Dbs[i];
                        if (string.Equals(db.Info.Name, s.Name, StringComparison.Ordinal)
                            && string.Equals(db.PlcName, s.PlcName, StringComparison.Ordinal))
                            return (true, i == 0);
                    }
                    return (false, false);
                },
                addActiveDbFromSummary: s => AddedSummaries.Add(s),
                findActiveDb: s =>
                {
                    foreach (var db in _state.Dbs)
                    {
                        if (string.Equals(db.Info.Name, s.Name, StringComparison.Ordinal)
                            && string.Equals(db.PlcName, s.PlcName, StringComparison.Ordinal))
                            return db;
                    }
                    return null;
                },
                removeActiveDb: db =>
                {
                    RemovedDbs.Add(db);
                    var next = _state.Dbs.Where(d => !ReferenceEquals(d, db)).ToList();
                    _state = new ActiveSetState(next, _state.Stashes, _state.AnchorPlcName);
                },
                hasDataBlockSwitcher: () => false,
                loadAvailableDataBlocks: _ => { },
                getAvailableDataBlocks: () => null,
                hasEnumerateDataBlocks: false,
                onDataBlockListItemToggled: null);
        }
    }
}
