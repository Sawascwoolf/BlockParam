# Multi-Seat License Deployment

For multi-seat customers, BlockParam supports rolling out and rotating the license key via existing IT deployment tooling — batch scripts, SCCM, Intune, or GPO — instead of asking every engineer to paste the key into the License dialog.

## How it works

On every Add-In start, `OnlineLicenseService` looks up the license key in this order:

1. **Machine-wide managed file** — `%PROGRAMDATA%\BlockParam\license.key`
2. **Per-user cache** (default fallback) — `%APPDATA%\BlockParam\license.json`

If the managed file exists and contains a non-empty key:

- That key wins. The per-user cache is replaced with the new key.
- If the key changed since the last start, the cached server response is invalidated so the next heartbeat re-validates against the license server.
- The per-instance ID is preserved across rotations, so the server can swap the key on the same session slot without burning a fresh seat.
- The License dialog shows a read-only hint (`Managed by IT — key from %PROGRAMDATA%\BlockParam\license.key`) and disables the input box and the Remove License button — users can't accidentally overwrite or remove a managed key.

UNC and network paths are explicitly **not** supported. The Add-In only reads a local path. Push the file to each machine with your normal deployment tooling.

## Rolling out the key

Drop a single file containing the license key onto every seat:

```
%PROGRAMDATA%\BlockParam\license.key
```

The file must contain only the key (whitespace and trailing newlines are stripped). Example using a batch script:

```bat
@echo off
if not exist "%PROGRAMDATA%\BlockParam" mkdir "%PROGRAMDATA%\BlockParam"
> "%PROGRAMDATA%\BlockParam\license.key" echo PRO-XXXX-XXXX-XXXX
icacls "%PROGRAMDATA%\BlockParam\license.key" /inheritance:r /grant:r "Authenticated Users:(R)" "Administrators:(F)" "SYSTEM:(F)"
```

PowerShell equivalent:

```powershell
$dir = "$env:ProgramData\BlockParam"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Set-Content -Path "$dir\license.key" -Value "PRO-XXXX-XXXX-XXXX" -Encoding ASCII -NoNewline
icacls "$dir\license.key" /inheritance:r /grant:r "Authenticated Users:(R)" "Administrators:(F)" "SYSTEM:(F)"
```

Restart TIA Portal on each seat to pick up the new key. Seats already running keep their cached key until the next start.

## Rotating the key

Rotation is a single-file replace. Push a new `license.key` to every seat with the same deployment script — every seat picks it up on the next Add-In start with no user interaction. The server-side concurrent-session validation continues unchanged.

## Recommended ACLs

`%PROGRAMDATA%\BlockParam\license.key` is a low-sensitivity file (the key is also visible in HTTP traffic to the license server), but a tight ACL keeps users from tampering with it locally:

| Principal               | Permission |
| ----------------------- | ---------- |
| `Authenticated Users`   | Read       |
| `Administrators`        | Full       |
| `SYSTEM`                | Full       |

The example scripts above set these permissions.

## What stays the same

- Online heartbeat and concurrent-session validation are unchanged — this only changes **how the key gets onto the machine**, not how it's validated.
- Free tier behavior is unchanged.
- The per-user cache (`%APPDATA%\BlockParam\license.json`) is still used when no managed file is present — single-engineer installs work exactly as before.

## Troubleshooting

- **Seat still shows Free after rollout**: confirm the file exists at `%PROGRAMDATA%\BlockParam\license.key` (not `%APPDATA%`), contains exactly the key, and that TIA Portal has been restarted. Then check `%APPDATA%\BlockParam\blockparam.log` for an `Adopting managed license key from ...` line.
- **Heartbeat fails after rotation**: the license server must accept the new key. The Add-In itself does not validate the key locally — verify on the server side that the key is provisioned for the customer.
- **User wants to revert to a personal key**: remove `%PROGRAMDATA%\BlockParam\license.key` (requires admin) and restart TIA. The Add-In falls back to the per-user cache, and the License dialog becomes editable again.
