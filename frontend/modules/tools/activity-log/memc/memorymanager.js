/* === Memory Manager (Memory Console) ===
 *
 * Rendering only. Every number comes from the backend together with a quality code
 * that says how it was obtained; this file never derives a byte figure of its own.
 */

let memcEnabled = false;
let memcIntervalMs = 0;
let _memcTab = 'overview';
let _memcLast = null;
let _memcOpenModules = new Set();
// Static text is sent once by the backend and cached here, so the live payload
// stays small. Without this the console was 58% of all WebView bridge traffic.
let _memcNotes = {};
let _memcLegend = [];

function memcBytes(n) {
    if (n === null || n === undefined) return '—';
    const neg = n < 0;
    let v = Math.abs(n);
    let out;
    if (v < 1024) out = v + ' B';
    else if (v < 1048576) out = (v / 1024).toFixed(1) + ' KB';
    else if (v < 1073741824) out = (v / 1048576).toFixed(2) + ' MB';
    else out = (v / 1073741824).toFixed(3) + ' GB';
    return (neg ? '-' : '') + out;
}

function memcSigned(n) {
    if (n === null || n === undefined) return '—';
    return (n > 0 ? '+' : '') + memcBytes(n);
}

function memcNum(n) {
    if (n === null || n === undefined || n < 0) return '—';
    return n.toLocaleString();
}

function memcDur(ms) {
    if (!ms || ms < 0) return '—';
    const s = Math.floor(ms / 1000);
    const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
    return (h > 0 ? h + 'h ' : '') + (m > 0 || h > 0 ? m + 'm ' : '') + sec + 's';
}

function memcQ(code) {
    return `<span class="memc-q ${esc(code)}" title="${esc(memcQualityText(code))}">${esc(code)}</span>`;
}

function memcQualityText(code) {
    const hit = _memcLegend.find(x => x.code === code);
    return hit ? hit.description : code;
}

// The backend omits a note when it has not changed since it was last sent.
function memcNote(key, incoming) {
    if (incoming === null || incoming === undefined) return _memcNotes[key];
    _memcNotes[key] = incoming;
    return incoming;
}

/* Toolbar button visibility — driven only by /memc */

function memcApplyState(payload) {
    memcEnabled = !!(payload && payload.enabled);
    memcIntervalMs = (payload && payload.intervalMs) || 0;
    const btn = document.getElementById('memcOpenBtn');
    if (btn) btn.style.display = memcEnabled ? '' : 'none';
    if (!memcEnabled) {
        _memcLast = null;
        _memcNotes = {};
        _memcLegend = [];
        const ov = document.getElementById('modalMemoryManager');
        if (ov && ov.style.display !== 'none') closeMemoryManager();
    }
}

function openMemoryManager() {
    if (!memcEnabled) {
        if (typeof showToast === 'function') showToast(false, 'Memory Console is off. Enable it with /memc true');
        return;
    }
    const ov = document.getElementById('modalMemoryManager');
    if (!ov) return;
    ov.style.display = 'flex';
    _memcNotes = {};
    _memcLegend = [];
    memcSend('memcOpen', { allSeries: _memcTab === 'growth' });
}

function closeMemoryManager() {
    const ov = document.getElementById('modalMemoryManager');
    if (ov) ov.style.display = 'none';
    memcSend('memcClose');
}

function memcSend(action, extra) {
    sendToCS(Object.assign({ action }, extra || {}));
}

function memcCapture(slot) { memcSend('memcCapture', { slot }); }
function memcCompare()     { memcSwitchTab('ab'); memcSend('memcCompare'); }
function memcDeep()        { memcSend('memcDeep'); }
function memcForceGc()     { memcSwitchTab('gc'); memcSend('memcForceGc'); }

function memcSwitchTab(tab) {
    const wasGrowth = _memcTab === 'growth';
    _memcTab = tab;
    // The full series list is ~45 KB per push, so it is only requested where it is shown.
    if (wasGrowth !== (tab === 'growth')) memcSend('memcDetail', { allSeries: tab === 'growth' });
    document.querySelectorAll('#memcTabs .memc-tab').forEach(b =>
        b.classList.toggle('active', b.dataset.memcTab === tab));
    document.querySelectorAll('#memcBody .memc-pane').forEach(p =>
        p.hidden = p.dataset.memcPane !== tab);
}

/* Live payload */

function memcHandleLive(p) {
    if (!p) return;
    _memcLast = p;
    memcEnabled = !!p.enabled;
    if (Array.isArray(p.legend) && p.legend.length) _memcLegend = p.legend;

    const dot = document.getElementById('memcLiveDot');
    if (dot) dot.classList.toggle('on', memcEnabled);

    const head = document.getElementById('memcHeadline');
    if (head) {
        const ws = memcFind(p.process, 'workingSet');
        const gcH = memcFind(p.gc, 'heap');
        head.textContent = `WS ${memcBytes(ws ? ws.bytes : 0)}  ·  GC ${memcBytes(gcH ? gcH.bytes : 0)}`
            + `  ·  ${p.sampleCount} samples @ ${p.intervalMs}ms  ·  up ${memcDur(p.processUptimeMs)}`;
    }

    memcRenderAttribution(p);
    memcRenderRows('memcProcRows', p.process);
    memcRenderRows('memcGcRows', p.gc);
    memcRenderSeries(p);
    memcRenderModules(p);
    memcRenderGrowth(p);
    memcRenderSnapBar(p);
    memcRenderGcCompare(p.gcCompare);
    memcRenderLegend(p);
    memcRenderSelf(p);
}

function memcFind(rows, key) {
    if (!Array.isArray(rows)) return null;
    return rows.find(r => r.key === key) || null;
}

function memcRenderAttribution(p) {
    const el = document.getElementById('memcAttr');
    if (!el || !p.attribution) return;
    const a = p.attribution;
    el.innerHTML = `
        <div class="memc-card">
            <div class="memc-card-k">Process Private</div>
            <div class="memc-card-v">${memcBytes(a.processPrivate)}</div>
            <div class="memc-card-s">Measured commit charge of VRCNext.exe.</div>
        </div>
        <div class="memc-card">
            <div class="memc-card-k">Attributed</div>
            <div class="memc-card-v">${memcBytes(a.totalAttributed)}</div>
            <div class="memc-card-s">${memcBytes(a.managedAttributed)} managed (instrumented) + ${memcBytes(a.nativeAttributed)} native (probes).</div>
        </div>
        <div class="memc-card warnbox">
            <div class="memc-card-k">Unattributed managed</div>
            <div class="memc-card-v">${memcBytes(a.managedUnattributed)}</div>
            <div class="memc-card-s">GC heap ${memcBytes(a.gcHeap)} minus what our instrumentation covers.</div>
        </div>
        <div class="memc-card warnbox">
            <div class="memc-card-k">Unattributed native</div>
            <div class="memc-card-v">${memcBytes(a.nativeUnattributed)}</div>
            <div class="memc-card-s">Private ${memcBytes(a.processPrivate)} − GC committed ${memcBytes(a.gcCommitted)} − probes.</div>
        </div>`;
}

function memcRenderRows(id, rows) {
    const el = document.getElementById(id);
    if (!el || !Array.isArray(rows)) return;
    let html = '';
    for (const r of rows) {
        let val;
        if (!r.measurable) val = '<span class="memc-row-v na">not measurable</span>';
        else if (r.count >= 0 && r.bytes === 0) val = `<span class="memc-row-v">${memcNum(r.count)}</span>`;
        else val = `<span class="memc-row-v">${memcBytes(r.bytes)}</span>`;
        const note = memcNote('row/' + r.key, r.note);
        const t = note ? ` title="${esc(note)}"` : '';
        html += `<div class="memc-row"${t}><span class="memc-row-k">${esc(r.label)}</span>${val}${memcQ(r.quality)}</div>`;
    }
    el.innerHTML = html;
}

function memcRenderSeries(p) {
    const t = document.getElementById('memcSeriesTable');
    const meta = document.getElementById('memcSeriesMeta');
    if (!t) return;
    if (meta && p.profiler) {
        meta.textContent = `${p.profiler.seriesCount} series · history ${memcBytes(p.profiler.historyBytes)} · `
            + `${memcBytes(p.profiler.selfAllocPerSample)} allocated per sample · ${p.profiler.sampleDurationMs} ms per pass`;
    }
    const keep = (p.series || []).filter(s => s.key.startsWith('proc.') || s.key.startsWith('gc.') || s.key.startsWith('attr.'));
    t.innerHTML = memcSeriesTableHtml(keep);
}

function memcSeriesTableHtml(rows) {
    let html = '<thead><tr><th>Series</th><th>Current</th><th>Previous</th><th>Δ</th><th>Start</th>'
             + '<th>Min</th><th>Max</th><th>Avg</th><th>Since start</th><th>Per min</th><th>Samples</th><th>Trend</th></tr></thead><tbody>';
    for (const s of rows) {
        const cls = d => d > 0 ? 'pos' : d < 0 ? 'neg' : 'dim';
        const isCount = s.key === 'proc.handles' || s.key === 'proc.threads';
        const F = isCount ? memcNum : memcBytes;
        const FS = isCount ? (v => (v > 0 ? '+' : '') + memcNum(v)) : memcSigned;
        html += `<tr>
            <td>${esc(s.label)}</td>
            <td>${F(s.current)}</td>
            <td class="dim">${F(s.previous)}</td>
            <td class="${cls(s.delta)}">${FS(s.delta)}</td>
            <td class="dim">${F(s.start)}</td>
            <td class="dim">${F(s.min)}</td>
            <td>${F(s.max)}</td>
            <td class="dim">${F(s.avg)}</td>
            <td class="${cls(s.growthSinceStart)}">${FS(s.growthSinceStart)}</td>
            <td class="${cls(s.windowGrowthPerMinute)}">${s.hasGrowthRate ? FS(s.windowGrowthPerMinute) : '—'}</td>
            <td class="dim">${s.sampleCount}</td>
            <td class="memc-trend ${esc(s.trend)}">${esc(s.trend)}</td>
        </tr>`;
    }
    return html + '</tbody>';
}

function memcRenderModules(p) {
    const el = document.getElementById('memcModules');
    const hint = document.getElementById('memcModulesHint');
    if (!el) return;
    const mods = (p.modules || []).slice().sort((a, b) => b.attributedBytes - a.attributedBytes);
    if (hint) {
        const anyDeep = mods.some(m => (m.resources || []).some(r => r.quality === 'count' && /Deep Measure/.test(r.note || '')));
        hint.innerHTML = anyDeep
            ? 'Some byte sizes need a full walk of a JSON tree. Press <strong>Deep Measure</strong> to compute them; until then those rows show only their item count.'
            : 'Byte sizes are computed by walking the live structures. Rows marked <code>count</code> or <code>unmeasurable</code> deliberately show no byte figure.';
    }
    let html = '';
    for (const m of mods) {
        const open = _memcOpenModules.has(m.id);
        const state = m.active ? 'running' : (m.everActive ? 'stopped' : 'idle');
        const retained = m.lifecycleNote && /still holding/.test(m.lifecycleNote) && !m.active;
        html += `<div class="memc-mod${open ? ' open' : ''}" data-memc-mod="${esc(m.id)}">
            <div class="memc-mod-head" onclick="memcToggleModule('${jsq(m.id)}')">
                <span class="msi memc-mod-chev">chevron_right</span>
                <span class="memc-mod-name">${esc(m.label)}</span>
                <span class="memc-mod-state ${state}">${state}</span>
                ${m.throughputBytes > 0 ? `<span class="memc-mod-flow" title="Cumulative bytes pushed over the bridge. A flow, not resident memory.">${memcBytes(m.throughputBytes)} sent</span>` : ''}
                <span class="memc-mod-bytes">${memcBytes(m.attributedBytes)}</span>
            </div>
            <div class="memc-mod-body"${open ? '' : ' hidden'}>`;
        if (m.lifecycleNote)
            html += `<div class="memc-life${retained ? ' retained' : ''}">${esc(m.lifecycleNote)}</div>`;
        for (const r of (m.resources || [])) {
            let val;
            if (r.quality === 'unmeasurable') val = '<span class="memc-res-v na">not measurable</span>';
            else if (r.quality === 'count') val = '<span class="memc-res-v na">—</span>';
            else if (r.quality === 'throughput') val = `<span class="memc-res-v" style="color:var(--cyan)" title="Cumulative flow, not resident memory">${memcBytes(r.bytes)}</span>`;
            else val = `<span class="memc-res-v">${memcBytes(r.bytes)}</span>`;
            html += `<div class="memc-res">
                <span class="memc-res-k">${esc(r.label)}</span>
                <span class="memc-res-cat">${esc(r.category)}</span>
                <span class="memc-res-n">${r.count >= 0 ? 'n=' + memcNum(r.count) : ''}</span>
                ${val}${memcQ(r.quality)}
            </div>`;
            const rNote = memcNote(m.id + '/' + r.id, r.note);
            if (rNote) html += `<div class="memc-note">${esc(rNote)}</div>`;
            if (r.contendedReads > 0) html += `<div class="memc-note">${r.contendedReads} sample(s) hit the structure mid-modification and reused the previous clean value.</div>`;
        }
        if (m.informationalBytes > 0)
            html += `<div class="memc-note">Informational only (file sizes, not resident RAM): ${memcBytes(m.informationalBytes)}. Not counted toward attribution.</div>`;
        html += '</div></div>';
    }
    el.innerHTML = html;
}

function memcToggleModule(id) {
    if (_memcOpenModules.has(id)) _memcOpenModules.delete(id);
    else _memcOpenModules.add(id);
    const node = document.querySelector(`.memc-mod[data-memc-mod="${CSS.escape(id)}"]`);
    if (!node) return;
    const open = _memcOpenModules.has(id);
    node.classList.toggle('open', open);
    const body = node.querySelector('.memc-mod-body');
    if (body) body.hidden = !open;
}

function memcRenderGrowth(p) {
    const t = document.getElementById('memcGrowthTable');
    if (!t) return;
    if (!p.allSeries) {
        t.innerHTML = '<tbody><tr><td>Loading the full series list…</td></tr></tbody>';
        return;
    }
    const rows = (p.series || [])
        .filter(s => s.sampleCount >= 4)
        .slice()
        .sort((a, b) => Math.abs(b.windowGrowthPerMinute) - Math.abs(a.windowGrowthPerMinute));
    let html = '<thead><tr><th>Series</th><th>Current</th><th>Start</th><th>Since start</th>'
             + '<th>Per min</th><th>Peak</th><th>Window</th><th>Samples</th><th>Trend</th></tr></thead><tbody>';
    for (const s of rows) {
        const cls = d => d > 0 ? 'pos' : d < 0 ? 'neg' : 'dim';
        html += `<tr>
            <td>${esc(s.label)}</td>
            <td>${memcBytes(s.current)}</td>
            <td class="dim">${memcBytes(s.start)}</td>
            <td class="${cls(s.growthSinceStart)}">${memcSigned(s.growthSinceStart)}</td>
            <td class="${cls(s.windowGrowthPerMinute)}">${s.hasGrowthRate ? memcSigned(s.windowGrowthPerMinute) : '—'}</td>
            <td class="dim">${memcBytes(s.max)}</td>
            <td class="dim">${s.windowMinutes} min</td>
            <td class="dim">${s.sampleCount}</td>
            <td class="memc-trend ${esc(s.trend)}">${esc(s.trend)}</td>
        </tr>`;
    }
    t.innerHTML = html + '</tbody>';
}

function memcRenderSnapBar(p) {
    const el = document.getElementById('memcSnapBar');
    if (!el || !p.snapshots) return;
    const card = (name, s) => {
        if (!s) return `<div class="memc-snap empty"><div class="memc-snap-k">${name}</div><div class="memc-snap-v">not captured</div></div>`;
        return `<div class="memc-snap"><div class="memc-snap-k">${name}</div><div class="memc-snap-v">
            ${new Date(s.takenAtUtc).toLocaleTimeString()}<br>
            WS ${memcBytes(s.workingSet)} · private ${memcBytes(s.privateMemory)}<br>
            GC heap ${memcBytes(s.gcHeap)} · attributed ${memcBytes(s.attributed)}
        </div></div>`;
    };
    el.innerHTML = card('Baseline (console start)', p.snapshots.baseline)
                 + card('Snapshot A', p.snapshots.a)
                 + card('Snapshot B', p.snapshots.b);
}

function memcHandleCompare(p) {
    const el = document.getElementById('memcCompare');
    if (!el) return;
    if (!p || !p.ok) {
        el.innerHTML = `<div class="memc-hint">${esc((p && p.reason) || 'Nothing to compare yet.')}</div>`;
        return;
    }
    const a = p.attribution;
    const cls = d => d > 0 ? 'pos' : d < 0 ? 'neg' : 'dim';

    let html = `<div class="memc-attr">
        <div class="memc-card">
            <div class="memc-card-k">Process private delta</div>
            <div class="memc-card-v">${memcSigned(a.processPrivateDelta)}</div>
            <div class="memc-card-s">${(p.elapsedMs / 60000).toFixed(1)} min between A and B.</div>
        </div>
        <div class="memc-card">
            <div class="memc-card-k">Explained</div>
            <div class="memc-card-v">${memcSigned(a.explainedDelta)}</div>
            <div class="memc-card-s">managed ${memcSigned(a.managedAttributedDelta)} · native ${memcSigned(a.nativeAttributedDelta)} · GC overhead ${memcSigned(a.gcOverheadDelta)}</div>
        </div>
        <div class="memc-card warnbox">
            <div class="memc-card-k">Unattributed delta</div>
            <div class="memc-card-v">${memcSigned(a.unattributedDelta)}</div>
            <div class="memc-card-s">${esc(a.formula)}</div>
        </div>
        <div class="memc-card">
            <div class="memc-card-k">Snapshots</div>
            <div class="memc-card-v" style="font-size:calc(13px + var(--fs-off, 0px));">${new Date(p.a.takenAtUtc).toLocaleTimeString()} → ${new Date(p.b.takenAtUtc).toLocaleTimeString()}</div>
            <div class="memc-card-s">Local time.</div>
        </div>
    </div>`;

    html += '<div class="memc-panel"><div class="memc-panel-head"><span>Process and GC</span></div><div class="memc-tablewrap"><table class="memc-table">'
          + '<thead><tr><th>Metric</th><th>A</th><th>B</th><th>Δ</th><th>Source</th></tr></thead><tbody>';
    for (const r of p.rows) {
        html += `<tr><td>${esc(r.label)}</td><td class="dim">${memcBytes(r.a)}</td><td>${memcBytes(r.b)}</td>`
              + `<td class="${cls(r.delta)}">${memcSigned(r.delta)}</td><td>${memcQ(r.quality)}</td></tr>`;
    }
    html += '</tbody></table></div></div>';

    html += '<div class="memc-panel"><div class="memc-panel-head"><span>Per module</span><span class="memc-src">instrumented + probes only</span></div><div class="memc-tablewrap"><table class="memc-table">'
          + '<thead><tr><th>Module</th><th>A</th><th>B</th><th>Δ</th><th>State A → B</th></tr></thead><tbody>';
    for (const m of p.modules) {
        if (m.a === 0 && m.b === 0) continue;
        html += `<tr><td>${esc(m.label)}</td><td class="dim">${memcBytes(m.a)}</td><td>${memcBytes(m.b)}</td>`
              + `<td class="${cls(m.delta)}">${memcSigned(m.delta)}</td>`
              + `<td class="dim">${m.activeA ? 'running' : 'idle'} → ${m.activeB ? 'running' : 'idle'}</td></tr>`;
        for (const r of (m.resources || [])) {
            if (r.delta === 0) continue;
            html += `<tr><td style="padding-left:28px;color:var(--tx3);">${esc(r.label)}</td>`
                  + `<td class="dim">${r.attributed ? memcBytes(r.a) : '—'}</td>`
                  + `<td class="dim">${r.attributed ? memcBytes(r.b) : '—'}</td>`
                  + `<td class="${cls(r.delta)}">${r.attributed ? memcSigned(r.delta) : '—'}</td>`
                  + `<td>${memcQ(r.quality)}</td></tr>`;
        }
    }
    html += '</tbody></table></div></div>';
    el.innerHTML = html;
}

function memcRenderGcCompare(g) {
    const el = document.getElementById('memcGcCompare');
    if (!el) return;
    if (!g) return;
    const row = (k, b, a) => `<tr><td>${esc(k)}</td><td class="dim">${memcBytes(b)}</td><td>${memcBytes(a)}</td>`
        + `<td class="${a - b > 0 ? 'pos' : a - b < 0 ? 'neg' : 'dim'}">${memcSigned(a - b)}</td></tr>`;
    let html = `<div class="memc-hint">Forced collection at ${new Date(g.atUtc).toLocaleTimeString()}.</div>`;
    html += '<div class="memc-panel"><div class="memc-panel-head"><span>Before / After</span></div><div class="memc-tablewrap"><table class="memc-table">'
          + '<thead><tr><th>Metric</th><th>Before</th><th>After</th><th>Δ</th></tr></thead><tbody>'
          + row('Working Set', g.before.workingSet, g.after.workingSet)
          + row('Private Memory', g.before.privateMemory, g.after.privateMemory)
          + row('GC Heap', g.before.gcHeap, g.after.gcHeap)
          + row('GC Committed', g.before.gcCommitted, g.after.gcCommitted)
          + row('GC Fragmentation', g.before.fragmented, g.after.fragmented)
          + `<tr><td>Finalization queue</td><td class="dim">${memcNum(g.before.finalizers)}</td><td>${memcNum(g.after.finalizers)}</td><td class="dim">—</td></tr>`
          + '</tbody></table></div></div>';
    html += '<div class="memc-panel"><div class="memc-panel-head"><span>What this tells us</span></div><div class="memc-rows single">';
    for (const d of g.derived) {
        html += `<div class="memc-row" title="${esc(d.formula)}"><span class="memc-row-k">${esc(d.label)}</span>`
              + `<span class="memc-row-v">${memcBytes(d.bytes)}</span></div>`
              + `<div class="memc-note">${esc(d.formula)}</div>`;
    }
    html += '</div></div>';
    el.innerHTML = html;
}

function memcRenderLegend(p) {
    const el = document.getElementById('memcLegend');
    if (!el || !_memcLegend.length) return;
    el.innerHTML = _memcLegend.map(q =>
        `<div class="memc-legend-row">${memcQ(q.code)}<span>${esc(q.description)}</span></div>`).join('');
}

function memcRenderSelf(p) {
    const el = document.getElementById('memcSelfRows');
    if (!el || !p.profiler) return;
    const f = p.profiler;
    el.innerHTML =
        `<div class="memc-row"><span class="memc-row-k">Allocated per sampling pass</span><span class="memc-row-v">${memcBytes(f.selfAllocPerSample)}</span>${memcQ('runtime')}</div>`
      + `<div class="memc-row"><span class="memc-row-k">Sample history footprint</span><span class="memc-row-v">${memcBytes(f.historyBytes)}</span>${memcQ('instrumented')}</div>`
      + `<div class="memc-row"><span class="memc-row-k">Series retained</span><span class="memc-row-v">${memcNum(f.seriesCount)}</span>${memcQ('measured')}</div>`
      + `<div class="memc-row"><span class="memc-row-k">Time per sampling pass</span><span class="memc-row-v">${f.sampleDurationMs} ms</span>${memcQ('measured')}</div>`
      + `<div class="memc-row"><span class="memc-row-k">Sampler thread</span><span class="memc-row-v">${esc(f.threadName)}</span>${memcQ('measured')}</div>`
      + `<div class="memc-row"><span class="memc-row-k">Sampling interval</span><span class="memc-row-v">${p.intervalMs} ms</span>${memcQ('measured')}</div>`;
}

function memcHandleExported(p) {
    if (!p) return;
    if (p.ok) {
        if (typeof showToast === 'function') showToast(true, 'Memory analysis exported (' + memcBytes(p.bytes) + ')');
    } else if (typeof showToast === 'function') {
        showToast(false, 'Export failed: ' + (p.error || 'unknown error'));
    }
}
