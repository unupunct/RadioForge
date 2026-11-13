# Installing RadioForge

**Requirements:** Windows 10 (1809 or newer) or Windows 11, 64-bit. The installer includes the .NET runtime, so nothing else has to be installed. FFmpeg is optional (only for AAC/Ogg streaming and FLAC recording).

1. Download `RadioForge-Setup.exe` from the GitHub *Releases* page.
2. Run it. Windows SmartScreen may warn about an unknown publisher — the installer is **not code-signed**. Choose *More info → Run anyway* if you trust the download.
3. Pick the install folder (default `C:\Program Files\RadioForge`) and optionally a desktop shortcut.
4. Start RadioForge; the setup wizard opens on first run.

**Upgrading:** run the newer setup over the existing installation (same application ID). Settings, profiles, playlists and logs in `%APPDATA%\RadioForge` are untouched.

**Uninstalling:** *Settings → Apps → RadioForge → Uninstall*. You are asked whether to delete your settings/profiles as well (default: keep). Recordings are never deleted.

**Silent install (scripts/IT):** `RadioForge-Setup.exe /VERYSILENT /NORESTART` (add `/TASKS=desktopicon` for the desktop shortcut, `/DIR="D:\Radio"` to change the folder).

**Separate data folder:** start `RadioForge.exe --data-dir D:\MyRadioData` to keep all data elsewhere.

## Verification status
The installer was built and exercised on the development PC only (silent install, launch, uninstall). It has **not** been tested on a clean Windows VM, so a clean-machine installation is unverified until someone tries it.
