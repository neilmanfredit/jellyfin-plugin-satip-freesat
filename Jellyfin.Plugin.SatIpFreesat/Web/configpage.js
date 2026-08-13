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

    const DISEQC_PORTS = ['', 'A', 'B', 'C', 'D'];

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
            const diseqc = live.diSEqCPort ?? saved.diSEqCPort ?? '';
            const satLabel = live.satelliteLabel ?? saved.satelliteLabel ?? 'Astra 2 (28.2°E)';
            const diseqcOpts = DISEQC_PORTS.map(p =>
                `<option value="${escHtml(p)}"${p === diseqc ? ' selected' : ''}>${escHtml(p || '— none —')}</option>`
            ).join('');
            const row = document.createElement('tr');
            row.innerHTML =
                `<td>Tuner ${i + 1}</td>` +
                `<td><input type="number" class="emby-input tuner-port" min="1" max="65535" value="${escHtml(port)}" style="width:80px"></td>` +
                `<td><input type="number" class="emby-input tuner-frontend" min="1" max="32" value="${escHtml(fe)}" style="width:70px"></td>` +
                `<td><select class="emby-select tuner-diseqc" style="min-width:80px">${diseqcOpts}</select></td>` +
                `<td><input type="text" class="emby-input tuner-satellite" value="${escHtml(satLabel)}" placeholder="Astra 2 (28.2°E)" style="min-width:130px"></td>`;
            tbody.appendChild(row);
        }
    }

    function collectTuners() {
        const ports = view.querySelectorAll('.tuner-port');
        const frontends = view.querySelectorAll('.tuner-frontend');
        const diseqcs = view.querySelectorAll('.tuner-diseqc');
        const satellites = view.querySelectorAll('.tuner-satellite');
        const tuners = [];
        for (let i = 0; i < ports.length; i++) {
            tuners.push({
                rtspPort: parseInt(ports[i].value, 10) || 554,
                frontendNumber: parseInt(frontends[i].value, 10) || (i + 1),
                diSEqCPort: diseqcs[i]?.value ?? '',
                satelliteLabel: satellites[i]?.value?.trim() || 'Astra 2 (28.2°E)',
            });
        }
        return tuners;
    }

    // ── regions (static — no API call needed) ─────────────────────────────

    const REGIONS = [
        { key: 'anglia',      label: 'East of England (Anglia)' },
        { key: 'border',      label: 'Border (Cumbria & South Scotland)' },
        { key: 'central',     label: 'Central England (Midlands)' },
        { key: 'channel',     label: 'Channel Islands' },
        { key: 'granada',     label: 'North West England (Granada)' },
        { key: 'london',      label: 'London' },
        { key: 'meridian',    label: 'South & South East England (Meridian)' },
        { key: 'stv_central', label: 'Central Scotland (STV Central)' },
        { key: 'stv_north',   label: 'North Scotland (STV North / Grampian)' },
        { key: 'tynetees',    label: 'North East England (Tyne Tees)' },
        { key: 'utv',         label: 'Northern Ireland (UTV)' },
        { key: 'wales',       label: 'Wales' },
        { key: 'west',        label: 'West of England (Bristol/Bath)' },
        { key: 'westcountry', label: 'South West England (West Country)' },
        { key: 'yorkshire',   label: 'Yorkshire' },
    ];

    const POSTCODE_AREAS = {
        CA:'border', DG:'border', TD:'border',
        B:'central', CV:'central', DY:'central', WS:'central', WV:'central',
        TF:'central', SY:'central', LE:'central', NN:'central', DE:'central',
        NG:'central', LN:'central', WR:'central', OX:'central',
        GY:'channel', JE:'channel',
        M:'granada', SK:'granada', OL:'granada', BL:'granada', WN:'granada',
        WA:'granada', L:'granada', CH:'granada', CW:'granada', PR:'granada',
        BB:'granada', FY:'granada', LA:'granada', ST:'granada', IM:'granada',
        E:'london', EC:'london', N:'london', NW:'london', SE:'london',
        SW:'london', W:'london', WC:'london', BR:'london', CR:'london',
        DA:'london', EN:'london', HA:'london', IG:'london', KT:'london',
        RM:'london', SM:'london', TW:'london', UB:'london', WD:'london',
        AL:'london', HP:'london',
        SO:'meridian', PO:'meridian', GU:'meridian', RH:'meridian',
        BN:'meridian', TN:'meridian', ME:'meridian', CT:'meridian',
        SP:'meridian', BH:'meridian', RG:'meridian', SL:'meridian',
        IP:'anglia', NR:'anglia', CO:'anglia', CM:'anglia', SS:'anglia',
        CB:'anglia', PE:'anglia', SG:'anglia', LU:'anglia', MK:'anglia',
        BT:'utv',
        G:'stv_central', ML:'stv_central', PA:'stv_central', KA:'stv_central',
        FK:'stv_central', KY:'stv_central', EH:'stv_central',
        AB:'stv_north', IV:'stv_north', PH:'stv_north', DD:'stv_north',
        KW:'stv_north', ZE:'stv_north', HS:'stv_north',
        NE:'tynetees', SR:'tynetees', DH:'tynetees', DL:'tynetees', TS:'tynetees',
        CF:'wales', SA:'wales', NP:'wales', LD:'wales', LL:'wales',
        BS:'west', BA:'west', GL:'west', HR:'west', SN:'west',
        EX:'westcountry', PL:'westcountry', TQ:'westcountry',
        TR:'westcountry', TA:'westcountry', DT:'westcountry',
        LS:'yorkshire', BD:'yorkshire', HD:'yorkshire', HX:'yorkshire',
        WF:'yorkshire', YO:'yorkshire', HU:'yorkshire', DN:'yorkshire',
        S:'yorkshire', HG:'yorkshire',
    };

    function postcodeToRegionKey(postcode) {
        let pc = postcode.trim().toUpperCase().replace(/\s+/g, '');
        // Strip inward code: trailing digit + 2 letters (e.g. "1AA") → outward code
        if (pc.length > 3 && /\d[A-Z]{2}$/.test(pc)) pc = pc.slice(0, -3);
        // Extract leading letters only → area code
        const area = (pc.match(/^[A-Z]+/) || [''])[0];
        return POSTCODE_AREAS[area] || null;
    }

    function loadRegions(selectEl, currentKey) {
        for (const r of REGIONS) {
            const opt = document.createElement('option');
            opt.value = r.key;
            opt.textContent = r.label;
            selectEl.appendChild(opt);
        }
        if (currentKey) selectEl.value = currentKey;
    }

    function resolvePostcode(postcode, selectEl) {
        if (!postcode) return;
        const key = postcodeToRegionKey(postcode);
        if (key) {
            selectEl.value = key;
            const label = (REGIONS.find(r => r.key === key) || {}).label || key;
            Dashboard.alert('Region resolved to: ' + label);
        } else {
            Dashboard.alert('Could not identify a Freesat region from that postcode — please select manually.');
        }
    }

    // ── scan progress polling ──────────────────────────────────────────────

    let _scanPoller = null;

    function startPolling() {
        if (_scanPoller) return;
        pollScanProgress(); // immediate first tick
        _scanPoller = setInterval(pollScanProgress, 2000);
    }

    function stopPolling() {
        if (_scanPoller) { clearInterval(_scanPoller); _scanPoller = null; }
    }

    async function pollScanProgress() {
        const statusEl = view.querySelector('#scanStatus');
        const progressEl = view.querySelector('#scanProgress');
        const btn = view.querySelector('#btnScan');
        try {
            const data = await apiGet('SatIpFreesat/scan/progress');
            statusEl.textContent = 'Status: ' + (data.message || '—');
            const scanning = data.state === 'scanning';
            btn.disabled = scanning;
            if (scanning) {
                progressEl.style.display = '';
                if (data.percent == null) {
                    progressEl.removeAttribute('value'); // indeterminate pulse
                } else {
                    progressEl.value = data.percent;
                }
            } else {
                if (data.state === 'done') progressEl.value = 100;
                progressEl.style.display = data.state === 'idle' ? 'none' : '';
                stopPolling();
            }
        } catch { /* ignore transient errors; keep polling */ }
    }

    async function refreshScanStatus() {
        const el = view.querySelector('#scanStatus');
        try {
            const data = await apiGet('SatIpFreesat/scan/progress');
            el.textContent = 'Status: ' + (data.message || '—');
            if (data.state === 'scanning') {
                view.querySelector('#btnScan').disabled = true;
                startPolling();
            }
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

        view.querySelector('#scanStatus').textContent = 'Status: starting scan…';

        try {
            await apiPost('SatIpFreesat/scan', {
                serverAddress,
                tuners: collectTuners(),
                regionKey,
            });
            startPolling();
        } catch (err) {
            if (err && err.status === 409) {
                // Another scan is already running — just start polling its progress
                startPolling();
            } else {
                view.querySelector('#scanStatus').textContent = 'Status: failed to start scan — see Jellyfin logs';
                Dashboard.alert('Failed to start scan. Check Jellyfin logs for details.');
            }
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
        loadRegions(regionSelect, cfg.regionKey);
        loadForm(cfg);
        await refreshScanStatus();
    }

    // Rebuild tuner table when count spinner changes
    view.querySelector('#tunerCount').addEventListener('change', function () {
        const n = Math.max(1, Math.min(16, parseInt(this.value, 10) || 1));
        this.value = n;
        buildTunerTable(n, null);
    });

    view.querySelector('#btnResolvePostcode').addEventListener('click', function () {
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
