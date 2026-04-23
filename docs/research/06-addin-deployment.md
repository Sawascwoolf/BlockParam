# TIA Portal V20 Add-In Deployment

## Zielframework: net48 (NICHT net6.0!)

**Validiert am 10.04.2026 durch Analyse der Siemens-Assemblies, NuGet-Pakete und offizieller Beispiele.**

### Beweis-Kette

| Quelle | Ergebnis |
|---|---|
| `Siemens.Engineering.AddIn.dll` (V20) | PE32, CLR v4.0.30319 (.NET Framework 4.x) |
| `Siemens.Engineering.AddIn.Publisher.exe` | PE32, CLR v4.0.30319 |
| `Siemens.Engineering.AddIn.DebugStarter.exe` | PE32+, CLR v4.0.30319 |
| `Siemens.Engineering.dll` (V20) | PE32, CLR v4.0.30319 |
| NuGet `AddIn.Extensions` 20.0 | Ziel: net48 only |
| NuGet `Openness.Extensions` alle Versionen | Ziel: net48 only |
| Offizielles Siemens OPC UA Add-In Beispiel | `<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>` |
| Siemens Openness Code Snippets Repo | `<TargetFramework>net48</TargetFramework>` |
| V20 PublicAPI Ordnerstruktur | Keine net48/net6.0 Unterordner (flach = nur net48) |

### Warum net6.0 nicht funktioniert

TIA Portal V20 (`TIA Portal.exe`) ist eine .NET Framework 4.8 Anwendung. Add-Ins werden
in-process über `Siemens.Engineering.AddIn.dll` geladen, die auf dem .NET Framework CLR läuft.
Eine net6.0 Assembly benötigt CoreCLR und kann nicht im .NET Framework Prozess geladen werden.

**Hinweis:** V21 scheint eine Aufspaltung in `net48`/`net6.0` Unterordner zu haben
(basierend auf HintPath-Referenzen im OPC UA Beispiel). Für V20 gilt das nicht.

### Hinweis zu `Siemens.Collaboration.Net.TiaPortal.Packages.OpennessAddIn`

Dieses NuGet-Paket hat **keine `lib/`-Ordner** - es ist ein reines MSBuild-Targets-Paket
das zur Build-Zeit die Siemens-Assemblies aus der TIA Portal Installation referenziert.
Es "unterstützt" net6.0 im Sinne dass es sich restoren lässt, aber die referenzierten
Assemblies (.NET Framework) können nicht in einer net6.0 Anwendung geladen werden.

## Korrekte Projektkonfiguration

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Siemens.Collaboration.Net.TiaPortal.Packages.OpennessAddIn" Version="20.*" />
  </ItemGroup>
</Project>
```

- `LangVersion=latest` erlaubt moderne C#-Syntax (C# 12/13) auch auf net48
- `ImplicitUsings` und `Nullable` funktionieren auf net48 in SDK-style Projekten
- `UseWPF` funktioniert auf net48

## Provider-Klasse (V20 korrekt)

Validiert gegen offizielles Siemens Beispiel (`tia-portal-applications/tia-addin-opc-ua-modelled-interface`):

```csharp
public sealed class AddInProvider : ProjectTreeAddInProvider
{
    private readonly TiaPortal _tiaPortal;

    public AddInProvider(TiaPortal tiaPortal)
    {
        _tiaPortal = tiaPortal;
    }

    protected override IEnumerable<ContextMenuAddIn> GetContextMenuAddIns()
    {
        yield return new MyContextMenu(_tiaPortal);
    }
}
```

- **Kein `[ExportAddInProvider]` Attribut** in V20/V21 (war evtl. ältere API)
- `GetContextMenuAddIns()` statt `GetSelection()`
- Konstruktor erhält `TiaPortal` Parameter

## Publisher.exe

**Pfad:** `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.Publisher.exe`

### Konfiguration (XSD-Namespace V20)

```xml
<PackageConfiguration xmlns="http://www.siemens.com/automation/Openness/AddIn/Publisher/V20">
  <Author>Sawascwoolf</Author>
  <Description>Bulk edit Data Block start values</Description>
  <AddInVersion>1.0.0</AddInVersion>
  <Product>
    <Name>BlockParam</Name>
    <Id>BlockParam</Id>
    <Version>1.0.0</Version>
  </Product>
  <FeatureAssembly>
    <AssemblyInfo>
      <Assembly>BlockParam.dll</Assembly>
    </AssemblyInfo>
  </FeatureAssembly>
  <RequiredPermissions>
    <TIAPermissions>
      <TIA.ReadWrite/>
    </TIAPermissions>
    <SecurityPermissions>
      <System.Security.Permissions.FileIOPermission/>
      <System.Security.Permissions.UIPermission/>
    </SecurityPermissions>
  </RequiredPermissions>
</PackageConfiguration>
```

### Aufruf

```bash
# Config neben DLL kopieren, dann Publisher aufrufen:
copy AddInPublisherConfig.xml bin\Release\net48\
"C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.Publisher.exe" \
  -f "bin\Release\net48\AddInPublisherConfig.xml" \
  -o "%APPDATA%\Siemens\Automation\Portal V20\UserAddIns\BlockParam.addin" \
  -c -v
```

**Wichtig:** Assembly-Pfad in Config ist relativ zur Config-Datei. Daher Config ins Output-Verzeichnis kopieren.

## Deployment-Pfad

```
%APPDATA%\Siemens\Automation\Portal V20\UserAddIns\
```

TIA Portal scannt diesen Ordner beim Start und lädt alle `.addin`-Dateien.

## DebugStarter.exe

**Pfad:** `C:\Program Files\Siemens\Automation\Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.DebugStarter.exe`

Argument: Pfad zur kompilierten DLL. Startet TIA Portal mit geladenem Add-In.

```bash
"...\Siemens.Engineering.AddIn.DebugStarter.exe" "pfad\zur\BlockParam.dll"
```

**Voraussetzung:** DLL muss net48 sein, da DebugStarter selbst .NET Framework ist.

## Zusammenfassung der notwendigen Änderungen

1. `BlockParam.csproj`: `net6.0-windows` → `net48`
2. `BlockParam.Tests.csproj`: entsprechend anpassen
3. `BlockParam.DevLauncher.csproj`: entsprechend anpassen
4. Code-Anpassungen für net48-Kompatibilität (falls nötig)
5. Publisher im PostBuild aufrufen
6. `.addin` nach `%APPDATA%\...\UserAddIns\` deployen
