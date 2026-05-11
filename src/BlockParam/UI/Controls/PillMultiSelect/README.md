# PillMultiSelect

A self-contained WPF multi-select control: rounded trigger pill, popup with
search box, checkbox list, and a Select all / Reset footer. Modeled after
modern web pill-dropdowns (Linear / Notion / Figma).

See `assets/screenshots/pill/` (in the repo root) for the seven canonical
states, and `src/BlockParam.PillSample/` for a standalone WPF app that
consumes this control with **zero** reference to the rest of BlockParam.

## Vendoring

Drop these 14 files into any WPF project. That's it.

```
PillMultiSelect.xaml          PillMultiSelectInternalState.cs
PillMultiSelect.xaml.cs       PillItemSource.cs
PillRelayCommand.cs           PillSelectionSync.cs
PillViewModelBase.cs          PillFormatterCoordinator.cs
PillRowViewModel.cs           PillOverflowFormatter.cs
PillTooltipMode.cs            PillOverflowOptions.cs
PillTooltipFormatters.cs      MemberPathResolver.cs
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

Code-only escape hatches (override the corresponding DP when both are set):
`DisplayFormatter`, `TooltipFormatter`, `DisplaySelector`, `AbbreviationSelector`,
`FilterPredicate`.

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

See `src/BlockParam.PillSample/MainWindow.xaml` for three scenes
(default formatting, overflow + tooltip, compact mode).

## Tests

The sibling tests in `src/BlockParam.Tests/Pill*Tests.cs` (108 cases) cover
the bindable surface, selection sync, overflow formatter, item lifecycle,
tooltip modes, and snapshot ordering. They only need xUnit + Moq — no
host-project setup — and can travel with the control if desired.
