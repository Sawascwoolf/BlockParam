using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BlockParam.Models;
using BlockParam.UI;
using FluentAssertions;
using Xunit;

namespace BlockParam.Tests;

/// <summary>
/// Focused tests for the active-set state container (#80 slice 8a).
/// Slice 8a owns the <see cref="ActiveSetState"/> snapshot + the bound
/// <c>StashedDbs</c> collection; mutators still live on the host VM.
/// The contracts under test:
///
/// <list type="bullet">
///   <item><c>SetState</c> swaps the snapshot and raises
///       <c>StateChanged(old, new)</c> with the previous snapshot, not
///       the new one twice.</item>
///   <item><c>SetState</c> with a reference-equal snapshot is a no-op
///       (no event, no StashedDbs churn).</item>
///   <item><c>StashedDbs</c> mirror is rebuilt only when the
///       <c>Stashes</c> dictionary reference changed, not on every
///       SetState — so a Dbs-only mutation doesn't perturb the bound
///       collection.</item>
///   <item><c>StashedDbs</c> entries are sorted (FolderPath, DbName)
///       so display order is stable across snapshot swaps.</item>
///   <item><c>HasStashedDbs</c> tracks the mirror count and raises
///       PropertyChanged when it flips.</item>
/// </list>
/// </summary>
public class ActiveSetViewModelTests
{
    [Fact]
    public void Constructor_NullInitial_Throws()
    {
        Action act = () => new ActiveSetViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_SeedsStateAndEmptyStashes()
    {
        var initial = Snap(Db("Foo"));

        var vm = new ActiveSetViewModel(initial);

        vm.State.Should().BeSameAs(initial);
        vm.StashedDbs.Should().BeEmpty();
        vm.HasStashedDbs.Should().BeFalse();
    }

    [Fact]
    public void Constructor_SeedsStashesIntoBoundCollection()
    {
        var stash = Stash("DBa", "/a");
        var initial = Snap(Db("Anchor"), stashes: new Dictionary<string, StashedDbState>
        {
            ["k"] = stash,
        });

        var vm = new ActiveSetViewModel(initial);

        vm.StashedDbs.Should().ContainSingle().Which.Should().BeSameAs(stash);
        vm.HasStashedDbs.Should().BeTrue();
    }

    [Fact]
    public void SetState_RaisesStateChangedWithOldAndNew()
    {
        var old = Snap(Db("A"));
        var vm = new ActiveSetViewModel(old);
        var next = Snap(Db("A"), Db("B"));

        (ActiveSetState? captOld, ActiveSetState? captNew) = (null, null);
        vm.StateChanged += (o, n) => { captOld = o; captNew = n; };

        vm.SetState(next);

        captOld.Should().BeSameAs(old, "old snapshot is the one being replaced");
        captNew.Should().BeSameAs(next);
        vm.State.Should().BeSameAs(next);
    }

    [Fact]
    public void SetState_SameReference_IsNoOp()
    {
        var snap = Snap(Db("A"));
        var vm = new ActiveSetViewModel(snap);
        int events = 0;
        vm.StateChanged += (_, _) => events++;

        vm.SetState(snap);

        events.Should().Be(0);
    }

    [Fact]
    public void SetState_Null_Throws()
    {
        var vm = new ActiveSetViewModel(Snap(Db("A")));

        Action act = () => vm.SetState(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SetState_DbsOnlyChange_DoesNotResyncStashedDbs()
    {
        // Same Stashes reference → mirror sync skipped (perf + stability:
        // the inspector section's IsExpanded state survives Dbs-only
        // changes like Add / Solo / Remove that don't touch stashes).
        var stash = Stash("X");
        var stashes = new Dictionary<string, StashedDbState> { ["k"] = stash };
        var initial = Snap(Db("A"), stashes: stashes);
        var vm = new ActiveSetViewModel(initial);
        var sentinel = vm.StashedDbs[0];

        vm.SetState(initial.With(dbs: new[] { Db("A"), Db("B") }));

        vm.StashedDbs.Should().HaveCount(1);
        vm.StashedDbs[0].Should().BeSameAs(sentinel,
            "Dbs-only change reuses the same StashedDbState instance");
    }

    [Fact]
    public void SetState_StashesChanged_RebuildsBoundCollection()
    {
        var s1 = Stash("OldDb");
        var initial = Snap(Db("A"), stashes: new Dictionary<string, StashedDbState>
        {
            ["k1"] = s1,
        });
        var vm = new ActiveSetViewModel(initial);
        var s2 = Stash("NewDb");

        vm.SetState(initial.With(stashes: new Dictionary<string, StashedDbState>
        {
            ["k2"] = s2,
        }));

        vm.StashedDbs.Should().ContainSingle().Which.Should().BeSameAs(s2);
    }

    [Fact]
    public void SetState_StashesSortedByFolderPathThenName()
    {
        var b = Stash("Beta", folder: "FolderA");
        var a = Stash("Alpha", folder: "FolderA");
        var z = Stash("Zeta", folder: "FolderB");

        var initial = Snap(Db("Anchor"), stashes: new Dictionary<string, StashedDbState>
        {
            ["k1"] = z,
            ["k2"] = b,
            ["k3"] = a,
        });

        var vm = new ActiveSetViewModel(initial);

        vm.StashedDbs.Select(s => s.DbName).Should().Equal("Alpha", "Beta", "Zeta");
    }

    [Fact]
    public void SetState_StashesAppearingFromEmpty_FlipsHasStashedDbsAndNotifies()
    {
        var vm = new ActiveSetViewModel(Snap(Db("A")));
        var raised = CapturePropertyChanges(vm);
        var s = Stash("New");

        vm.SetState(vm.State.With(stashes: new Dictionary<string, StashedDbState>
        {
            ["k"] = s,
        }));

        vm.HasStashedDbs.Should().BeTrue();
        raised.Should().Contain(nameof(ActiveSetViewModel.HasStashedDbs));
    }

    [Fact]
    public void SetState_StashesDisappearingToEmpty_FlipsHasStashedDbsAndNotifies()
    {
        var initial = Snap(Db("A"), stashes: new Dictionary<string, StashedDbState>
        {
            ["k"] = Stash("X"),
        });
        var vm = new ActiveSetViewModel(initial);
        var raised = CapturePropertyChanges(vm);

        vm.SetState(initial.With(stashes: new Dictionary<string, StashedDbState>()));

        vm.HasStashedDbs.Should().BeFalse();
        raised.Should().Contain(nameof(ActiveSetViewModel.HasStashedDbs));
    }

    [Fact]
    public void SetState_DbsOnlyChange_DoesNotRaiseHasStashedDbsPropertyChanged()
    {
        // Dbs-only swap shouldn't fire HasStashedDbs PropertyChanged — the
        // mirror isn't re-synced, so the OnPropertyChanged inside the sync
        // method never runs. Cheap check that the no-op path is honored.
        var initial = Snap(Db("A"), stashes: new Dictionary<string, StashedDbState>
        {
            ["k"] = Stash("X"),
        });
        var vm = new ActiveSetViewModel(initial);
        var raised = CapturePropertyChanges(vm);

        vm.SetState(initial.With(dbs: new[] { Db("A"), Db("B") }));

        raised.Should().NotContain(nameof(ActiveSetViewModel.HasStashedDbs));
    }

    [Fact]
    public void StashedDbs_CollectionInstanceIsStableAcrossSnapshots()
    {
        // XAML bindings hold the ObservableCollection reference — the slice
        // mutates it in place via Clear() + Add() rather than replacing the
        // collection. Guards against a future "set StashedDbs = new …()"
        // regression that would silently break ItemsControl bindings.
        var vm = new ActiveSetViewModel(Snap(Db("A")));
        var collection = vm.StashedDbs;

        vm.SetState(vm.State.With(stashes: new Dictionary<string, StashedDbState>
        {
            ["k"] = Stash("S"),
        }));

        vm.StashedDbs.Should().BeSameAs(collection);
    }

    // --- helpers ---

    private static ActiveSetState Snap(
        ActiveDb anchor,
        IDictionary<string, StashedDbState>? stashes = null,
        string anchorPlcName = "")
        => new ActiveSetState(
            new[] { anchor },
            stashes != null
                ? new Dictionary<string, StashedDbState>(stashes)
                : new Dictionary<string, StashedDbState>(),
            anchorPlcName);

    private static ActiveSetState Snap(params ActiveDb[] dbs)
        => new ActiveSetState(
            dbs,
            new Dictionary<string, StashedDbState>(),
            "");

    private static ActiveDb Db(string name, string plc = "")
    {
        var info = new DataBlockInfo(name, 1, "Optimized", "GlobalDB", Array.Empty<MemberNode>());
        return new ActiveDb(info, $"<Block name='{name}' />", onApply: null, plcName: plc);
    }

    private static StashedDbState Stash(string name, string folder = "")
    {
        var summary = new DataBlockSummary(name, folder, plcName: "", number: 1);
        return new StashedDbState(summary, Array.Empty<StashedEditEntry>());
    }

    private static List<string?> CapturePropertyChanges(INotifyPropertyChanged vm)
    {
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
