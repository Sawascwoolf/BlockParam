# Terms and Conditions

_Last updated: **[YYYY-MM-DD — fill in before publishing]**_

These Terms govern your use of the **BlockParam** TIA Portal Add-In ("the Add-In")
and the related Pro license service. By installing the Add-In or activating a
Pro license, you agree to these Terms.

If you don't agree, don't install or activate the Add-In.

## 1. Parties

| Role | Entity |
|---|---|
| Provider / publisher of the Add-In | **[LEGAL ENTITY NAME]**, [REGISTERED ADDRESS], [COUNTRY]. VAT-ID: [VAT-ID]. Contact: [support@lautimweb.de](mailto:support@lautimweb.de). Hereafter "we" / "us". |
| Merchant of Record (seller of the Pro license to the buyer) | **Lemon Squeezy LLC** ("Lemon Squeezy"). See [Lemon Squeezy Buyer Terms](https://www.lemonsqueezy.com/buyer-terms). |
| You | The natural or legal person installing the Add-In or buying a Pro license. |

When you buy a Pro license through `blockparam.lemonsqueezy.com`, **Lemon Squeezy
is the seller of record**. The contract for the *purchase transaction* (payment,
tax, refunds, chargebacks, invoicing) is between you and Lemon Squeezy under their
terms. The contract for the *Add-In itself and the Pro service* is between you
and us under these Terms.

## 2. The product

BlockParam is a TIA Portal Add-In that bulk-edits Data Block start values. It
runs locally inside Siemens TIA Portal on your machine. It is distributed in two
tiers:

| Tier | What you get | Price |
|---|---|---|
| Free | Full feature set, capped at **200 value changes per calendar day** | € 0 |
| Pro | Full feature set, **unlimited** value changes | **50 € / year (net)** |

A "value change" is one individual start-value write to a DB; staging edits in
the dialog is free; comment writes do not consume the quota. See
[`docs/user/licensing.md`](../user/licensing.md) for the precise counting rules.

The source code is published under the [MIT License](../../LICENSE). The MIT
licence covers the code; **the Pro license** is a separate commercial entitlement
to use the unlimited tier and is governed by these Terms.

## 3. Pro license grant

When your Pro purchase is completed, we grant you a **non-exclusive,
non-transferable, worldwide license** to use the Pro tier of the Add-In for the
duration of the active subscription, subject to these Terms.

**Per-key limits**

- Default Pro plan: **1 concurrent session** per license key.
- Activating on a second machine while the first is still active will fail with
  a "too many sessions" error.
- Multi-seat plans are available — contact [support@lautimweb.de](mailto:support@lautimweb.de).

**You may**

- Use the Add-In on as many machines as you own, sequentially, by releasing the
  seat on the old machine and re-activating on the new one.
- Deploy the license key via SCCM, Intune, GPO or other IT tooling for managed
  multi-seat plans (see [`docs/admin-license-deployment.md`](../admin-license-deployment.md)).
- Read, fork and modify the source code under the MIT License.

**You may not**

- Share, resell, sublicense, or publish your Pro license key.
- Circumvent or attempt to circumvent the license check, the daily quota, or the
  license server (e.g. by patching the binary, spoofing the heartbeat, or
  re-routing `license.lautimweb.de`). Note: forking the MIT source and removing
  the check in your own build is permitted by the MIT licence — but the resulting
  build is no longer "BlockParam Pro" and is not covered by Pro support.
- Use the Pro tier without a valid, paid-up license key.

## 4. Subscription, billing, refunds

- The Pro tier is sold as an **annual subscription**. It auto-renews on the
  anniversary date unless cancelled.
- All payment, tax collection, invoicing, chargebacks and refunds are handled by
  Lemon Squeezy as Merchant of Record. Manage your subscription at
  [app.lemonsqueezy.com/my-orders](https://app.lemonsqueezy.com/my-orders).
- **Refunds**: subject to Lemon Squeezy's refund policy. EU consumers retain
  their statutory right of withdrawal (14 days) for digital subscriptions where
  applicable. Contact billing support via Lemon Squeezy or
  [support@lautimweb.de](mailto:support@lautimweb.de).
- **Lapsed subscription**: if a renewal payment fails or you cancel, the license
  server stops returning a valid Pro response on the next heartbeat. The Add-In
  remains Pro for the 48 h offline cache window, then drops back to Free. Your
  data, configs and rules are not affected.

## 5. License verification & connectivity

The Pro tier requires periodic contact with our license server at
`https://license.lautimweb.de`:

- One activation request when you paste the key.
- A heartbeat every **2 hours** while the dialog is open, used to release the
  seat when you stop using it.
- A **48 h offline cache**: if the server is unreachable, the Add-In stays Pro
  for 48 h before falling back to Free.

If you're behind a corporate firewall, your IT must allow HTTPS to
`license.lautimweb.de`.

The data sent in these requests is described in the
[Privacy Policy](privacy.md) — it does not include any of your project, DB or
process data.

## 6. Acceptable use

You agree to use the Add-In only with TIA Portal projects that you have the
right to modify. **You are solely responsible for the changes the Add-In writes
to your DBs.** We strongly recommend:

- Running BlockParam against a project copy first.
- Using the bulk-preview before clicking Apply.
- Keeping a backup / version-control snapshot of the project before bulk Apply.

## 7. No warranty / disclaimer

The Add-In is provided **"as is"**, without warranty of any kind, to the maximum
extent permitted by applicable law. This restates the MIT disclaimer in the
[`LICENSE`](../../LICENSE) file. We do not warrant that:

- The Add-In is free of bugs.
- It will be compatible with every TIA Portal version, project structure, UDT
  layout, or third-party Add-In.
- A bulk operation will produce the result you intended on every input.

In particular, Siemens, the TIA Portal product, the Openness API and the
Xcelerator Marketplace are **not** endorsements or warranties by Siemens of this
Add-In.

## 8. Limitation of liability

To the maximum extent permitted by applicable law, our aggregate liability under
or in connection with these Terms is **limited to the amount you paid us for
the Pro license in the 12 months preceding the event giving rise to the claim**.

We are **not liable** for indirect, incidental, special, consequential, or
punitive damages, including lost profits, lost production time, downtime, data
loss, or damage to PLC programs, even if we were advised of the possibility.

Statutory rights that cannot be waived under mandatory consumer law (EU/national)
are not affected. Liability for intent and gross negligence, for personal injury
caused by us, and under the German Product Liability Act
(*Produkthaftungsgesetz*) is unaffected.

## 9. Support

| Channel | Scope | Response target |
|---|---|---|
| [GitHub Issues](https://github.com/Sawascwoolf/BlockParam/issues) | Bugs, feature requests (Free + Pro) | Best effort |
| [support@lautimweb.de](mailto:support@lautimweb.de) | Billing, license, Pro support | Best effort, business days, [TIMEZONE] |

We do **not** offer a contractual SLA for Free or Pro at the standard price.
Custom SLAs are available on request for multi-seat customers.

## 10. Termination

- You can terminate at any time by cancelling the subscription in the
  Lemon Squeezy customer portal. Pro access remains until the end of the paid
  period.
- We may terminate your Pro license, with or without notice, if you breach
  these Terms (e.g. key sharing, circumventing the license check, illegal use).
- On termination of Pro, the Add-In falls back to Free. Your local data,
  configs, and rules are kept on your machine.

## 11. Changes to these Terms

We may update these Terms to reflect changes in the product, pricing, or law.
Material changes will be announced on the BlockParam landing page and in the
release notes. Continued use of Pro after the effective date of a change
constitutes acceptance.

The Git history of this file in the
[BlockParam repository](https://github.com/Sawascwoolf/BlockParam) is the
authoritative changelog.

## 12. Governing law and jurisdiction

These Terms are governed by the laws of **[GOVERNING LAW — e.g. Federal Republic
of Germany]**, excluding the UN Convention on Contracts for the International
Sale of Goods (CISG).

Place of jurisdiction for disputes with merchants, legal persons under public
law, or special funds under public law is **[COURT — e.g. the courts competent
for our registered seat]**.

EU consumers may also bring proceedings under the consumer-protection laws of
their country of residence and may use the EU
[Online Dispute Resolution platform](https://ec.europa.eu/consumers/odr). We are
not obliged to participate in dispute-resolution proceedings before a consumer
arbitration board.

## 13. Severability

If any provision of these Terms is held invalid or unenforceable, the remaining
provisions remain in full force.

## 14. Contact

| | |
|---|---|
| Provider | **[LEGAL ENTITY]** |
| Address | [REGISTERED ADDRESS] |
| Email | [support@lautimweb.de](mailto:support@lautimweb.de) |
| Web | [https://blockparam.lautimweb.de](https://blockparam.lautimweb.de) |
| VAT-ID | [VAT-ID] |
| Commercial register | [HRB ... — if applicable] |
