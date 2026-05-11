# Walkthrough handoff — 2026-05-11 → next session

## Context

PR #74 manual walkthrough today surfaced 5 real issues. The user clarified intent for each (resolution comments are pinned on each issue). **Next task: write 5 failing-test bug specs** that codify those intents. Pattern follows `a35c46a` (red rows committed as bug specs, fixes land later).

**Do NOT implement the fixes.** Tests only.

## Repo state

- Branch `claude/fix-freemium-counter-Yxy8G`, synced with origin (fast-forwarded to `58641b9`).
- `src/BlockParam.DevLauncher/Program.cs` is **dirty / uncommitted** — `buildActiveDbForSummary` wiring + `--plc` arg added today. Either fold into the test commit or split it; user's call.
- PR #74 body was updated today with the walkthrough findings.

## The 5 tests to write

Order: easiest → hardest, so the first commits land green-on-red quickly.

### 1. #95 — Cross-DB focus row exclusivity (cheapest)

- File: `src/BlockParam.Tests/BulkChangeViewModelInvariantTests.cs`
- Resolution: single global focus row. Setting `IsSelected = true` on a leaf in DB A must clear it on any prior leaf in DB B.
- Two shapes possible — pick one:
  - **A.** Add Invariant 10 in `AssertInvariants`: "at most one node in `RootMembers` tree has `IsSelected == true`." Every existing test row gets stricter; expect several to start failing on this guard alone, which is fine for a red commit.
  - **B.** Dedicated row `Selection_PlainClickInDbA_ClearsPriorSelectionInDbB`: build 2-DB env, set `dbBLeaf.IsSelected = true`, then `dbALeaf.IsSelected = true`, assert exactly one selected, and it's `dbALeaf`.
- Verify `MemberNodeViewModel.IsSelected` (already at line 145) is the right surface; if WPF holds extra per-tree state, the fix lives outside this test's reach — that's fine, the test still pins the VM contract.

### 2. #93 — Dropdown-add must not prompt; pending survives via PendingEditStore

- File: same.
- Resolution: remove the `AddActiveDbWithPendingEditPrompt` foreach loop. PendingEditStore (`9814a6e`) makes the orphan-on-rebuild bug a non-issue.
- Test name: `Add_DropdownCheck_AnchorHasEdits_NoPromptFires_PendingEditSurvives`
- Setup uses the existing `ActiveSetTestBuilder` — anchor `flat-db.xml` + `WithDropdownPeer("nested-struct-db.xml")` + `WithPendingEditsOn("FlatDB", 1)`. **Do NOT call `WithPromptResults(...)`.**
- Toggle `peerRow.IsActive = true`.
- Assert: `AskYesNoCancelCallCount == 0`; `AllActiveDbs.Count == 2`; anchor's pending leaf value unchanged; `PendingInlineEditCount == 1`; `AssertInvariants(env.Vm)`.
- Will fail today because `PromptForPendingEditsOnRemove` is called inside the foreach → `RecordingFakeMessageBox` throws "no scripted response" (or the user's later choice flips state). Either failure mode is the red signal.
- **Also rewrite existing Row 11** (`DropdownRow_ToggleToAdd_AnchorHasEdits_PromptCancel_LeavesActiveSetUnchanged`) to reflect the new intent — it currently locks in the prompt-fires-and-cancels behaviour, which becomes wrong once #93 lands.

### 3. #91 — Multi-DB title carries no single DB's name

- File: same.
- Resolution: chip-only header in multi-DB. Single-DB session keeps the current title shape.
- Test name: `Title_MultiDb_DoesNotContainAnySingleDbName`
- Setup: 2-DB env via `WithAnchor` + `WithPeer`.
- Assert: `env.Vm.Title.Should().NotContain("FlatDB")` and the peer's name too.
- Will fail today because `BuildTitle(version, plcName, dbName)` always renders the anchor's name (`UI/BulkChangeViewModel.cs:1857-1858`).
- **Also add a sibling row** `Solo_ChipBody_TitleRefreshesOnCascade`:
  - 3-DB env → record `vm.Title` → solo to DB B via chip body → assert `vm.Title` reflects the post-solo single-DB shape.
  - Captures the `#91` comment about solo not firing `OnPropertyChanged(Title)`. Row 14 covers chip-close-anchor already; this plugs the solo gap.

### 4. #90 — Scope dedupe must not collapse orthogonal 2D dim slices

- File: `src/BlockParam.Tests/HierarchyAnalyzerMultiDbTests.cs`
- Resolution: dedupe key = `(MatchCount, sorted member-path set)`. Two scopes only collapse if they target the same node set.
- Fixture work needed — existing `array-db.xml`'s `Matrix` is `Array[0..1, 0..2]` (rectangular; row-fix=3, col-fix=2, no dedupe trigger). Options:
  - **Add** a `SquareMatrix` member to `array-db.xml`: `Datatype="Array[0..2, 0..2] of Int"`. Cell `[1,2]` → row-fix `[1,*]` = 3, col-fix `[*,2]` = 3.
  - **Or** new fixture `array-2d-symmetric-db.xml` with `Array[1..2, 1..2] of UDT_Unit` (UDT with multiple fields) to match the user's original repro shape.
- Recommend the in-place edit — smaller diff, no new fixture file.
- Test name: `AnalyzeMulti_2dArraySymmetricInteriorLeaf_BothRowFixAndColFixSurvive`
- Assert: result contains a scope whose `AncestorName` contains `[1,*]` AND a separate scope containing `[*,2]`; both have equal `MatchCount`; their `MatchingMembers` sets are **not** equivalent (orthogonal cell sets).
- Will fail today because `DeduplicateByMatchCount` keeps the first scope per `MatchCount` and drops the second.

### 5. #92 — Reactivate prompts user (additive vs replace) — heaviest

- File: `BulkChangeViewModelInvariantTests.cs`
- Resolution: when `AllActiveDbs.Count >= 2`, clicking a `PENDING IN <DB>` header must fire a prompt (additive / replace / cancel). Single-DB session skips the prompt.
- Test name: `Reactivate_StashHeader_OtherDbsActive_PromptsForAdditiveOrReplace`
- Two flavours probably needed:
  - `Reactivate_StashHeader_PromptAdditive_KeepsOtherActive` — `WithPromptResults(YesNoCancelResult.Yes)` → active set ends as `[other, stashed]`.
  - `Reactivate_StashHeader_PromptReplace_DropsOthers` — `WithPromptResults(YesNoCancelResult.No)` → active set ends as `[stashed]` only.
- **Must also rewrite existing Row 8** (`Reactivate_StashHeader_OneOtherActiveNoEdits_SoloesAndRestores`) — it currently asserts `HaveCount(1)` after reactivate, which the user explicitly said is the wrong default. Split it into the additive and replace branches above.
- Will fail today because `ReactivateStashedDb` (`UI/BulkChangeViewModel.cs:1422`) calls `ComposeRemoveOthers` unconditionally — no prompt fires, the `RecordingFakeMessageBox` doesn't get called, the result count is wrong vs. the new assertion.

## After tests are red

- Run `dotnet test BlockParam.sln` and confirm exactly 5 new rows are red (plus any pre-existing rows that flip on the new Invariant 10 if you went that route for #95 — those need updating in the same commit).
- Single commit titled e.g.
  ```
  test: capture 5 multi-DB bugs as failing invariant rows (#90/#91/#92/#93/#95)
  ```
- Body lists each row + its issue # + the resolution it codifies. Co-Authored-By line per repo convention.
- **Do not push without user OK** — test phrasings encode the user's intent and they may want to review.
- Do NOT touch the PR body again; today's update already references the issues.

## Files not in scope

- `src/BlockParam/UI/BulkChangeViewModel.cs` — fixes live here; tomorrow is tests only.
- `src/BlockParam/UI/PendingEditStore.cs` — recently introduced; fully covered.
- `src/BlockParam/Services/HierarchyAnalyzer.cs` — #90's fix lives here; tomorrow is the failing test only.

## Open thread (not blocking)

- DevLauncher `Program.cs` uncommitted. Two test fixtures already exercise `buildActiveDbForSummary` (look at `ActiveSetTestBuilder.BuildForSummary` line ~827) so the wiring isn't load-bearing for tomorrow's tests — committing it can wait.
- Minor UX gaps not filed individually (dropdown row doesn't flag pending-edit DBs; transient "Apply unavailable on add-prompt" observation) — subsumed by the resolutions on #93 and friends.
