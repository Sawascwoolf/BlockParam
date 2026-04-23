# Entwicklung ohne TIA Portal Installation

## Übersicht

Das Add-In kann vollständig ohne TIA Portal entwickelt und getestet werden.
Alle TIA-spezifischen Daten (DB-Exporte, Variablentabellen) liegen als
XML-Fixtures im Test-Projekt vor.

## Benötigte Testdaten

### 1. Datenbaustein-Exporte (SimaticML XML)

Exportierte DBs liegen unter `src/BlockParam.Tests/Fixtures/`:

| Datei | Beschreibung |
|---|---|
| `v20-tp307.xml` | Realer V20-Export: GlobalDB mit verschachtelten UDTs (driveMessagesConfig_UDT → messageConfig_UDT) |
| `v20-tp308-instancedb.xml` | Realer V20-Export: InstanceDB (DB als UDT-Instanz) |
| `udt-instances-db.xml` | Vereinfachte Test-Fixture (altes Format ohne nested Sections) |
| `demo-db.xml` | Demo-DB mit Mix aus Setpoint- und Runtime-Membern |

### 2. Variablentabellen (Tag Tables)

Variablentabellen werden über das `ITagTableReader`-Interface gelesen.
Für Tests ohne TIA Portal gibt es zwei Ansätze:

#### a) Fake-Implementation (für Unit Tests)

```csharp
public class FakeTagTableReader : ITagTableReader
{
    public IReadOnlyList<TagTableEntry> ReadTagTable(string tableName)
    {
        // Beispieldaten - im echten TIA Projekt aus Variablentabellen
        if (tableName.StartsWith("MOD_"))
            return new List<TagTableEntry>
            {
                new("MOD_FOERDERER_1", "5", "Int", "Förderer Halle 1"),
                new("MOD_FOERDERER_2", "6", "Int", "Förderer Halle 2"),
                new("MOD_MAIN_DRIVE", "10", "Int", "Hauptantrieb"),
            };
        // ... analog für ELE_ und MES_
        return new List<TagTableEntry>();
    }

    public IReadOnlyList<string> GetTagTableNames() =>
        new List<string> { "MOD_Constants", "ELE_Constants", "MES_Constants" };
}
```

#### b) Exportierte Variablentabellen (XML)

TIA Portal exportiert Variablentabellen als SimaticML XML.
Format einer exportierten Variablentabelle:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <SW.Tags.PlcTagTable ID="0">
    <AttributeList>
      <Name>MOD_Constants</Name>
    </AttributeList>
    <ObjectList>
      <SW.Tags.PlcTag ID="1">
        <AttributeList>
          <DataTypeName>Int</DataTypeName>
          <LogicalAddress>%MW100</LogicalAddress>
          <Name>MOD_FOERDERER_1</Name>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="2" CompositionName="Comment">
            <ObjectList>
              <MultilingualTextItem ID="3" CompositionName="Items">
                <AttributeList>
                  <Culture>de-DE</Culture>
                  <Text>Förderer Halle 1</Text>
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Tags.PlcTag>
      <!-- weitere Tags... -->
    </ObjectList>
  </SW.Tags.PlcTagTable>
</Document>
```

**Hinweis:** Die tatsächlichen Variablentabellen-Exporte aus dem Referenzprojekt
werden unter `src/BlockParam.Tests/Fixtures/tagtables/` abgelegt sobald sie
aus TIA Portal exportiert wurden.

### 3. Relevante XML-Strukturen im DB-Export

#### SetPoint-Attribut

Das TIA Portal setzt ein `SetPoint`-Attribut auf UDT-Instanzen:

```xml
<Member Name="blocked" Datatype="&quot;messageConfig_UDT&quot;">
  <AttributeList>
    <BooleanAttribute Name="SetPoint" SystemDefined="true">true</BooleanAttribute>
  </AttributeList>
  <Sections>
    <Section Name="None">
      <Member Name="moduleId" Datatype="Int">
        <StartValue>5</StartValue>
      </Member>
      <Member Name="actualValue" Datatype="Int" />
      <!-- actualValue hat keinen StartValue und soll vom SetPoint-Filter ausgeschlossen werden -->
    </Section>
  </Sections>
</Member>
```

**Wichtig:**
- Das `SetPoint`-Attribut steht nur auf der UDT-Instanz, NICHT auf den Leaf-Membern
- Leaf-Member erben aktuell SetPoint vom Parent
- `actualValue` soll trotz Vererbung NICHT als Setpoint gelten (Config-Ausschluss)
- Leaf-Member ohne `<StartValue>` haben den Default-Wert 0 (Int) / false (Bool) etc.

#### Verschachtelte UDTs

UDT-Instanzen haben ihre Member in `<Sections><Section Name="None">`,
nicht als direkte `<Member>`-Kinder:

```
Member (UDT-Instanz)
  └── Sections
        └── Section[@Name="None"]
              ├── Member (Leaf oder weitere UDT-Instanz)
              └── Member
                    └── Sections
                          └── Section[@Name="None"]
                                └── Member (Leaf)
```

Inline-Structs haben ihre Member als direkte Kinder:

```
Member[@Datatype="Struct"]
  ├── Member (Leaf)
  └── Member (UDT-Instanz mit Sections)
```

## DevLauncher (WPF Dialog ohne TIA)

Das Projekt `BlockParam.DevLauncher` startet den Bulk-Change-Dialog
standalone mit einer Test-XML. Zum Testen:

```bash
dotnet run --project src/BlockParam.DevLauncher
```

Die Demo-Datei liegt unter `src/BlockParam.DevLauncher/demo-db.xml`.
Für Tests mit den echten V20-Exporten die Datei austauschen.

## Build & Test

```bash
dotnet build
dotnet test
```

Alle 140+ Tests laufen ohne TIA Portal Installation.
Die Siemens-NuGet-Pakete werden nur für den Release-Build benötigt.

## Relevante Dateien für F-080..F-094

| Datei | Zweck |
|---|---|
| `Services/ITagTableReader.cs` | Interface für Variablentabellen-Zugriff |
| `Services/TagTableCache.cs` | Cache für gelesene Tabellen |
| `Services/AutocompleteProvider.cs` | Autovervollständigung aus Config + TagTable |
| `Config/BulkChangeConfig.cs` | Config-Modell (Rules, TagTableReference) |
| `Services/CommentGenerator.cs` | Kommentar-Generierung |
| `UI/BulkChangeViewModel.cs` | ViewModel mit Apply/Autocomplete-Logik |
| `Models/TagTableEntry.cs` | Datenmodell für Variablentabellen-Einträge |
