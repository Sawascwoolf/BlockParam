using System.Linq;
using BlockParam.Config;
using BlockParam.Licensing;
using BlockParam.Models;
using BlockParam.Services;
using BlockParam.SimaticML;
using BlockParam.UI;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Coverage for #190 — the anchor DB's identity (<see cref="ActiveDb.PlcName"/>)
/// must always carry the real PLC name, even in single-PLC projects, while
/// whether the "{PLC} / " chrome is shown stays a separate decision
/// (<c>showPlcChrome</c>, derived from <c>plcCount &gt; 1</c>).
///
/// <para>
/// Root cause recap: <c>BulkChangeContextMenu</c> used to seed the anchor's
/// identity from a display-only <c>displayPlcName</c> (blanked out for
/// single-PLC projects), while the dropdown's own row for that same DB
/// carried the real PLC name from <see cref="DataBlockSummary"/> (which never
/// blanks it). <see cref="ActiveSetViewModel"/> matches active rows on
/// <c>(Name, PlcName)</c>, so the anchor stopped matching its own row —
/// rendering an unchecked checkbox for an already-active DB, and a second
/// <see cref="ActiveDb"/> (duplicate pill) if the user "checked" it.
/// </para>
/// </summary>
public class BulkChangeViewModelPlcIdentityTests
{
    [Fact]
    public void SinglePlcProject_AnchorIdentity_MatchesItsOwnDropdownRow()
    {
        // The dropdown lists the SAME physical DB the anchor was built from,
        // with the real PLC name (exactly what DataBlockSummary always
        // carries — see DataBlockSummary's own "Identity is (PlcName,
        // FolderPath, Name)" doc). If the host correctly seeds the anchor's
        // identity with the real name (currentPlcName: "PLC_1", not ""),
        // GetActiveStatusFor must resolve the row as already active/anchor —
        // no separate "unchecked" row for a DB that's already open.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        var ownRow = new DataBlockSummary(info.Name, "", plcName: "PLC_1");

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_1",
            showPlcChrome: false,
            enumerateDataBlocks: () => new[] { ownRow },
            switchToDataBlock: _ => xml);

        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);

        var row = vm.ActiveSet.FilteredDataBlockItems.Should().ContainSingle().Subject;
        row.IsActive.Should().BeTrue(
            "the anchor's own dropdown row must resolve as active — its identity " +
            "(Name, PlcName) must match the ActiveDb the dialog was opened with");
        row.IsAnchor.Should().BeTrue();

        // No accidental second pill for the same PLC/DB.
        vm.ActiveSet.PlcPills.Should().ContainSingle();
        vm.AllActiveDbs.Should().ContainSingle();
    }

    [Fact]
    public void AddActiveDbToSet_RefusesDuplicate_WhenBuiltIdentityAlreadyActive()
    {
        // Defense-in-depth for the #190 shape: even if a caller upstream
        // hands the dropdown a row whose *summary* PlcName doesn't match the
        // active set (so GetActiveStatusFor renders it unchecked), the
        // built ActiveDb's real identity might still collide with what's
        // already active — e.g. buildActiveDbForSummary resolving the DB
        // against the project (real PLC) while the row's summary carried a
        // stale/blank PlcName. Checking that row must not append a second
        // ActiveDb for the same physical block.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);

        var configLoader = new ConfigLoader(null);
        var bulkService = new BulkChangeService(new ChangeLogger(), configLoader);
        var tracker = Substitute.For<IUsageTracker>();
        tracker.GetStatus().Returns(new UsageStatus(0, 100));

        // The dropdown row's summary carries a mismatched (blank) PlcName,
        // so it reads as inactive even though the same physical DB is
        // already the anchor under its real identity ("PLC_1").
        var mismatchedRow = new DataBlockSummary(info.Name, "", plcName: "");

        var vm = new BulkChangeViewModel(
            info, xml,
            new HierarchyAnalyzer(), bulkService, tracker, configLoader,
            currentPlcName: "PLC_1",
            showPlcChrome: false,
            enumerateDataBlocks: () => new[] { mismatchedRow },
            switchToDataBlock: _ => xml,
            // Simulates a builder that resolves against the real project PLC
            // regardless of what the row's summary said — exactly what
            // BulkChangeContextMenu's buildActiveDbForSummary does today.
            buildActiveDbForSummary: _ => new ActiveDb(info, xml, onApply: null, plcName: "PLC_1"));

        vm.ActiveSet.OpenDataBlocksDropdownCommand.Execute(null);
        var row = vm.ActiveSet.FilteredDataBlockItems.Should().ContainSingle().Subject;
        row.IsActive.Should().BeFalse(
            "setup: the mismatched summary must NOT resolve as active — otherwise " +
            "this test isn't exercising the AddActiveDbToSet path at all");

        row.IsActive = true; // user checks the (wrongly-unchecked) row

        vm.AllActiveDbs.Should().ContainSingle(
            "the dedup guard must refuse to append a second ActiveDb for a DB " +
            "that's already active under its real identity");
    }

    [Fact]
    public void AddActiveDbFromSummary_RefusesDuplicate_WhenBuiltIdentityAlreadyActive()
    {
        // Same guard, exercised via the pill-popup add path (#169
        // PillSelectionCoordinator → ActiveSetViewModel.AddActiveDbFromSummary)
        // rather than the dropdown-row path above.
        var xml = TestFixtures.LoadXml("flat-db.xml");
        var info = new SimaticMLParser().Parse(xml);
        var summary = new DataBlockSummary(info.Name, "", plcName: "PLC_1");

        var initial = new ActiveSetState(
            new[] { new ActiveDb(info, xml, onApply: null, plcName: "PLC_1") },
            new System.Collections.Generic.Dictionary<string, StashedDbState>(),
            anchorPlcName: "PLC_1");

        var vm = new ActiveSetViewModel(
            initial,
            messageBox: null,
            pendingEditStore: null,
            getModelToDb: null,
            getStartValueForNode: _ => null,
            buildActiveDbForSummary: _ => new ActiveDb(info, xml, onApply: null, plcName: "PLC_1"),
            enumerateDataBlocks: null,
            switchToDataBlock: null,
            tryApplyActiveDbInPlace: null,
            restoreStashOntoLive: null,
            setStatus: _ => { },
            getPendingCount: () => 0,
            dispatcher: null);

        int stateChangedCount = 0;
        vm.StateChanged += (_, _) => stateChangedCount++;

        vm.AddActiveDbFromSummary(summary);

        stateChangedCount.Should().Be(0, "a refused duplicate add must not install a new snapshot");
        vm.State.Dbs.Should().ContainSingle();
    }
}
