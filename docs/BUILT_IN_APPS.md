# Built-in sign-in apps (maintainers)

Release builds sign users in with OAuth apps registered for Cuebot itself, so streamers just click **Sign in**. The
apps' IDs and secrets are not in the repository: CI compiles them in from repository secrets
(`src/Cuebot.Core/Cuebot.Core.csproj` writes `BuiltInApps.g.cs` from the environment variables below).

A build without them (a fork, a pull request, a local or AUR source build) still works: each page then opens
**Advanced: use my own … app** and asks for the user's own app, as before. Users can always enter their own app there;
an own app, once entered, is used instead of the built-in one until it is cleared.

Desktop apps cannot keep a secret: anyone can extract these values from the program. That is the accepted risk of
this setup (no token server). What it means in practice: someone could build a program that presents itself as
"Cuebot" on the consent screens. The platforms can revoke the app if it is abused; if that happens, rotate the secret
and ship a new release. Never reuse these apps for anything that needs the secret to stay confidential.

## Secrets

Set them under GitHub → repository → Settings → Secrets and variables → Actions, or with `gh secret set NAME`.
Unset ones only produce a warning in the release run; that service then asks users for their own app.

| Secret | Service |
| --- | --- |
| `CUEBOT_TWITCH_CLIENT_ID` | Twitch (no secret needed) |
| `CUEBOT_GOOGLE_CLIENT_ID`, `CUEBOT_GOOGLE_CLIENT_SECRET` | YouTube browser sign-in and Google Drive backups ("Desktop app" client) |
| `CUEBOT_GOOGLE_DEVICE_CLIENT_ID`, `CUEBOT_GOOGLE_DEVICE_CLIENT_SECRET` | YouTube "sign in with a code" (optional; "TVs and Limited Input devices" client) |
| `CUEBOT_KICK_CLIENT_ID`, `CUEBOT_KICK_CLIENT_SECRET` | Kick |
| `CUEBOT_TREATSTREAM_CLIENT_ID`, `CUEBOT_TREATSTREAM_CLIENT_SECRET` | TreatStream |

For a local build with the apps, export the same variables before `dotnet build` / `build/publish.sh`. Values may only
contain URL-safe characters (`A-Z a-z 0-9 . _ ~ + / = -`); the build fails otherwise, without printing the value.

## Registering the apps

### Twitch

[dev.twitch.tv/console](https://dev.twitch.tv/console) → Register Your Application: name "Cuebot", OAuth redirect
`http://localhost`, category **Chat Bot**, client type **Public**. Copy the Client ID. Cuebot signs in with the
device-code flow, which needs no secret.

### Google (YouTube and Drive)

One Google Cloud project for both:

1. Enable **YouTube Data API v3** and **Google Drive API**.
2. Google Auth Platform → Branding: app name "Cuebot", support e-mail, a homepage and a **privacy policy** URL on a
   domain you own and verify in Search Console. Audience: **External**, then **Publish app**.
3. Data access: `https://www.googleapis.com/auth/youtube`, `https://www.googleapis.com/auth/youtube.force-ssl`
   (sensitive) and `https://www.googleapis.com/auth/drive.file` (non-sensitive).
4. Clients → Create client → **Desktop app** → `CUEBOT_GOOGLE_CLIENT_ID` / `_SECRET`. Optionally a second client of
   type **TVs and Limited Input devices** → `CUEBOT_GOOGLE_DEVICE_CLIENT_ID` / `_SECRET` (without it, YouTube offers
   browser sign-in only). No redirect URLs: Cuebot uses a random `http://127.0.0.1` port with PKCE.
5. Submit the app for **verification** (the YouTube scopes are sensitive: Google asks for a demo video and the privacy
   policy). Until it is verified, users see an "unverified app" warning and at most 100 users can sign in in total.

**Quota.** All users of the built-in app share the project's YouTube quota: 10,000 units a day by default, and one
live stream with default settings uses about 2,500 units an hour (chat polling dominates; see
`src/Cuebot.YouTube/YOUTUBE.md`). Request a higher quota (the YouTube API Services audit) before relying on it, and
point heavy users to **Advanced: use my own Google Cloud project**, which gives them their own quota.

### Kick

kick.com → Settings → Developer → Create app: redirect URL `http://localhost:47582/callback` exactly (the built-in app
always uses port 47582; users with their own app may pick another), scopes as listed in
`src/Cuebot.Kick/Auth/KickScopes.cs`. Copy the Client ID and Client Secret.

### TreatStream

TreatStream account settings → API apps: redirect URL `http://localhost:8913/callback` exactly (the built-in app
always uses port 8913). Copy the Client ID and Client Secret.
