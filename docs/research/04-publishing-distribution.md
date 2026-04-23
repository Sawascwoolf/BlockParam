# Publishing & Distribution of TIA Portal Add-Ins

## Deployment Mechanisms

### Local Deployment
Place the `.addin` file in the user's UserAddIns directory:
```
%AppData%\Siemens\Automation\Portal V<XX>\UserAddIns
```

### Build-Time Deployment
If the build runs with elevated privileges, the build package deploys the
`.addin` file automatically to the UserAddIns directory.

## Signing & Certificates

- `AddInPublisherConfiguration.xml` includes a section for certificates
- The build package auto-detects certificates from the project structure
- TIA Portal displays certificate information for Add-Ins
- Add-Ins can be signed or unsigned
- Signed Add-Ins show verified publisher information in TIA Portal
- For distribution: a code-signing certificate (PFX) placed in the project
  structure is automatically picked up by the Publisher tool

## Distribution Channels

### 1. Direct Distribution
- Share the `.addin` file directly
- Users place it in their UserAddIns directory
- Simplest approach for internal/team use

### 2. Siemens Xcelerator Marketplace
- URL: `xcelerator.siemens.com`
- The marketplace for Siemens digital solutions
- Requires partnership through Siemens Connect program
- Sellers must expose functional blocks through APIs
- Must adhere to Siemens API guidelines

### 3. Siemens Industry Online Support (SIOS)
- URL: `support.industry.siemens.com`
- Can host Add-Ins as downloadable contributions

### 4. GitHub / Open Source
- Distribute source code and build instructions
- Users build the `.addin` file themselves
- Suitable for community/open-source projects

## Supported TIA Portal Versions

Add-Ins are supported from **V15.1** onward.

| Version | NuGet Package Version | Status |
|---|---|---|
| V15.1 | 15.x | Legacy |
| V16 | 16.x | Legacy |
| V17 | 17.x | Supported |
| V18 | 18.x | Supported |
| V19 | 19.x | Supported |
| V20 | 20.x | Current |
| V21 | 21.x | Latest |

The build package version number maps to the TIA Portal version
(e.g., `20.*` for V20).

## Freemium Model Considerations

For implementing a freemium model in a TIA Portal Add-In:

### Usage Tracking Options
- **Local file-based counter**: Store usage count in a local encrypted file
  - Path: `%AppData%\<AppName>\usage.dat`
  - Reset daily based on system date
  - Pros: No internet required, simple
  - Cons: Can be circumvented by deleting file or changing system date
- **Online license server**: Validate usage against a cloud service
  - Pros: Tamper-resistant, supports subscription management
  - Cons: Requires internet, more complex infrastructure
- **Hardware-bound license**: Tie license to machine fingerprint
  - Pros: Prevents sharing
  - Cons: Complex, problematic for VM environments

### Siemens Licensing
- Siemens provides LKSP (License Key System for Partners) for ISVs
- Integration requires Siemens partnership agreement
- Alternative: Use own licensing system independent of Siemens

## Key References
- [Siemens Xcelerator Marketplace](https://xcelerator.siemens.com/global/en.html)
- [Siemens Developer Portal](https://developer.siemens.com/)
- [TIA Add-In Build Package (GitHub)](https://github.com/tia-portal-applications/tia-addin-build-package)
