# Comment Rules

Comment rules let BlockParam **generate the Comment column for you** based on a
template. They live inside the same rule files as value constraints — there's no
separate "comment file" type — and are edited via the **Comment Template** field
in the [Rule Editor](rule-editor.md).

## When comments are written

Comments are generated **per UDT instance**, not per leaf member. When you stage
or apply a bulk edit, BlockParam walks up from each affected leaf to find every
UDT ancestor with a matching comment rule and regenerates that ancestor's
comment.

> **Existing comments are overwritten** when the rule's path pattern matches.
> Members with no matching comment rule are left untouched.

## Template syntax

A template is a free-form string with `{placeholder}` tokens that resolve at
generate time.

### Built-in placeholders

| Placeholder | Resolves to |
|---|---|
| `{db}` | The Data Block name (e.g. `DB_ProcessPlant_A1`). |
| `{parent}` | The direct parent member name. |
| `{self}` | The UDT instance's own name. |
| `{memberName}` | The start value of a child member of this UDT (e.g. `{moduleId}` → the value of the `moduleId` field). |

### Tag-table-aware placeholders

When the matched UDT contains a child member with a `tagTableReference`, the
following expansions look up the tag-table entry and pull a field from it:

| Placeholder | Resolves to |
|---|---|
| `{memberName.value}` | The tag-table entry's value (the constant the member resolves to). |
| `{memberName.name}` | The tag-table entry's symbolic name (e.g. `MOD_PUMP_01`). |
| `{memberName.comment}` | The tag-table entry's comment in the active TIA Portal language. |

If the lookup fails (no tag table, no matching entry), the placeholder falls back
to the raw start value.

### Example template

```
{db}.{parent} ({moduleId}, {elementId}) : {moduleId.comment}
```

For a `messageConfig_UDT` instance under `alarms[3]` in a DB called
`DB_Foerderer1`, this might generate:

```
DB_Foerderer1.alarms (12, 5) : Pump 1 — Overcurrent
```

…where `12` is the `moduleId` start value, `5` is the `elementId`, and the
trailing text comes from the `MOD_*` tag table comment for entry `12`.

## Multiple languages

If your TIA project has comments in multiple languages, BlockParam writes the
generated comment in the **editing language** for the project. Reference-language
fallbacks are handled automatically when a tag-table comment is missing in the
active language.

If you need a rule to write comments in a specific language regardless of the
project's editing language, set the `commentLanguage` field on the rule (this is
not yet exposed in the editor — set it via the JSON file). See
[`docs/example-config.jsonc`](../example-config.jsonc).

## Example: a comment rule file

Saved as `messageConfig-comment.json` in any of the three [rule directories](rule-editor.md#rule-sources--priority):

```json
{
  "version": "1.0",
  "rules": [
    {
      "pathPattern": ".*{udt:messageConfig_UDT}$",
      "commentTemplate": "{db}.{parent} ({moduleId}, {elementId}) : {moduleId.comment}",
      "commentLanguage": "en-US"
    }
  ]
}
```

Notes:

- The pattern matches the **UDT instance itself** (no `\.memberName$` suffix) —
  the comment is written on the UDT, not on its leaf members.
- You can mix value-validation fields and `commentTemplate` in the same rule —
  they are not mutually exclusive.

## Disabling comment generation

There is no global "off" switch. To stop generating comments for a member type,
either:

- **Delete** the comment rule via the Rule Editor, or
- **Edit** the rule and clear the `commentTemplate` field.

Existing comments in the DB are not removed when you delete a rule — the rule
just stops writing new ones.

## Next

- [Rule editor](rule-editor.md) — where you author comment rules.
- [Tag-table integration](tag-tables.md) — what powers `{memberName.comment}`.
- [Config storage](config-storage.md) — file locations and backups.
