# RadioForge — user guide

RadioForge is a Windows internet-radio broadcasting studio: a live mixing console, playlists with Auto DJ, a soundboard, a scheduler, recording, and **several simultaneous streaming destinations** (SHOUTcast v1/v2 and Icecast 2), each with its own encoder settings.

* Installation: [INSTALLATION.md](INSTALLATION.md)
* Connecting to your streaming host: [STREAMING_SETUP.md](STREAMING_SETUP.md)
* Problems: [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

## First start
The setup wizard asks for station info, your streaming server, audio devices and quality, then runs a connection test. You can skip any step and finish later under **Streaming Servers**, **Audio Settings** and **Application Settings → Run setup wizard**.

## The sections
| Section | What it does |
|---|---|
| **Dashboard** | ON AIR state, connections, listener count (only when the server reports it), now playing / next / program, mic state, input and output meters, real-time visualizer (8 modes, 24 presets, fullscreen), quick actions. |
| **Studio** | 5 channels (Music, Microphone, Effects, Auxiliary, System) with fader, mute, solo, meters and peak/clip indicator; master volume, balance, limiter, output routing; microphone chain (gain, gate, compressor, 3-band EQ, reverb, echo), push-to-talk, **ducking** (amount, attack, release, threshold), voice monitoring (off by default) and a microphone test. |
| **Playlist Manager** | Playlists and folders, drag-and-drop, reorder, M3U import/export, random/repeat, crossfade, **Auto DJ**, "use for Auto DJ". |
| **Soundboard** | 48 pads on 4 pages: name, colour, icon, file, volume, mode (once / loop / over music / replace music / after current track), hotkey, per-pad stop, drag-and-drop, preview mode (plays on your speakers only, never on air). |
| **Streaming Servers** | Unlimited servers, enable/disable, start/stop one or all, **Test connection**, live bitrate/uptime/reconnects/last error, per-server log. |
| **Broadcast Scheduler** | Week and month calendar; playlists, programs, ad breaks, jingles, news, pre-recorded shows, announcements; one-off / daily / weekly / monthly repeats; priorities; conflict warnings. Runs while the window is minimized to the tray. |
| **Audio Library** | Import files/folders, tags and artwork, search, duplicate and missing-file detection. |
| **Recordings** | Record master, microphone or a single channel to WAV / MP3 / FLAC while streaming; history, playback, delete. |
| **Audio Settings** | Devices, sample rate, latency/buffer, aux and system-audio capture. |
| **Statistics** | Per-server table and charts (bitrate, listeners, availability, stability), uptime, CSV export, event log. |
| **Logs** | Structured, redacted logs (also written to `%APPDATA%\RadioForge\logs`). |
| **Application Settings** | Layout reset, profiles (import/export without passwords), theme, accent, font scaling, tray/close behaviour, notifications, keyboard shortcuts, storage, backup/restore, FFmpeg path, diagnostics. |

## Sound: same volume, clean sound, smooth transitions
* **Radio sound** (*Studio → master* or *Audio Settings*): *Gentle*, *Radio* or *Loud* adds a rumble filter, automatic gain control (levels every track, up to ±12 dB), a 3-band compressor and a soft clipper before the master limiter. It applies to everything you broadcast and record. The live readout shows how much the leveler and compressor are working.
* **Loudness normalisation** (*Audio Settings*): measures each track in LUFS (ITU BS.1770) and gain-matches it to your target (default −16 LUFS). Run *Audio Library → Analyze loudness* once, or let RadioForge measure tracks the first time they play. Use it with Radio sound for the most even result.
* **Crossfade / gapless** (*Playlist Manager*): 0 s = gapless (next track starts the instant the current one ends); above 0 s = equal-power crossfade. *Trim silence between tracks* (Audio Settings) skips silent intros and starts the next track early after a silent ending.

## Beginner and show tools
* **Simple mode** (*Application Settings → Interface*): sidebar shows only On Air, Playlists, Soundboard, Servers and Settings. The **On Air** page has one big broadcast button, the now-playing card, transport, microphone, volume and four jingles, and tells you what to do next. It is also available in normal mode.
* **Sample content**: original royalty-free demo tracks, a sample playlist and four jingles (setup wizard option, or *Application Settings → Add sample music and jingles*).
* **Voice tracks**: in *Playlist Manager* press *Record voice track*, speak, press stop. The microphone is recorded privately (it never goes on air and does not duck the music) and the link is inserted after the selected track.
* **Archive shows + podcast**: tick *Archive* on a scheduled event. It is recorded while it is on and added to `podcast.xml` in the recordings folder when you set *Podcast URL* (the public address where you upload that folder).
* **Stream health alerts**: *Application Settings → Stream health alerts* — Discord/Slack-compatible webhook and/or e-mail; use *Send test alert*. Webhook URL and SMTP password are stored encrypted.
* **Now-playing web page**: *Application Settings → Now-playing web page* — `http://localhost:8765/` (and `/nowplaying.json`). Tick *Allow other devices on my network* to open it from a phone; Windows may ask for firewall access. It shows listeners only when your server reports them.

## Accessibility
Every button, slider, list and text field has an accessible name for screen readers, checked by an automated test on every page. Navigation works with the keyboard (Tab, arrows, Space/Enter, your shortcuts). Known gap: scheduler calendar events are clickable blocks only — use the *New event* button or double-click; a keyboard-friendly event list is planned.

## Keyboard shortcuts (defaults, configurable)
`Space` play/pause · `Ctrl+N` next track · `Ctrl+R` start/stop recording · `F1` microphone · `F2`–`F5` jingles 1–4 · `Ctrl+Shift+B` start broadcast · `Ctrl+Shift+S` stop broadcast.

Shortcuts work while RadioForge has focus. In *Application Settings → Keyboard shortcuts* you can tick **System-wide** for any shortcut so it also works while another program has focus (requires Ctrl/Shift/Alt or an F-key; Start/Stop broadcast are system-wide by default). If another program already owns the combination, RadioForge tells you. Unmodified keys are ignored while you type in a text box.

## Running in the background
Closing the window during a broadcast asks whether to keep broadcasting in the system tray. The tray icon shows a red dot when on air and offers Open / Start-Stop broadcast / Exit. RadioForge cannot broadcast while Windows is shut down or asleep or while the application is not running — scheduled events only fire while it runs.

## Where data lives
`%APPDATA%\RadioForge` — `settings.json`, `profiles\<id>\profile.json` and `radioforge.db` (SQLite: library, playlists, schedule, recording history, statistics), `logs`, `backups` (rolling automatic backups of the config files). Server passwords are stored encrypted with **Windows DPAPI (current user)**; they never appear in logs or exports, so an imported profile needs its passwords re-entered.

## Encoding
MP3 uses the built-in LAME encoder. **AAC and Ogg Vorbis streaming and FLAC recording need FFmpeg** — use *Application Settings → Advanced → Download FFmpeg* (LGPL build from the BtbN project, about 175 MB, stored in your data folder), install your own (for example `winget install Gyan.FFmpeg`), or set its path there. Destinations with identical MP3/AAC settings share one encoder; Ogg destinations always get their own.
