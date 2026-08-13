# jellyfin-plugin-satip-freesat

A Jellyfin plugin that connects directly to a SAT>IP server for UK Freesat reception.
No tvheadend, no external EPG grabber, no XMLTV feed — everything comes from the satellite.

## What it does

- Implements Jellyfin's `ITunerHost` interface to expose a SAT>IP device as a live TV tuner
- Scans DVB-S SI tables (NIT/SDT/BAT) from the MPEG-TS stream to build the full Freesat channel list
- Selects the correct regional bouquet (BBC One, ITV1, local news) based on a UK postcode
- Assigns proper Freesat LCNs (channel numbers) from live BAT data, with a curated fallback table
- Excludes BSkyB bouquets entirely — Freesat and Sky share the same orbital position (28.2°E) and muxes; this plugin only surfaces Freesat-tagged content
- Provides EPG data via `IListingsProvider` by reading DVB EIT tables directly from the stream
- Exposes an RTSP stream URL per channel; Jellyfin/ffmpeg connects to the SAT>IP device and handles decoding, transcoding, and recording

## Screenshots

### Configuration page

![Configuration page](docs/screenshots/config-page.png)

The configuration page groups settings into collapsible sections: SAT>IP device (server address and per-tuner table), UK region (postcode lookup and region select), Channel scan (scan button and progress bar), Streaming and sharing, Subtitles and video, and Channel maintenance.

### Scan in progress

![Scan progress bar](docs/screenshots/scan-progress.png)

During a scan the progress bar shows an indeterminate pulse while reading the NIT/BAT tables from the bootstrap mux, then switches to a 0–100% bar as each SDT mux is scanned in turn. The current mux frequency and polarisation are shown in the status line.

### Status page

![Status page](docs/screenshots/status-page.png)

The status page shows live device reachability, last scan results, and the first 10 channels in LCN order. It polls automatically every 10 seconds (2 seconds while a scan is running).

## Requirements

### SAT>IP server

Any SAT>IP-compliant device with a DVB-S/S2 tuner pointed at Astra 2 (28.2°E):

- Geniatech T230C
- TBS 5530 / 6522
- Inverto iLNB
- Telestar digiHD TS 15 plus
- Any other DVB-S2 SAT>IP server

The device must be on the same network as your Jellyfin server and accessible via RTSP (default port 554).

### Satellite dish

Pointed at **Astra 2 at 28.2°E** (the UK Freesat/Sky satellite cluster). A standard Sky minidish with a quad or octo LNB works. Signal quality must be sufficient to receive the bootstrap transponder (11425 MHz H, 27500 kSym/s DVB-S).

## Installation via plugin repository (recommended)

1. Open Jellyfin **Dashboard → Plugins → Repositories**
2. Click **+** and paste the repository URL:
   ```
   https://neilmanfredit.github.io/jellyfin-plugin-satip-freesat/manifest.json
   ```
3. Click **Save**, then go to **Catalogue**
4. Search for **SAT>IP Freesat** and click **Install**
5. Restart Jellyfin when prompted

Jellyfin picks the correct zip automatically based on your server version:

| Zip file | Jellyfin | .NET |
|---|---|---|
| `jellyfin-plugin-satip-freesat-vX.Y.Z-jellyfin10.zip` | 10.9+ | net9.0 |
| `jellyfin-plugin-satip-freesat-vX.Y.Z-jellyfin12.zip` | 12.x | net10.0 |

### Manual installation

1. Download the appropriate zip from the [Releases](https://github.com/neilmanfredit/jellyfin-plugin-satip-freesat/releases) page
2. Unzip the DLL into Jellyfin's plugin directory (e.g. `/config/plugins/satip-freesat/`)
3. Restart Jellyfin

## Configuration

### Setup

1. Go to **Dashboard → Plugins → SAT>IP Freesat**
2. Under **SAT>IP device**, enter the server's **IP address** (e.g. `192.168.1.100`)
3. Set the **number of tuners** — this rebuilds the tuner table below it, one row per tuner. For each tuner set the RTSP port (default `554`), the `src=` number (the SAT>IP frontend index), and optionally a DiSEqC port label and satellite name if you have a multi-switch setup
4. Under **UK region**, enter your **postcode** and click **Look up region** — this resolves to the correct Freesat regional bouquet (e.g. `SK6` → North West England / Granada). Confirm or adjust the dropdown
5. Under **Channel scan**, click **Scan for channels** and watch the progress bar. A full scan typically takes **1–5 minutes**
6. Click **Save settings**

### What the scan does

| Step | Action |
|---|---|
| 1 | Tune 11425 MHz H (bootstrap transponder) |
| 2 | Read NIT → discover all 28.2°E muxes |
| 3 | Read BAT → discover Freesat bouquets + LCNs (runs alongside step 2) |
| 4 | Tune up to 30 muxes in turn, read SDT → discover all services |
| 5 | Apply bouquet filter and LCN mapping → build channel list |

### Additional settings

| Section | Settings |
|---|---|
| **Streaming and sharing** | Share one RTSP session between viewers of the same channel · RTP receive buffer size · Stall watchdog timeout |
| **Subtitles and video** | Expose DVB subtitle/teletext streams to Jellyfin · Preferred subtitle language (ISO 639-2) · Force deinterlace (experimental) |
| **Channel maintenance** | Clear the cached channel store so the next scan fully replaces it |

### Auto-rescan

Set **Auto-rescan interval (hours)** in the Channel scan section. Set to `0` to disable automatic rescanning (manual only). A rescan is lightweight if the channel list is stable — it replaces the stored list only when a new scan completes successfully.

### Status page

Go to **Dashboard → Plugins → SAT>IP Freesat Status** to see:

- Plugin version and server address
- Live device reachability (TCP probe with round-trip time)
- Per-tuner summary (src=, DiSEqC port, satellite, RTSP port)
- Last scan summary (channels found, muxes scanned, region, timestamp)
- First 10 channels in LCN order with frequency and HD/SD/Radio badge

The page refreshes automatically every 10 seconds, or every 2 seconds while a scan is running.

### ITV1 regional variant

All ITV1 regional feeds broadcast as **"ITV1 HD"** on the satellite — they differ only by service ID and transponder, not by name. The plugin picks the instance matched by the BAT/LCN data for your selected region and assigns it to channel 103. If the wrong region is showing, re-run a scan after confirming the correct region is selected.

## Live TV

After a successful scan, go to **Dashboard → Live TV** and add:

1. A **tuner host** — type: `SAT>IP Freesat`
2. A **listings provider** — type: `SAT>IP Freesat EPG`

Jellyfin calls the plugin for the channel list and stream URLs. Each stream is an `rtsp://` URL pointing directly at your SAT>IP device; Jellyfin's built-in ffmpeg handles decoding and optional transcoding.

## EPG

The plugin reads **DVB EIT** (Event Information Table) data from the MPEG-TS stream for each channel. DVB EIT provides present/following events and a partial schedule typically covering 24–48 hours.

Freesat also broadcasts an OpenTV-format EPG (8-day schedule on dedicated PIDs); OpenTV parsing is not implemented in this release.

## Plugin repository

| Property | Value |
|---|---|
| Repository URL | `https://neilmanfredit.github.io/jellyfin-plugin-satip-freesat/manifest.json` |
| Repository page | https://neilmanfredit.github.io/jellyfin-plugin-satip-freesat/ |
| Releases | https://github.com/neilmanfredit/jellyfin-plugin-satip-freesat/releases |

## Licence

Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International (CC BY-NC-ND 4.0).
See [LICENSE](LICENSE).
