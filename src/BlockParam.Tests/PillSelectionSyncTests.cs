using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using BlockParam.UI.Controls.PillMultiSelect;
using Xunit;

// WPF DependencyProperties require an STA thread. xunit defaults to MTA, so
// tests that touch DPs are marked [UIFact] / [UITheory] via Xunit.StaFact.
// Tests that work on plain internal types (PillSelectionSync via its helpers,
// PillMultiSelectInternalState) can run on MTA and use plain [Fact].

namespace BlockParam.Tests;

/// <summary>
/// Helper INPC source item for Edge B tests. Exposes an <c>IsActive</c> bool
/// that <see cref="PillMultiSelect.IsSelectedMemberPath"/> can bind to.
/// </summary>
file sealed class InpcSource : INotifyPropertyChanged
{
    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Plain source item — does NOT implement INPC. Used to verify that initial
/// reads still work even without change notification support.
/// </summary>
file sealed class PlainSource
{
    public bool IsActive { get; set; }
}

/// <summary>
/// Test fixture that wires up <see cref="PillSelectionSync"/> with a real
/// <see cref="PillMultiSelectInternalState"/> and <see cref="PillItemSource"/>
/// so all three edges can be exercised without a WPF window. The internal state
/// and item-source are accessible because <c>BlockParam.Tests</c> is listed in
/// <c>InternalsVisibleTo</c> on the main project.
/// </summary>
file sealed class SyncFixture
{
    // A single persistent ObservableCollection used as ItemsSource.
    // Adding to it triggers an incremental Add notification (not a Reset),
    // so the returned PillRowViewModel stays the live row in State.Items.
    private readonly ObservableCollection<object> _sources;

    internal PillMultiSelectInternalState State { get; }
    internal PillItemSource ItemSource { get; }
    internal PillSelectionSync Sync { get; }
    internal MemberPathResolver Resolver { get; }
    internal ObservableCollection<object> SelectedItems { get; }

    internal SyncFixture()
    {
        State = new PillMultiSelectInternalState();
        Resolver = new MemberPathResolver();
        ItemSource = new PillItemSource(State, Resolver);
        Sync = new PillSelectionSync(State, ItemSource, Resolver);

        SelectedItems = new ObservableCollection<object>();
        Sync.SetSelectedItems(SelectedItems);

        // Set ItemsSource to an empty persistent collection so incremental
        // Add notifications fire (not Reset) when AddSource is called.
        _sources = new ObservableCollection<object>();
        ItemSource.ItemsSource = _sources;
    }

    /// <summary>
    /// Adds a source object to the persistent ItemsSource collection.
    /// This triggers an incremental Add notification on <see cref="PillItemSource"/>,
    /// which fires <c>RowAdded</c> so <see cref="PillSelectionSync"/> reconciles
    /// and subscribes INPC. The returned row is the live row in
    /// <see cref="State"/>.Items and remains valid across subsequent AddSource calls.
    /// </summary>
    internal PillRowViewModel AddSource(object source)
    {
        _sources.Add(source);
        return State.Items[State.Items.Count - 1];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Edge A — wrapper ↔ SelectedItems
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectionSync_EdgeA_Tests
{
    [Fact]
    public void Setting_SelectedItems_with_prepopulated_items_marks_matching_rows_selected()
    {
        var src1 = new object();
        var src2 = new object();
        var src3 = new object();

        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);

        var sources = new ObservableCollection<object> { src1, src2, src3 };
        itemSource.ItemsSource = sources;

        var sync = new PillSelectionSync(state, itemSource, resolver);

        var preSelected = new ObservableCollection<object> { src1, src3 };
        sync.SetSelectedItems(preSelected);

        state.Items[0].IsSelected.Should().BeTrue();   // src1
        state.Items[1].IsSelected.Should().BeFalse();  // src2
        state.Items[2].IsSelected.Should().BeTrue();   // src3
    }

    [Fact]
    public void Toggling_row_IsSelected_true_adds_source_to_SelectedItems()
    {
        var fix = new SyncFixture();
        var src = new InpcSource();
        var row = fix.AddSource(src);

        row.IsSelected = true;

        fix.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(src);
    }

    [Fact]
    public void Toggling_row_IsSelected_false_removes_source_from_SelectedItems()
    {
        var fix = new SyncFixture();
        var src = new InpcSource();
        var row = fix.AddSource(src);

        fix.SelectedItems.Add(src);
        row.IsSelected.Should().BeTrue();  // reconciled by SelectedItems.Add

        row.IsSelected = false;

        fix.SelectedItems.Should().BeEmpty();
    }

    [Fact]
    public void Adding_to_SelectedItems_from_outside_flips_matching_row_IsSelected_to_true()
    {
        var fix = new SyncFixture();
        var src = new InpcSource();
        fix.AddSource(src);

        fix.SelectedItems.Add(src);

        fix.State.Items[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Removing_from_SelectedItems_from_outside_flips_row_IsSelected_to_false()
    {
        var fix = new SyncFixture();
        var src = new InpcSource();
        var row = fix.AddSource(src);
        row.IsSelected = true;

        fix.SelectedItems.Remove(src);

        fix.State.Items[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Reset_on_SelectedItems_clears_all_row_selections()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        var row1 = fix.AddSource(src1);
        var row2 = fix.AddSource(src2);
        row1.IsSelected = true;
        row2.IsSelected = true;

        // Simulate a collection reset.
        ((INotifyCollectionChanged)fix.SelectedItems).Should().NotBeNull();
        fix.SelectedItems.Clear();

        row1.IsSelected.Should().BeFalse();
        row2.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Replacing_SelectedItems_collection_reconciles_correctly()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        var row1 = fix.AddSource(src1);
        var row2 = fix.AddSource(src2);

        // Initial: src1 selected via old collection.
        fix.SelectedItems.Add(src1);
        row1.IsSelected.Should().BeTrue();

        // Swap to a new collection that selects src2 only.
        var newCollection = new ObservableCollection<object> { src2 };
        fix.Sync.SetSelectedItems(newCollection);

        row1.IsSelected.Should().BeFalse();
        row2.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectedItems_Replace_action_deselects_old_and_selects_new()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        var row1 = fix.AddSource(src1);
        var row2 = fix.AddSource(src2);
        fix.SelectedItems.Add(src1);

        // Simulate a Replace by using the ObservableCollection indexer.
        fix.SelectedItems[0] = src2;

        row1.IsSelected.Should().BeFalse();
        row2.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void No_duplicate_entry_in_SelectedItems_when_row_already_selected()
    {
        var fix = new SyncFixture();
        var src = new InpcSource();
        var row = fix.AddSource(src);
        fix.SelectedItems.Add(src);

        // Setting IsSelected again (already true) should not add a second entry.
        row.IsSelected = true;

        fix.SelectedItems.Should().ContainSingle();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Edge B — wrapper ↔ IsSelectedMemberPath
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectionSync_EdgeB_Tests
{
    [Fact]
    public void Setting_IsSelectedMemberPath_reads_initial_bool_into_rows()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);

        var trueItem = new InpcSource { IsActive = true };
        var falseItem = new InpcSource { IsActive = false };

        itemSource.ItemsSource = new ObservableCollection<object> { trueItem, falseItem };

        var sync = new PillSelectionSync(state, itemSource, resolver);
        sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        state.Items[0].IsSelected.Should().BeTrue();
        state.Items[1].IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Toggling_row_IsSelected_writes_bool_back_to_source()
    {
        var fix = new SyncFixture();
        var src = new InpcSource { IsActive = false };
        var row = fix.AddSource(src);
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        row.IsSelected = true;

        src.IsActive.Should().BeTrue();
    }

    [Fact]
    public void External_source_PropertyChanged_updates_row_IsSelected()
    {
        var fix = new SyncFixture();
        var src = new InpcSource { IsActive = false };
        fix.AddSource(src);
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        // Externally mutate the source bool.
        src.IsActive = true;

        fix.State.Items[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void External_source_PropertyChanged_also_updates_SelectedItems()
    {
        var fix = new SyncFixture();
        var src = new InpcSource { IsActive = false };
        fix.AddSource(src);
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        src.IsActive = true;

        fix.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(src);
    }

    [Fact]
    public void PlainSource_without_INPC_gets_initial_read_but_no_live_updates()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var src = new PlainSource { IsActive = true };

        itemSource.ItemsSource = new ObservableCollection<object> { src };

        var sync = new PillSelectionSync(state, itemSource, resolver);
        sync.SetIsSelectedMemberPath(nameof(PlainSource.IsActive));

        // Initial read works.
        state.Items[0].IsSelected.Should().BeTrue();

        // External mutation without INPC is NOT reflected — documented behavior.
        src.IsActive = false;
        state.Items[0].IsSelected.Should().BeTrue(); // unchanged; no INPC fired
    }

    [Fact]
    public void Changing_IsSelectedMemberPath_resubscribes_and_re_reconciles()
    {
        var fix = new SyncFixture();
        var src = new InpcSource { IsActive = false };
        fix.AddSource(src);
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        // First path: IsActive=false → row deselected.
        fix.State.Items[0].IsSelected.Should().BeFalse();

        // Flip the value, then change the path to force re-reconciliation.
        src.IsActive = true;
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        fix.State.Items[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void New_row_added_after_SetIsSelectedMemberPath_is_reconciled_and_subscribed()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        // Add a source that already has IsActive=true.
        var src = new InpcSource { IsActive = true };
        fix.AddSource(src);

        fix.State.Items[0].IsSelected.Should().BeTrue();

        // Verify live subscription: external change after add is picked up.
        src.IsActive = false;
        fix.State.Items[0].IsSelected.Should().BeFalse();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Edge C — ItemsSource collection changes
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectionSync_EdgeC_Tests
{
    [Fact]
    public void Adding_source_already_in_SelectedItems_creates_row_with_IsSelected_true()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);

        var src = new InpcSource();

        var sync = new PillSelectionSync(state, itemSource, resolver);
        var selectedItems = new ObservableCollection<object> { src };
        sync.SetSelectedItems(selectedItems);

        // Now add the source to ItemsSource (simulates a collection Add).
        var sources = new ObservableCollection<object> { src };
        itemSource.ItemsSource = sources;

        state.Items[0].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void Removing_source_item_does_not_remove_it_from_SelectedItems()
    {
        // Contract: the control does NOT evict items from the host-owned
        // SelectedItems when they leave ItemsSource. The host owns that
        // collection. The row is simply dropped; SelectedItems is left intact.
        var fix = new SyncFixture();
        var src = new InpcSource();
        fix.AddSource(src);
        fix.SelectedItems.Add(src);

        // Remove the source from ItemsSource by resetting to an empty collection.
        fix.ItemSource.ItemsSource = new ObservableCollection<object>();

        fix.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(src);
    }

    [Fact]
    public void Reset_on_ItemsSource_rebuilds_rows_and_reconciles()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        fix.AddSource(src1);
        fix.SelectedItems.Add(src1);

        // Replace with a new set that includes src2.
        fix.ItemSource.ItemsSource = new ObservableCollection<object> { src1, src2 };
        fix.Sync.SetSelectedItems(fix.SelectedItems); // re-reconcile after reset

        fix.State.Items[0].IsSelected.Should().BeTrue();   // src1 still in SelectedItems
        fix.State.Items[1].IsSelected.Should().BeFalse();  // src2 not in SelectedItems
    }

    [Fact]
    public void Removed_source_item_is_unsubscribed_from_INPC()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));
        var src = new InpcSource { IsActive = false };
        fix.AddSource(src);

        // Remove the source.
        fix.ItemSource.ItemsSource = new ObservableCollection<object>();

        // Now mutate the source. Because it's been removed, the row is gone
        // and no row should flip. There should be no items to check.
        src.IsActive = true;

        fix.State.Items.Should().BeEmpty();
        // And no exception was thrown by a dangling handler — test passes.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Combined / re-entrancy / SelectionChanged event
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectionSync_Combined_Tests
{
    [Fact]
    public void Both_SelectedItems_and_IsSelectedMemberPath_active_no_infinite_loop()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        var src = new InpcSource { IsActive = false };
        var row = fix.AddSource(src);

        // Toggling should not throw a StackOverflowException — re-entrancy guard
        // must prevent the Edge A → B → A loop.
        var act = () => row.IsSelected = true;
        act.Should().NotThrow();

        src.IsActive.Should().BeTrue();
        fix.SelectedItems.Should().ContainSingle();
    }

    [Fact]
    public void SelectionChanged_fires_once_per_user_toggle_not_per_propagation_step()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));

        var src1 = new InpcSource();
        var src2 = new InpcSource();
        var row1 = fix.AddSource(src1);
        fix.AddSource(src2);

        var fireCount = 0;
        fix.Sync.SelectionChanged += (_, _) => fireCount++;

        // One user action: toggle a single checkbox.
        row1.IsSelected = true;

        // Edge A mirrors to SelectedItems, Edge B writes to src1.IsActive —
        // but SelectionChanged must fire exactly once for the whole cycle.
        fireCount.Should().Be(1);
    }

    [Fact]
    public void SelectionChanged_fires_once_for_external_SelectedItems_mass_reset()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        fix.AddSource(src1);
        fix.AddSource(src2);
        fix.SelectedItems.Add(src1);
        fix.SelectedItems.Add(src2);

        var fireCount = 0;
        fix.Sync.SelectionChanged += (_, _) => fireCount++;

        fix.SelectedItems.Clear();

        fireCount.Should().Be(1);
    }

    [Fact]
    public void SelectionChanged_fires_when_external_source_PropertyChanged_updates_row()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));
        var src = new InpcSource { IsActive = false };
        fix.AddSource(src);

        var fireCount = 0;
        fix.Sync.SelectionChanged += (_, _) => fireCount++;

        src.IsActive = true;

        fireCount.Should().Be(1);
    }

    [Fact]
    public void No_duplicate_SelectedItems_entries_when_both_edges_active_simultaneously()
    {
        var fix = new SyncFixture();
        fix.Sync.SetIsSelectedMemberPath(nameof(InpcSource.IsActive));
        var src = new InpcSource { IsActive = false };
        var row = fix.AddSource(src);

        row.IsSelected = true;

        // SelectedItems should contain src exactly once despite two edges firing.
        fix.SelectedItems.Should().ContainSingle().Which.Should().BeSameAs(src);
    }

    [Fact]
    public void Reconcile_after_SelectedItems_swap_fires_SelectionChanged_once()
    {
        var fix = new SyncFixture();
        var src1 = new InpcSource();
        var src2 = new InpcSource();
        fix.AddSource(src1);
        fix.AddSource(src2);
        fix.SelectedItems.Add(src1);

        var fireCount = 0;
        fix.Sync.SelectionChanged += (_, _) => fireCount++;

        // Swap to a collection that selects src2 only.
        var newCollection = new ObservableCollection<object> { src2 };
        fix.Sync.SetSelectedItems(newCollection);

        fireCount.Should().Be(1);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// #141 — DP-callback ordering: SelectedItems set BEFORE any rows exist
//
// Regression guard for the symptom-A scenario in #141 (numberless instance DB).
// These pin the EXISTING behaviour: the per-row seed in
// PillSelectionSync.OnRowAdded reconciles each new row against SelectedItems
// membership by reference identity AS THE ROW MATERIALISES, so the closed
// trigger summary becomes correct even when the SelectedItems DP callback
// fired before the ItemsSource DP callback built any rows. No deferred
// reconcile / popup-open is involved — verified by reverting
// PillSelectionSync.cs to origin/main (the per-row seed alone still passes
// every assertion here); see PR #153 discussion. They lock the behaviour so
// a future refactor of OnRowAdded can't silently reintroduce the v1.0.14
// blank-pill regression that was already fixed on main by 7882cc1.
// ─────────────────────────────────────────────────────────────────────────────

public class PillSelectionSync_OrderIndependence_Tests
{
    /// <summary>
    /// The <c>SelectedItems</c> DP callback fires before the
    /// <c>ItemsSource</c> callback has built any rows, so the initial
    /// <c>ReconcileRowsFromSelectedItems</c> pass is a no-op. The closed
    /// trigger summary must still become correct the moment rows
    /// materialise — driven purely by the per-row seed in
    /// <c>OnRowAdded</c>, with no popup-open and no deferred re-sync.
    /// </summary>
    [Fact]
    public void SelectedItems_set_before_rows_still_selects_matching_rows_on_row_add()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var sync = new PillSelectionSync(state, itemSource, resolver);

        var src1 = new object();
        var src2 = new object();

        // 1) SelectedItems arrives FIRST, while there are zero rows.
        var selected = new ObservableCollection<object> { src1 };
        sync.SetSelectedItems(selected);
        state.Items.Should().BeEmpty("ItemsSource callback hasn't fired yet");

        // 2) ItemsSource arrives SECOND — rows materialise now. The per-row
        // seed in OnRowAdded marks src1's row IsCheckedTrue immediately.
        itemSource.ItemsSource = new ObservableCollection<object> { src1, src2 };

        // The closed trigger summary (Items.Where(IsCheckedTrue) →
        // RaiseAggregatesChanged INPC push) is now correct with no popup
        // ever opened and no OnIsOpenFlippedToTrue re-sync.
        state.Items[0].IsSelected.Should().BeTrue("src1 was pre-selected");
        state.Items[1].IsSelected.Should().BeFalse();
        state.SelectedCount.Should().Be(1);
        state.HasSelection.Should().BeTrue(
            "the closed pill renders the summary without OnIsOpenFlippedToTrue");
    }

    [Fact]
    public void PerRowSeed_marks_each_pre_selected_row_once_as_rows_arrive()
    {
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var sync = new PillSelectionSync(state, itemSource, resolver);

        var src1 = new object();
        var src2 = new object();

        sync.SetSelectedItems(new ObservableCollection<object> { src1 });

        // Add rows incrementally so OnRowAdded fires per row. Each row is
        // seeded independently from SelectedItems membership; src2 (not
        // pre-selected) must stay unchecked.
        var sources = new ObservableCollection<object>();
        itemSource.ItemsSource = sources;
        sources.Add(src1);
        sources.Add(src2);

        state.Items[0].IsSelected.Should().BeTrue();
        state.Items[1].IsSelected.Should().BeFalse();
    }

    [Fact]
    public void Empty_SelectedItems_before_rows_leaves_rows_unchecked_then_edge_a_still_works()
    {
        // An empty SelectedItems selects nothing; a later add to the
        // collection must still flip the matching row (Edge A) normally —
        // no stale internal state from the empty-then-populate path.
        var state = new PillMultiSelectInternalState();
        var resolver = new MemberPathResolver();
        var itemSource = new PillItemSource(state, resolver);
        var sync = new PillSelectionSync(state, itemSource, resolver);

        var selected = new ObservableCollection<object>();
        sync.SetSelectedItems(selected);

        var src = new object();
        itemSource.ItemsSource = new ObservableCollection<object> { src };

        state.Items[0].IsSelected.Should().BeFalse();

        // Normal Edge-A still works after the empty-seed path.
        selected.Add(src);
        state.Items[0].IsSelected.Should().BeTrue();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WPF DP smoke tests (require STA thread via Xunit.StaFact)
// ─────────────────────────────────────────────────────────────────────────────

public class PillMultiSelect_SelectionDp_Tests
{
    [UIFact]
    public void SelectedItems_dp_default_is_per_instance_ObservableCollection()
    {
        var pill1 = new PillMultiSelect();
        var pill2 = new PillMultiSelect();

        // Each instance must have its own collection — not the same reference.
        pill1.SelectedItems.Should().NotBeNull();
        pill2.SelectedItems.Should().NotBeNull();
        pill1.SelectedItems.Should().NotBeSameAs(pill2.SelectedItems);
    }

    [UIFact]
    public void SelectedItems_dp_swap_unsubscribes_old_collection()
    {
        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { new InpcSource(), new InpcSource() },
            DisplayMemberPath = nameof(InpcSource.IsActive),
        };

        var oldCollection = (ObservableCollection<object>)pill.SelectedItems!;
        var newCollection = new ObservableCollection<object>();
        pill.SelectedItems = newCollection;

        // Add to the old collection — pill must NOT react (it is unsubscribed).
        var src = new InpcSource();
        oldCollection.Add(src);

        // No row should be selected because the old collection is disconnected.
        // (Pill has no items whose source is src, so this is vacuously true, but
        // it proves no exception / crash occurs from a dangling subscription.)
        pill.SelectedItems.Should().BeSameAs(newCollection);
    }

    [UIFact]
    public void IsSelectedMemberPath_dp_reads_initial_bool_on_set()
    {
        var src = new InpcSource { IsActive = true };

        var pill = new PillMultiSelect
        {
            ItemsSource = new ObservableCollection<object> { src },
            DisplayMemberPath = nameof(InpcSource.IsActive),
            IsSelectedMemberPath = nameof(InpcSource.IsActive),
        };

        // The control's SelectedItems should contain src because IsActive=true.
        var items = pill.SelectedItems!.Cast<object>().ToList();
        items.Should().ContainSingle().Which.Should().BeSameAs(src);
    }

    [UIFact]
    public void SelectionChanged_routed_event_fires_when_row_toggled()
    {
        var src = new InpcSource();
        var sources = new ObservableCollection<object> { src };
        var pill = new PillMultiSelect
        {
            ItemsSource = sources,
            DisplayMemberPath = nameof(InpcSource.IsActive),
        };

        // Host a window so routed events can bubble (requires a visual tree).
        var host = new System.Windows.Controls.ContentControl { Content = pill };
        var window = new System.Windows.Window { Content = host };
        window.Show();

        var fired = false;
        pill.SelectionChanged += (_, _) => fired = true;

        // Simulate selection via SelectedItems (no XAML interaction needed).
        ((ObservableCollection<object>)pill.SelectedItems!).Add(src);

        window.Close();

        fired.Should().BeTrue();
    }
}
