# SimaticML XML Format for Data Blocks

SimaticML (SIMATIC Markup Language) is a Siemens XML standard for exchanging
software data in TIA Portal.

## High-Level Structure of an Exported Global DB

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <DocumentInfo>
    <Created>...</Created>
    <ExportSetting>WithDefaults</ExportSetting>
  </DocumentInfo>

  <SW.Blocks.GlobalDB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor />
      <HeaderFamily />
      <HeaderName />
      <HeaderVersion>0.1</HeaderVersion>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Static">
            <!-- Members defined here -->
          </Section>
        </Sections>
      </Interface>
      <IsOnlyStoredInLoadMemory>false</IsOnlyStoredInLoadMemory>
      <IsWriteProtectedInAS>false</IsWriteProtectedInAS>
      <MemoryLayout>Optimized</MemoryLayout>
      <MemoryReserve>0</MemoryReserve>
      <Name>MyDataBlock</Name>
      <Number>1</Number>
      <ProgrammingLanguage>DB</ProgrammingLanguage>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">...</MultilingualText>
      <MultilingualText ID="2" CompositionName="Title">...</MultilingualText>
    </ObjectList>
  </SW.Blocks.GlobalDB>
</Document>
```

## Block Type Root Elements

| Block Type | XML Root Element |
|---|---|
| Global DB | `SW.Blocks.GlobalDB` |
| Instance DB | `SW.Blocks.InstanceDB` |
| Function Block | `SW.Blocks.FB` |
| Function | `SW.Blocks.FC` |
| Organization Block | `SW.Blocks.OB` |

## Member Elements with StartValues

```xml
<Section Name="Static">
  <!-- Simple types -->
  <Member Name="Speed" Datatype="Int" Remanence="NonRetain" Accessibility="Public">
    <Comment>
      <MultiLanguageText Lang="en-US">Motor speed setpoint</MultiLanguageText>
    </Comment>
    <StartValue>1500</StartValue>
  </Member>

  <Member Name="Temperature" Datatype="Real" Remanence="NonRetain" Accessibility="Public">
    <StartValue>25.5</StartValue>
  </Member>

  <Member Name="Enable" Datatype="Bool" Remanence="NonRetain" Accessibility="Public">
    <StartValue>true</StartValue>
  </Member>

  <!-- Nested struct -->
  <Member Name="Config" Datatype="Struct">
    <Member Name="MaxSpeed" Datatype="Int">
      <StartValue>3000</StartValue>
    </Member>
    <Member Name="MinSpeed" Datatype="Int">
      <StartValue>0</StartValue>
    </Member>
  </Member>
</Section>
```

## Interface Section Types

| Section Name | Description |
|---|---|
| `Static` | Persistent data (most common for Global DBs) |
| `Input` | Input parameters (for FBs/FCs) |
| `Output` | Output parameters |
| `InOut` | In/Out parameters |
| `Temp` | Temporary variables |
| `Constant` | Constant values |
| `Return` | Return value |

## Common Data Types

| Datatype | Example StartValue |
|---|---|
| `Bool` | `true` / `false` |
| `Int` | `1500` |
| `DInt` | `100000` |
| `Real` | `25.5` |
| `LReal` | `3.14159265` |
| `String` | `'Hello'` |
| `WString` | `'Hello'` |
| `Time` | `T#5s` |
| `Date` | `D#2024-01-01` |
| `Struct` | (contains nested Members) |
| `Array[0..9] of Int` | (contains Subelement entries) |
| `"UDT_Name"` | (references a user-defined type) |

## Array Representation

```xml
<Member Name="Speeds" Datatype="Array[0..4] of Int">
  <Subelement Path="0"><StartValue>100</StartValue></Subelement>
  <Subelement Path="1"><StartValue>200</StartValue></Subelement>
  <Subelement Path="2"><StartValue>300</StartValue></Subelement>
  <Subelement Path="3"><StartValue>400</StartValue></Subelement>
  <Subelement Path="4"><StartValue>500</StartValue></Subelement>
</Member>
```

## Key XML Rules

- `ID` attributes must be unique integers across the entire XML document
- The namespace on `<Sections>` varies by TIA Portal version:
  - `http://www.siemens.com/automation/Openness/SW/Interface/v5` (V20)
  - Earlier versions use `v2`, `v3`, `v4`
- `MemoryLayout` can be `Optimized` or `Standard`
- When reimporting, the XML interface structure must match the current block definition

## Key References
- [XML Structure of Block Interface Section (V20)](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/xml-structure-of-the-block-interface-section)
- [SimaticML Library (GitHub)](https://github.com/caprican/SimaticML)
- [TiaUtilities (GitHub)](https://github.com/Parozzz/TiaUtilities)
