# TIA Portal Cloud (SaaS) — Add-In Implications

**Researched: 2026-04-26.** Covers the Siemens TIA Portal SaaS offering and what
it means for distributing BlockParam.

## TL;DR

- **TIA Portal Cloud is not a re-implemented web TIA Portal.** It is the
  **standard desktop TIA Portal binary** running inside a Siemens-managed
  Windows VM, accessed via **Remote Desktop Connection**. The Add-In runtime
  is therefore the same as on a local install in principle.
- **Latest stack (as of 2026-03-25): TIA Portal Cloud V6.1 with TIA Portal V21.**
- **The blocking unknown** for BlockParam is whether the cloud VM lets the
  end user (or admin) drop a `.addin` file into the `AddIns/` folder. Public
  Siemens docs do not (yet) document an Add-In install path for the cloud VM.
  We must verify with a trial subscription or via the Xcelerator partner team
  before claiming "TIA Portal Cloud compatible".
- **V21 has breaking Openness API changes.** Our V20 build will **not** load in
  V21. We need a parallel V21 artifact built against
  `Siemens.Collaboration.Net.TiaPortal.Openness.Extensions 21.x`.
- **Industrial Operations X** is the umbrella SaaS/Xcelerator portfolio that
  includes TIA Portal Cloud, the Marketplace, the Cloud Connector and the new
  AI engineering agents (Eigen). It is the channel — not a separate runtime
  Add-Ins target.

## What TIA Portal Cloud actually is

| Aspect | Reality |
|---|---|
| Runtime | Standard TIA Portal desktop binary in a hosted Windows VM |
| Access | Remote Desktop Connection (RDP) from the user's PG/PC |
| Hardware access | **TIA Portal Cloud Connector** (locally installed tunnel) bridges the cloud VM to local PG/PC interfaces and physical SIMATIC controllers. Per-PG/PC license required. |
| Storage | Private cloud project storage, sharable with colleagues |
| Versions | V6.1 supports V21; older Cloud versions: V4.2 → V19, etc. (one Cloud VM per major TIA version) |
| Pricing | Annual / monthly / pay-per-use subscription; no upfront license |
| Distribution channel | Siemens Xcelerator Marketplace |

Implication: any feature that works because it is a normal Windows .NET
Framework process (file I/O, registry, COM, our XML round-trip approach) keeps
working in the cloud VM — assuming we are allowed to install the Add-In there.

## Add-In install on the cloud VM — the open question

Standard local install path:

```
C:\Program Files\Siemens\Automation\Portal V21\AddIns\<name>.addin
```

…requires admin rights (the Publisher writes to Program Files), then the user
activates it from the Add-Ins task card and approves the requested permissions.

**On TIA Portal Cloud we don't know yet whether:**

1. The user gets local admin in the VM and can drop `.addin` files at will,
2. There is a **per-user `UserAddIns`** path that survives across sessions
   (this is documented for V20 but not confirmed for the cloud image),
3. Add-Ins must go through a **Marketplace install** flow that the
   tenant admin approves, or
4. Custom (non-Marketplace) Add-Ins are simply **disallowed** for the
   subscription tier.

None of (1)–(4) is publicly documented in the cloud-specific docs. The
generic V21 Add-In docs describe install/activation as if it were a normal
desktop install, with no cloud caveats.

**Action for BlockParam**: open a support / partner ticket, or take a
TIA Portal Cloud trial subscription, and verify the install path before any
"works in the cloud" marketing claim.

## V21 vs V20: what changed for Add-Ins

From the V21 Openness docs and NuGet metadata:

- **Openness API has breaking changes in V21.** Applications built against
  TIA Portal **V20 or below are not compatible** with V21+. Officially called
  out as "Major changes for long-term stability in TIA Portal Openness V21".
- **NuGet packages are version-locked**:
  - V20: `Siemens.Collaboration.Net.TiaPortal.Openness.Extensions 20.x`
  - V21: `Siemens.Collaboration.Net.TiaPortal.Openness.Extensions 21.x`
  - The `Resolver` is now **V2.x** if you need to support pre-V21 versions in
    the same binary; otherwise you build one artifact per TIA major.
- **Target framework**: still `net48` for both V20 and V21 in-process Add-Ins
  (research file `06-addin-deployment.md` notes a `net48/net6.0` split appearing
  in V21 sample HintPaths — needs verification with the V21 SDK installed, but
  the in-process load model itself has not moved off .NET Framework 4.8 for the
  Add-In host).

**Action for BlockParam**:

- Add a **V21 build configuration** (`BlockParam.V21.csproj` or a
  `<TargetTia>` MSBuild property switching the `Openness.Extensions` package
  version and the Siemens HintPaths).
- Ship **two `.addin` artifacts**: `BlockParam.V20.addin` and
  `BlockParam.V21.addin`. The Marketplace listing should expose both.
- Update `bump-version.sh` to package and deploy both targets in one go (or at
  minimum take a `--tia=20|21` flag).

## Industrial Operations X — the channel context

"Industrial Operations X" is Siemens' umbrella for the IT/OT-converged
automation portfolio inside Siemens Xcelerator. It is **not** a new Add-In
runtime. From a BlockParam standpoint it matters because:

- It is the **brand under which Marketplace listings are surfaced** to cloud
  customers — the same Xcelerator Marketplace path we already plan for.
- Siemens' own AI engineering agents ("Eigen") sit alongside Add-Ins in this
  portfolio. We may want to position BlockParam as complementary
  ("deterministic bulk parameterization") to avoid being seen as overlapping
  with AI-driven code generation.
- Cloud customers are billed on subscription, so a **freemium per-machine**
  counter is fragile in the cloud (the VM identity may not be stable per
  user). Plan an **online license check** before targeting the cloud channel
  seriously — `Licensing/` is already isolated for this.

## Distribution checklist for cloud-readiness

1. Build & sign a **V21 artifact** alongside the V20 one. Validate it on a
   real V21 install.
2. Confirm with Siemens whether `.addin` files can be installed in a TIA
   Portal Cloud VM, and via what mechanism (admin upload, Marketplace flow,
   per-user folder).
3. If install works: capture screenshots of BlockParam running inside the
   cloud VM via RDP for the Marketplace listing.
4. Replace machine-fingerprint freemium with an **online license check**
   before claiming cloud support — the per-machine counter will reset on
   every new VM session.
5. On the Marketplace listing, declare the supported TIA versions explicitly
   (`V20`, `V21`) and call out cloud compatibility separately once verified.

## Open questions to confirm

- [x] Is there a documented `UserAddIns` path under `%APPDATA%` that the cloud
      VM exposes to the end user? **Local V21 install confirmed (2026-04-26):
      `%APPDATA%\Siemens\Automation\Portal V21\UserAddIns\` exists out of the
      box and is the path Siemens's own V21 sample deploys to. Cloud-VM
      exposure of this path is still untested.** See `08-v21-addin-build.md`.
- [ ] Does Siemens require Marketplace certification before an Add-In can be
      installed in TIA Portal Cloud, or is it a free-form copy-to-folder?
- [x] Does the V21 SDK actually ship a `net6.0` flavor for in-process Add-Ins,
      or is the `net6.0` HintPath only for out-of-process Openness clients?
      **Confirmed `net48` only for in-process. The V21 install ships
      `Portal V21\PublicAPI\V21\net48\Siemens.Engineering.AddIn.*.dll`; no
      sibling `net6.0\` folder exists. Siemens's V21 sample targets `v4.8`.**
- [ ] Are Add-Ins disabled by tenant policy in some cloud SKUs?
- [x] Will our V20 `.addin` load on V21? **No, confirmed 2026-04-26: V21
      rejects the V20 manifest at load time ("Add-In wird in der aktuellen
      TIA Portal-Version nicht unterstützt"). Two artifacts required —
      details and rationale in `08-v21-addin-build.md`.**

## Sources

- [TIA Portal Cloud — Siemens Global](https://www.siemens.com/global/en/products/automation/industry-software/automation-software/tia-portal/highlights/tia-portal-cloud.html)
- [TIA Portal Cloud V6.1 with TIA Portal V21 now available (Siemens, 2026-03-25)](https://www.industry-mobile-support.siemens-info.com/en/article/detail/109794456)
- [TIA Portal Cloud — Xcelerator Marketplace](https://xcelerator.siemens.com/global/en/all-offerings/products/t/tia-portal-cloud.html)
- [TIA Portal Cloud Connector — Siemens GB](https://www.siemens.com/en-gb/products/tia-portal/cloud-connector/)
- [Using TIA Portal in a Virtualized Infrastructure (Siemens whitepaper)](https://cache.industry.siemens.com/dl/files/064/109486064/att_881021/v2/109486064_TIA_Portal_virtualized_en.pdf)
- [V21 Openness — Major changes for long-term stability](https://docs.tia.siemens.cloud/r/en-us/v21/readme-tia-portal-openness/major-changes-for-long-term-stability-in-tia-portal-openness-v21)
- [V21 Add-Ins — Basics of Add-Ins](https://docs.tia.siemens.cloud/r/en-us/v21/introduction-to-the-tia-portal/extending-tia-portal-functions-with-add-ins/basics-of-add-ins)
- [NuGet — Siemens.Collaboration.Net.TiaPortal.Openness.Extensions 21.x](https://www.nuget.org/packages/Siemens.Collaboration.Net.TiaPortal.Openness.Extensions)
- [TIA Portal Openness Compatibility Matrix V16–V21 (T-IA Connect)](https://t-ia-connect.com/en/compatibility-tia-portal-openness)
- [TIA Portal V21 Technical slides EN (Domenico Madeo, Nov 2025)](https://domenicomadeo.com/wp-content/uploads/2025/11/TIA-Portal-V21-technical-slides-EN.pdf)
- [Industrial Operations X — Siemens](https://www.siemens.com/en-us/company/insights/industrial-operations-x/)
- [Siemens unveils technologies at CES 2026 (Eigen agent, AI portfolio)](https://press.siemens.com/global/en/pressrelease/siemens-unveils-technologies-accelerate-industrial-ai-revolution-ces-2026)
