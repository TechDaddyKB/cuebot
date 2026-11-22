# Code signing (maintainers)

Windows release builds sign `Cuebot.exe` and Cuebot's own DLLs (`Cuebot*.dll`) with **Azure Artifact Signing**
(formerly Trusted Signing), so Windows shows a verified publisher instead of "Unknown publisher". Third-party DLLs are
left as their authors shipped them. Linux packages are not signed. macOS disk images are signed with a Developer ID certificate and notarized by Apple (see
[macOS](#macos-developer-id-and-notarization) below).

Signing does not make SmartScreen's "Windows protected your PC" disappear on day one: SmartScreen trusts a publisher once
its signed files have built download history, typically over a few weeks and a few hundred clean installs. Every
release must be signed with the same identity to keep that reputation.

## How CI signs

The `windows-package` job in `.github/workflows/build.yml`:

1. publishes the Windows build without zipping it (`tools/publish.ps1 -NoZip`),
2. signs `Cuebot.exe` and `Cuebot*.dll` with `azure/artifact-signing-action`, logging in to Azure through OpenID Connect
   (no stored password or secret),
3. checks that `Cuebot.exe` carries a valid signature,
4. zips the signed files (`tools/publish.ps1 -ZipOnly`).

Signing runs only when the variable `AZURE_SIGNING_ACCOUNT` is set, and never for pull requests, so forks and pull
requests still build unsigned packages. The job runs in the GitHub environment **signing**; the Azure login trusts only
that environment of this repository.

### Variables

Set these on the **signing** environment (Settings > Environments > signing > Environment variables). None of them are
secrets.

| Variable | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `AZURE_CLIENT_ID` | Application (client) ID of the app registration used for signing |
| `AZURE_SIGNING_ENDPOINT` | The signing account's endpoint, e.g. `https://eus.codesigning.azure.net/` (it depends on the region) |
| `AZURE_SIGNING_ACCOUNT` | Signing account name |
| `AZURE_SIGNING_PROFILE` | Certificate profile name |

## Setting it up

Steps 1 to 3 need the account owner (payment and a government ID). The rest can be done by anyone with Owner access to
the subscription.

1. **Azure subscription.** Create an Azure account and a **Pay-As-You-Go** subscription. Free, trial and sponsored
   subscriptions are not supported by Artifact Signing.
2. **Signing account.** In the Azure portal create a resource group (for example `cuebot-signing`), register the
   `Microsoft.CodeSigning` resource provider on the subscription, then create an **Artifact Signing account** in a nearby
   region with the **Basic** plan (US$9.99 a month, billed from creation).
3. **Identity validation.** In the signing account open **Identity validations** > **New identity** > **Public** and
   choose **Individual** (available to individuals in the USA and Canada) or **Organization**. Complete the
   verification Microsoft asks for. The validated name is what Windows shows as the publisher.
4. **Certificate profile.** When the validation is approved, open **Certificate profiles** > **Create** > **Public Trust**
   and pick the validated identity.
5. **App registration for GitHub.** In Microsoft Entra ID create an app registration (for example
   `cuebot-github-signing`). Under **Certificates & secrets** > **Federated credentials** add a credential for GitHub
   Actions: organization `TechDaddyKB` (ID `6316740`), repository `cuebot` (ID `1435222275`), entity type
   **Environment**, environment `signing`. The repository uses GitHub's immutable OIDC subjects, so the subject is
   `repo:TechDaddyKB@6316740/cuebot@1435222275:environment:signing`; check the current format with
   `gh api repos/TechDaddyKB/cuebot/actions/oidc/customization/sub`.
6. **Permission.** On the certificate profile (or the signing account), add a role assignment: role **Artifact Signing
   Certificate Profile Signer**, member the app registration from step 5.
7. **GitHub.** Create the environment **signing** in the repository and set the variables above on it. Optionally limit
   it to the `main` branch and `v*` tags under **Deployment branches and tags**.
8. **Check.** Push to `main` (or re-run the build workflow) and look for "Valid" in the **Check the signature** step of
   the `windows-package` job. In Windows, the signed `Cuebot.exe`'s Properties > Digital Signatures tab shows the
   publisher.

## Cost

Basic plan: US$9.99 a month for up to 5,000 signatures. Each Windows build signs about 25 files, so every push to `main`
and every release fit easily.

## macOS: Developer ID and notarization

The `macos-package` job in `.github/workflows/build.yml` runs `packaging/macos/build-app.sh`, which signs every Mach-O
file and `Cuebot.app` with a **Developer ID Application** certificate and the hardened runtime (entitlements in
`packaging/macos/Cuebot.entitlements`), submits the app to Apple's notary service, staples the ticket, then builds the
`.dmg`, signs it, notarizes it and staples it too. A downloaded image therefore opens without Gatekeeper's "unidentified
developer" warning, even offline.

Like the Windows job it runs in the GitHub environment **signing**, never for pull requests. Without the secrets (forks,
pull requests) the job builds ad-hoc signed images as before.

### Secrets on the signing environment

| Name | Kind | Value |
| --- | --- | --- |
| `MACOS_CERT_P12_BASE64` | secret | The Developer ID Application certificate with its private key, exported as .p12, base64 encoded |
| `MACOS_CERT_PASSWORD` | secret | The password of that .p12 |
| `MACOS_SIGN_ID` | secret | The identity name, `Developer ID Application: Name (TEAMID)` |
| `NOTARY_KEY_P8_BASE64` | secret | An App Store Connect API key (team key, Developer role) for notarization, the `AuthKey_*.p8` file base64 encoded |
| `NOTARY_KEY_ID` | secret | That key's ID |
| `NOTARY_ISSUER_ID` | secret | The issuer ID shown on the App Store Connect API keys page |

Use a key of its own for notarization, separate from the key used to upload the iOS companion.

### Running it locally

```sh
CUEBOT_MACOS_SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" \
CUEBOT_NOTARY_KEY_PATH=/path/to/AuthKey_XXXX.p8 CUEBOT_NOTARY_KEY_ID=XXXX CUEBOT_NOTARY_ISSUER_ID=... \
  packaging/macos/build-app.sh 1.2.3 osx-arm64
```

Without the `CUEBOT_NOTARY_*` variables the script signs but does not notarize.

### Notes

- Permissions macOS asked for before (Input Monitoring, microphone, Accessibility) are tied to the code signature. The first
  Developer ID release looks like a different app to macOS, so users grant them once more.
- When Apple rejects a submission, the build prints Apple's notarization log. The usual cause is a Mach-O file that is not
  signed with a secure timestamp.
- The Developer ID certificate is valid for five years; renew it before it expires and replace the two certificate secrets.
