/* eslint-env browser */
/* global ApiClient */
'use strict';

export default function (view) {
    let pollTimer = null;
    let requestInFlight = null;

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#039;');
    }

    function metric(label, value, help) {
        const title = help ? ` title="${escapeHtml(help)}"` : '';
        return `<div class="sifMetric"${title}><span class="sifMetricLabel">${escapeHtml(label)}</span><span class="sifMetricValue">${escapeHtml(value)}</span></div>`;
    }

    function badge(text, cls) {
        return `<span class="sifBadge ${escapeHtml(cls)}">${escapeHtml(text)}</span>`;
    }

    function fmtTime(iso) {
        if (!iso) return '—';
        try {
            const d = new Date(iso);
            return isNaN(d) ? iso : d.toLocaleString();
        } catch { return iso; }
    }

    function fmtMs(ms) {
        if (ms == null || ms < 0) return '—';
        return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
    }

    function renderStatus(status) {
        const reachable = status.deviceReachable;
        const reachableClass = reachable ? 'sifBadgeGood' : (status.serverAddress ? 'sifBadgeBad' : '');
        const reachableText = !status.serverAddress ? 'Not configured'
            : reachable ? `Reachable (${fmtMs(status.deviceReachableMs)})` : 'Unreachable';

        // Top summary
        const tunerCount = Array.isArray(status.tuners) ? status.tuners.length : 0;
        view.querySelector('#statusSummary').innerHTML =
            metric('Plugin version', status.pluginVersion || '—') +
            metric('Device', status.serverAddress || 'Not configured') +
            metric('Tuners', tunerCount ? `${tunerCount} configured` : '—') +
            metric('Device status', reachableText + (status.deviceReachableError ? ` · ${status.deviceReachableError}` : '')) +
            metric('Channels', status.hasChannels ? String(status.channelCount) : 'No scan yet') +
            metric('Region', status.regionLabel || '—') +
            metric('Last scan', fmtTime(status.lastScanTime));

        // Device detail — one metric per tuner
        const tuners = Array.isArray(status.tuners) ? status.tuners : [];
        const tunerHtml = tuners.map(t =>
            metric(`Tuner ${t.index} (src=${t.frontendNumber})`, `port ${t.rtspPort}`)
        ).join('');
        view.querySelector('#deviceDetail').innerHTML =
            metric('Address', status.serverAddress || '—') +
            tunerHtml +
            `<div class="sifMetric"><span class="sifMetricLabel">Reachability</span><span class="sifMetricValue">${badge(reachableText, reachableClass)}</span></div>` +
            (status.deviceReachableError ? metric('Connectivity error', status.deviceReachableError) : '');

        // Scan detail
        const scanHtml = status.hasChannels
            ? metric('Channels found', String(status.channelCount)) +
              metric('Muxes scanned', status.muxCount ? String(status.muxCount) : '—') +
              metric('Region', status.regionLabel || '—') +
              metric('Scan time', fmtTime(status.lastScanTime))
            : '';

        view.querySelector('#scanDetail').innerHTML = status.hasChannels
            ? scanHtml
            : '<div class="sifEmpty" style="grid-column:1/-1">No scan results yet. Go to the Configuration page and run a scan.</div>';

        // Channel table (top 10)
        const rows = Array.isArray(status.topChannels) ? status.topChannels : [];
        if (rows.length > 0) {
            const rowHtml = rows.map(ch =>
                `<tr>
                    <td>${escapeHtml(String(ch.number))}</td>
                    <td>${escapeHtml(ch.name)}</td>
                    <td>${ch.isHD ? badge('HD', 'sifBadgeGood') : ch.isRadio ? badge('Radio', '') : badge('SD', '')}</td>
                    <td>${escapeHtml(ch.frequencyMHz ? ch.frequencyMHz.toFixed(1) + ' MHz' : '—')}</td>
                </tr>`
            ).join('');
            view.querySelector('#channelTable').innerHTML =
                `<div class="sifTableWrap"><table class="sifTable">
                    <thead><tr>
                        <th scope="col">LCN</th>
                        <th scope="col">Channel</th>
                        <th scope="col">Type</th>
                        <th scope="col">Frequency</th>
                    </tr></thead>
                    <tbody>${rowHtml}</tbody>
                </table></div>
                <div class="sifMuted" style="margin-top:.5em;font-size:.88em">Showing first ${rows.length} of ${status.channelCount} channels. Run a full scan to update.</div>`;
        } else {
            view.querySelector('#channelTable').innerHTML = '';
        }

        view.querySelector('#statusUpdated').textContent = `Updated ${new Date(status.generatedUtc).toLocaleTimeString()}`;
    }

    function loadStatus(showLoading) {
        if (requestInFlight) return requestInFlight;
        const announcer = view.querySelector('#statusAnnouncer');
        if (showLoading) {
            view.querySelector('#statusUpdated').textContent = 'Refreshing…';
            if (announcer) announcer.textContent = 'Refreshing SAT>IP Freesat status.';
        }

        requestInFlight = ApiClient.getJSON(ApiClient.getUrl('SatIpFreesat/detailed-status'))
            .then(status => {
                renderStatus(status);
                if (showLoading && announcer) announcer.textContent = 'Status refreshed.';
            })
            .catch(err => {
                const msg = err?.message || String(err);
                view.querySelector('#statusUpdated').textContent = 'Unable to load status';
                view.querySelector('#statusSummary').innerHTML =
                    `<div class="sifEmpty" style="grid-column:1/-1" role="alert">Status endpoint failed: ${escapeHtml(msg)}</div>`;
                view.querySelector('#deviceDetail').innerHTML = '';
                view.querySelector('#scanDetail').innerHTML = '';
                view.querySelector('#channelTable').innerHTML = '';
            })
            .finally(() => { requestInFlight = null; });

        return requestInFlight;
    }

    function startPolling() {
        stopPolling();
        loadStatus(true);
        pollTimer = setInterval(() => loadStatus(false), 10000);
    }

    function stopPolling() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }

    async function rebuildChannels() {
        const resultEl = view.querySelector('#rebuildResult');
        resultEl.textContent = 'Clearing channel store…';
        try {
            await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('SatIpFreesat/rebuild-channels') });
            resultEl.textContent = 'Channel store cleared. Trigger a new scan from the Configuration page to repopulate.';
            loadStatus(true);
        } catch {
            resultEl.textContent = 'Failed to clear channel store — see Jellyfin logs.';
        }
    }

    view.addEventListener('viewshow', startPolling);
    view.addEventListener('viewhide', stopPolling);

    view.querySelector('#btnRefreshStatus').addEventListener('click', () => loadStatus(true));
    view.querySelector('#btnRebuildChannels').addEventListener('click', rebuildChannels);
}
