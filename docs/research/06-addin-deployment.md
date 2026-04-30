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
      <System.Security.Permissions.FileDialogPermission/>
      <System.Security.Permissions.EnvironmentPermission/>
      <System.Net.WebPermission/>
      <Siemens.Engineering.AddIn.Permissions.ProcessStartPermission/>
      <System.Security.Permissions.SecurityPermission.UnmanagedCode/>
    </SecurityPermissions>
  </RequiredPermissions>
</PackageConfiguration>
```

### Permission set rationale (BlockParam)

The shipping Add-In was audited against `Siemens.Engineering.AddIn.Publisher.xsd` (the
canonical schema lists every permission the publisher accepts). `System.UnrestrictedAccess`
is **not** required — the explicit set above covers every runtime call:

| Permission | Why BlockParam needs it |
|---|---|
| `FileIOPermission` | SimaticML XML export/import in `%TEMP%\BlockParam\`; config, profiles, license cache, usage tracker, UI settings in `%APPDATA%\BlockParam\`; reading per-project rules directories. |
| `UIPermission` | WPF bulk-change dialog, license key dialog, config editor, autocomplete dropdown, inline hint popup, MessageBox prompts. |
| `FileDialogPermission` | `FolderBrowserDialog` in the config editor for choosing the shared rules directory. |
| `EnvironmentPermission` | `Environment.GetFolderPath(SpecialFolder.ApplicationData)` for per-user storage paths and `Environment.MachineName` used by the machine-bound license obfuscation. |
| `WebPermission` | `HttpClient` calls to the BlockParam license server (`OnlineLicenseService`) for activation/validation. |
| `ProcessStartPermission` (Siemens) | `Process.Start(url, UseShellExecute=true)` to open the default browser for shop checkout and customer-portal links from the license dialog. Narrower than `UnmanagedCode` for this specific call. |
| `SecurityPermission.UnmanagedCode` | Required transitively by WPF rendering, `HttpClient` socket I/O, and Newtonsoft.Json's reflection-emit code paths. WPF and HttpClient demand this even after declaring the higher-level WebPermission/UIPermission. |

Permissions explicitly **not** needed (verified by grep of `src/BlockParam`):
ODBC/OleDb/SqlClient, EventLog, Printing, Smtp, NetworkInformation, Socket,
IsolatedStorageFile, KeyContainer, Registry, Store, WebBrowser, Media.

### Partial-trust verification — what actually breaks the addin

Declaring `<SecurityPermissions>` instead of `<UnrestrictedPermissions>` switches
the addin from full trust to partial trust. **Before** the runtime ever consults
the permission set, the JIT runs IL verification on every loaded assembly. Any
unverifiable IL throws `System.Security.VerificationException` ("Dieser Vorgang
kann die Laufzeit destabilisieren.") and TIA silently fails to activate the
addin. The error lands as a `.dr` zip in
`C:\ProgramData\Siemens\Automation\Portal V20\Diagnostics\<product-id>\<guid>\`
— unzip and read `ErrorReport.xml` to see the failing assembly.

This means the question "will the narrow permission set work?" has *two* parts:

1. **Permission grant** — covered by the table above.
2. **IL verification** — every transitive dependency must be partial-trust safe.

Verified by an isolated spike addin (PermSpike, lives at `/spike/PermSpike` and
is gitignored — see commit message for "Permission narrowing spike"):

| Dependency | Partial-trust verifies? | Notes |
|---|---|---|
| WPF (window, button, TextBox, ScrollViewer, MessageBox) | ✅ Pass | Spike opened a WPF dialog without issue. |
| Newtonsoft.Json 13.x — **public top-level DTOs** | ✅ Pass | Round-trip of a public top-level class with primitive/array/nested-public-class properties succeeded. |
| Newtonsoft.Json 13.x — **private nested DTOs** | ❌ Fail | `MethodAccessException` from CAS reflection check (`RestrictedMemberAccess` would be needed). BlockParam does not have any non-public serialized DTOs, so this is not a concern in practice — but watch for it if anyone introduces one. |
| Serilog 3.x + Serilog.Sinks.File 5.x | ❌ Fail | `VerificationException` from `Serilog.Parsing.PropertyToken.get_IsPositional()` during `LoggerConfiguration.WriteTo.File(...)`. Cannot be remediated by adding more permissions — Serilog's IL itself does not verify under partial trust. **The narrow permission path requires removing Serilog.** |

#### Things that don't help (verified the hard way)

- Adding `SecurityPermission.UnmanagedCode` does not fix Serilog. `UnmanagedCode`
  controls native interop, not IL verification — those are different stages.
- The XSD does not list `ReflectionPermission`, so we cannot grant
  `RestrictedMemberAccess` to make Newtonsoft.Json reach private members. Keep
  serialized types public and top-level.

#### Why Siemens's bundled examples use UnrestrictedAccess

`C:\Program Files\Siemens\Automation\Portal V20\AddIns\ShowScripts.addin` (the
sample addin shipped with TIA V20) declares `System.UnrestrictedAccess`. That is
not a recommendation — it is a workaround for exactly this verification problem.
Any addin that pulls in a non-trivial managed dependency stack will hit it
unless every transitive dependency is partial-trust safe.

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

TIA Portal scannt diesen Ordner sowohl beim Start als auch periodisch während einer
laufenden Session. Neu hineinkopierte `.addin`-Dateien erscheinen ohne Neustart in
der **Add-ins**-Task Card (rechter Fensterrand). Der Benutzer muss das Add-In dort
manuell **aktivieren** &mdash; danach erscheint ein Berechtigungs-Prompt. Nach
Bestätigung bleibt das Add-In über Sessions hinweg aktiviert. Bei jedem Update
(neue `.addin`-Datei im Ordner) erscheint die Berechtigungsabfrage erneut.

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
