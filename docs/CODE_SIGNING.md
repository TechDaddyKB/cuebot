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

## iOS: TestFlight and the App Store

`.github/workflows/ios-release.yml` releases Cuebot Companion for iOS without a Mac at hand. The Developer ID certificate
and notarization above do not apply to iOS: App Store builds need an **Apple Distribution** certificate and an **App
Store** provisioning profile.

### Each release

1. Add `clients/ios/release-notes/<version>.txt` (what users see under "What's New", at most 4000 characters). A release
   tag without this file fails before anything is uploaded.
2. Push the tag `vX.Y.Z`. CI builds, signs and uploads the build to TestFlight. Release candidates (`vX.Y.Z-rc.N`) do
   not run this workflow.
3. Approve the waiting `app-store-review` deployment on the workflow run in GitHub.
4. CI waits for Apple to process the build, creates the App Store version, attaches the build, sets the notes, prepares
   App Review Information and submits it to App Review. The version releases automatically once Apple approves it.

App Review needs a way to use the app without a Cuebot, so the submit job also:

- uploads `clients/ios/review/cuebot-review.mp4`, unless the version already has a finished attachment. Replace that file
  when pairing or the main screens change. It is public, so it must show only made-up data.
- adds the [test server](https://cuebot-test.techdaddydigital.com/) pairing steps to the App Review notes if they are
  missing, and keeps the rest of the notes. Keep the test server running for as long as the app is on the App Store. If it
  does not answer, the job warns and still submits, because the video shows the app.

Only one version can be with Apple at a time: while another is waiting or in review, the submit job stops without
changing anything. Re-run that job once the earlier version is live. Re-running is safe at every stage.

Without the secrets, a tag skips this workflow's work with a warning. Screenshots and other store listing changes are
still made in App Store Connect.

### One-time setup

1. Make a certificate request:

   ```sh
   clients/ios/ci/setup-secrets.sh request ~/cuebot-ios-signing
   ```

2. At developer.apple.com, under Certificates, create an **Apple Distribution** certificate from that `.csr` and download
   the `.cer`.
3. Under Profiles, create an **App Store Connect** distribution profile for `digital.techdaddy.cuebot.companion` with that
   certificate and download it.
4. In App Store Connect, under Users and Access, Integrations, create a **team key with the App Manager role**, used only
   for this workflow. Download the `.p8` and note the issuer ID.
5. Store everything (the script prints no secret values):

   ```sh
   clients/ios/ci/setup-secrets.sh install ~/cuebot-ios-signing/ios-distribution.key distribution.cer \
     Cuebot.mobileprovision AuthKey_XXXXXXXXXX.p8 <issuer-id>
   ```

6. Test from `main`: run the workflow by hand with `build-only`, then with `testflight`.

### Secrets

| Environment | Name | Value |
| --- | --- | --- |
| `signing` | `IOS_DIST_CERT_P12_BASE64`, `IOS_DIST_CERT_PASSWORD` | The Apple Distribution certificate and private key |
| `signing` | `IOS_APPSTORE_PROFILE_BASE64` | The App Store provisioning profile |
| `signing`, `app-store-review` | `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8_BASE64` | The App Manager API key |

The `app-store-review` environment must keep a required reviewer and allow only `v*` tags. The workflow refuses to run a
release if the reviewer is gone, because GitHub would otherwise recreate a deleted environment with no protection.

### Renewal

The certificate and the profile each last one year, and the workflow warns 30 days before the profile expires. Repeat
steps 1 to 3 and 5. A Mac is not needed.
