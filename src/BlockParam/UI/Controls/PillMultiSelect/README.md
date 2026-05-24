# PillMultiSelect

A self-contained WPF multi-select control: rounded trigger pill, popup with
search box, checkbox list, and a Select all / Reset footer. Modeled after
modern web pill-dropdowns (Linear / Notion / Figma).

The popup half ships as a standalone control too — `MultiSelectDropdown` —
so it can be reused outside the pill (DataGrid cell editors, fly-out
panels, sidebars). See "Standalone dropdown" below.

See `assets/screenshots/pill/` (in the repo root) for the seven canonical
states.

## Vendoring

Drop these 19 source files into any WPF project. That's it. (This README is
just for you — leave it behind or take it along.)

```
PillMultiSelect.xaml              MultiSelectDropdown.xaml
PillMultiSelect.xaml.cs           MultiSelectDropdown.xaml.cs
MultiSelectInternalState.cs       MultiSelectItemSource.cs
MultiSelectSelectionSync.cs       MultiSelectFormatter.cs
MultiSelectRowViewModel.cs        MultiSelectGroupViewModel.cs
MultiSelectViewModelBase.cs       MultiSelectRelayCommand.cs
MultiSelectGroupTemplateConverters.cs   MultiSelectLog.cs
MemberPathResolver.cs             PillOverflowFormatter.cs
PillOverflowOptions.cs            PillTooltipMode.cs
PillTooltipFormatters.cs
```

Family naming: the **`PillMultiSelect`** UserControl is the pill-styled
trigger + popup composition. The reusable primitives — state, item source,
selection sync, formatter, dropdown — share the **`MultiSelect*`** prefix.
Pill-trigger-specific helpers (overflow, tooltip) keep the **`Pill*`**
prefix.

Dependencies: **.NET BCL + WPF only.** No third-party packages, no
project-internal helpers. Targets net48; should also build on net6.0-windows
and later with `<UseWPF>true</UseWPF>`.

Rename the `BlockParam.UI.Controls.PillMultiSelect` namespace to match your
project if you want — it has no other meaning.

### Diagnostics

The control writes a handful of `MultiSelectLog.Information(...)` lines
around the host→control DP boundary (instantiation, `IsOpen`/`Label`/
`ItemsSource`/`SelectedItems` changes). These exist because invisible
bindings produce a silent empty pill — see #141 for the bug they catch.

The sink defaults to no-op. To forward into your logger:

```csharp
MultiSelectLog.Sink = msg => YourLogger.Information(msg);
```

Wire it once at app startup. If you don't, the lines are silently dropped.

## Public API

| DP / property            | Type                                  | Purpose                                            |
|--------------------------|---------------------------------------|----------------------------------------------------|
| `ItemsSource`            | `IEnumerable`                         | Source items (any CLR type).                       |
| `DisplayMemberPath`      | `string?`                             | Property to render in the popup list and trigger.  |
| `AbbreviationMemberPath` | `string?`                             | Short form used in the trigger summary.            |
| `SelectedItems`          | `IList`                               | Two-way bindable selection (mutated in place).     |
| `IsSelectedMemberPath`   | `string?`                             | Bool property on source items, instead of `SelectedItems`. |
| `Label`                  | `string`                              | Header text shown left of the selection summary.   |
| `Icon`                   | `Geometry?`                           | Leading icon inside the trigger.                   |
| `OverflowOptions`        | `PillOverflowOptions?`                | Abbreviation + collapse thresholds.                |
| `TooltipMode`            | `PillTooltipMode`                     | `None` / `FullNames` / `AbbrevAndFullNames`.       |
| `IsOpen`                 | `bool`                                | Two-way bindable popup state.                      |
| `PopupWidth`             | `double`                              | Defaults to 280.                                   |
| `PopupMaxListHeight`     | `double`                              | Defaults to 280 (≈10 rows).                        |
| `ShowSearchBox`          | `bool`                                | Defaults to true.                                  |
| `ShowFooterActions`      | `bool`                                | Defaults to true.                                  |
| `SearchPlaceholder`      | `string?`                             | Defaults to `"Search..."`.                         |
| `ClearTooltip`           | `string?`                             | Defaults to `"Clear"`.                             |
| `SelectAllText`          | `string?`                             | Defaults to `"Select all"`.                        |
| `ResetText`              | `string?`                             | Defaults to `"Reset"`.                             |
| `GroupKeyMemberPath`     | `string?`                             | Property to bucket rows into expandable groups.    |
| `GroupHeaderTemplate`    | `DataTemplate?`                       | Overrides the default group header rendering.      |

Code-only escape hatches (override the corresponding DP when both are set):
`DisplayFormatter`, `TooltipFormatter`, `DisplaySelector`, `AbbreviationSelector`,
`FilterPredicate`, `GroupKeySelector`.

## Tri-state rows

`MultiSelectRowViewModel.IsSelected` is `bool?`. Leaf rows toggled inside
the popup stay binary (the checkbox has `IsThreeState=false`); the
indeterminate state only ever appears when an external `bool?` source
property pushes `null` through `IsSelectedMemberPath`. `SelectedItems`
always represents fully-checked source items — indeterminate rows are
intentionally absent.

## Grouping

Set `GroupKeyMemberPath` to a property name on the source items (or use the
`GroupKeySelector` CLR escape hatch for richer key resolution). The popup
renders one expandable section per distinct group-key value with:

- a **tri-state header checkbox** that aggregates child selection
  (`true` / `false` / `null`) and writes back to every child when toggled,
- an **expand chevron** that hides/shows the children, and
- a **n / N count badge**.

Typing in the search box temporarily forces every group that has at least one
matching row to be expanded so the hit is discoverable — collapsed groups
restore their prior state when the search clears.

The built-in header template is sufficient out of the box; bind
`GroupHeaderTemplate` to a custom `DataTemplate` whose DataContext is
`MultiSelectGroupViewModel` (with `Key`, `Header`, `IsSelected`,
`IsExpanded`, `SelectedCount`, `TotalCount`) when you want richer
rendering.

Routed event: `SelectionChanged` (fires once per reconciliation cycle).

## Localization

All user-facing strings are English literals by default. To localize, bind the
five string DPs (`SearchPlaceholder`, `ClearTooltip`, `SelectAllText`,
`ResetText`, `PillOverflowOptions.PlusMoreFormat`) to your own resource lookup.

## Minimal usage

```xml
<pill:PillMultiSelect ItemsSource="{Binding Members}"
                      DisplayMemberPath="Name"
                      AbbreviationMemberPath="Initials"
                      Label="Members"
                      SelectedItems="{Binding SelectedMembers, Mode=OneWay}" />
```

## Standalone dropdown (DataGrid cells, fly-out panels, ...)

The popup half is also shipped as `MultiSelectDropdown` — a chrome-agnostic
`UserControl` containing only the search box, the (optionally grouped)
checkbox list, and the Select-all / Reset footer. Use it when you want the
selection UI without the pill trigger, e.g. as a `DataGridTemplateColumn`
cell editor, a sidebar panel, or a fly-out that opens on a custom button.

The dropdown is **DataContext-driven** — bind it to a
`MultiSelectInternalState` you wire up yourself. The four collaborators
are `public` for exactly this use case:

```csharp
var state    = new MultiSelectInternalState();
var resolver = new MemberPathResolver();
var source   = new MultiSelectItemSource(state, resolver) {
                   ItemsSource       = myItems,
                   DisplayMemberPath = "Name",
               };
var sync     = new MultiSelectSelectionSync(state, source, resolver);
_           = new MultiSelectFormatter(state, source); // trigger-only — harmless here
sync.SetSelectedItems(mySelectedList);
```

```xml
<pill:MultiSelectDropdown DataContext="{Binding TheState}" Width="320"/>
```

Listen to `sync.SelectionChanged` (or watch `mySelectedList`) for changes.
The dropdown ships **no chrome** of its own — wrap it in whatever `Border`,
`Popup`, or panel your host needs. For a DataGrid cell editor the pattern
is `DataGridTemplateColumn.CellEditingTemplate` → `Popup` (with
`StaysOpen="False"`) → `MultiSelectDropdown` inside; the column's bound
list flows into `sync.SetSelectedItems`.

## Tests

The sibling tests in `src/BlockParam.Tests/Pill*Tests.cs` cover the bindable
surface, selection sync, overflow formatter, item lifecycle, tooltip modes,
and snapshot ordering. They only need xUnit + StaFact + FluentAssertions —
no other host-project setup — and can travel with the control if desired.

Caveat for vendoring the tests: several reach `internal` types
(`MultiSelectRowViewModel` and friends were promoted to `public` in #141 to
unblock partial-trust WPF binding, so the public surface is wider now —
but some test-only fixtures still use `[InternalsVisibleTo]`). The project
that owns the control needs `[InternalsVisibleTo("YourTestProject")]` for
those to compile. Tests that only touch the public DP surface
(`PillBindableApiTests.cs`, `PillOverflowFormatterTests.cs`) don't need it.
