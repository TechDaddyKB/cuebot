# Backups on Google Drive

Cuebot can also copy [backups](backups.md) to **your own** Google Drive, so they survive if your computer breaks. Go
to **Settings**, tab **Google Drive**.

## Setting it up

1. Click **Sign in with Google**, pick your account and allow access.
2. Check that **Upload every new backup to Google Drive** is ticked (it is from the start). Cuebot puts them in a
   folder called **Cuebot Backups** and keeps the newest **10** there. You can change the number.
3. **Upload now** sends one right away. The page lists the backups on your Drive.
4. To get one back, click **Restore...** next to it.

Cuebot can only see the files it made itself. It cannot look at anything else in your Drive. **Sign out** removes its
access; your backups stay on Drive.

Backups on Drive hold your settings, actions and overlays, but not your sign-ins, tokens or keys. Restoring on another
computer means signing in to your accounts again there.

## Using your own Google Cloud project (advanced)

Most people can skip this. Open **Advanced: use my own Google Cloud project** only if your Cuebot has no Google app of
its own. The steps are on the page: switch on the Drive API in Google Cloud, make a login key of type "Desktop app",
paste its **Client ID** and **Client secret**, and sign in again. Leave them empty to use Cuebot's.
