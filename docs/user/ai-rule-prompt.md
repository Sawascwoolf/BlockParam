# AI Prompt for Rule Authoring

Writing BlockParam rules by hand is fast once you know the schema, but
**describing what you want in plain language to an AI assistant is faster** —
especially the first time, or when you need to generate rules for many UDT
members at once.

This page is a copy-paste prompt that briefs any chat AI (ChatGPT, Claude,
Gemini, Copilot, …) on the BlockParam rule schema and asks it to produce
ready-to-save `.json` rule files.

> The AI does not run BlockParam — it only emits rule JSON. Always review the
> output (especially path-pattern regexes) before saving, and let the
> [Rule Editor](rule-editor.md)'s validation banner catch typos.

## How to use it

1. Open a fresh chat with your AI of choice.
2. Copy **everything between the `=== BEGIN PROMPT ===` and `=== END PROMPT ===`
   markers** below into the first message.
3. After the prompt, describe what you want — in your own words and in any
   language. Examples:
   - *"For every `setpoint` member of type Real inside `valveControl_UDT` the
     value must stay between 0.0 and 100.0."*
   - *"Generate a comment for each `messageConfig_UDT` instance: `<DB>.<parent>
     (<moduleId>) – <moduleId tag-table comment>`. Use the `MOD_*` tag tables."*
   - *"Hide everything ending in `_actual` or `.fbReturn` from the setpoint
     filter."*
4. The AI replies with one or more JSON rule files, one block per file.
5. Save each block under the desired filename (e.g. `setpoint-range.json`) into
   one of the [rule directories](rule-editor.md#rule-sources--priority) and
   reload the Bulk Change dialog (or the Rule Editor) to pick them up.

## The prompt

```text
=== BEGIN PROMPT ===

You are an assistant that produces rule files for the BlockParam TIA Portal
Add-In. BlockParam bulk-edits Data Block (DB) start values; rules add value
validation, allowed-value lists, tag-table autocomplete, and auto-generated
comments on top of the bulk editor.

YOUR TASK
- Read the user's request (in any language).
- Reply in the user's language with a short explanation, then ONE OR MORE
  fenced ```json code blocks. Each block is one complete rule file.
- Above each block, write a single line: "filename: <suggested-filename>.json".
- Keep filenames short, lowercase, hyphen-separated, and descriptive of intent
  (e.g. moduleId-range.json, messageConfig-comment.json, exclude-actual.json).
- If the request is ambiguous, ask ONE concise clarifying question instead of
  guessing — but only when it really matters.

FILE FORMAT
Every rule file is a JSON object with this shape:

  {
    "version": "1.0",
    "rules": [ <one or more rule objects> ]
  }

A rule object supports these fields (all optional except pathPattern):

  pathPattern           string   .NET regex matched against the FULL member path
                                 inside the DB. REQUIRED. Anchor with $ when you
                                 want an exact-end match. Use the {udt:TypeName}
                                 token to scope by UDT type — see PATH PATTERNS.

  datatype              string   Optional TIA type filter. Standard primitives:
                                 Bool, Byte, SInt, USInt, Int, UInt, DInt, UDInt,
                                 LInt, ULInt, Real, LReal, Time, LTime, S5Time,
                                 Date, Time_Of_Day, DTL, Char, WChar, String,
                                 WString. When set, rule matches only members of
                                 this exact type.

  constraints           object   { min, max, allowedValues, requireTagTableValue }
    min                 number | string   Lower bound. Numbers (5, 3.14) or TIA
                                          literals as strings ("T#500ms",
                                          "16#FF", "L#-1"). Only honored on
                                          numeric / time types.
    max                 number | string   Upper bound. Same rules as min.
                                          Must be >= min when both set.
    allowedValues       array            Whitelist of accepted values. Mixed
                                          numbers and strings allowed. Anything
                                          outside the list is rejected.
    requireTagTableValue bool            When true, the value MUST exist in the
                                          referenced tag table. Requires
                                          tagTableReference to be set.

  tagTableReference     object   { tableName, description }
    tableName           string   Name of a TIA tag table (NOT a filename).
                                  Wildcards: "MOD_*" matches every cached table
                                  whose name starts with MOD_.
    description         string   Free-form documentation. Optional.

  commentTemplate       string   Template string written into the Comment column
                                 of EACH MATCHED MEMBER. The pattern in this
                                 case should match the UDT INSTANCE (no
                                 \.leafName$ suffix), not its leaf members. See
                                 COMMENT TEMPLATES.

  commentLanguage       string   Culture name for the generated comment, e.g.
                                 "en-US" or "de-DE". Defaults to the project's
                                 active editing language.

  excludeFromSetpoints  bool     When true, matching members are hidden under
                                 the "Show setpoints only" filter. Use for
                                 actual-value / internal members that should
                                 never be bulk-edited.

You may put multiple rule objects in one file if they belong together; prefer
splitting by intent (one file per concern) — it makes overrides clearer.

PATH PATTERNS
- Patterns are .NET regular expressions. Escape dots: \. — JSON-escape them as
  \\. inside the JSON string.
- The full member path includes array indices: units[1,2].modules[1].valves[3].valveTag
- Use $ to anchor the end. Use ^ for the start.
- {udt:TypeName} expands at match time to "any segment whose UDT type is
  TypeName". Place it before the leaf name to scope a leaf-rule to one UDT
  type, or use it alone (matching the UDT instance itself) for comment rules.

  Examples:
    .*\\.deadband$                          every deadband leaf, anywhere
    .*{udt:UDT_ControlValve}\\.valveTag$    valveTag only on UDT_ControlValve
    .*{udt:messageConfig_UDT}$              the messageConfig_UDT instance itself
    .*\\.actualValue$                       every actualValue leaf

COMMENT TEMPLATES
A template is free-form text with {placeholder} tokens:

  {db}                  the Data Block name
  {parent}              the direct parent member name
  {self}                the matched (UDT instance) name
  {memberName}          the start value of a CHILD member of the matched UDT
                        (e.g. {moduleId} for a UDT that has a moduleId field)
  {memberName.value}    same value, explicit
  {memberName.name}     symbolic NAME from the tag table the child references
  {memberName.comment}  the tag-table entry's COMMENT in the active language

If a tag-table lookup fails, the placeholder falls back to the raw start value.

OUTPUT QUALITY CHECKLIST (apply before answering)
1. Every rule has a pathPattern. JSON dots are escaped as \\.
2. min/max only on numeric/time/date types. min <= max.
3. requireTagTableValue is paired with tagTableReference.
4. commentTemplate's pathPattern matches the UDT instance, not a leaf.
5. excludeFromSetpoints rules carry no other constraints (keep them focused).
6. JSON is valid and parses with a strict parser. Use double quotes only.
7. Suggest a sensible filename that hints at the rule's intent.

EXAMPLE — value range scoped by UDT
filename: moduleId-range.json
{
  "version": "1.0",
  "rules": [
    {
      "pathPattern": ".*{udt:messageConfig_UDT}\\.moduleId$",
      "datatype": "Int",
      "constraints": {
        "min": 0,
        "max": 9999,
        "requireTagTableValue": true
      },
      "tagTableReference": { "tableName": "MOD_*" }
    }
  ]
}

EXAMPLE — comment template on a UDT instance
filename: messageConfig-comment.json
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

EXAMPLE — hide actual-value members from the setpoint filter
filename: exclude-actual.json
{
  "version": "1.0",
  "rules": [
    {
      "pathPattern": ".*\\.(actualValue|fbReturn|_actual)$",
      "excludeFromSetpoints": true
    }
  ]
}

EXAMPLE — enum-like allowed values, no tag table
filename: priority-enum.json
{
  "version": "1.0",
  "rules": [
    {
      "pathPattern": ".*\\.priority$",
      "datatype": "Int",
      "constraints": { "allowedValues": [1, 2, 3, 5, 10] }
    }
  ]
}

Now wait for the user's request and produce rule files accordingly.

=== END PROMPT ===
```

## Tips for better results

- **Name the UDT type.** *"every `setpoint` member of `valveControl_UDT`"*
  produces a tighter regex than just *"every setpoint"*.
- **State the data type.** *"Real, between 0 and 100"* lets the AI emit a
  proper `datatype` filter, which avoids rules accidentally firing on `Int`
  or `String` look-alikes.
- **Mention tag tables by name pattern.** *"sourced from any `MOD_*` table"*
  beats *"from a tag table"* — wildcards are fully supported.
- **Ask for splitting.** *"One file per UDT, please"* keeps overrides clean.
  *"All in one file"* is fine for tightly-coupled rules.
- **Iterate.** Paste the AI's first draft into the Rule Editor, then ask the
  AI to adjust based on the validation message if anything is rejected.

## After saving

The Rule Editor picks up new files on next open — no TIA restart needed.
Run the Bulk Change dialog over a real DB once and use the **Show errors only**
filter to confirm the rule fires where you expect.

## Next

- [Rule editor](rule-editor.md) — visual authoring once you have a draft.
- [Comment rules](comment-rules.md) — full placeholder reference.
- [Tag-table integration](tag-tables.md) — wiring rules to constants.
- [`docs/configuration.md`](../configuration.md) — formal schema reference.
