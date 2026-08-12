/* eslint-env browser */
/* global ApiClient, Dashboard */
'use strict';

const PLUGIN_ID = '9d4f3a1c-8c7e-4b5d-a6f0-2e9b1c8d7a3f';

export default function (view) {
    function getConfig() {
        return ApiClient.getPluginConfiguration(PLUGIN_ID);
    }

    function saveConfig(cfg) {
        return ApiClient.updatePluginConfiguration(PLUGIN_ID, cfg);
    }

    function intVal(el, fallback, min, max) {
        const v = parseInt(el.value, 10);
        return Number.isFinite(v) ? Math.max(min, Math.min(max, v)) : fallback;
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    async function loadRegions(selectEl, currentKey) {
        try {
            const data = await ApiClient.getJSON(ApiClient.getUrl('SatIpFreesat/regions'));
            const regions = Array.isArray(data) ? data : [];
            for (const r of regions) {
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

    async function updateScanStatus(statusEl) {
        try {
            const data = await ApiClient.getJSON(ApiClient.getUrl('SatIpFreesat/status'));
            statusEl.textContent = 'Status: ' + (data.message || '—');
        } catch {
            statusEl.textContent = 'Status: unable to reach plugin API';
        }
    }

    async function resolvePostcode(postcode, selectEl) {
        if (!postcode) return;
        try {
            const url = ApiClient.getUrl('SatIpFreesat/resolve-region', { postcode });
            const data = await ApiClient.getJSON(url);
            for (const opt of selectEl.options) {
                if (opt.value === data.regionKey) {
                    selectEl.value = data.regionKey;
                    Dashboard.alert('Region resolved to: ' + escapeHtml(data.regionLabel));
                    return;
                }
            }
            Dashboard.alert('Postcode resolved to "' + escapeHtml(data.regionKey) + '" but it was not in the list. Please select manually.');
        } catch {
            Dashboard.alert('Could not resolve postcode. Check the postcode and try again.');
        }
    }

    async function triggerScan(form, statusEl, spinner) {
        const serverAddress = form.querySelector('#serverAddress').value.trim();
        const regionKey = form.querySelector('#regionSelect').value;

        if (!serverAddress) { Dashboard.alert('Enter a SAT>IP server address first.'); return; }
        if (!regionKey) { Dashboard.alert('Select a region first.'); return; }

        spinner.style.display = '';
        statusEl.textContent = 'Status: scanning…';
        form.querySelector('#btnScan').disabled = true;

        const body = {
            serverAddress,
            rtspPort: intVal(form.querySelector('#rtspPort'), 554, 1, 65535),
            frontendNumber: intVal(form.querySelector('#frontendNumber'), 1, 1, 8),
            tunerCount: intVal(form.querySelector('#tunerCount'), 1, 1, 16),
            regionKey,
        };

        try {
            const data = await ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('SatIpFreesat/scan'),
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json',
            });
            statusEl.textContent = 'Status: ' + (data.message || 'complete');
            Dashboard.alert(data.message || 'Scan complete.');
        } catch {
            statusEl.textContent = 'Status: scan failed — see Jellyfin logs for details';
            Dashboard.alert('Scan failed. Check Jellyfin logs for details.');
        } finally {
            spinner.style.display = 'none';
            form.querySelector('#btnScan').disabled = false;
        }
    }

    async function rebuildChannels(resultEl) {
        resultEl.textContent = 'Clearing channel store…';
        try {
            await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('SatIpFreesat/rebuild-channels') });
            resultEl.textContent = 'Channel store cleared. Trigger a new scan to repopulate.';
        } catch {
            resultEl.textContent = 'Failed to clear channel store — see Jellyfin logs.';
        }
    }

    function loadConfig(form, cfg) {
        form.querySelector('#serverAddress').value = cfg.serverAddress || '';
        form.querySelector('#rtspPort').value = cfg.rtspPort || 554;
        form.querySelector('#frontendNumber').value = cfg.frontendNumber || 1;
        form.querySelector('#tunerCount').value = cfg.tunerCount || 1;
        form.querySelector('#postcode').value = cfg.postcode || '';
        form.querySelector('#autoScanIntervalHours').value = cfg.autoScanIntervalHours ?? 24;
        form.querySelector('#enableStreamSharing').checked = cfg.enableStreamSharing !== false;
        form.querySelector('#rtpReceiveBufferKiB').value = cfg.rtpReceiveBufferKiB || 512;
        form.querySelector('#packetTimeoutSeconds').value = cfg.packetTimeoutSeconds ?? 10;
        form.querySelector('#exposeSubtitleStreams').checked = cfg.exposeSubtitleStreams === true;
        form.querySelector('#preferredSubtitleLanguage').value = cfg.preferredSubtitleLanguage || 'eng';
        form.querySelector('#forceDeinterlace').checked = cfg.forceDeinterlace === true;
    }

    function collectConfig(form, existing, regionSelect) {
        return Object.assign({}, existing, {
            serverAddress: form.querySelector('#serverAddress').value.trim(),
            rtspPort: intVal(form.querySelector('#rtspPort'), 554, 1, 65535),
            frontendNumber: intVal(form.querySelector('#frontendNumber'), 1, 1, 8),
            tunerCount: intVal(form.querySelector('#tunerCount'), 1, 1, 16),
            postcode: form.querySelector('#postcode').value.trim(),
            regionKey: regionSelect.value,
            regionLabel: regionSelect.options[regionSelect.selectedIndex]?.text || '',
            autoScanIntervalHours: intVal(form.querySelector('#autoScanIntervalHours'), 24, 0, 168),
            enableStreamSharing: form.querySelector('#enableStreamSharing').checked,
            rtpReceiveBufferKiB: intVal(form.querySelector('#rtpReceiveBufferKiB'), 512, 64, 8192),
            packetTimeoutSeconds: intVal(form.querySelector('#packetTimeoutSeconds'), 10, 0, 120),
            exposeSubtitleStreams: form.querySelector('#exposeSubtitleStreams').checked,
            preferredSubtitleLanguage: form.querySelector('#preferredSubtitleLanguage').value.trim() || 'eng',
            forceDeinterlace: form.querySelector('#forceDeinterlace').checked,
        });
    }

    view.addEventListener('viewshow', async function () {
        const form = view.querySelector('#SatIpFreesatConfigForm');
        const regionSelect = view.querySelector('#regionSelect');
        const statusEl = view.querySelector('#scanStatus');
        const spinner = view.querySelector('#scanSpinner');
        const rebuildResult = view.querySelector('#rebuildResult');

        let cfg;
        try {
            cfg = await getConfig();
        } catch {
            cfg = {};
        }

        while (regionSelect.options.length > 1) regionSelect.remove(1);
        await loadRegions(regionSelect, cfg.regionKey);
        loadConfig(form, cfg);
        await updateScanStatus(statusEl);

        view.querySelector('#btnResolvePostcode').addEventListener('click', () => {
            resolvePostcode(form.querySelector('#postcode').value.trim(), regionSelect);
        });

        view.querySelector('#btnScan').addEventListener('click', () => {
            triggerScan(form, statusEl, spinner);
        });

        view.querySelector('#btnRebuildChannels').addEventListener('click', () => {
            rebuildChannels(rebuildResult);
        });

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            const updatedCfg = collectConfig(form, cfg, regionSelect);
            await saveConfig(updatedCfg);
            cfg = updatedCfg;
            Dashboard.processPluginConfigurationUpdateResult();
        });
    });
}
