/* eslint-env browser */
/* global ApiClient, Dashboard */
'use strict';

const PLUGIN_ID = '9d4f3a1c-8c7e-4b5d-a6f0-2e9b1c8d7a3f';

export default function (view) {
    // ── helpers ────────────────────────────────────────────────────────────

    function apiGet(path) {
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(path), dataType: 'json' });
    }

    function apiGetQ(path, params) {
        return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(path, params), dataType: 'json' });
    }

    function apiPost(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json',
        });
    }

    function intVal(el, fallback, min, max) {
        const v = parseInt(el.value, 10);
        return Number.isFinite(v) ? Math.max(min, Math.min(max, v)) : fallback;
    }

    function escHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // ── tuner table ────────────────────────────────────────────────────────

    function buildTunerTable(count, existing) {
        const tbody = view.querySelector('#tunerTableBody');
        // Preserve current values from any existing rows before rebuilding
        const current = collectTuners();
        tbody.innerHTML = '';
        for (let i = 0; i < count; i++) {
            // Priority: current row values → saved config → sensible defaults
            const saved = Array.isArray(existing) ? (existing[i] || {}) : {};
            const live = current[i] || {};
            const port = live.rtspPort || saved.rtspPort || 554;
            const fe = live.frontendNumber || saved.frontendNumber || (i + 1);
            const row = document.createElement('tr');
            row.innerHTML =
                `<td>Tuner ${i + 1}</td>` +
                `<td><input type="number" class="emby-input tuner-port" min="1" max="65535" value="${escHtml(port)}" style="width:80px"></td>` +
                `<td><input type="number" class="emby-input tuner-frontend" min="1" max="32" value="${escHtml(fe)}" style="width:70px"></td>`;
            tbody.appendChild(row);
        }
    }

    function collectTuners() {
        const ports = view.querySelectorAll('.tuner-port');
        const frontends = view.querySelectorAll('.tuner-frontend');
        const tuners = [];
        for (let i = 0; i < ports.length; i++) {
            tuners.push({
                rtspPort: parseInt(ports[i].value, 10) || 554,
                frontendNumber: parseInt(frontends[i].value, 10) || (i + 1),
            });
        }
        return tuners;
    }

    // ── regions ────────────────────────────────────────────────────────────

    async function loadRegions(selectEl, currentKey) {
        try {
            const regions = await apiGet('SatIpFreesat/regions');
            for (const r of (Array.isArray(regions) ? regions : [])) {
                const opt = document.createElement('option');
                opt.value = r.key;
                opt.textContent = r.label;
                if (r.key === currentKey) opt.selected = true;
                selectEl.appendChild(opt);
            }
        } catch (e) {
            console.error('SAT>IP Freesat: could not load regions', e);
        }
    }

    async function resolvePostcode(postcode, selectEl) {
        if (!postcode) return;
        try {
            const data = await apiGetQ('SatIpFreesat/resolve-region', { postcode });
            for (const opt of selectEl.options) {
                if (opt.value === data.regionKey) {
                    selectEl.value = data.regionKey;
                    Dashboard.alert('Region resolved to: ' + data.regionLabel);
                    return;
                }
            }
            Dashboard.alert('Region "' + data.regionKey + '" not found in list — please select manually.');
        } catch {
            Dashboard.alert('Could not resolve postcode. Check the postcode and try again.');
        }
    }

    // ── scan status ────────────────────────────────────────────────────────

    async function refreshScanStatus() {
        const el = view.querySelector('#scanStatus');
        try {
            const data = await apiGet('SatIpFreesat/status');
            el.textContent = 'Status: ' + (data.message || '—');
        } catch {
            el.textContent = 'Status: unable to reach plugin API';
        }
    }

    // ── scan ───────────────────────────────────────────────────────────────

    async function triggerScan() {
        const serverAddress = view.querySelector('#serverAddress').value.trim();
        const regionKey = view.querySelector('#regionSelect').value;
        if (!serverAddress) { Dashboard.alert('Enter a SAT>IP server address first.'); return; }
        if (!regionKey) { Dashboard.alert('Select a region first.'); return; }

        const spinner = view.querySelector('#scanSpinner');
        const statusEl = view.querySelector('#scanStatus');
        const btn = view.querySelector('#btnScan');
        spinner.style.display = '';
        btn.disabled = true;
        statusEl.textContent = 'Status: scanning…';

        try {
            const data = await apiPost('SatIpFreesat/scan', {
                serverAddress,
                tuners: collectTuners(),
                regionKey,
            });
            statusEl.textContent = 'Status: ' + (data.message || 'complete');
            Dashboard.alert(data.message || 'Scan complete.');
        } catch {
            statusEl.textContent = 'Status: scan failed — see Jellyfin logs for details';
            Dashboard.alert('Scan failed. Check Jellyfin logs for details.');
        } finally {
            spinner.style.display = 'none';
            btn.disabled = false;
        }
    }

    // ── rebuild ────────────────────────────────────────────────────────────

    async function rebuildChannels() {
        const resultEl = view.querySelector('#rebuildResult');
        resultEl.textContent = 'Clearing channel store…';
        try {
            await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('SatIpFreesat/rebuild-channels') });
            resultEl.textContent = 'Channel store cleared. Trigger a new scan to repopulate.';
        } catch {
            resultEl.textContent = 'Failed to clear channel store — see Jellyfin logs.';
        }
    }

    // ── load / save ────────────────────────────────────────────────────────

    function loadForm(cfg) {
        view.querySelector('#serverAddress').value = cfg.serverAddress || '';
        view.querySelector('#postcode').value = cfg.postcode || '';
        view.querySelector('#autoScanIntervalHours').value = cfg.autoScanIntervalHours ?? 24;
        view.querySelector('#enableStreamSharing').checked = cfg.enableStreamSharing !== false;
        view.querySelector('#rtpReceiveBufferKiB').value = cfg.rtpReceiveBufferKiB || 512;
        view.querySelector('#packetTimeoutSeconds').value = cfg.packetTimeoutSeconds ?? 10;
        view.querySelector('#exposeSubtitleStreams').checked = cfg.exposeSubtitleStreams === true;
        view.querySelector('#preferredSubtitleLanguage').value = cfg.preferredSubtitleLanguage || 'eng';
        view.querySelector('#forceDeinterlace').checked = cfg.forceDeinterlace === true;

        const tuners = Array.isArray(cfg.tuners) && cfg.tuners.length > 0 ? cfg.tuners : [{ rtspPort: 554, frontendNumber: 1 }];
        const countEl = view.querySelector('#tunerCount');
        countEl.value = tuners.length;
        buildTunerTable(tuners.length, tuners);
    }

    function collectForm(existing) {
        const regionSelect = view.querySelector('#regionSelect');
        return Object.assign({}, existing, {
            serverAddress: view.querySelector('#serverAddress').value.trim(),
            tuners: collectTuners(),
            postcode: view.querySelector('#postcode').value.trim(),
            regionKey: regionSelect.value,
            regionLabel: regionSelect.options[regionSelect.selectedIndex]?.text || '',
            autoScanIntervalHours: intVal(view.querySelector('#autoScanIntervalHours'), 24, 0, 168),
            enableStreamSharing: view.querySelector('#enableStreamSharing').checked,
            rtpReceiveBufferKiB: intVal(view.querySelector('#rtpReceiveBufferKiB'), 512, 64, 8192),
            packetTimeoutSeconds: intVal(view.querySelector('#packetTimeoutSeconds'), 10, 0, 120),
            exposeSubtitleStreams: view.querySelector('#exposeSubtitleStreams').checked,
            preferredSubtitleLanguage: view.querySelector('#preferredSubtitleLanguage').value.trim() || 'eng',
            forceDeinterlace: view.querySelector('#forceDeinterlace').checked,
        });
    }

    // ── page lifecycle ─────────────────────────────────────────────────────

    let cfg = {};
    let loaded = false;

    async function onViewShow() {
        if (loaded) { await refreshScanStatus(); return; }
        loaded = true;

        try { cfg = await ApiClient.getPluginConfiguration(PLUGIN_ID); } catch { cfg = {}; }

        const regionSelect = view.querySelector('#regionSelect');
        while (regionSelect.options.length > 1) regionSelect.remove(1);
        await loadRegions(regionSelect, cfg.regionKey);
        loadForm(cfg);
        await refreshScanStatus();
    }

    // Rebuild tuner table when count spinner changes
    view.querySelector('#tunerCount').addEventListener('change', function () {
        const n = Math.max(1, Math.min(16, parseInt(this.value, 10) || 1));
        this.value = n;
        buildTunerTable(n, null);
    });

    view.querySelector('#btnResolvePostcode').addEventListener('click', () => {
        resolvePostcode(view.querySelector('#postcode').value.trim(), view.querySelector('#regionSelect'));
    });

    view.querySelector('#btnScan').addEventListener('click', triggerScan);

    view.querySelector('#btnRebuildChannels').addEventListener('click', rebuildChannels);

    view.querySelector('#SatIpFreesatConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        const updated = collectForm(cfg);
        ApiClient.updatePluginConfiguration(PLUGIN_ID, updated)
            .then(() => {
                cfg = updated;
                Dashboard.processPluginConfigurationUpdateResult();
            })
            .catch(() => Dashboard.alert('Failed to save settings. Check the Jellyfin logs.'));
    });

    view.addEventListener('viewshow', onViewShow);
}
