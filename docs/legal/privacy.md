# Privacy Policy

_Last updated: **2026-05-06**_

This Privacy Policy explains what personal data the **BlockParam** TIA Portal
Add-In and the related Pro license service collect, why, and what your rights
are. We aim to collect as little as possible.

## 1. Controller

The controller for the data processing described here is:

> **Tobias Laubscher** (Einzelunternehmer, trading as *lautimweb*)
> Enkenbacher Str. 55
> 67691 Hochspeyer
> Germany
> Phone: +49 176 21156188
> Email: [support@lautimweb.de](mailto:support@lautimweb.de)
> VAT-ID: DE313892898

A statutory Data Protection Officer (*Datenschutzbeauftragter*) is **not
appointed** — the controller is an Einzelunternehmer below the §38 BDSG
threshold of 20 persons regularly engaged in automated processing of personal
data. For data-protection requests, write to the address above with the subject
"Privacy".

## 2. TL;DR — what BlockParam sends and stores

| What | Where it goes | Why |
|---|---|---|
| **License key, instance ID, machine name, Add-In version** | Our license server `https://license.lautimweb.de` (every activation + every 2 h heartbeat) | Verify your Pro entitlement and enforce the per-key concurrent-session limit |
| **Your name, email, billing address, payment data, country, VAT-ID, IP** | Lemon Squeezy (only during checkout) | Take payment, calculate tax, issue invoices, comply with tax law |
| `config.json`, rule files, log file | **Local only** — `%APPDATA%\BlockParam\` on your machine | Save your settings and rules between sessions |
| DB exports, tag tables (DevLauncher only) | **Local only** — `%TEMP%\BlockParam\` | Cache TIA exports for the development launcher |

What we **do not** collect:

- DB content, tag values, or any project data.
- TIA Portal project paths or file names.
- Telemetry, usage statistics, error reports (unless you actively email a log
  to support).
- Cookies in the Add-In (it's a desktop app — no browser, no cookies).

## 3. Detail: license server (`license.lautimweb.de`)

When you activate Pro and while the Add-In's dialog is open, the Add-In sends
the following to our license server over HTTPS:

| Field | Example | Purpose |
|---|---|---|
| `licenseKey` | `PRO-XXXX-XXXX-XXXX` | Identify which subscription |
| `instanceId` | random GUID generated on first activation | Distinguish concurrent seats |
| `machineName` | `WS-PLC-12` | Show in support tickets when you ask "which machine is holding my seat?" |
| `addinVersion` | `0.7.2` | Diagnostics, deprecation handling |
| `osLanguage` (optional) | `de-DE` | Localised error messages |

The server also logs the **request timestamp and source IP** at the
infrastructure level (standard web-server logs), retained for **30 days** for
abuse-prevention and debugging.

**Legal basis (GDPR):**

- Activation and heartbeat: **Art. 6(1)(b) GDPR** — performance of the contract
  to provide the Pro service.
- IP / access logs: **Art. 6(1)(f) GDPR** — legitimate interest in operating
  and securing the service.

**Retention:**

- Active license records are kept while the subscription is active.
- License records and invoice records are then retained for the
  statutory tax-law period (**up to 10 years** under German *Abgabenordnung*
  / equivalent local rules).
- Web-server access logs: **30 days**, then deleted.

**Recipients / sub-processors for the license server:**

| Provider | Role | Location | Safeguard |
|---|---|---|---|
| ALL-INKL.COM — Neue Medien Münnich, Inh. René Münnich | Server hosting | Germany | Data-Processing Agreement (Art. 28 GDPR), EU-based |

## 4. Detail: payment via Lemon Squeezy

When you buy Pro at `blockparam.lemonsqueezy.com`, **Lemon Squeezy LLC is the
Merchant of Record and the controller for the payment transaction**, not us. We
receive only:

- The customer email and name (so we can send the license key and respond to
  support requests).
- The order ID and product / variant.
- The payment status and renewal events (via webhook).

We do **not** receive your full credit-card number, CVV, bank account, or full
billing address. Those are processed by Lemon Squeezy and their payment
processors.

| | |
|---|---|
| Merchant of Record | Lemon Squeezy LLC |
| Their privacy policy | [lemonsqueezy.com/privacy](https://www.lemonsqueezy.com/privacy) |
| Buyer terms | [lemonsqueezy.com/buyer-terms](https://www.lemonsqueezy.com/buyer-terms) |

Legal basis for our processing of the data we receive from Lemon Squeezy:
**Art. 6(1)(b) GDPR** (contract performance) and **Art. 6(1)(c) GDPR**
(compliance with tax-record retention obligations).

## 5. Detail: data on your local machine

The Add-In stores the following on your computer. None of it is sent to us.

| Path | Content | When it's written |
|---|---|---|
| `%APPDATA%\BlockParam\config.json` | Add-In settings, license-server URL override, telemetry toggle (none), language | Whenever you change a setting |
| `%APPDATA%\BlockParam\rules\*.json` | Your rule definitions | When you save a rule |
| `%APPDATA%\BlockParam\license.cache` | Encrypted cache of the last successful license check (for the 48 h offline window) | After every successful heartbeat |
| `%APPDATA%\BlockParam\blockparam.log` | Diagnostic log: warnings, errors, license-check round-trips. **No DB content.** | While the Add-In runs |
| `%TEMP%\BlockParam\` | Dev-launcher only — cached TIA exports for UI testing | Only when running the DevLauncher |

You can delete any of these at any time. Deleting `license.cache` simply forces
a fresh check on next start.

## 6. Marketing website

If you visit our website at **https://blockparam.lautimweb.de** the hosting
provider (ALL-INKL.COM) stores standard access logs (URL, IP, user-agent,
referrer, timestamp), retained for 30 days.

| | |
|---|---|
| Cookies | None set by us. A self-hosted Matomo session cookie is set only if you have not opted out via the Matomo opt-out toggle on the site. |
| Analytics | **Self-hosted Matomo** on the same ALL-INKL.COM server. IPs are anonymised before storage; no third-party processor receives the data. |
| Embedded fonts | Self-hosted (no third-party CDN call). |
| Embedded videos | Self-hosted MP4 (no YouTube / Vimeo embed). |

Legal basis: **Art. 6(1)(f) GDPR** (legitimate interest in operating a website),
or your consent under **Art. 6(1)(a) GDPR** where required (e.g. non-essential
cookies).

## 7. Recipients outside the EU

| Provider | Country | Mechanism |
|---|---|---|
| Lemon Squeezy LLC | USA | Standard Contractual Clauses (EU Commission Decision 2021/914), see [Lemon Squeezy DPA](https://www.lemonsqueezy.com/dpa) |

We do not otherwise transfer personal data outside the EU/EEA.

## 8. Your rights under GDPR

You can exercise the following rights at any time, free of charge, by emailing
[support@lautimweb.de](mailto:support@lautimweb.de):

- **Access** to the personal data we hold about you (Art. 15).
- **Rectification** of inaccurate data (Art. 16).
- **Erasure** ("right to be forgotten", Art. 17).
- **Restriction** of processing (Art. 18).
- **Portability** in a machine-readable format (Art. 20).
- **Objection** to processing based on legitimate interest (Art. 21).
- **Withdrawal of consent** at any time, with effect for the future (Art. 7(3)),
  where processing is based on consent.

You also have the right to **lodge a complaint with a supervisory authority**
(Art. 77). For us, the competent authority is **Der Landesbeauftragte für den
Datenschutz und die Informationsfreiheit Rheinland-Pfalz (LfDI RLP),
Hintere Bleiche 34, 55116 Mainz, Germany —
[https://www.datenschutz.rlp.de](https://www.datenschutz.rlp.de)**.

## 9. Children

The Add-In is a professional engineering tool not directed at children. We do
not knowingly process personal data of persons under 16.

## 10. Security

- All client–server traffic uses TLS (HTTPS).
- The license cache is signed so it can't be silently extended past 48 h.
- Server-side license records are stored in a relational database on the
  EU-hosted infrastructure listed above (ALL-INKL.COM, Germany).
- Access to the license server is restricted to the operator and is logged.

## 11. Changes to this policy

We may update this policy. Material changes will be announced in the release
notes and on the landing page. The Git history of this file in the
[BlockParam repository](https://github.com/Sawascwoolf/BlockParam) is the
authoritative changelog.

## 12. Contact

| | |
|---|---|
| Controller | **Tobias Laubscher** (Einzelunternehmer, trading as *lautimweb*) |
| Address | Enkenbacher Str. 55, 67691 Hochspeyer, Germany |
| Phone | +49 176 21156188 |
| Email | [support@lautimweb.de](mailto:support@lautimweb.de) |
| VAT-ID | DE313892898 |
| Subject line | "Privacy" |
