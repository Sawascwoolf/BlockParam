# Multi-DB Workflow

The single-DB workflow (see [bulk-workflow.md](bulk-workflow.md)) edits start
values inside **one** Data Block. Real projects often repeat the same UDT
instances across **several** DBs — one per process unit, one per drive — and
the same parameter has to stay in sync across them.

Multi-DB editing brings those blocks into **one session**: add peer DBs without
reopening the dialog, scope an edit across all of them, and park half-finished
work on a DB while you deal with another.

## Persona

**Marco, commissioning engineer.** Mid-size plant, one Data Block per process
unit — `DB_ProcessPlant_A1`, `_B1`, `_C1` — each full of the same
`valveControl_UDT` / `module_UDT` instances. The structure repeats; the values
don't. During SAT he tunes setpoints that must stay identical across units that
behave the same physically.

## The pain in raw TIA

One DB at a time. Open `DB_A`, retype the deadband on every `valveControl`
instance, close it, open `DB_B`, do it again, `DB_C`, again. Three blocks,
three passes, and no cross-check that they actually ended up matching.

## The workflow

### 1. Add a peer DB

From the Bulk Change dialog on `DB_A`, click the **`+`** next to the active-DB
chip. A searchable dropdown lists the project's DBs (the current one
pre-checked). Filter, check `DB_ProcessPlant_B1`. The tree rebuilds into
multi-DB shape: one synthetic root per DB, a two-chip strip. No reopen.

### 2. Scope across DBs

Select a member that exists in both DBs — e.g.
`units[*].modules[*].valves[*].valveTag`. The scope dropdown now offers, above
the within-DB scopes, a **"… — across all selected DBs"** mega-scope. Pick it.
Bulk Preview shows entries **grouped by DB**, total = the sum across both.

### 3. Apply once, across both

One **Apply**. Status reports per-DB progress ("2/2 DBs applied"). The usage
counter ticks up by the combined count. One action, both blocks, guaranteed
identical.

### 4. Park work without losing it — Stash

Inline-edit a value on `DB_A` only → a yellow pending row. You need to jump to
another DB but aren't ready to commit `DB_A`. Click the **`×`** on `DB_A`'s
chip. A three-way prompt appears: **Apply / Stash and continue / Cancel**. Pick
**Stash**. The sidebar shows a `PENDING IN DB_A` header; the active set
continues without it. The half-finished edit is safe, not discarded.

### 5. Bring it back — Reactivate (add vs. replace)

Add `DB_C`, then click the `PENDING IN DB_A` header to bring the stashed work
back. A prompt offers **"Add to current session" / "Replace with it" /
Cancel**:

- **Add** → `DB_A` rejoins the active set; its yellow pending row is restored
  exactly where you left it.
- **Replace** → the session collapses to `DB_A` only, single-DB title.

### 6. Drop back to one DB — Solo

Done comparing. Click the **body** of a chip (not the `×`). The session solos
to that DB: the tree collapses and the title bar returns to single-DB shape.
Fast exit from multi-DB back to focused single-DB.

### 7. Multiple PLCs

When the selected DBs live on different PLCs, the chip strip groups chips under
**PLC headers** ("PLC_1" / "PLC_2"). The model scales past one controller —
member identity is per `(DB, PLC)`, so identically-named DBs on different PLCs
never collide.

## What stays the same

- Rules (path patterns, comment templates, tag-table references) match exactly
  as they do in single-DB mode.
- Nothing is written until **Apply** / **Apply & Close**.
- Validation failure on **any** DB blocks the whole apply — no partial writes.
