# Getting Started

This page walks you from a fresh download to your first bulk edit.

## Requirements

- **TIA Portal V20 or V21** (V19 and older are not supported).
- **Windows 10 or 11**, .NET Framework 4.8 (already present on modern Windows).

## 1. Download the `.addin`

Each release ships two `.addin` files — pick the one that matches your TIA Portal version.

| TIA Portal | Direct download (always latest) |
|---|---|
| **V20** | [`BlockParam-TIA-V20.addin`](https://github.com/Sawascwoolf/BlockParam/releases/latest/download/BlockParam-TIA-V20.addin) |
| **V21** | [`BlockParam-TIA-V21.addin`](https://github.com/Sawascwoolf/BlockParam/releases/latest/download/BlockParam-TIA-V21.addin) |

Both URLs always resolve to the most recent release. Older versions live on
the [Releases page](https://github.com/Sawascwoolf/BlockParam/releases).

A `.addin` for the wrong TIA version will not load (and TIA will not tell you why).
If the Add-in never appears in the task card after enabling, double-check the version match.

## 2. Drop the `.addin` into the right folder

The folder differs between V20 and V21:

| TIA Portal | Folder | Notes |
|---|---|---|
| **V20** | `C:\Program Files\Siemens\Automation\Portal V20\AddIns\` | Machine-wide; needs admin rights to write. |
| **V21** | `%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\` | Per-user; no admin needed. |

TIA Portal can stay open — the Add-Ins folder is rescanned live, no restart required.

## 3. Enable BlockParam in TIA Portal

Open the **Add-ins** task card on the right edge of the TIA Portal window.
You'll see `BlockParam.addin` listed but disabled.

<p align="center">
  <img src="../../assets/screenshots/install/01_enable_addin.png" alt="Add-ins task card with BlockParam.addin not yet enabled" width="260">
  <img src="../../assets/screenshots/install/02_permission_prompt.png" alt="TIA permission prompt for BlockParam.addin" width="260">
  <img src="../../assets/screenshots/install/03_activated.png" alt="BlockParam.addin enabled (green check)" width="260">
</p>

1. Toggle the switch next to **BlockParam**.
2. TIA shows a security/permission prompt — confirm it. The Add-In runs under TIA's
   partial-trust sandbox and only touches files in your `%APPDATA%\BlockParam\` folder
   plus the DBs you explicitly open.
3. The green check appears. The Add-In stays enabled across TIA sessions.

> When you update BlockParam (drop a newer `.addin` into the same folder), TIA shows
> the permission prompt **once more** for that new version. Confirm it again.

## 4. Open the dialog

Right-click any Data Block in the project tree → **BlockParam...** → **Edit Start Values...**

<p align="center">
  <img src="../../assets/screenshots/install/04_context_menu.png" alt="Right-click context menu on a Data Block showing BlockParam → Edit Start Values..." width="500">
</p>

The dialog opens with the DB analyzed and ready to edit. You should see the member tree
on the left and the inspector panel on the right.

<p align="center">
  <img src="../../assets/screenshots/01_hero.png" alt="BlockParam main dialog opened on a deeply nested DB" width="700">
</p>

The Add-In can be opened on **any** DB without configuration — you don't need to
write a `config.json` first. Configuration only adds value constraints, comment
templates, and tag-table autocomplete.

## Next steps

- [Bulk apply workflow](bulk-workflow.md) — your first bulk edit, end to end.
- [Rule editor](rule-editor.md) — once you want validation and constraints.
- [Licensing](licensing.md) — what's included in the free tier.
