/* eslint-env browser */
/* global ApiClient, Dashboard */
'use strict';

(function () {
    function getConfig() {
        return ApiClient.getPluginConfiguration('9d4f3a1c-8c7e-4b5d-a6f0-2e9b1c8d7a3f');
    }

    function saveConfig(cfg) {
        return ApiClient.updatePluginConfiguration('9d4f3a1c-8c7e-4b5d-a6f0-2e9b1c8d7a3f', cfg);
    }

    async function loadRegions(selectEl, currentKey) {
        try {
            const resp = await ApiClient.fetch({
                type: 'GET',
                url: ApiClient.getUrl('SatIpFreesat/regions'),
            });
            const regions = typeof resp === 'string' ? JSON.parse(resp) : resp;
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

    async function updateStatus(statusEl) {
        try {
            const resp = await ApiClient.fetch({
                type: 'GET',
                url: ApiClient.getUrl('SatIpFreesat/status'),
            });
            const data = typeof resp === 'string' ? JSON.parse(resp) : resp;
            statusEl.textContent = 'Status: ' + data.message;
        } catch {
            statusEl.textContent = 'Status: unable to reach plugin API';
        }
    }

    async function resolvePostcode(postcode, selectEl) {
        if (!postcode) return;
        try {
            const url = ApiClient.getUrl('SatIpFreesat/resolve-region', { postcode });
            const resp = await ApiClient.fetch({ type: 'GET', url });
            const data = typeof resp === 'string' ? JSON.parse(resp) : resp;
            for (const opt of selectEl.options) {
                if (opt.value === data.regionKey) {
                    selectEl.value = data.regionKey;
                    Dashboard.alert('Region resolved to: ' + data.regionLabel);
                    return;
                }
            }
            Dashboard.alert('Postcode resolved to region key "' + data.regionKey +
                '" but it was not found in the region list. Please select manually.');
        } catch (e) {
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

        const body = {
            serverAddress,
            rtspPort: parseInt(form.querySelector('#rtspPort').value, 10) || 554,
            frontendNumber: parseInt(form.querySelector('#frontendNumber').value, 10) || 1,
            tunerCount: parseInt(form.querySelector('#tunerCount').value, 10) || 1,
            regionKey,
        };

        try {
            const resp = await ApiClient.fetch({
                type: 'POST',
                url: ApiClient.getUrl('SatIpFreesat/scan'),
                data: JSON.stringify(body),
                contentType: 'application/json',
            });
            const data = typeof resp === 'string' ? JSON.parse(resp) : resp;
            statusEl.textContent = 'Status: ' + data.message;
            Dashboard.alert(data.message);
        } catch (e) {
            statusEl.textContent = 'Status: scan failed — see Jellyfin logs for details';
            Dashboard.alert('Scan failed. Check Jellyfin logs for details.');
        } finally {
            spinner.style.display = 'none';
        }
    }

    async function onPageLoad(view) {
        const form = view.querySelector('#SatIpFreesatConfigForm');
        const regionSelect = view.querySelector('#regionSelect');
        const statusEl = view.querySelector('#scanStatus');
        const spinner = view.querySelector('#scanSpinner');

        const cfg = await getConfig();

        await loadRegions(regionSelect, cfg.regionKey);

        form.querySelector('#serverAddress').value = cfg.serverAddress || '';
        form.querySelector('#rtspPort').value = cfg.rtspPort || 554;
        form.querySelector('#frontendNumber').value = cfg.frontendNumber || 1;
        form.querySelector('#tunerCount').value = cfg.tunerCount || 1;
        form.querySelector('#postcode').value = cfg.postcode || '';

        await updateStatus(statusEl);

        view.querySelector('#btnResolvePostcode').addEventListener('click', () => {
            const pc = form.querySelector('#postcode').value.trim();
            resolvePostcode(pc, regionSelect);
        });

        view.querySelector('#btnScan').addEventListener('click', () => {
            triggerScan(form, statusEl, spinner);
        });

        form.addEventListener('submit', async (e) => {
            e.preventDefault();
            const updatedCfg = Object.assign({}, cfg, {
                serverAddress: form.querySelector('#serverAddress').value.trim(),
                rtspPort: parseInt(form.querySelector('#rtspPort').value, 10) || 554,
                frontendNumber: parseInt(form.querySelector('#frontendNumber').value, 10) || 1,
                tunerCount: parseInt(form.querySelector('#tunerCount').value, 10) || 1,
                postcode: form.querySelector('#postcode').value.trim(),
                regionKey: regionSelect.value,
                regionLabel: regionSelect.options[regionSelect.selectedIndex]?.text || '',
            });
            await saveConfig(updatedCfg);
            Dashboard.processPluginConfigurationUpdateResult();
        });
    }

    window.SatIpFreesatConfig = { pageLoad: onPageLoad };

    document.addEventListener('viewshow', (e) => {
        const view = e.detail?.view;
        if (view && view.id === 'SatIpFreesatConfigPage') {
            onPageLoad(view);
        }
    });
})();
