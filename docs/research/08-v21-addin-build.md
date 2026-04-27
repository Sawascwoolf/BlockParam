# TIA Portal V21 Add-In Build

**Researched: 2026-04-26.** Building an in-process Add-In for TIA Portal V21
(.NET Framework 4.8). Companion to `06-addin-deployment.md` (V20 build) and
`07-tia-cloud-saas.md` (V21 + Cloud overview).

## TL;DR

- **A V20-built `.addin` does not load in V21.** Confirmed locally on
  TIA Portal V21 build **2100.0.121.1**: copying our V20 package into
  `%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\` produces the error
  > Das Laden von 'BlockParam.addin' ist nicht möglich, da das Add-In in der
  > aktuellen TIA Portal-Version nicht unterstützt wird.

  Rejected at the manifest level — no `.dr` diagnostics zip, no assembly
  load. The V21 Publisher uses a new XSD namespace
  (`http://www.siemens.com/automation/Openness/AddIn/Publisher/V21`) and TIA
  gates compatibility on it before loading the DLL.

- **No V21 NuGet meta-package exists.** `Siemens.Collaboration.Net.TiaPortal.Packages.OpennessAddIn`
  tops out at `20.0.1744191010` (2025-04-11). Same for `…AddIn.Extensions` (20.0.x)
  and `…AddIn.Build` (17.0.x). Only the `Openness.*` family was rev'd to 21.x
  on 2025-12-12 — but those are the *out-of-process* Openness packages
  (`Packages.Openness 21.x`, `Openness.Extensions 21.x`, `Openness.Resolver 2.0.x`),
  not the in-process Add-In stack. Verified against the
  [Siemens NuGet profile](https://www.nuget.org/profiles/Siemens). Siemens does
  not run a public alternate feed for the missing packages.

- **Siemens's official V21 sample uses direct `<Reference HintPath>` into the
  local TIA install** — no NuGet for the AddIn DLLs. This is the canonical V21
  approach until/unless Siemens publishes a V21 meta-package. See
  `tia-portal-applications/tia-addin-opc-ua-modelled-interface` (`main` branch,
  commit `faec855` "Added TIA Portal V21 references", 2026-03-09).

- **One `.addin` cannot serve both V20 and V21.** Three independent reasons stack:
  (1) manifest namespace gating, (2) breaking Openness API changes in V21
  (Siemens "Major changes for long-term stability"), (3) in-process binding to
  whichever `Siemens.Engineering.dll` the host TIA process loaded at startup.
  The Resolver V2.x package only helps **out-of-process** Openness clients.
  Ship two artifacts: `BlockParam.V20.addin` and `BlockParam.V21.addin`.

## Assemblies V21 ships for Add-In authors

`C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\`:

| Assembly | Purpose | V20 equivalent |
|---|---|---|
| `Siemens.Engineering.AddIn.Base.dll` | `ProjectTreeAddInProvider`, `ContextMenuAddIn`, base addin types | merged into `Siemens.Engineering.AddIn.dll` |
| `Siemens.Engineering.AddIn.Permissions.dll` | Permission types referenced by manifest | `…AddIn.Permissions.dll` (same name) |
| `Siemens.Engineering.AddIn.Step7.dll` | Step7-specific addin entry points | inside V20 monolith |
| `Siemens.Engineering.AddIn.Safety.dll` | Safety addin entry points | n/a |
| `Siemens.Engineering.AddIn.Utilities.dll` | Helpers | `…AddIn.Utilities.dll` (same name) |
| `Siemens.Engineering.Base.dll` | `TiaPortal`, project tree, Compiler, base API | inside V20 `Siemens.Engineering.dll` |
| `Siemens.Engineering.Step7.dll` | `SW`, `SW.Blocks`, `SW.Tags`, `SW.Types` | inside V20 `Siemens.Engineering.dll` |
| `Siemens.Engineering.Safety.dll` | Safety editor types | n/a |
| `Siemens.Engineering.SafetyValidation.dll` | Safety validation | n/a |
| `Siemens.Engineering.WinCC.dll` / `…Extension.dll` | WinCC types | inside V20 `Siemens.Engineering.Hmi.dll` |
| `Siemens.Engineering.TeamcenterGateway.dll` | Teamcenter integration | n/a |

V21 splits the V20 monolith (`Siemens.Engineering.dll` + `Siemens.Engineering.AddIn.dll`)
into per-feature assemblies. **For BlockParam** the relevant ones are:
`AddIn.Base`, `AddIn.Permissions`, `AddIn.Step7`, `AddIn.Utilities`, `Base`, `Step7`.

## Canonical csproj reference block (V21)

In a SDK-style csproj, conditional on a `TiaVersion=21` MSBuild dimension:

```xml
<PropertyGroup Condition="'$(TiaVersion)' == '21'">
  <TiaPortalV21Root Condition="'$(TiaPortalV21Root)' == ''">C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48</TiaPortalV21Root>
</PropertyGroup>
<ItemGroup Condition="'$(TiaVersion)' == '21'">
  <Reference Include="Siemens.Engineering.AddIn.Base">
    <HintPath>$(TiaPortalV21Root)\Siemens.Engineering.AddIn.Base.dll</HintPath>
    <Private>False</Private>
  </Reference>
  <!-- …repeat for Permissions, Step7, Utilities, Base, Step7 -->
</ItemGroup>
```

- `<Private>False</Private>` keeps the Siemens DLLs out of `bin/` — TIA Portal
  loads them from its own install at runtime, not from our package.
- `$(TiaPortalV21Root)` is overridable per machine if Siemens is installed in
  a non-default location.
- Build machine **must** have TIA Portal V21 installed at the resolved path.
  This is a hard requirement Siemens's own sample also imposes; there is no
  way around it without a NuGet meta-package.

## Manifest changes (V20 → V21)

Diff `addin-publisher-v20.xml` vs `addin-publisher-v21.xml`:

| Field | V20 | V21 |
|---|---|---|
| `xmlns` | `http://www.siemens.com/automation/Openness/AddIn/Publisher/V20` | `http://www.siemens.com/automation/Openness/AddIn/Publisher/V21` |
| `<AddInVersion>` | SemVer string (e.g. `1.0.0`) | Literal token `V21` |

Everything else (Author, Description, Product, FeatureAssembly,
AdditionalAssemblies, RequiredPermissions structure) is byte-identical between
V20 and V21 in our case. The V21 XSD adds optional elements (`Certificates`,
`DisplayInMultiuser`, `AddInTimeoutConfiguration`) that we don't use.

The `<AddInVersion>V21</AddInVersion>` token is what the official Siemens V21
sample uses (`AddInPublisherConfig.xml`, `tia-portal-applications/tia-addin-opc-ua-modelled-interface`).
The XSD declares it `xs:string` so any value parses, but Siemens's convention
for V21 is the literal `V21`. Stay aligned with the convention.

## Publisher invocation

| | V20 | V21 |
|---|---|---|
| Tool | `Portal V20\PublicAPI\V20.AddIn\Siemens.Engineering.AddIn.Publisher.exe` | `Portal V21\PublicAPI\V21\Siemens.Engineering.AddIn.Publisher.exe` |
| Flags | `-f <config> -o <out> -c -v` | same |
| XSD | `…\V20.AddIn\Siemens.Engineering.AddIn.Publisher.xsd` | `…\V21\Siemens.Engineering.AddIn.Publisher.xsd` |

Same CLI surface, just a different binary in a different path. Note the V21
folder is `…\PublicAPI\V21\` (not `V21.AddIn\` like V20) — Siemens flattened
the layout.

## Deployment paths

- Per-user (no admin): `%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\`
- Machine-wide (needs admin): `C:\Program Files\Siemens\Automation\Portal V21\AddIns\`
  (folder does **not** exist on a fresh install — would need to be created)

The V21 install does not ship a system-wide `AddIns\` folder out of the box,
only `SystemAddIns\`. Per-user is the path of least resistance and matches
what Siemens's V21 sample defaults to (`%userprofile%\AppData\Roaming\Siemens\Automation\Portal V21\UserAddIns\`).

## Repo wiring (BlockParam-specific)

- **Single csproj, MSBuild dimension `TiaVersion=20|21`** (default 20 keeps the
  current `bin\Release\net48\` output path — DevLauncher and Tests resolve
  ProjectReference there without changes). V21 builds divert to `bin\…\v21\`.
- `addin-publisher-v20.xml` and `addin-publisher-v21.xml` — only diff is
  `xmlns` and `<AddInVersion>`.
- `bump-version.sh` builds + packages both targets.
- For V21-only code branches (if any Openness API breakage forces them), use
  `#if TIA_V21` — the constant is defined automatically when `TiaVersion=21`.

## Sources

- [V21 sample csproj — `tia-addin-opc-ua-modelled-interface` (Siemens, main branch, 2026-03)](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface/blob/main/src/AddInOpcUaInterface/AddInOpcUaInterface.csproj)
- [V21 sample manifest — `AddInPublisherConfig.xml`](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface/blob/main/src/AddInOpcUaInterface/AddInPublisherConfig.xml)
- [V21 docs — Creating a C# program (uses `PublicAPI\V21\net48` direct refs)](https://docs.tia.siemens.cloud/r/en-us/v21/introduction-to-the-tia-portal/extending-tia-portal-functions-with-add-ins/programming-add-ins/creating-a-c-program)
- [V21 docs — Programming Add-Ins (Openness Generator removed in V21)](https://docs.tia.siemens.cloud/r/en-us/v21/introduction-to-the-tia-portal/extending-tia-portal-functions-with-add-ins/programming-add-ins/introduction-to-programming-add-ins)
- [Siemens NuGet profile — confirms no V21 OpennessAddIn](https://www.nuget.org/profiles/Siemens)
- [`OpennessAddIn 20.0.1744191010` (newest available)](https://www.nuget.org/packages/Siemens.Collaboration.Net.TiaPortal.Packages.OpennessAddIn)
- [`Openness.Extensions 21.0.1765368043` (out-of-process, not for in-process AddIn)](https://www.nuget.org/packages/Siemens.Collaboration.Net.TiaPortal.Openness.Extensions)
- [V21 Openness — Major changes for long-term stability](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)
