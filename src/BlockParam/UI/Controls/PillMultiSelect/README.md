# PillMultiSelect

A self-contained WPF multi-select control: rounded trigger pill, popup with
search box, checkbox list, and a Select all / Reset footer. Modeled after
modern web pill-dropdowns (Linear / Notion / Figma).

See `assets/screenshots/pill/` (in the repo root) for the seven canonical
states.

## Vendoring

Drop these 16 source files into any WPF project. That's it. (This README is
just for you — leave it behind or take it along.)

```
PillMultiSelect.xaml          PillMultiSelectInternalState.cs
PillMultiSelect.xaml.cs       PillItemSource.cs
PillRelayCommand.cs           PillSelectionSync.cs
PillViewModelBase.cs          PillFormatterCoordinator.cs
PillRowViewModel.cs           PillOverflowFormatter.cs
PillGroupViewModel.cs         PillOverflowOptions.cs
PillTooltipMode.cs            MemberPathResolver.cs
PillTooltipFormatters.cs      PillGroupTemplateConverters.cs
```

Dependencies: **.NET BCL + WPF only.** No third-party packages, no
project-internal helpers. Targets net48; should also build on net6.0-windows
and later with `<UseWPF>true</UseWPF>`.

Rename the `BlockParam.UI.Controls.PillMultiSelect` namespace to match your
project if you want — it has no other meaning.

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

`PillRowViewModel.IsSelected` is `bool?`. Leaf rows toggled inside the popup
stay binary (the checkbox has `IsThreeState=false`); the indeterminate state
only ever appears when an external `bool?` source property pushes `null`
through `IsSelectedMemberPath`. `SelectedItems` always represents fully-checked
source items — indeterminate rows are intentionally absent.

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
`PillGroupViewModel` (with `Key`, `Header`, `IsSelected`, `IsExpanded`,
`SelectedCount`, `TotalCount`) when you want richer rendering.

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

## Tests

The sibling tests in `src/BlockParam.Tests/Pill*Tests.cs` cover the bindable
surface, selection sync, overflow formatter, item lifecycle, tooltip modes,
and snapshot ordering. They only need xUnit + StaFact + FluentAssertions —
no other host-project setup — and can travel with the control if desired.

Caveat for vendoring the tests: several reach `internal` types
(`PillRowViewModel`, `PillMultiSelectInternalState`). The project that owns
the control needs `[InternalsVisibleTo("YourTestProject")]` for those to
compile. Tests that only touch the public DP surface
(`PillBindableApiTests.cs`, `PillOverflowFormatterTests.cs`) don't need it.
