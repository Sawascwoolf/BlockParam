# TIA Portal Bulk Change - Add-In

## Project Overview

A TIA Portal Add-In for bulk editing Data Block (DB) start values and parameters.
Target: Siemens Xcelerator Marketplace / App Store distribution.

## Key Decisions

- **Target**: TIA Portal V20 (requires .NET Framework 4.8)
- **Language**: C# / .NET Framework 4.8 (LangVersion=latest for modern syntax)
- **UI Language**: English (with i18n architecture via resource files for future localization)
- **License**: Open Source + commercial dual license (Freemium)
- **Monetization**: 200 free value changes per calendar day, charged on successful Apply (one quota unit per individual change written, whether bulk-staged or inline). Pro tier removes the cap.
- **Target Users**: PLC programmers & commissioning engineers for efficient parameter management

## Technology Stack

- **Framework**: TIA Portal Add-In (Siemens.Engineering.AddIn)
- **API**: TIA Portal Openness (Siemens.Engineering)
- **UI**: Context menu on DB → dialog with member tree (V1), WPF enhanced (V2+)
- **Build**: MSBuild + Siemens.Collaboration.Net.TiaPortal.AddIn.Build NuGet package
- **Packaging**: .addin file via Siemens.Engineering.AddIn.Publisher.exe

## Core Use Case

Bulk editing start values **within a single DB** that contains many UDT instances.
DBs often have deeply nested structures where the same variable names repeat across
many UDT instances and must stay in sync.

- **Right-click on a DB in the project tree** → "Bulk Change..." menu entry
- Add-In opens a **dialog with member tree view** where user selects member and scope
- **Dynamic scope levels**: Dialog detects the actual hierarchy depth and offers
  a bulk option for each level (e.g. parent, grandparent, ... up to DB root)
- **Note**: TIA Portal Add-Ins cannot inject into the DB editor context menu —
  only the project tree. All member selection happens in the Add-In's own dialog.
- **Works without config** (generic analysis), config adds value constraints, allowed-value lists, comment automation
- **V1**: Context menu + dialog, **V2**: WPF UI + autocomplete, **V3**: Diff preview

## Architecture Notes

- Add-In integrates into TIA Portal's context menu on DB start values
- Two approaches for DB manipulation:
  1. **Direct API** (`GetAttribute`/`SetAttribute`) - for single value changes
  2. **XML Export/Import** (SimaticML) - for bulk operations
- Bulk operations primarily use the XML approach for efficiency
- Use `ExclusiveAccess` for bulk performance (note: disables TIA undo stack)
- Context menu build must be < 200ms (performance critical)

## Research Documentation

Detailed technical research is split into token-efficient files:

| File | Content |
|---|---|
| `docs/research/01-tia-addin-framework.md` | Add-In project structure, NuGet packages, lifecycle, deployment |
| `docs/research/02-openness-api-datablocks.md` | Openness API hierarchy, DB access patterns, two modification approaches |
| `docs/research/03-simaticml-xml-format.md` | SimaticML XML structure, Member/StartValue format, data types |
| `docs/research/04-publishing-distribution.md` | App Store publishing, signing, licensing options |
| `docs/research/05-code-patterns.md` | Ready-to-use C# code patterns for Add-In and DB manipulation |
| `docs/research/06-addin-deployment.md` | V20 deployment: net48 requirement, Publisher, DebugStarter, UserAddIns path |
| `docs/research/07-tia-cloud-saas.md` | TIA Portal Cloud (V6.1 / V21) SaaS implications, V21 Openness breaking changes, cloud Add-In install open questions |
| `docs/research/08-v21-addin-build.md` | V21 in-process Add-In build: no V21 NuGet meta-package, direct `<Reference HintPath>` to `Portal V21\PublicAPI\V21\net48\`, manifest changes (`<AddInVersion>V21</AddInVersion>`), why one `.addin` cannot serve V20 + V21 |

## Version Bump & Deploy

Use the `bump-version.sh` script to update the version, build, package and deploy:

```bash
bash bump-version.sh <major.minor.patch>   # e.g. bash bump-version.sh 0.3.0
```

The script:
1. Updates `<Version>` in `BlockParam.csproj` and `addin-publisher.xml`
2. Builds Release configuration
3. Packages `.addin` via `Siemens.Engineering.AddIn.Publisher.exe`
4. Deploys to `C:\Program Files\Siemens\Automation\Portal V20\AddIns\BlockParam.addin`

After deployment, restart TIA Portal to load the new version.

## DevLauncher (UI Testing without TIA Portal)

The project includes a standalone WPF launcher for testing the UI without TIA Portal:

```bash
dotnet build src/BlockParam.DevLauncher -c Debug
src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe
```

- Loads DB XML from `%TEMP%\BlockParam\TP307.xml` (real TIA export) or a bundled demo file
- Loads config from `%APPDATA%\BlockParam\config.json`
- Loads tag tables from `%TEMP%\BlockParam\TagTables\`

**Important**: The DevLauncher is a WPF application.
- **Launch directly** from bash (`src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe`),
  do NOT use `cmd.exe /c start` or `start` — the WPF app silently fails to show when launched that way.
- The process blocks the terminal while the GUI is open. After launching, **always wait for user feedback**
  before taking further action — do not interpret the process completion as "the user is done testing".

### Headless screenshot capture (marketing assets)

DevLauncher supports a `--capture` mode for regenerating website screenshots after UI changes — no manual steps, no screen recording.

```bash
src/BlockParam.DevLauncher/bin/Debug/net48/BlockParam.DevLauncher.exe \
    --capture <out.png> [<dbName>]
```

- `<out.png>` — output path (relative to CWD or absolute). Parent dirs are created.
- `<dbName>` — optional; defaults to `DB_ProcessPlant_A1`. Resolved against `%TEMP%\BlockParam\` just like interactive mode (bare name, `.xml`, or absolute path).
- Renders via `RenderTargetBitmap` at 2× for retina-crisp PNGs (WPF content only — no OS window chrome).
- Forces `en-US` culture so column headers / buttons are English regardless of OS language.
- Dialog auto-closes after capture; process exits — unlike interactive mode, it does NOT block waiting for the user.

Canonical outputs live in `assets/screenshots/`. To add a new shot, pick a preset DB (export one into `%TEMP%\BlockParam\` from TIA if missing) and add a capture line. For richer states (expanded tree, member selected, dialog open), extend `Program.cs` with a small script hook — do not try to drive the running UI from outside.

### Workflow video

For rebuilding the narrated workflow MP4 (`assets/screenshots/workflow/workflow_inline.mp4`), use the `recreate-workflow-video` skill — it bundles the rebuild-then-capture-then-stitch steps and the rationale for each.

## Development Guidelines

- Read only the research file relevant to the current task (token efficiency)
- Follow Siemens Add-In conventions (ProjectTreeAddInProvider, ContextMenuAddIn)
- Use `Siemens.Collaboration.Net.TiaPortal.Openness.Extensions` for convenience methods
- All user-facing strings must go through i18n resource files
- Freemium counter logic must be isolated in its own service for easy swap to online licensing later
- When clarifying anything with the user, prefer the **AskUserQuestion** tool over
  free-form prose. Always include a concrete set of options and **recommend one of
  them** (mark it in the question text) so the user can accept with a single click
  instead of typing a reply.
- When a PR resolves GitHub issues, put **closing keywords** in the PR body
  (one per line: `Closes #7`, `Fixes #13`, `Resolves #9`). GitHub auto-closes
  them on merge — no post-merge `gh issue close` sweep needed. Plain references
  like `(#7)` in the title do not trigger auto-close. If you forget and the
  issues stay open after merge, then use
  `gh issue close <N> -c "Resolved by #<PR>"` to catch up.

## Architecture Guardrails

These rules exist to stop the codebase from drifting further toward the
maintainability/DRY problems already filed as issues (#78, #79, #80, #81, #82).
They apply to **new and modified code**. Do not retrofit old code in
unrelated PRs — fix it under the issue that owns the cleanup.

### Hard rules (must follow in every change)

- **No new file I/O outside a storage layer.** `File.*`, `Directory.*`,
  `Path.GetTempPath()`, `Environment.GetFolderPath(...)` are 55+ scattered
  calls today. Do not add a 56th. New code that needs to read/write goes
  through the existing path-helper or, if none fits, introduce one — don't
  copy-paste another `Directory.CreateDirectory(...) + try/catch` pair.
- **No new path string literals.** Anything matching `BlockParam/...`,
  `%APPDATA%\BlockParam`, `%TEMP%\BlockParam`, or `UserFiles\BlockParam`
  must reference a single named constant. If the constant doesn't exist
  yet, add it in one place; don't introduce a sixth duplicate.
- **No new `_activeDbs` / `_stashedDbs` mutation sites.** Per #78, all
  active-set changes go through the snapshot setter once it lands. Until
  then, route any new mutation through the existing `RemoveActiveDb` /
  `SoloActiveDbByReference` / `ReactivateStashedDb` helpers — do not add a
  seventh entry path.
- **No path-string identity for members across DBs.** Per #82, member
  identity is `(ActiveDb, MemberNode)`. New code that needs to look up a
  member by path must take the owning `ActiveDb` as a parameter; never
  scan all roots.
- **No business logic in code-behind (`*.xaml.cs`).** Selection state,
  filtering, validation, persistence — these belong in the ViewModel.
  Code-behind is for: focus, scroll, attached-property bridging, and
  `InitializeComponent`. If you need shared visual behavior (column sizing,
  drag-select), write an attached behavior, not another `OnXyzChanged`
  handler.
- **No new sync primitives in `OnlineLicenseService` without auditing the
  existing ones.** The class currently mixes `lock`, `volatile`, and
  `Interlocked` over overlapping state. Any change touching `_licenseData`,
  `_cache`, `_proActive`, or `_heartbeatTimer` must pick one model and
  remove the conflicts within the touched scope. Never call HTTP / I/O
  inside `lock (_lock)`.
- **No new user-facing strings inline.** Every string the user sees goes
  through `Res.Get` / `Res.Format` against `Strings.resx`. Same key for
  the same concept across files — don't add a near-duplicate key when one
  exists.

### Default to extracting, not extending, when touching hotspots

If your change adds non-trivial logic to any of the following, prefer
extracting a small focused class for the new logic (kept in the same
folder) over enlarging the host:

| Hotspot | Current LOC | Owning issue |
|---|---:|---|
| `UI/BulkChangeViewModel.cs` | 4,563 | #80 |
| `AddIn/BulkChangeContextMenu.cs` | 981 | #81 |
| `UI/BulkChangeDialog.xaml.cs` | 1,033 | (no issue yet) |
| `Licensing/OnlineLicenseService.cs` | 565 | (no issue yet) |
| `Services/TiaDataTypeValidator.cs` | 556 | (no issue yet) |

Adding 50 lines of new feature logic to a 4,500-line ViewModel is not
"a small change" — it cements the god class. Add a sibling class, wire it
from the constructor, and bind through it.

### DRY checklist before merging

- Searched for the verb you just wrote (`Filter`, `Match`, `Save`, `Load`,
  `Export`, `Build`, `Resolve`) in nearby files — used the existing one if
  it covers your case.
- No new `if (x.Contains(filter, StringComparison.OrdinalIgnoreCase) || y.Contains(...) || z.Contains(...))`
  block — that lives in `MemberSearchService` / the planned `StringMatcher`.
- No new copy of the `if (_field != value) { _field = value; OnPropertyChanged(...) }`
  shape unless `ViewModelBase.SetProperty<T>` truly doesn't fit — and if it
  doesn't, say why in the PR description. The helper already exists and is
  used by every VM except `BulkChangeViewModel` (#87).
- No new `List<T>` field that is mutated from more than one method without
  a snapshot/setter cascade (#78 / #79).

### When in doubt

File a new issue rather than inflating the host file. Reference the
relevant existing refactor issue (#78–#82) so reviewers can see the
seams you're not crossing. The cost of a small new file is much lower
than the cost of another method on a god class.

## Project Structure

```
src/
  BlockParam/                       # Main Add-In project (.NET Framework 4.8)
    AddIn/                          # Add-In entry point, context menu
    Services/                       # Business logic (DB analysis, bulk ops)
    Models/                         # Data models (MemberInfo, ChangeSet, etc.)
    UI/                             # WPF Views and ViewModels
    Localization/                   # Resource files (en, de, ...)
    Licensing/                      # Freemium counter, online license validation
    Resources/                      # App icon (blockparam.ico, PNGs)
  BlockParam.Tests/                 # Unit tests
docs/
  research/                         # Technical research (see table above)
```
