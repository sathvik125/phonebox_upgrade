<p align="center"><img src="agent/PhoneBox.Agent/phonebox.ico" width="72" alt="PhoneBox icon"></p>

<h1 align="center">PhoneBox</h1>

<p align="center">
  <b>English</b> ·
  <a href="docs/README.tr.md">Türkçe</a> ·
  <a href="docs/README.de.md">Deutsch</a> ·
  <a href="docs/README.fr.md">Français</a> ·
  <a href="docs/README.es.md">Español</a> ·
  <a href="docs/README.it.md">Italiano</a> ·
  <a href="docs/README.pt.md">Português</a> ·
  <a href="docs/README.nl.md">Nederlands</a> ·
  <a href="docs/README.pl.md">Polski</a> ·
  <a href="docs/README.ru.md">Русский</a> ·
  <a href="docs/README.el.md">Ελληνικά</a> ·
  <a href="docs/README.vi.md">Tiếng Việt</a> ·
  <a href="docs/README.zh.md">中文 (简体)</a>
</p>

<p align="center"><b>Turn an old Android phone into a desk panel for your computer.</b><br>
PC stats, AI coding sessions, music and touch controls over USB or Wi-Fi.<br>
Windows, Linux and macOS. No root, PhoneBox account or cloud service required.</p>

<p align="center">
  <a href="#my-additions"><b>My additions</b></a> ·
  <a href="#quick-start"><b>Quick start</b></a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows%20%C2%B7%20Linux%20%C2%B7%20macOS-.NET%2010-512bd4" alt="Windows, Linux, macOS; .NET 10">
  <img src="https://img.shields.io/badge/Android-5%2B-3ddc84" alt="Android 5+">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT license"></a>
</p>

<p align="center"><img src="docs/hero-0.6.png" width="900" alt="PhoneBox on three phones showing automation, home and planner pages"><br>
<sub>Example layouts with demo data.</sub></p>

PhoneBox pairs a full-screen **Android app** with a small **desktop agent**. Build your panel in a browser-based
visual editor: **124 widgets**, **30 templates**, multiple pages and a live preview. Connect several phones with
their own layouts, or switch one phone between paired computers.

## My additions

This repository is my own version of PhoneBox. It is based on the open-source PhoneBox project by Ersin Koç
(MIT license, see [LICENSE](LICENSE)), and adds the features below on top of it.

### Support for older Android versions

Phones and tablets with an older Android System WebView (before Chromium 105, for example stock Android 6 and 7
devices) do not understand CSS container queries, so widget cards showed with the wrong sizes, overlapping text or
cut-off content. [cq-compat.js](android/app/src/main/assets/www/cq-compat.js) adds a fallback for these WebViews:

- it rewrites the panel's container-query units (`cqw`, `cqh`, `cqmin`, `cqmax`) and `@container` rules into plain
  CSS that every WebView understands;
- it measures each card with a `ResizeObserver` and keeps its size up to date as pages, widgets and layouts change;
- newer WebViews are left alone and use the native container queries.

Tested on a vivo 1606 (Android 6). To force the fallback on a newer device for testing, open the panel with `?cqcompat`.

### Video streaming for Now playing

Tap the **Now playing** card to watch the video that is playing on the computer, full screen and live, for example a
YouTube tab in the browser. The sound keeps playing on the computer.

- **Turn it on:** in the settings, edit the Now playing widget and enable **Show the video when tapped**.
- **Close it:** tap the video or press Back.
- **Settings on the device (⚙):** resolution (Auto = the device's screen, 720p, 1080p, 1440p or the original size up
  to 4K) and quality (Standard, High or Max, 1×–3× bitrate). Each phone or tablet remembers its own choice.

How it works: the agent captures only the playing window (Windows Graphics Capture), crops it to the page area,
encodes it with the computer's H.264 encoder (Media Foundation) and streams it on `/api/screen`. The phone decodes it
with its hardware decoder (WebCodecs) and draws each frame as soon as it is ready. The Android app reads the stream
natively ([ScreenStream.kt](android/app/src/main/java/net/oxog/phonebox/deskpanel/ScreenStream.kt)), so frames are not
delivered in bursts.

Requirements and limits:

- **Windows only** for now ([ScreenFeed.cs](agent/PhoneBox.Agent/ScreenFeed.cs)).
- The player window must not be minimized; Windows does not draw minimized windows.
- Chromium browsers (Chrome, Edge, Brave) stop drawing a window that is fully covered by another one. Start the
  browser with `--disable-features=CalculateNativeWinOcclusion` to keep it drawing in the background.
- USB gives the smoothest picture. Over Wi-Fi, use **Auto** and **Standard** for the lowest delay.
- One viewer at a time.

### Installing this version

This version has no published releases. Build the agent and the APK from source (see
[development commands](docs/DEVELOPMENT.md#development)) and install them yourself. In the agent's settings, under
**Updates**, turn off **Install new releases automatically**: an update installs the original PhoneBox release, which
does not have these features.

## What you can do

| Use it for | What appears on the phone |
|---|---|
| **PC monitoring** | CPU, GPU, RAM, disks, network and ping; charts, GPU details and process controls |
| **AI coding** | Claude, Codex and Antigravity quotas, reset times and burn-rate forecasts; local Claude Code and Codex sessions; GitHub reviews and CI status |
| **Touch controls** | App and URL shortcuts, key combinations and macros; application audio, microphone, media, OBS and system controls through [Control Deck](docs/CONTROL-DECK.md) |
| **Everyday use** | Now Playing, internet radio, clocks, weather, calendars, notes, checklists, pomodoro and timers |
| **Automation** | Webhook values and notifications, Home Assistant controls and rules that react to activity on the computer |

Tap an AI session for its model, timing and token details. Opt in to Claude Code permission prompts to answer
**Allow** or **Deny** from the phone. Optional [CodexBar integration](docs/GUIDE.md#widgets) adds quota providers
and a cost overview.

<p align="center"><a href="docs/tour-0.6.mp4"><img src="docs/tour-0.6.gif" width="780" alt="PhoneBox tour showing notifications, a Claude Code permission prompt and automatic page switching"></a><br>
<sub>A tour with demo data: notifications, permission prompts and automatic page switching. <a href="docs/tour-0.6.mp4">Watch the MP4</a></sub></p>

## Quick start

You need an **Android 5+ phone** (Android 6 or later recommended) and a supported computer. USB uses a data cable and USB debugging; Wi-Fi pairing
works without either. Release agents include .NET, so you do not need to install the runtime.

### 1. Install the desktop agent

Build the agent from this repository (see [development commands](docs/DEVELOPMENT.md#development)) and run it.
It opens the settings at <http://127.0.0.1:8765>. See [Installing this version](#installing-this-version).

> **Windows:** self-built agents are unsigned, so SmartScreen, Smart App Control or antivirus software may warn or
> block them. See the [installation warnings](docs/SETUP.md#quick-start). USB debugging should be authorized only on
> computers you trust.

The [installation guide](docs/SETUP.md) covers options, dependencies, install paths, troubleshooting and uninstalling.
Its setup scripts ([Windows](setup.ps1) · [Linux/macOS](setup.sh)) install the original PhoneBox release, without the
additions above.

### 2. Connect the phone

| USB | Wi-Fi only |
|---|---|
| Enable **Developer options → USB debugging**, connect a data cable and accept the phone's authorization prompt. Install the APK you built with `adb install -r`. | Install the APK you built on the phone. Wi-Fi is off by default: open the settings page (tray icon, or `http://127.0.0.1:8765`), turn on **General → Wi-Fi & Tailscale → Let the phone connect without the USB cable**, click **Save**, then **Restart to apply** at the top. Choose **Pair a phone over Wi-Fi**, scan the pairing QR code and confirm the matching check code. |
| Wakes and opens the panel on connection; supports app updates over adb. | No adb or developer options needed; install app updates manually unless you later configure USB. |

The phone app is not in any app store; build it from this repository. It runs on Android 5.0 and newer; there is
no iOS version.

For automatic USB/Wi-Fi fallback, enable Wi-Fi and connect the phone over USB once.
[Connection details and Tailscale setup →](docs/SETUP.md#wi-fi--tailscale)

### 3. Make it yours

Open <http://127.0.0.1:8765> (or double-click the Windows tray icon). Under **Pages**, choose a template, drag and
resize widgets, then **Save**. Use **Live preview** to see unsaved edits with live data.
Swipe, tap the page dots or use the phone's volume keys to move between pages.

More on building and installing from source: [installation guide](docs/SETUP.md#other-ways-to-install) and
[development guide](docs/DEVELOPMENT.md).

## Pages & templates

Start with Home, System, AI quotas, Sessions, Music, Planner, Automation or a Control Deck template, then adapt the
12 × 6 grid to your phone. Portrait and landscape layouts are supported. Smart Stack puts up to 12 widgets in one slot.

| Home | AI sessions |
|---|---|
| ![Clock, weather, media and PC metrics](docs/screenshots/home.png) | ![Coding sessions and GitHub activity](docs/screenshots/work.png) |
| **System** | **Layout editor** |
| ![CPU, GPU, memory and network charts](docs/screenshots/system.png) | ![Pages and widget settings in the visual editor](docs/screenshots/settings-editor.png) |

Screenshots use demo data. [Browse the template gallery and widgets](docs/GUIDE.md#pages--templates).
Change fonts, size, spacing, corners and surfaces globally or for one page or widget with the [appearance guide](docs/APPEARANCE.md).

## Platform support

| | Windows | Linux | macOS |
|---|---|---|---|
| Release builds | x64 | x64 / ARM64, glibc | Apple silicon / Intel |
| CPU, RAM, disks, network, processes | ✓ | ✓ | ✓ |
| GPU data | Windows counters; NVIDIA via `nvidia-smi`; extra sensors via [LibreHardwareMonitor](docs/SETUP.md#librehardwaremonitor-on-windows) | NVIDIA / AMD; Intel clock | Apple silicon, when exposed by macOS |
| Media | System media sessions | `playerctl` | `nowplaying-cli`; Spotify / Music fallback |
| Volume | Native | `wpctl` / `pactl` | System output; availability varies by device |
| Agent interface | Tray icon + browser settings | Browser settings | Browser settings |

Widgets depend on the host's hardware, installed tools and permissions. Advanced Control Deck features have
[their own platform requirements](docs/CONTROL-DECK.md). Windows ARM and musl Linux (such as Alpine) do not have
release builds. See the [full compatibility table](docs/SETUP.md#what-works-where).

## Documentation

| Guide | Contents |
|---|---|
| [Installation and connections](docs/SETUP.md) | Setup options, warnings, manual and source installs, USB, Wi-Fi, Tailscale, updates, uninstall and troubleshooting |
| [User guide](docs/GUIDE.md) | Template gallery, widgets, AI integrations, editor, Claude Code prompts, webhooks and automation rules |
| [Appearance](docs/APPEARANCE.md) | Fonts, surfaces, presets, inheritance and live preview |
| [Control Deck](docs/CONTROL-DECK.md) | Audio, media, OBS, shortcuts, tasks, protocols, dependencies and platform limits |
| [Discord widget](docs/DISCORD.md) | Creating the Discord application, connecting it, controls and troubleshooting |
| [Development](docs/DEVELOPMENT.md) | Architecture, local demo, builds, tests, widget sources and isolated agent runs |
| [Contributing](CONTRIBUTING.md) | Contribution scope, review expectations, privacy and required checks |
| [Changelog](CHANGELOG.md) | Release history and unreleased changes |
| [Release checklist](docs/RELEASING.md) | Maintainer build, signing and publication workflow |

The [UMIDIGI BISON extras](extras/umidigi-bison) contain the reversible, no-root tune-up used on the original development phone.

## Security & privacy

The desktop settings server listens on `127.0.0.1:8765`; USB reaches it through `adb reverse`.
Optional Wi-Fi uses HTTPS on port 8766 with a pinned certificate and a paired key. Settings and pairing endpoints
stay local. The phone requests configured actions by ID; commands and integration credentials stay on the computer.

PhoneBox has no telemetry, analytics or accounts of its own. Optional widgets and integrations contact external
services for weather, quotas, GitHub and other data, and the agent checks GitHub for new releases of the original
PhoneBox. The video stream stays between the computer and the phone.
Supported public feeds can fall back through the agent over USB when the phone has no internet.
The [privacy policy](PRIVACY.md) lists outbound connections and how to disable them.
Read the [security details](docs/GUIDE.md#security--privacy). To report a vulnerability, follow [SECURITY.md](SECURITY.md).

## Development

The agent uses **.NET 10**; the Android app uses **Kotlin and a WebView** with plain JavaScript widgets.
For Android builds, install **JDK 17** and **Android SDK Platform 35** (the SDK the app is built with; the app itself
runs on Android 5.0, API 21, and later).
Preview the panel and editor from source with Python 3, using simulated data:

```sh
python tools/demo-server.py
```

Open <http://127.0.0.1:8790/panel/> or <http://127.0.0.1:8790/settings/>.
See [development commands](docs/DEVELOPMENT.md#development) for running the agent, building the APK and testing changes.
Bundled runtime and library notices are in [THIRD-PARTY-NOTICES.txt](agent/PhoneBox.Agent/THIRD-PARTY-NOTICES.txt).

## License

[MIT](LICENSE). PhoneBox was created by Ersin Koç; the additions in this repository are mine. The original copyright
notice in [LICENSE](LICENSE) is kept, as the MIT license requires.
