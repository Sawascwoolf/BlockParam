# TIA Portal Bulk Change - Add-In

## Project Overview

A TIA Portal Add-In for bulk editing Data Block (DB) start values and parameters.
Target: Siemens Xcelerator Marketplace / App Store distribution.

## Key Decisions

- **Target**: TIA Portal V20 (requires .NET Framework 4.8)
- **Language**: C# / .NET Framework 4.8 (LangVersion=latest for modern syntax)
- **UI Language**: English (with i18n architecture via resource files for future localization)
- **License**: Open Source + commercial dual license (Freemium)
- **Monetization**: 3 free bulk operations per calendar day, then paid
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
