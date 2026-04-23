# TIA Portal Add-In Framework

## Overview

TIA Portal Add-Ins are the modern way to extend TIA Portal (supported from V15.1+).
They integrate into TIA Portal's context menus. Custom UI is limited to dialog windows.

## UI Constraints (Verified)

- **No docked tabs/editor views**: There is no API to register a custom view as a
  tab alongside native TIA editors. No `EditorViewAddInProvider` exists.
- **Context menu only**: Add-Ins can only inject items into context menus via the
  AddInProvider types listed below.
- **Dialog windows only**: Custom UI must be a WPF/WinForms dialog launched via
  `ShowDialog()` or `ShowDialogInForeground()` (from AddIn.Extensions package).
- **No DB editor integration**: The context menu appears in the project tree, not
  inside the DB editor table. The Add-In receives a `DataBlock` object, not a
  specific member or cell.
- Available UI helpers: `AddInMessageBox.Show()`, `ShowDialogInForeground()`

## Project Structure

A TIA Add-In requires at minimum two classes:

- **View Provider** - defines UI areas where context menus appear
  - Available: `ProjectTreeAddInProvider`, `GlobalLibraryTreeAddInProvider`,
    `ProjectLibraryTreeAddInProvider`, `DevicesAndNetworksAddInProvider`
- **Context Menu Handler** - inherits from `ContextMenuAddIn`, defines menu entries and logic

## .NET Framework Requirements

| TIA Portal Version | .NET Target Framework |
|---|---|
| V16 | .NET Framework 4.6.2 |
| V17 | .NET Framework 4.8 |
| V18 | .NET Framework 4.8 |
| V19 | .NET Framework 4.8 |
| V20+ | .NET Framework 4.8 and/or .NET 6.0 |

## Key NuGet Packages

| Package | Purpose |
|---|---|
| `Siemens.Collaboration.Net.TiaPortal.AddIn.Build` | Build tooling: auto-detects TIA install, references assemblies, generates templates, runs Publisher |
| `Siemens.Collaboration.Net.TiaPortal.AddIn.Extensions` | Extension methods for messages, dialogs, context menus, and Openness operations |
| `Siemens.Collaboration.Net.TiaPortal.Packages.OpennessAddIn` | Transitively references all `Siemens.Engineering.AddIn` assemblies |
| `Siemens.Collaboration.Net.TiaPortal.Openness.Extensions` | Extension methods: `AllDevices()`, `AllPlcSoftwares()`, `AllBlocks()`, `Parent<T>()`, attribute helpers |
| `Siemens.Collaboration.Net.TiaPortal.Openness.Resolver` | Assembly resolution for `Siemens.Engineering.dll` |

Core assembly: **`Siemens.Engineering.AddIn.dll`** (auto-referenced from TIA Portal installation).

## Packaging & Deployment

### .addin File Creation
The compiled DLL is converted to an `.addin` file using `Siemens.Engineering.AddIn.Publisher.exe`,
located in the TIA Portal installation directory under `PublicAPI/`.
The build package automates this as a post-build event.

### Manifest: AddInPublisherConfiguration.xml
```xml
<!-- Specifies: -->
<!-- - FeatureAssembly: path to the compiled DLL -->
<!-- - AdditionalAssemblies: auto-detected dependencies -->
<!-- - Certificates: auto-detected signing certificates -->
<!-- - Description, version, author name -->
<!-- - Special rights/permissions for the Add-In -->
```

### Deployment Path
```
C:\Users\<username>\AppData\Roaming\Siemens\Automation\Portal V<XX>\UserAddIns
```
If the build runs with elevated privileges, the `.addin` file is deployed there automatically.

## Build Tasks Workflow (MSBuild)
1. **EnsureEnvironment** - validates/corrects .NET framework targeting
2. **Template** - generates view provider and menu implementation stubs if none exist
3. **Publisher** - executes Publisher.exe to create `.addin` file
4. **DeployLaunchSettings** - configures TIA Add-In Tester for debugging
5. **WriteChanges** - persists modifications to project files

## Add-In Lifecycle

```csharp
// View Provider - determines where the context menu appears
[ExportAddInProvider(typeof(MyAddInProvider))]
public class MyAddInProvider : ProjectTreeAddInProvider
{
    protected override MenuSelectionProvider GetSelection()
    {
        return new MenuSelectionProvider(new MyContextMenu());
    }
}

// Context Menu Implementation
public class MyContextMenu : ContextMenuAddIn
{
    public MyContextMenu() : base("My Tool Name") { }

    protected override void BuildContextMenuItems(
        ContextMenuAddInRoot addInRootSubmenu)
    {
        addInRootSubmenu.Items.AddActionItem<IEngineeringObject>(
            "Menu Item Text",
            OnClick,
            OnUpdateStatus,
            OnError);
    }

    private void OnClick(MenuSelectionProvider<IEngineeringObject> provider)
    {
        // Access selected objects and perform operations
    }

    private MenuStatus OnUpdateStatus(
        MenuSelectionProvider<IEngineeringObject> provider)
    {
        return MenuStatus.Enabled;
    }

    private void OnError(Exception ex)
    {
        // Error handling
    }
}
```

## Key References
- [TIA Add-In Build Package (GitHub)](https://github.com/tia-portal-applications/tia-addin-build-package)
- [TIA Add-Ins Getting Started (Siemens)](https://support.industry.siemens.com/cs/attachments/109779415/109779415_TIA_Add-In_Getting_Started_DOC_V1_3_EN.pdf)
- [TIA Portal OPC UA Add-In (GitHub)](https://github.com/tia-portal-applications/tia-addin-opc-ua-modelled-interface) - real-world example
- [TIA Portal Applications (GitHub Org)](https://github.com/tia-portal-applications)
