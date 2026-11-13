# Third-party components

RadioForge itself is MIT licensed. It uses these components (licenses verified from the package metadata at build time — re-check before redistributing a modified build):

| Component | Use | License |
|---|---|---|
| .NET 10 / WPF (self-contained runtime) | application runtime | MIT |
| NAudio 2.2.1 (+ NAudio.Wasapi/WinMM/Core) | audio devices, mixing, DSP helpers | MIT |
| NAudio.Lame 2.1.0 (bundles LAME `libmp3lame`) | MP3 encoding | wrapper MIT; **LAME is LGPL 2.1** (dynamically linked DLLs `libmp3lame.32/64.dll`, replaceable) |
| NAudio.Vorbis 1.5.0 / NVorbis | Ogg Vorbis file decoding | MIT |
| TagLibSharp 2.3.0 | reading tags / artwork | **LGPL 2.1** (dynamically linked, replaceable) |
| Microsoft.Data.Sqlite / SQLitePCLRaw | SQLite storage | MIT / Apache-2.0 (SQLite is public domain) |
| CommunityToolkit.Mvvm | MVVM helpers | MIT |
| FFmpeg (optional, **not bundled**) | AAC / Ogg streaming and FLAC recording | LGPL or GPL depending on the build the user installs |

Because LAME and TagLibSharp are LGPL, you may replace those DLLs with your own builds. FFmpeg is only launched as a separate process if the user installed it.
