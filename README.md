# WeaveFXP v1.0.0

WeaveFXP is a self-hosted FXP client with a dark web UI, a headless transfer
engine and a JSON API. It is built for site-to-site racing, manual browsing and
automation from tools on your LAN.

The app runs as one executable. Start `WeaveFXP.exe`, open the web UI, and the
engine runs in the same process.

<img width="2552" height="1274" alt="image" src="https://github.com/user-attachments/assets/e33d9bc4-8c23-4e71-a291-942f81c1a47a" />

<img width="2552" height="1274" alt="image" src="https://github.com/user-attachments/assets/40e7a846-3320-4c74-bc23-4db16cb6ebd9" />

## Download And Start

Choose the release for your machine:

- Windows: `WeaveFXP-v1.0.0-win-x64.zip`
- Linux PC/server: `WeaveFXP-v1.0.0-linux-x64.zip`
- Linux ARM64: `WeaveFXP-v1.0.0-linux-arm64.zip`

1. Download the release for your platform.
2. Extract the executable into its own folder, for example `D:\WeaveFXP`.
3. Start `WeaveFXP.exe` on Windows or `WeaveFXP` on Linux.
4. Open the web UI:

```text
http://127.0.0.1:8788
```

On first start WeaveFXP creates a `data` folder next to the executable. That
folder contains settings, sites, queue history, release checks and logs.

On Linux, make the binary executable:

```bash
chmod +x WeaveFXP
./WeaveFXP --no-browser --urls http://0.0.0.0:8788
```

## LAN And API Access

The web UI and API use the same listener. For LAN access, bind to all interfaces:

```bat
WeaveFXP.exe --urls http://0.0.0.0:8788
```

Then connect from another computer with:

```text
http://<this-pc-ip>:8788
```

Set the API password in Settings. API calls use:

```text
X-WeaveFXP-API-Key: <password>
```

The API documentation page inside the app contains ready-to-use examples for
health, sites, browsing, FXP, spread/race jobs, downloads, logs, history and
maintenance.

## Features

- Dashboard with engine status, queue pressure, throughput and recent activity.
- Multi-tab FTP browser with remote and local browsing.
- Right-click action shell for browser rows, queue rows and logs.
- Site manager with sections, groups/affils, completion markers, skiplist and
  order list settings.
- Site-to-site FXP with PASV/EPSV, PRET, SSCN/CPSV, TLS data handling and XDUPE.
- Race jobs with target-aware duplicate skipping before transfer start.
- SFV-based completion checks, with configurable marker fallback per site.
- Queue actions: start, pause, stop, cancel, retry, restart and remove.
- History pages for transfer jobs and release checks.
- System/API/FTP/transfer logs.
- Windows tray icon with status, notifications and stop/quit actions.
- Plain JSON API for integrating external race managers and scripts.

## Build From Source

Requirements:

- .NET 8 SDK

Run from the repository root:

```powershell
dotnet run --project WeaveFxp.Web
```

The development server listens on `http://127.0.0.1:8788` unless overridden with
`--urls` or `WEAVEFXP_URL`.

## Publish Releases

Windows:

```bat
publish.bat
publish.bat win-x64
publish.bat win-x64 linux-x64 linux-arm64
```

Linux/macOS shell:

```bash
./publish.sh
./publish.sh linux-x64 linux-arm64 win-x64
```

The publish scripts create:

```text
Release\win-x64\WeaveFXP.exe
Release\linux-x64\WeaveFXP
Release\linux-arm64\WeaveFXP
Release\zips\WeaveFXP-v1.0.0-win-x64.zip
Release\zips\WeaveFXP-v1.0.0-linux-x64.zip
Release\zips\WeaveFXP-v1.0.0-linux-arm64.zip
```

Ship the zip for the target platform. Runtime `data` is not included in release
zips and is created on first start.

## Data Folder

Default:

```text
data\
```

Override by setting:

```bat
set WEAVEFXP_STATE=D:\WeaveFXP\data\state.json
```

## Changelog

### v1.0.0

- Initial C#/.NET 8 Blazor Server release.
- Added single-exe publishing for Windows, Linux x64 and Linux ARM64.
- Added dashboard, browser, sites, races, history, logs, settings, API docs and
  changelog pages.
- Added site sections, groups/affils, skiplist, order list and completion marker
  configuration.
- Added FTP/FXP support for PASV/EPSV, PRET, SSCN/CPSV, TLS data channels,
  CP437 control text and XDUPE.
- Added race queue actions, throughput tracking, progress rows and pre-transfer
  duplicate skipping.
- Added Windows tray icon, notifications and app stop/quit actions.
