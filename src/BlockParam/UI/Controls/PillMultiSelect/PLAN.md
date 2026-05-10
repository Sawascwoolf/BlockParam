# PillMultiSelect — Reusable Control v1 Plan

> **Status**: in progress. Delete this file once Phase 4 lands.

## Why

Today the control is a `UserControl` driven by a publicly-instantiated
`PillMultiSelectViewModel`. Every host must hand-populate items, manually
search the wrapper VM by `Payload` to apply initial selection, and listen
to `INotifyPropertyChanged` events on a derived string property to detect
selection changes. There's no `ItemsSource` for XAML hosts and no
`SelectedItems` collection or `SelectionChanged` event. As an internal
helper in BlockParam this is fine; as a control reused across multiple
host apps it falls over.

The lift turns it into a WPF-idiomatic bindable control — `ItemsSource`,
`SelectedItems`, member paths — so any host can wire it up like
`ListBox` / `ComboBox` and stop knowing about the wrapper VM.

## Locked v1 design decisions

| Decision | Choice | Rationale |
|---|---|---|
| Base class | `UserControl` + DependencyProperties | Reuses existing XAML; ~2.5 days; promotion to a templated `Control` later is mechanical if a host ever needs to retheme. |
| `ItemTemplate` | Not in v1 | `DisplayMemberPath` + `AbbreviationMemberPath` cover ~95% of cases. Keeps row layout (checkbox, indent, text) under control ownership so grouping/tri-state stays consistent across consumers. |
| `Func` escape hatches | Kept as plain CLR properties | DPs cover the XAML 95% case; code-only hosts wanting fully custom formatting don't have to subclass. |
| `SelectedItems` ownership | Control-owned default `ObservableCollection<object>`, host can rebind two-way | Matches `ListBox.SelectedItems` idiom; zero-config use just works. |
| `IsSelectedMemberPath` | Included in v1 alongside `SelectedItems` | "Decoration of an enum" hosts (sensors/drives) already carry an `IsActive` bool — letting them bind it directly is more ergonomic than mirroring into a separate collection. Both patterns coexist; wrapper-row `IsSelected` is the canonical internal source of truth. |
| Internal class names | `PillMultiSelectInternalState`, `PillRowViewModel` (both `internal`) | Clearer than the old `PillMultiSelectViewModel` / `PillMultiSelectItemViewModel` once they're no longer the public face of the control. |
| `SelectedItems` DP type | `IList` (default `ObservableCollection<object>`) | DPs can't be generic. Matches `ListBox.SelectedItems`. Hosts can two-way-bind any `IList<T>` they want. |
| `TooltipMode` | New enum DP `{ None, FullNames, AbbrevAndFullNames }` for XAML hosts; `TooltipFormatter` Func kept as code escape hatch | XAML hosts get a discoverable enum; code hosts retain full control. |
| Localization defaults | Unchanged: `Res.Get` fallback in property getters; setters override | BlockParam stays unchanged; non-BlockParam hosts set the strings directly via DPs and never reference `BlockParam.Localization`. |
| Grouping (scenario B: decoration of an enum) | **Deferred to v2** | View-level concern over a flat `IEnumerable<T>`. Additive — doesn't change v1 DP shape. v1 keeps the seams open (nullable `GroupKey` slot on the row VM, `ICollectionView.GroupDescriptions` hook untouched, row template doesn't bake in "no indent column"). |
| Group-aware summary collapse ("Sensors" instead of "LB, +N more") | **Deferred to v2** | Same group infrastructure feeds it. v1 keeps `PillOverflowFormatter.Format` signature stable; v2 adds an overload that accepts group context. |

## v1 public API surface

### DependencyProperties on `PillMultiSelect` (the UserControl)

| DP | Type | Default | Purpose |
|---|---|---|---|
| `ItemsSource` | `IEnumerable` | `null` | Source items, any `T`. Watches `INotifyCollectionChanged` for incremental updates when present. |
| `SelectedItems` | `IList` | per-instance `ObservableCollection<object>` | Two-way bindable selection. Modifying it from host code updates the popup checkboxes; user toggles update it. |
| `IsSelectedMemberPath` | `string` | `null` | Path to a `bool` property on each source item. When set, the control reads/writes that property directly (and listens for external mutations via `INotifyPropertyChanged`). |
| `DisplayMemberPath` | `string` | `null` | Path to the full-name string. Falls back to `ToString()`. |
| `AbbreviationMemberPath` | `string` | `null` | Path to the short-form string. Falls back to `DisplayMemberPath`. |
| `Label` | `string` | `""` | Trigger pill caption. |
| `Icon` | `Geometry` | `null` | Trigger leading icon. |
| `OverflowOptions` | `PillOverflowOptions` | new w/ no thresholds | Trigger summary degradation rules. |
| `TooltipMode` | `PillTooltipMode` enum | `None` | `None` / `FullNames` / `AbbrevAndFullNames`. |
| `PopupWidth` | `double` | `280` | Popup chrome width. |
| `PopupMaxListHeight` | `double` | `280` | Vertical scroll cap on the item list. |
| `ShowSearchBox` | `bool` | `true` | Search row visibility. |
| `ShowFooterActions` | `bool` | `true` | Footer (Select all / Reset / count) visibility. |
| `ClearTooltip` | `string` | resx fallback | Tooltip on the trigger's X button. |
| `SelectAllText` | `string` | resx fallback | "Select all" footer button caption. |
| `ResetText` | `string` | resx fallback | "Reset" footer button caption. |
| `SearchPlaceholder` | `string` | resx fallback | Search box placeholder. |

### Routed event

```csharp
public static readonly RoutedEvent SelectionChangedEvent = ...;
public event RoutedEventHandler SelectionChanged { add; remove; }
```

Fires once per "selection set changed" — deduplicated, not once per checkbox toggle when `SelectedItems` is mass-replaced.

### Code-only escape hatches (plain CLR properties on the UserControl)

```csharp
public Func<object, string>? DisplaySelector { get; set; }
public Func<object, string>? AbbreviationSelector { get; set; }
public Func<IReadOnlyList<object>, string>? DisplayFormatter { get; set; }
public Func<IReadOnlyList<object>, string?>? TooltipFormatter { get; set; }
public Func<object, string, bool>? FilterPredicate { get; set; }
```

When these are set, they take precedence over the matching member-path or
mode DP. They're plain CLR properties (not DPs) — they don't bind from
XAML, but code-only hosts can drop them in for full custom rendering.

## Internal architecture

```
PillMultiSelect (UserControl, public, ~250 LOC code-behind)
  │
  ├── DPs declared statically; per-instance state via property-changed callbacks
  ├── _internalState : PillMultiSelectInternalState  (created in ctor)
  ├── _itemSource    : PillItemSource                (handles ItemsSource → rows)
  ├── _selectionSync : PillSelectionSync             (handles wrapper ↔ SelectedItems / IsSelected member path)
  └── _memberPathResolver : MemberPathResolver       (caches PropertyDescriptors)
  │
  ▼
PillMultiSelectInternalState (internal, was PillMultiSelectViewModel)
  ├── ObservableCollection<PillRowViewModel> _rows
  ├── ListCollectionView _filteredView  (search + snapshot ordering)
  ├── IsOpen, SearchText, SortSelectedFirst, layout knobs, formatters
  └── XAML inside the UserControl binds against this via DataContext

PillRowViewModel (internal, was PillMultiSelectItemViewModel)
  ├── object Source             ← reference to the host's item
  ├── string Display, Abbreviation  (resolved via MemberPathResolver)
  ├── bool IsSelected           ← canonical internal selection state
  ├── bool WasSelectedAtSort    ← snapshot ordering
  └── object? GroupKey          ← v1: always null. v2 grouping populates this.
```

**Why a separate `PillMultiSelectInternalState`** rather than collapsing
into the UserControl: lets the existing XAML keep using
`{Binding IsOpen, ...}`-style bindings unchanged. The UserControl sets
its content's `DataContext` to `_internalState` and propagates DP changes
into it. Clean separation between "WPF DP plumbing" (code-behind) and
"presentation state" (internal VM).

**Why a separate `PillItemSource`**: rebuilding the wrapper-row list when
`ItemsSource` changes (or when the source's collection mutates) is its
own concern — subscription management, row dictionary upkeep, member-path
resolution per row. Pulling it out of the code-behind keeps the
code-behind under 300 LOC.

**Why a separate `PillSelectionSync`**: three sync edges (wrapper ↔
`SelectedItems`, wrapper ↔ source's `IsActive` bool, wrapper ↔ checkbox
UI) plus a re-entrancy guard plus `SelectionChanged` event raising. Easy
to get wrong; isolated class with focused tests is the safer place.

## Phased breakdown

### Phase 1 — Foundation (UserControl with DPs, ItemsSource scaffolding)
**Done when**: a host can write
`<PillMultiSelect ItemsSource="{Binding ...}" DisplayMemberPath="..." Label="..." />`
and rows render correctly. Selection is internal-UI-only.

Tasks:
1. Rename `PillMultiSelectViewModel` → `PillMultiSelectInternalState`, make `internal`.
2. Rename `PillMultiSelectItemViewModel` → `PillRowViewModel`, make `internal`. Update `Display` / `Abbreviation` to be settable (the wrapper now derives them via member-path resolution, not constructor).
3. Add `MemberPathResolver` (caches `PropertyDescriptor` per `(Type, path)`).
4. Add `PillItemSource` (ItemsSource → rows lifecycle).
5. Add UserControl-level DPs: `ItemsSource`, `DisplayMemberPath`, `AbbreviationMemberPath`, plus all existing-VM properties (`Label`, `Icon`, all layout knobs, all localization knobs, `OverflowOptions`).
6. UserControl ctor creates `_internalState`, sets content `DataContext` to it.
7. Each DP's property-changed callback propagates to `_internalState`.
8. Update DevLauncher to use the new API (DP-driven, not VM-driven).
9. Update tests to hit the new types (renames + member-path-driven row construction).

### Phase 2 — Selection sync
**Done when**: `SelectedItems="{Binding ..., Mode=TwoWay}"` round-trips both ways AND `IsSelectedMemberPath="IsActive"` mutates source items directly AND both can be active simultaneously.

Tasks:
1. Add `SelectedItems` DP with per-instance default `ObservableCollection<object>`.
2. Add `IsSelectedMemberPath` DP.
3. Add `PillSelectionSync` class with three edges + re-entrancy guard.
4. Subscribe to source items' `INotifyPropertyChanged` when `IsSelectedMemberPath` is set.
5. Add `SelectionChanged` routed event.
6. Tests for each edge in isolation + combined.

### Phase 3 — Genericize formatter + tooltip
**Done when**: `PillOverflowFormatter` operates on source items, not wrapper rows; `TooltipMode` enum DP works for XAML hosts; Func escape hatches still work.

Tasks:
1. `PillOverflowFormatter.Format<T>(IReadOnlyList<T>, Func<T,string> display, Func<T,string> abbrev, PillOverflowOptions)`. Existing tests adapt to call this with raw source items.
2. `PillTooltipMode` enum + `TooltipMode` DP. `PillTooltipFormatters` becomes the implementation lookup table.
3. Make sure Func escape hatches (`DisplayFormatter`, `TooltipFormatter`, `DisplaySelector`, etc.) override DPs cleanly.

### Phase 4 — Tests + docs cleanup
**Done when**: full test coverage on new public API; PLAN.md is deleted from the repo (its content was a one-time refactor guide, not a permanent doc).

Tasks:
1. Re-bucket existing tests:
   - **Keep, retargeted to internal types**: snapshot ordering, search filter, overflow formatter mechanics.
   - **Rewrite for DP surface**: ItemsSource swapping, SelectedItems sync, IsSelectedMemberPath sync, SelectionChanged event.
   - **Delete**: tests of the old public VM API (e.g. "Items is read-only IReadOnlyList").
2. STA-thread test fixture for DP-driven tests (xunit `STAFactAttribute` or local helper).
3. Delete this PLAN.md.

## v2 follow-up (not in scope; tracked as forward-compatible seams in v1)

- **Grouping** (`GroupKeyMemberPath`, `GroupKeySelector`, group header template, expand/collapse state). Scenario B per the design discussion.
- **Tri-state parent checkboxes** derived from per-group selection counts.
- **Group-aware overflow summary**: "Sensors" instead of "LB, IN, +5 more" when the whole group is selected. New overload on `PillOverflowFormatter.Format` takes group context.
- **Templated `Control` upgrade** if a future host needs full retheming via `ControlTemplate`.

## Risks / known constraints

- **DP collection defaults are shared across instances unless initialized per-instance.** Initialize `SelectedItems` default in the ctor or via the property-changed callback's first hit, not in the metadata default value.
- **`SelectedItems` whole-collection swap.** The DP changed-callback must unsubscribe from the old collection's `CollectionChanged` and subscribe to the new one's. Same pattern as `ItemsControl.ItemsSource`.
- **Selection-sync re-entrancy**: a `bool _syncing` guard wraps each propagation cycle. If both `SelectedItems` and `IsSelectedMemberPath` are bound and disagree at startup, last-writer-wins.
- **Source items not implementing `INotifyPropertyChanged`** can't observe external `IsActive` mutations. Document and accept; same constraint WPF data binding has everywhere else.
- **Member-path reflection cost**: mitigated by `MemberPathResolver` caching `PropertyDescriptor` per `(Type, path)`. One reflection lookup per (T, path), not per row.
- **STA-thread test fixture**: WPF DPs require STA. xunit defaults to MTA — use `[STAFact]` (Xunit.StaFact NuGet) or local helper.

## Maintainability bar

- Code-behind in `PillMultiSelect.xaml.cs` stays under 300 LOC. Anything bigger gets pulled into a sibling helper (`PillItemSource`, `PillSelectionSync`, `MemberPathResolver`) — same principle as the BlockParam architecture guardrails.
- `PillMultiSelectInternalState` (the renamed VM) keeps doing what it does today; we don't pile selection-sync, member-path resolution, or DP propagation on top of it.
- One concern per file. One responsibility per class.
- Each phase is independently committable and ships behind passing tests.
