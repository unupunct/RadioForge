# Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| **Authentication failed** | Wrong source password or username. Icecast: the user is usually `source`. SHOUTcast: use the *source/DJ* password, not admin. Re-type the password in *Streaming Servers*. |
| **Server unavailable / timed out** | Wrong host or port, firewall, or the server is down. SHOUTcast: enter the listener port; RadioForge uses port+1 for the source. |
| **Mount point rejected (403/404)** | Mount already in use by another encoder, mount not allowed for your account, or your host does not allow the format (for example AAC/Ogg on an MP3-only mount). |
| **"AAC needs FFmpeg"** | Install FFmpeg (`winget install Gyan.FFmpeg`) or set its path under *Application Settings → Advanced*. MP3 and WAV work without it. |
| **No sound on the speakers** | *Studio → Output to speakers* must be on; choose the right device under *Audio Settings* and press *Apply devices*. Streaming does not need the speakers. |
| **Microphone silent** | The mic starts muted: press F1 or the mic button. Check *Windows Settings → Privacy → Microphone* allows desktop apps. Use *Studio → Microphone test*. |
| **Echo / feedback** | Keep voice monitoring off, or use headphones on a *different* device than the program output. Never capture "System audio" from the device RadioForge plays to. |
| **Crackling audio** | Raise latency in *Audio Settings* (for example 150–200 ms) and apply; close CPU-heavy programs. |
| **Listeners show n/a** | The host does not publish statistics pages. This is normal. |
| **A scheduled event did not start** | RadioForge must be running (the tray is fine) and the PC must not be asleep. Check the event is enabled, the station time zone and *Logs → Scheduler*. |
| **Track skipped** | The file is missing or cannot be decoded; a notification and a log line name it. Use *Audio Library → Missing files*. |
| **Audio device removed** | The engine recovers automatically (retries with back-off, falls back to the default device) and notifies you. A software clock keeps streaming alive while no output device exists. |

## Diagnostics
*Application Settings → Advanced → Copy diagnostics* copies versions, devices, server states and recent log lines (credentials redacted). Crash reports are written to `%APPDATA%\RadioForge\logs\crash-*.txt`. Enable *Debug logging* for more detail.
