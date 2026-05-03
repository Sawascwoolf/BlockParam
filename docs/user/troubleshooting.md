# Troubleshooting

If something is wrong, the diagnostic log at `%APPDATA%\BlockParam\blockparam.log`
is the first place to look. Most errors below print a recognisable line there.

## The Add-In doesn't appear in TIA Portal

1. Confirm the file is in the **right folder for your TIA version**:
   - V20: `C:\Program Files\Siemens\Automation\Portal V20\AddIns\`
   - V21: `%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\`
2. Confirm the file is the **right `.addin` for your TIA version**. A V20 `.addin`
   on V21 (or vice versa) silently fails to load — there's no error message in TIA.
   Re-download from the [releases page](https://github.com/Sawascwoolf/BlockParam/releases)
   and pick the matching variant.
3. Open the **Add-ins** task card on the right edge of TIA. The Add-In should
   appear there even if disabled. If it doesn't, the file isn't being read at all
   (wrong folder, wrong version, or corrupt download).
4. Toggle the Add-In on; confirm the security/permission prompt.

## "Edit Start Values…" is missing from the right-click menu

Make sure the right-click target is a **Data Block**. The menu entry is only
attached to DBs in the project tree, not to FBs, FCs, OBs, UDTs, or tag tables.

If the menu is missing on a real DB, BlockParam itself is not enabled — see the
previous section.

## "Openness API not available"

TIA Portal's Openness API is what BlockParam uses to read and write DBs.
If the log says "Openness API not available", common causes:

- TIA Portal is not running. Start TIA, open a project, then re-open the Add-In.
- The **Openness** feature was deselected during TIA installation. Re-run the
  TIA installer and ensure Openness is included.
- You're not in the **Siemens TIA Openness** Windows group on this machine. Add
  yourself (or have an admin add you) and sign out / in.

## "DB is locked" or "Block is exclusively locked by another user"

Another engineer has the DB open in a Detailed View, or you have it open in
another TIA window. BlockParam can't write while the DB is locked.

- Close every other view of the DB.
- If you're in a multi-user environment, make sure no one else has the DB
  checked out.
- Restart TIA Portal as a last resort to clear stale locks.

## "DB has unresolved UDTs — please compile first"

The DB references a UDT that isn't compiled, so XML export round-trips fail.
BlockParam offers to compile the project for you — accept and retry. If
compilation also fails, fix the underlying inconsistency in TIA before retrying.

## "Block is write-protected" / "Block is know-how protected"

Protected blocks can't be modified by Openness. BlockParam detects this up front
and warns before staging changes. Options:

- **Write protection**: remove it via right-click on the DB → **Properties** →
  **Attributes** → uncheck **Write-protected**. Re-apply after the bulk edit.
- **Know-how protection**: only the owner of the protection password can
  unprotect the block. Contact whoever owns it.

## Apply silently does nothing

A handful of root causes:

- The pending queue is empty. The status bar shows *"0 pending edits"*.
- All pending values are invalid (red rows). The Apply button is disabled until
  you fix them.
- You hit the **Free-tier daily quota** (200 value changes per day) — or your
  pending batch would push past it. Apply is disabled with a tooltip; the
  status bar shows *"This Apply would write N changes, but only M are left
  today."* See [Licensing](licensing.md).

## "Daily limit reached"

You've used all 200 free value changes for the calendar day. Options:

- Wait until **local midnight** for the counter to reset.
- Upgrade to [Pro](licensing.md#activating-pro) for unlimited changes.

The counter is local — there's no way to "borrow" tomorrow's quota by changing
the system clock (the Add-In detects backwards clock changes and the counter
sticks).

## Tag-table autocomplete is empty

The tag-table cache hasn't been populated, or the rule's table name doesn't match.

1. Confirm `%TEMP%\BlockParam\TagTables\` contains your exported `.xml` files.
2. Open one and check the `<SW.Tags.PlcTagTable Name="…">` attribute — that's
   the name your rule's `tagTableReference.tableName` must match (or match via
   wildcard like `MOD_*`).
3. Click the **Refresh** icon next to "Tag tables: N old" in the toolbar.
4. Check the log for `TagTables loaded:` lines — they list every table the cache
   picked up.

See [Tag-table integration](tag-tables.md#troubleshooting) for more.

## Pro features stopped working

The license cache expired without a successful heartbeat. The status bar reads
*"Free License — server unreachable"*.

- Check connectivity to `license.lautimweb.de` (HTTPS).
- Behind a corporate proxy: confirm the system-wide proxy is configured. The
  Add-In honors it.
- Click the **License button** in the dialog's bottom bar and click
  **Activate** again to force a fresh validation.

## "Too many sessions" on activation

Your Pro key is currently activated on another machine.

- On the other machine: open the License dialog (bottom-bar button) and click
  **Remove License** to release the seat immediately, or
- Wait 48 hours — the server releases stalled seats automatically after the
  heartbeat-miss window.

## The dialog opens slowly

Context-menu open should be < 200 ms; first dialog open on a large DB can take
a few seconds while the tree is parsed and rules are matched. If it routinely
takes more than 5 seconds:

- Check log for `Tree built:` and `Rules matched:` timing lines.
- Very large DBs (10 000+ members) genuinely take a few seconds — this is
  one-time work per dialog open.
- A misbehaving regex in a rule (e.g. catastrophic backtracking) can block the
  match phase. Check the log for which rule is slow and simplify its pattern.

## The UI looks too small / too large

Use **Ctrl + Scroll**, **Ctrl +/-**, or **Ctrl + 0** (reset) to zoom. The setting
persists per workstation in `%APPDATA%\BlockParam\ui-settings.json`. Default is
1.2× for high-DPI displays.

## Something else / a crash

1. Reproduce the issue and copy `%APPDATA%\BlockParam\blockparam.log`.
2. Open an issue at
   [github.com/Sawascwoolf/BlockParam/issues](https://github.com/Sawascwoolf/BlockParam/issues)
   with the log attached and a description of the steps.
3. For licensing-specific issues, email
   [support@lautimweb.de](mailto:support@lautimweb.de) with the log and your
   license key.

## Resetting BlockParam to a clean state

If something is genuinely wedged and you want a clean slate, see
[Config storage → Resetting BlockParam](config-storage.md#resetting-blockparam).
