# jellyfin-plugin-satip-freesat

A Jellyfin plugin that connects directly to a SAT>IP server for UK Freesat reception.
No tvheadend, no external EPG grabber, no XMLTV feed — everything comes from the satellite.

## What it does

- Implements Jellyfin's `ITunerHost` interface to expose a SAT>IP device as a live TV tuner
- Scans DVB-S SI tables (NIT/SDT/BAT) from the MPEG-TS stream to build the full Freesat channel list
- Selects the correct regional bouquet (BBC One, ITV1, local news) based on a UK postcode
- Assigns proper Freesat LCNs (channel numbers) from live BAT data, with a curated fallback table
- Excludes BSkyB bouquets entirely — Freesat and Sky share the same orbital position (28.2E) and muxes; this plugin only surfaces Freesat-tagged content
- Provides EPG data via `IListingsProvider` by reading DVB EIT tables directly from the stream
- Exposes an RTSP stream URL per channel; Jellyfin/ffmpeg connects to the SAT>IP device and handles decoding, transcoding, and recording

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

Pointed at **Astra 2 at 28.2°E** (the UK Freesat/Sky satellite cluster). A standard Sky minidish with a quad or octo LNB works. Signal quality should be good enough to receive the bootstrap transponder (11425 MHz H, 27500 kSym/s DVB-S).

## Installation via plugin repository (recommended)

1. Open Jellyfin **Dashboard → Plugins → Repositories**
2. Click **+** and paste the repository URL:
   ```
   https://neilmanfredit.github.io/jellyfin-plugin-satip-freesat/manifest.json
   ```
3. Click **Save**, then go to **Catalogue**
4. Search for **SAT>IP Freesat** and click **Install**
5. Restart Jellyfin when prompted

Jellyfin will automatically select the version that matches your server:

| Plugin version | Jellyfin version | .NET |
|---|---|---|
| 2.x | 12.x | net10.0 |
| 1.x | 10.11.x | net9.0 |

### Manual installation

1. Download the appropriate zip from the [Releases](https://github.com/neilmanfredit/jellyfin-plugin-satip-freesat/releases) page
2. Unzip the DLL into Jellyfin's plugin directory (e.g. `/config/plugins/satip-freesat/`)
3. Restart Jellyfin

## Configuration

### Setup wizard

1. Go to **Dashboard → Plugins → SAT>IP Freesat** (Configuration tab)
2. Enter the SAT>IP server's **IP address** (e.g. `192.168.1.100`) and RTSP port (default `554`)
3. Enter your **UK postcode** and click **Look up region** — this resolves to the correct Freesat regional bouquet (e.g. `SK6` → North West England / Granada)
4. Confirm or manually select the **region** from the dropdown
5. Click **Scan for channels** — this tunes the bootstrap mux (11425H), reads the NIT to discover all other muxes, scans each for services (SDT), then reads the BAT for bouquet/LCN data
6. Click **Save**

### What the scan does

| Step | Action |
|---|---|
| 1 | Tune 11425 MHz H (bootstrap transponder) |
| 2 | Read NIT → discover all 28.2°E muxes |
| 3 | Read BAT → discover Freesat bouquets + LCNs (runs alongside step 2) |
| 4 | Tune each mux, read SDT → discover all services |
| 5 | Apply bouquet filter and LCN mapping → build channel list |

A full scan of all Freesat muxes takes roughly **1–5 minutes** depending on signal strength and how many muxes are decodable. The plugin limits the scan to 30 muxes.

### Additional settings

The configuration page also exposes:

- **Streaming and sharing** — share one RTSP session between viewers of the same channel, RTP receive buffer size, stall watchdog timeout
- **Subtitles and video** — expose DVB subtitle/teletext streams to Jellyfin, preferred subtitle language (ISO 639-2), force deinterlace (experimental)
- **Channel maintenance** — rebuild the channel store without a full rescan

### Status page

Go to **Dashboard → Plugins → SAT>IP Freesat Status** to see:

- Live device reachability (TCP probe with round-trip time)
- Last scan summary (channels found, muxes scanned, region, timestamp)
- First 10 channels in LCN order with frequency and HD/SD/Radio type
- Channel maintenance actions

### ITV1 regional variant

All ITV1 regional feeds broadcast as **"ITV1 HD"** on the satellite with no way to distinguish them by name alone — they differ only by service ID and transponder. The plugin picks the first instance it sees and assigns it to channel 103. If this turns out to be the wrong region, trigger a rescan; if the problem persists, it indicates the correct regional feed is on a different mux.

## Live TV

After a successful scan, go to **Dashboard → Live TV** and add:

1. A **tuner host** — Type: `SAT>IP Freesat`
2. A **listings provider** — Type: `SAT>IP Freesat EPG`

Jellyfin will call the plugin to get the channel list and stream URLs. Streams are served as `rtsp://` URLs directly from your SAT>IP device; Jellyfin's built-in ffmpeg handles decoding and optional transcoding.

## EPG

The plugin reads **DVB EIT** (Event Information Table) data from the MPEG-TS stream for each channel. DVB EIT gives present/following and a partial schedule typically good for 24–48 hours.

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
