# Config Storage

This page lists every file BlockParam reads or writes, where it lives, and what's
safe to back up, version-control, or delete.

## Quick reference

| What | Path | Purpose |
|---|---|---|
| **Add-In binary** | `…\AddIns\BlockParam.addin` (V20) or `…\UserAddIns\BlockParam.addin` (V21) | The Add-In itself. See [Getting started](getting-started.md#2-drop-the-addin-into-the-right-folder). |
| **Settings** | `%APPDATA%\BlockParam\config.json` | The shared rules directory path and (optional) license server URL. Optional — created on first save. |
| **Local rules** | `%APPDATA%\BlockParam\rules\*.json` | Per-user rule files. One rule per file. |
| **Project rules** | `{TIAProjectPath}\UserFiles\BlockParam\*.json` | Rules that ship with the TIA project file. |
| **Shared rules** | (configured via `rulesDirectory` in `config.json`) | Team-wide rules. Often a network share. |
| **License (per-user)** | `%APPDATA%\BlockParam\license.json` + `license_cache.dat` | Activated license key + cached server response. |
| **License (managed)** | `%PROGRAMDATA%\BlockParam\license.key` | IT-managed license file. See [admin docs](../admin-license-deployment.md). |
| **Daily quota counter** | `%APPDATA%\BlockParam\usage.dat` | Encrypted free-tier counter. Resets at midnight local time. |
| **Tag-table cache** | `%TEMP%\BlockParam\TagTables\*.xml` | Manually-exported TIA tag tables. See [Tag-table integration](tag-tables.md#where-tag-tables-come-from). |
| **UDT cache** | `%TEMP%\BlockParam\UdtTypes\*.xml` | Cached UDT type definitions for set-point and comment resolution. |
| **UI settings** | `%APPDATA%\BlockParam\ui-settings.json` | Persisted UI zoom level. |
| **Log** | `%APPDATA%\BlockParam\blockparam.log` | Diagnostic log. Useful for support. |

## What's worth backing up

For most users:

- `%APPDATA%\BlockParam\rules\` — your hand-crafted rules.
- `%APPDATA%\BlockParam\config.json` — points at the shared rules directory.

The license file (`license.json`) can be re-activated with the original key, so
backing it up is convenient but not critical. Cached server responses
(`license_cache.dat`) regenerate on the next heartbeat.

The tag-table and UDT caches under `%TEMP%` are derivable from your TIA project —
no need to back them up.

## Version-control with the TIA project

Putting rules under `{TIAProjectPath}\UserFiles\BlockParam\` lets you commit them
to the same source control as your TIA project. Every engineer who clones the
project gets the rules automatically — they take precedence over any conflicting
Local rules.

This is the recommended location for:

- Rules tightly coupled to UDTs that live in the project.
- Comment templates that match the project's naming conventions.
- Anything you want to enforce across the whole team for this project only.

## Sharing rules across projects (the team-wide directory)

Open the [Rule Editor](rule-editor.md) and set **Shared Rules Directory** to a
path your whole team can read — typically a UNC path like
`\\server\share\tia-rules`. The setting is saved into `%APPDATA%\BlockParam\config.json`
on each machine, so you only configure it once per workstation.

Shared rules are the **lowest** priority — they're used unless overridden by a
Local or Project rule with the same filename. New engineers get the team's
defaults out of the box; advanced users can still override individual rules
locally.

## Migrating between machines

To move your personal setup to a new machine:

1. Copy `%APPDATA%\BlockParam\rules\` to the same path on the new machine.
2. Copy `%APPDATA%\BlockParam\config.json`.
3. Reactivate the license: enter your key in the License dialog (bottom-bar
   button), or copy `%APPDATA%\BlockParam\license.json` if you want to keep the
   same instance ID.
4. Re-export the tag tables you use into `%TEMP%\BlockParam\TagTables\`.

## Importing / exporting rule sets

A dedicated import/export feature is planned (see
[issue #36](https://github.com/Sawascwoolf/BlockParam/issues/36)). For now,
treat the `rules\*.json` directory as the unit of import/export — copy the
whole folder, or zip individual `.json` files to share with colleagues.

## Resetting BlockParam

To reset everything to a clean state:

```text
1. Close TIA Portal.
2. Delete %APPDATA%\BlockParam\         (rules, config, license, log)
3. Delete %TEMP%\BlockParam\            (caches)
4. Reopen TIA Portal.
```

The Add-In recreates the necessary directories on first run. The license has to
be re-activated.

## UI language

BlockParam ships with English (default) and German UI strings. The language is
picked from the OS culture on Windows, which is the same default TIA itself
uses for non-localized addin assemblies. (TIA Openness has no documented hook
that reflects the user's TIA UI-language dropdown reliably at runtime, so we
don't try to follow it.)

To force a specific language regardless of OS culture, add a `language` field
to `%APPDATA%\BlockParam\config.json`:

```json
{
  "language": "de"
}
```

Accepted values: `"en"` / `"de"` / any specific culture name like `"en-US"` or
`"de-DE"`. Restart TIA Portal for the change to take effect — Siemens's addin
API freezes context-menu labels at addin load.

## Schema reference

The on-disk format for rule files and `config.json` is documented separately:

- [`docs/configuration.md`](../configuration.md) — JSON schema and field reference.
- [`docs/example-config.jsonc`](../example-config.jsonc) — annotated examples.

## Next

- [Licensing](licensing.md) — daily quota and Pro tier.
- [Troubleshooting](troubleshooting.md) — when things go wrong.
