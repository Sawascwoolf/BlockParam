# Cancel on DB-switch prompt does not undo the active-set mutation

**Status:** open
**Branch observed on:** `claude/fix-freemium-counter-Yxy8G` (PR #74)
**Related:** PR #74 §B (#59 Stash & PLC prefix), PR #74 §I (#78 snapshot active-set + cascade funnel)

## Repro

1. Open the dialog with DB A active (active set = `[A]`).
2. Make ≥ 1 pending edit on a member in DB A.
3. Open the DB-switcher dropdown and click DB B.
4. The 3-way prompt fires: **Apply / Keep / Cancel**.
5. Click **Cancel**.

## Expected

Active set stays `[A]`. Cancel = no observable change anywhere in the dialog.

## Actual — root cause

**Active set ends up as `[A, B]`** — DB B was added to the active set when the dropdown row was clicked, *before* the prompt resolved. Cancel does not roll the active-set mutation back.

## Actual — downstream UI symptoms (all fall out of the `[A] → [A, B]` mutation)

- ❌ Tree view got **collapsed** (rebuilt for the new 2-DB synthetic-root layout).
- ❌ Member selection got **cleared** — bulk-edit panel shows the "Click a leaf member to select" placeholder.
- ❌ "New Value" field is **still populated** with the value that was active before the messagebox.
- ❌ Bulk Preview still shows the **preview computed from the prior selection**.

Net effect: a self-contradictory UI where the panel says no member is selected, yet displays the previous member's edited value and its bulk preview — and DB B is silently active even though the user explicitly cancelled the switch.

Pending edits on A do survive (count and values intact) — at least no data loss.

## Hypothesis

The dropdown's row-click handler is mutating `_activeDbs` (or its setter chain) eagerly and only *then* asking the user. The #78 snapshot+cascade refactor was supposed to be the single funnel for active-set mutations; the switcher click path likely still bypasses the snapshot guard and commits the new `_activeDbs` before the prompt's `DialogResult` is in hand.

Cancel branch needs to either:
- a) not mutate `_activeDbs` at all until after the prompt resolves, **or**
- b) revert `_activeDbs` (and the cascade-driven UI rebuild) on the Cancel path.

(a) is the cleaner fix and matches the #78 funnel intent — the prompt should be a pre-condition of the snapshot transaction, not a post-hoc rollback target.

## Acceptance criteria

- After Cancel on the 3-way switch prompt: `_activeDbs` is identical to before the dropdown click; tree expansion, member selection, NewValue text, Bulk Preview, validation banner are all byte-identical to their pre-click state.
- Add a `BulkChangeViewModelInvariantTests` row covering "Cancel on switch prompt" — assert active-set equality plus the same 5-property cross-cut used by the existing #78 matrix.
- While there: verify Keep and Apply also leave the active set in the *intended* shape (Apply: `[B]`, Keep: `[B]` with A's edits in stash). The current bug suggests the click handler may be writing the active set independently of the prompt outcome.
