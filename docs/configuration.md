# Configuration Guide

## Overview

BlockParam works **fully without a configuration file**. All bulk editing
features (start value changes, scope selection) are available immediately.

The optional configuration file adds:
- **Value constraints**: Min/max ranges, allowed-value lists
- **Comment generation**: Automatic comments based on the member hierarchy
- **Tag table references**: Metadata linking members to TIA tag tables (V2: autocomplete)

## File Location

The Add-In searches for `config.json` in these locations (first match wins):

1. `%AppData%\BlockParam\config.json` (user-specific)
2. Next to the `.addin` file in the Add-In directory

## File Format

JSON with optional comments (JSONC). See `docs/example-config.jsonc` for a
complete annotated example.

### Schema

```json
{
  "version": "1.0",
  "rules": [ ... ],
  "commentGeneration": { ... }
}
```

### Rules

Each rule matches members by `memberName` and optionally `datatype`:

```json
{
  "memberName": "ModuleId",
  "datatype": "Int",
  "constraints": {
    "min": 0,
    "max": 9999,
    "allowedValues": [1, 2, 3, 42]
  },
  "tagTableReference": {
    "tableName": "Constants_Modules",
    "description": "For V2 autocomplete"
  }
}
```

| Field | Required | Description |
|---|---|---|
| `memberName` | Yes | Name of the member to match (exact match) |
| `datatype` | No | Data type filter (e.g. "Int", "Bool"). If omitted, matches any type |
| `constraints.min` | No | Minimum allowed numeric value |
| `constraints.max` | No | Maximum allowed numeric value |
| `constraints.allowedValues` | No | List of allowed values. Rejects any value not in the list |
| `tagTableReference.tableName` | No | Name of a TIA tag table (metadata in V1, active in V2) |
| `tagTableReference.description` | No | Description for documentation |

### Comment Generation

```json
{
  "commentGeneration": {
    "enabled": true,
    "language": "en-US",
    "separator": " > ",
    "levels": ["db", "parent", "self"]
  }
}
```

| Field | Default | Description |
|---|---|---|
| `enabled` | `false` | Enable/disable comment generation |
| `language` | `"en-US"` | Language code for `MultiLanguageText` in SimaticML |
| `separator` | `" > "` | String between hierarchy levels |
| `levels` | `["db", "parent", "self"]` | Which hierarchy levels to include |

**Available levels:**
- `"db"` — Data Block name
- `"self"` — The member's own name
- `"parent"` — Direct parent member name
- `"grandparent"` — Grandparent member name

**Example output** (with `["db", "parent", "self"]` and separator `" > "`):
```
DB_Foerderer1 > Msg_CommError > ModuleId
```

## Behavior Without Config

When no config file exists:
- All values are accepted (no validation)
- No comment generation
- The tool functions as a generic bulk editor for any DB structure
