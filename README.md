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

## Installation

1. Build the plugin or download a release zip
2. Copy the DLL into Jellyfin's plugin directory (`/config/plugins/satip-freesat/`)
3. Restart Jellyfin
4. Go to **Dashboard → Plugins → SAT>IP Freesat** to configure

## Configuration

### Setup wizard

1. Enter the SAT>IP server's **IP address** (e.g. `192.168.1.100`) and RTSP port (default `554`)
2. Enter your **UK postcode** and click **Look up region** — this resolves to the correct Freesat regional bouquet (e.g. `SK6` → North West England / Granada)
3. Confirm or manually select the **region** from the dropdown
4. Click **Scan for channels** — this tunes the bootstrap mux (11425H), reads the NIT to discover all other muxes, scans each for services (SDT), then reads the BAT for bouquet/LCN data
5. Click **Save**

### What the scan does (detailed)

| Step | Action | Time |
|------|--------|------|
| 1 | Tune 11425 MHz H (bootstrap) | instant |
| 2 | Read NIT → discover all 28.2E muxes | ~30 s |
| 3 | Read BAT → discover Freesat and BSkyB bouquets + LCNs | included in step 2 |
| 4 | Tune each mux, read SDT → discover all services | ~15 s × N muxes |
| 5 | Apply bouquet filter and LCN mapping → channel list | instant |

A full scan of all Freesat muxes takes roughly **1–5 minutes** depending on signal strength and how many muxes are decodable. The plugin limits the scan to the 30 strongest/closest muxes.

### ITV1 regional variant

All ITV1 regional feeds broadcast as **"ITV1 HD"** on the satellite with no way to distinguish them by name alone — they differ only by service ID and transponder. The plugin picks the first instance it sees and assigns it to channel 103. If this turns out to be the wrong region (wrong local news / idents), trigger a rescan and check which service ID ends up at 103, then identify the correct one by playing briefly during regional news.

## Live TV

After a successful scan, go to **Dashboard → Live TV** and add a tuner host:
- Type: `SAT>IP Freesat`

Add a listings provider:
- Type: `SAT>IP Freesat EPG`

Jellyfin will call the plugin to get the channel list and stream URLs. Streams are served as `rtsp://` URLs directly from your SAT>IP device; Jellyfin's built-in ffmpeg handles decoding and optional transcoding.

## EPG

The plugin reads **DVB EIT** (Event Information Table) data from the MPEG-TS stream for each channel. Freesat also broadcasts an OpenTV-format EPG (8-day schedule on dedicated PIDs); OpenTV parsing is not implemented in this release — DVB EIT gives present/following and a partial schedule that is typically good for 24–48 hours.

## Licence

Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International (CC BY-NC-ND 4.0).
See [LICENSE](LICENSE).
