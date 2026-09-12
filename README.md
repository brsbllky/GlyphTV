<div align="center">
<img width="1900" height="650" alt="glyph_icon" src="https://github.com/user-attachments/assets/3fdd0d47-aa79-43b7-aa28-7f4685a4a77c" />

# GlyphTV 📺

**Modern, high-performance, dual-engine (MPV & VLC) IPTV / Media Player**  
Engineered with Avalonia UI and .NET 10 for a fluid, feature-rich desktop experience.

[![English](https://img.shields.io/badge/Language-English-blue?style=for-the-badge)](#)
[![Türkçe](https://img.shields.io/badge/Dil-T%C3%BCrk%C3%A7e-red?style=for-the-badge)](README.tr.md)

<br/>

[![C#](https://img.shields.io/badge/C%23-100%25-239120?style=flat&logo=csharp)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3.13-8B5CF6?style=flat&logo=avalonia)](https://avaloniaui.net/)
[![MPV](https://img.shields.io/badge/Engine-MPV-9B59B6?style=flat&logo=mpv)](https://mpv.io/)
[![VLC](https://img.shields.io/badge/Engine-LibVLC-FF8800?style=flat&logo=vlcmediaplayer)](https://www.videolan.org/vlc/)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D4?style=flat&logo=windows)](https://www.microsoft.com/windows)

</div>

---

## ✨ Features

- 📺 **Live TV** — Support for M3U files, remote M3U/M3U8 playlist URLs, and Xtream Codes API
- 🎬 **VOD (Movies)** — TMDb posters, trailers, cast lists, playback progress tracking, and favorites
- 🎞️ **Series** — Automatic season/episode parsing, episode thumbnails, and descriptions with intuitive navigation
- 📅 **EPG Program Guide** — XMLTV support, real-time broadcast progress indicators, and comprehensive program timelines
- 🔍 **Instant Search & Smart Navigation** — Results-focused dynamic search mode (auto-hides hero banner), automatic input reset on tab transitions, and A-Z / Z-A / Recently Added sorting
- ❤️ **Favorites** — Real-time synchronized favorites catalog for Live TV, Movies, and TV Series
- 🎨 **Dynamic Themes** — Midnight Navy (Dark) and Clean Light theme support
- 🎥 **TMDb Integration** — Automatic title sanitization, Levenshtein distance verification, poster/backdrop disk caching, and manual override support (`tmdb-overrides.json`)
- ⏱️ **Watch History** — Resume where you left off, progress percentage indicators, and one-click history clearing
- ⌨️ **Keyboard Shortcuts** — Full-featured playback controls via Space, F, M, and arrow keys
- 🔊 **Multi-Audio & Subtitles** — Audio channel/language selection, millisecond-precision subtitle delay sync, and 5.1 surround sound support
- 📐 **Dynamic Aspect Ratio** — 16:9, 4:3, 21:9, and Auto (Dynamic Aspect Ratio) modes

---

## 💻 System Requirements

- [Windows 10](https://www.microsoft.com/windows) / Windows 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) *(Required only when building from source code)*
- [MPV](https://mpv.io/) or [LibVLCSharp](https://github.com/videolan/libvlcsharp) libraries *(Bundled automatically with release binaries)*

---

## 🔒 Security & Trust

### Windows SmartScreen Warning

When launching GlyphTV for the first time, Windows SmartScreen may display the following notification:

> **"Windows protected your PC — Unknown publisher"**

**Why does this happen?**

SmartScreen displays this prompt for open-source applications that do not carry an expensive commercial EV Code Signing certificate registered with Microsoft. Such certificates cost hundreds of dollars annually, whereas this software is distributed completely free and open-source for the community. The application contains zero malicious code.

**How to bypass and run:**

1. Click **"More info"** on the SmartScreen dialog.
2. Click the **"Run anyway"** button that appears at the bottom.
3. The application will start immediately.

> ⚠️ You can review the VirusTotal report below or compile the application directly from source code yourself before launching.

---

### ✅ VirusTotal Scan Report

Every release binary is submitted and scanned on VirusTotal prior to release. You can verify the integrity yourself:

**Why might some scanners flag false positives?**

Certain heuristic antivirus algorithms may trigger false positives due to:
- **Self-contained packaging:** The .NET runtime and all assemblies are bundled into a single standalone executable.
- **Native multimedia engines:** **LibVLC** and **MPV** (`mpv-2.dll`, `libvlc.dll`, `libvlccore.dll`) contain native C/C++ compiled binaries.
- Unsigned executable heuristics.

| Field | Details |
|---|---|
| Release | `v2.1.1` |
| Binary | `GlyphTV.exe` |
| SHA256 | `7d234c1aaec0095b47cf0838803b6262db44352f00fe22f0195e525460dce482` |
| Detection Rate | **0 / 70 Clean** |
| VirusTotal Report | [View Analysis]https://www.virustotal.com/gui/file/7d234c1aaec0095b47cf0838803b6262db44352f00fe22f0195e525460dce482?nocache=1 |

---

## 📥 Installation

### Pre-built Binary (Recommended)

1. Download the latest release package from the [Releases](https://github.com/brsbllky/GlyphTV/releases) page.
2. Run `GlyphTV.exe` directly — no installation wizard or external dependencies required.

### Building from Source

> **TMDb API Key Required** — For movie/series posters and metadata, obtain a free API key from [themoviedb.org](https://www.themoviedb.org). Insert your key into `MainWindow.axaml.cs`:

```csharp
private const string TMDB_API_KEY = "your_tmdb_api_key_here";
```

> If no key is provided, the player functions normally; metadata posters and backdrops simply won't be fetched.

```bash
# Clone the repository
git clone https://github.com/brsbllky/GlyphTV.git
cd GlyphTV/GlyphTV

# Build and publish release binaries
dotnet publish -c Release
```

Compiled binaries will be generated in `bin/Release/net10.0-windows/publish/`.

---

## 🎮 Usage Guide

### Adding IPTV Playlists & Sources

1. Navigate to **Settings (⚙️) → IPTV Sources → Add New Source** from the top navigation bar.
2. Select your provider type:
   - **Xtream Codes** — Server URL, Username, and Password (includes password toggle visibility)
   - **M3U Link** — Remote M3U / M3U8 playlist URL
   - **M3U File** — Local `.m3u` / `.m3u8` file stored on your computer

### ⌨️ Keyboard Shortcuts

| Shortcut | Function |
|---|---|
| `Space` | Play / Pause |
| `F` | Toggle Fullscreen |
| `Esc` | Exit Fullscreen / Close Active Dialog |
| `M` | Mute / Unmute Audio |
| `← / →` | Seek backward / forward 10 seconds (VOD & Series) |
| `↑ / ↓` | Switch to previous / next channel (Live TV) |
| `Double Click` | Toggle Fullscreen on video viewport |

---

## 🛠️ Built With

| Framework / Tool | Version | Purpose |
|---|---|---|
| [Avalonia UI](https://avaloniaui.net/) | 11.3.13 | Modern cross-platform XAML desktop UI framework |
| [MPV Engine](https://mpv.io/) | Native | Ultra-low latency, hardware-accelerated media player core |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) | 3.9.6 | High-compatibility alternative media playback engine |
| [TMDb API](https://www.themoviedb.org/documentation/api) | v3 | Movie & Series metadata, posters, backdrops, and cast info |
| [.NET](https://dotnet.microsoft.com/) | 10 | High-performance application runtime (C# 13) |

---

## 📸 Screenshots

> 📌 **Disclaimer**: GlyphTV does not host, provide, or distribute any media streams or playlist content. Channels and media artwork visible in screenshots are solely for user interface demonstration purposes.

**Home & TMDb Hero Banner**  
![Home](screenshots/home.PNG)

**Independent Settings Modal**  
![Settings](screenshots/ayarlar.PNG)

**Live TV — Category & Channel Catalog**  
![Live TV](screenshots/canli.PNG)  
![Live TV 2](screenshots/canli2.PNG)

**Movies & TV Series Browser**  
![Movies](screenshots/vod.PNG)  
![Series](screenshots/dizi.PNG)

**Minimalist Movie & Series Detail Modal**  
![Media Details](screenshots/vod2.PNG)

**Live TV — EPG Broadcast Timeline Modal**  
![EPG Modal](screenshots/epg.PNG)

**Video Player (OSD & Controls Overlay)**  
![Player](screenshots/player.PNG)

**Favorites Dashboard**  
![Favorites](screenshots/favoriler.PNG)

**Instant Dynamic Search**  
![Search](screenshots/arama.PNG)

---

<div align="center">

GlyphTV — Built with Avalonia UI, MPV & LibVLCSharp.

**Designed by AkuLaTa**

</div>
