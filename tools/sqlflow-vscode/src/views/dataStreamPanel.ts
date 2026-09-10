import * as vscode from 'vscode';
import { DataStream } from '../api/controlPlaneClient';

interface SeriesPoint {
    date: string;
    rowsWritten: number;
    rowsInserted: number;
    rowsUpdated: number;
    rowsDeleted: number;
    runs: number;
    failures: number;
    excludedBackfillRuns: number;
    expected: number;
    severity: number;
    anomaly: boolean;
    reason: string | null;
    imputed: boolean;
    immature: boolean;
    unexpectedNull: boolean;
    trimmed: boolean;
}

interface Signal {
    detector: string;
    fired: boolean;
    score: number;
    direction: string;
    primary: boolean;
    detail: string;
}

const detectorLabels: Record<string, string> = {
    silence: 'No data now',
    nullDays: 'Missing days',
    cadence: 'Not running',
    rateChange: 'Volume rate',
    levelShift: 'Level shift',
    volumeOutlier: 'Outlying day',
};

const detectorMethods: Record<string, string> = {
    silence: "Gap since the last load against this stream's own tolerated gap",
    nullDays: "Empty days against the median week's delivery rate",
    cadence: 'Gap since the last run against its execution cadence',
    rateChange: 'Overdispersion-adjusted count rate, recent slice against baseline',
    levelShift: 'PELT change point over the residuals',
    volumeOutlier: 'Generalized ESD over trend-and-weekday residuals',
};

const categoryLabels: Record<string, string> = {
    stalled: 'No data arriving',
    failing: 'Failing',
    'gap-days': 'Missing days',
    'not-running': 'Not running',
    'less-than-normal': 'Less than normal',
    'more-than-normal': 'More than normal',
    'never-loaded': 'Never loaded',
    'insufficient-history': 'Not enough history',
    healthy: 'On pattern',
};

/**
 * The single-stream analysis, as a proper panel rather than a wall of JSON: the verdict, the pattern the
 * verdict is stated against, the daily volume against what was expected, and every detector's reasoning
 * including the ones that stayed quiet.
 *
 * It is drawn entirely from VS Code's own theme variables and an inline SVG, so it inherits the user's colour
 * theme exactly, needs no bundled assets, and satisfies a strict content-security policy: no network, no
 * external stylesheet, and one nonce-guarded script for the chart's hover readout.
 */
export class DataStreamPanel {
    private static current: DataStreamPanel | undefined;

    private constructor(private readonly panel: vscode.WebviewPanel) {}

    /** Shows the analysis, reusing the open panel so drilling into several streams does not litter the
     * editor with tabs. */
    static show(stream: DataStream, windowDays: number): void {
        const column = vscode.window.activeTextEditor?.viewColumn ?? vscode.ViewColumn.One;

        if (DataStreamPanel.current) {
            DataStreamPanel.current.panel.title = stream.flowName;
            DataStreamPanel.current.panel.webview.html = render(
                DataStreamPanel.current.panel.webview, stream, windowDays);
            DataStreamPanel.current.panel.reveal(column, true);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            'sqlflow.dataStream',
            stream.flowName,
            { viewColumn: column, preserveFocus: true },
            { enableScripts: true, retainContextWhenHidden: true }
        );
        panel.iconPath = new vscode.ThemeIcon('pulse');
        panel.webview.html = render(panel.webview, stream, windowDays);

        const instance = new DataStreamPanel(panel);
        DataStreamPanel.current = instance;
        panel.onDidDispose(() => {
            if (DataStreamPanel.current === instance) {
                DataStreamPanel.current = undefined;
            }
        });
    }
}

function render(webview: vscode.Webview, stream: DataStream, windowDays: number): string {
    const nonce = makeNonce();
    const series = (stream.series as SeriesPoint[] | null) ?? [];
    const signals = (stream.signals as Signal[] | undefined) ?? [];
    const profile = stream.profile as Record<string, any>;
    const pattern = profile.pattern as { shape: string; description: string; reliability: number };

    const csp = [
        "default-src 'none'",
        `style-src ${webview.cspSource} 'unsafe-inline'`,
        `script-src 'nonce-${nonce}'`,
    ].join('; ');

    return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="${csp}">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>${esc(stream.flowName)}</title>
<style>${styles()}</style>
</head>
<body>
<div class="wrap">
  <header>
    <div class="titles">
      <h1>${esc(stream.flowName)}</h1>
      <p class="target">${esc(String(stream.targetObject ?? stream.batch ?? 'no known target'))}</p>
    </div>
    <span class="pill ${severityClass(stream.severity)}">
      ${glyph(stream.status)} ${esc(categoryLabels[stream.category] ?? stream.category)}
    </span>
  </header>

  <p class="summary">${esc(stream.summary)}</p>

  <section class="callout">
    <div class="callout-label">Learned pattern</div>
    <p>${esc(pattern.description)}</p>
    <p class="meta">
      Shape <b>${esc(pattern.shape)}</b> ·
      normally delivers on <b>${pct(pattern.reliability)}</b> of expected days ·
      judged over the last ${windowDays} days
    </p>
  </section>

  <section class="kpis">
    ${kpi('Last load', days(profile.daysSinceLastLoad), 'since data arrived')}
    ${kpi('Empty days', `${profile.unexpectedNullDays}`, `vs ${num(profile.predictedNullDays, 1)} predicted`,
        profile.unexpectedNullDays > profile.predictedNullDays ? 'bad' : '')}
    ${kpi('Ran empty', `${profile.emptyRunDays}`, 'upstream produced nothing')}
    ${kpi('No run', `${profile.noRunDays}`, 'flow did not execute')}
    ${kpi('Typical load', compact(profile.medianRowsWrittenPerLoadedDay), 'median loading day')}
    ${kpi('Expected gap', `${num(profile.expectedGapDays, 1)}d`, String(profile.cadenceSource))}
  </section>

  ${chartSection(series, nonce)}

  <section class="averages">
    <h2>Per run averages</h2>
    <div class="avg-row">
      ${avg('Inserted', profile.avgRowsInsertedPerRun)}
      ${avg('Updated', profile.avgRowsUpdatedPerRun)}
      ${avg('Deleted', profile.avgRowsDeletedPerRun)}
      ${avg('Runs', profile.runs, true)}
      ${avg('Failures', profile.failures, true)}
    </div>
    ${profile.trimmedLoadDays > 0 ? `<p class="note">${profile.trimmedLoadDays} day(s) above ${
        compact(profile.trimFence)} rows were trimmed as suspected reprocessing before fitting. The chart above
        still shows what really landed.</p>` : ''}
  </section>

  <section class="detectors">
    <h2>Detectors <span class="muted">${stream.agreeingDetectors} of ${signals.length} agree</span></h2>
    ${signals.map(signalRow).join('')}
  </section>
</div>
<div id="tip" class="tooltip" hidden></div>
<script nonce="${nonce}">${script()}</script>
</body>
</html>`;
}

/** The daily volume against the expectation, as an inline SVG: bars for what arrived, a dashed line for what
 * was expected, and a marked baseline tick on every day that wrote nothing when it should have. */
function chartSection(series: SeriesPoint[], nonce: string): string {
    if (series.length === 0) {
        return '<section class="chart-empty">No analysed days in this window.</section>';
    }

    const width = 1000;
    const height = 220;
    const padLeft = 56;
    const padRight = 12;
    const padTop = 12;
    const padBottom = 26;
    const plotW = width - padLeft - padRight;
    const plotH = height - padTop - padBottom;

    const peak = Math.max(...series.map((p) => Math.max(p.rowsWritten, p.expected)), 1);
    const scale = (v: number) => plotH - (v / peak) * plotH;
    const slot = plotW / series.length;
    const barW = Math.max(Math.min(slot - 2, 26), 1);

    const bars = series.map((p, i) => {
        const x = padLeft + i * slot + (slot - barW) / 2;
        const h = p.rowsWritten > 0 ? Math.max(plotH - scale(p.rowsWritten), 1.5) : 0;
        const y = padTop + plotH - h;
        const cls = p.anomaly ? 'bar bad' : p.immature ? 'bar dim' : 'bar';
        // A day that wrote nothing when it should have has no bar to see, so it gets a baseline tick: the
        // finding this whole surface exists for must not be the one thing the chart cannot show.
        const mark = p.unexpectedNull
            ? `<rect class="miss" x="${round(x)}" y="${round(padTop + plotH - 4)}" width="${round(barW)}" height="4" rx="1"/>`
            : '';
        return `${mark}<rect class="${cls}" x="${round(x)}" y="${round(y)}" width="${round(barW)}" height="${round(h)}" rx="2" data-i="${i}"/>`;
    }).join('');

    const line = series
        .map((p, i) => `${round(padLeft + i * slot + slot / 2)},${round(padTop + scale(p.expected))}`)
        .join(' ');

    const grid = [0, 0.25, 0.5, 0.75, 1].map((f) => {
        const y = padTop + plotH - f * plotH;
        return `<line class="grid" x1="${padLeft}" y1="${round(y)}" x2="${width - padRight}" y2="${round(y)}"/>` +
            `<text class="axis" x="${padLeft - 8}" y="${round(y + 3.5)}" text-anchor="end">${compact(peak * f)}</text>`;
    }).join('');

    const ticks = series.map((p, i) => {
        const step = Math.max(1, Math.ceil(series.length / 10));
        if (i % step !== 0) {
            return '';
        }
        return `<text class="axis" x="${round(padLeft + i * slot + slot / 2)}" y="${height - 8}" text-anchor="middle">${
            esc(p.date.slice(5, 10))}</text>`;
    }).join('');

    return `<section class="chart">
    <div class="legend">
      <span><i class="sw ok"></i>Rows written</span>
      <span><i class="sw line"></i>Expected</span>
      <span><i class="sw bad"></i>Flagged day</span>
      <span><i class="sw miss"></i>Expected a load, got none</span>
    </div>
    <svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none" role="img"
         aria-label="Daily rows written against the expected volume">
      ${grid}
      ${bars}
      <polyline class="expected" points="${line}"/>
      ${ticks}
    </svg>
    <script type="application/json" id="series" nonce="${nonce}">${jsonIsland(series.map((p) => ({
        d: p.date.slice(0, 10),
        w: p.rowsWritten,
        e: Math.round(p.expected),
        i: p.rowsInserted,
        u: p.rowsUpdated,
        x: p.rowsDeleted,
        r: p.runs,
        f: p.failures,
        b: p.excludedBackfillRuns,
        n: p.unexpectedNull,
        a: p.anomaly,
        s: p.reason,
        m: p.immature,
        t: p.trimmed,
    })))}</script>
  </section>`;
}

/** Serialises the series for the JSON island. A script element is RAW TEXT, so HTML escaping would land
 * literally and break JSON.parse; the only sequence that can end the block early is "</", and escaping the
 * angle bracket as < is itself valid JSON. */
function jsonIsland(value: unknown): string {
    return JSON.stringify(value).replace(/</g, '\\u003c');
}

function signalRow(signal: Signal): string {
    const state = signal.fired ? 'fired' : 'quiet';
    const arrow = signal.direction === 'below' ? ' ↓' : signal.direction === 'above' ? ' ↑' : '';
    return `<div class="signal ${state}">
    <span class="dot">${signal.fired ? '●' : '○'}</span>
    <div class="signal-body">
      <div class="signal-head">
        <b>${esc(detectorLabels[signal.detector] ?? signal.detector)}${arrow}</b>
        ${signal.primary ? '<span class="tag">primary</span>' : ''}
        <span class="muted">${signal.fired ? `fired · ${pct(signal.score)} confidence` : 'quiet'}</span>
      </div>
      <p>${esc(signal.detail)}</p>
      <p class="method">${esc(detectorMethods[signal.detector] ?? '')}</p>
    </div>
  </div>`;
}

function kpi(label: string, value: string, caption: string, tone = ''): string {
    return `<div class="kpi"><div class="kpi-label">${esc(label)}</div>` +
        `<div class="kpi-value ${tone}">${esc(value)}</div>` +
        `<div class="kpi-caption">${esc(caption)}</div></div>`;
}

function avg(label: string, value: number, whole = false): string {
    return `<div class="avg"><span>${esc(label)}</span><b>${
        whole ? Math.round(value).toLocaleString() : compact(value)}</b></div>`;
}

function severityClass(severity: string): string {
    return severity === 'critical' ? 'crit' : severity === 'warning' ? 'warn' : 'info';
}

function glyph(status: string): string {
    switch (status) {
        case 'stalled':
            return '⊘';
        case 'degraded':
            return '⚠';
        case 'watch':
            return '◉';
        case 'healthy':
            return '✓';
        default:
            return '?';
    }
}

function days(value: number | null): string {
    if (value === null || value === undefined) {
        return 'never';
    }
    return value < 1 ? 'today' : value < 2 ? '1 day' : `${Math.round(value)} days`;
}

function num(value: number, places: number): string {
    return Number(value ?? 0).toFixed(places);
}

function compact(value: number): string {
    const v = Math.round(Number(value) || 0);
    if (Math.abs(v) >= 1_000_000) {
        return `${(v / 1_000_000).toFixed(1)}M`;
    }
    return Math.abs(v) >= 1_000 ? `${(v / 1_000).toFixed(0)}k` : String(v);
}

function pct(value: number): string {
    return `${Math.round((Number(value) || 0) * 100)}%`;
}

function round(value: number): string {
    return (Math.round(value * 100) / 100).toString();
}

function esc(value: string): string {
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function makeNonce(): string {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    let out = '';
    for (let i = 0; i < 32; i++) {
        out += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return out;
}

/** Everything is expressed in VS Code's own theme variables, so the panel matches whatever theme the user
 * runs, including high contrast, without shipping a palette of its own. */
function styles(): string {
    return `
:root { color-scheme: light dark; }
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: var(--vscode-font-family);
  font-size: var(--vscode-font-size);
  color: var(--vscode-foreground);
  background: var(--vscode-editor-background);
}
.wrap { max-width: 1100px; margin: 0 auto; padding: 20px 24px 40px; display: flex; flex-direction: column; gap: 18px; }
header { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.25rem; font-weight: 600; font-family: var(--vscode-editor-font-family); }
h2 { margin: 0 0 8px; font-size: 0.85rem; font-weight: 600; text-transform: uppercase; letter-spacing: .06em; opacity: .75; }
.target { margin: 2px 0 0; opacity: .7; font-family: var(--vscode-editor-font-family); font-size: .85rem; }
.summary { margin: 0; line-height: 1.55; }
.muted, .meta, .method, .kpi-caption, .note { opacity: .65; }
.pill {
  display: inline-flex; align-items: center; gap: 6px; white-space: nowrap;
  padding: 4px 10px; border-radius: 999px; font-size: .78rem; font-weight: 600;
  border: 1px solid currentColor;
}
.pill.crit { color: var(--vscode-charts-red); }
.pill.warn { color: var(--vscode-charts-yellow); }
.pill.info { color: var(--vscode-charts-blue); }
.callout {
  border-left: 3px solid var(--vscode-charts-blue);
  background: var(--vscode-textBlockQuote-background, rgba(127,127,127,.08));
  padding: 12px 14px; border-radius: 0 4px 4px 0;
}
.callout p { margin: 0; line-height: 1.5; }
.callout-label { font-size: .72rem; text-transform: uppercase; letter-spacing: .07em; opacity: .65; margin-bottom: 4px; }
.callout .meta { margin-top: 6px; font-size: .8rem; }
.kpis { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; }
.kpi {
  border: 1px solid var(--vscode-panel-border, rgba(127,127,127,.3));
  border-radius: 6px; padding: 10px 12px; background: var(--vscode-editorWidget-background, transparent);
}
.kpi-label { font-size: .68rem; text-transform: uppercase; letter-spacing: .07em; opacity: .65; }
.kpi-value { font-size: 1.3rem; font-weight: 600; font-variant-numeric: tabular-nums; margin-top: 2px; }
.kpi-value.bad { color: var(--vscode-charts-red); }
.kpi-caption { font-size: .74rem; margin-top: 1px; }
.chart svg { width: 100%; height: auto; display: block; overflow: visible; }
.chart-empty { opacity: .65; padding: 24px 0; }
.legend { display: flex; flex-wrap: wrap; gap: 14px; font-size: .74rem; opacity: .8; margin-bottom: 6px; }
.legend span { display: inline-flex; align-items: center; gap: 5px; }
.sw { width: 10px; height: 10px; border-radius: 2px; display: inline-block; }
.sw.ok { background: var(--vscode-charts-blue); }
.sw.bad { background: var(--vscode-charts-red); }
.sw.miss { background: var(--vscode-charts-red); height: 4px; border-radius: 1px; }
.sw.line { height: 0; border-top: 2px dashed var(--vscode-descriptionForeground); border-radius: 0; }
rect.bar { fill: var(--vscode-charts-blue); }
rect.bar.bad { fill: var(--vscode-charts-red); }
rect.bar.dim { fill: var(--vscode-charts-blue); opacity: .45; }
rect.miss { fill: var(--vscode-charts-red); }
rect.bar:hover { filter: brightness(1.25); }
polyline.expected {
  fill: none; stroke: var(--vscode-descriptionForeground);
  stroke-width: 1.5; stroke-dasharray: 4 3; opacity: .8;
}
line.grid { stroke: var(--vscode-panel-border, rgba(127,127,127,.25)); stroke-width: 1; }
text.axis { fill: var(--vscode-descriptionForeground); font-size: 10px; }
.averages .avg-row { display: flex; flex-wrap: wrap; gap: 18px; }
.avg { display: flex; flex-direction: column; }
.avg span { font-size: .7rem; text-transform: uppercase; letter-spacing: .06em; opacity: .65; }
.avg b { font-size: 1rem; font-variant-numeric: tabular-nums; }
.note { font-size: .78rem; margin: 10px 0 0; line-height: 1.45; }
.signal { display: flex; gap: 10px; padding: 10px 0; border-bottom: 1px solid var(--vscode-panel-border, rgba(127,127,127,.2)); }
.signal:last-child { border-bottom: none; }
.signal .dot { line-height: 1.4; opacity: .5; }
.signal.fired .dot { color: var(--vscode-charts-red); opacity: 1; }
.signal-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.signal-body p { margin: 3px 0 0; line-height: 1.5; }
.signal-body .method { font-size: .76rem; margin-top: 2px; }
.tag {
  font-size: .62rem; text-transform: uppercase; letter-spacing: .07em;
  padding: 1px 5px; border-radius: 3px;
  background: var(--vscode-badge-background); color: var(--vscode-badge-foreground);
}
.tooltip {
  position: fixed; pointer-events: none; z-index: 10;
  background: var(--vscode-editorHoverWidget-background);
  color: var(--vscode-editorHoverWidget-foreground);
  border: 1px solid var(--vscode-editorHoverWidget-border);
  border-radius: 4px; padding: 7px 9px; font-size: .78rem; line-height: 1.45;
  box-shadow: 0 2px 8px rgba(0,0,0,.28); max-width: 280px;
}
.tooltip b { font-family: var(--vscode-editor-font-family); }
`;
}

/** The chart's hover readout. The series rides in a JSON script block rather than in per-node attributes, so
 * the markup stays small on a long window. */
function script(): string {
    return `
(function () {
  var raw = document.getElementById('series');
  if (!raw) { return; }
  var data = JSON.parse(raw.textContent || '[]');
  var tip = document.getElementById('tip');
  var n = function (v) { return Number(v || 0).toLocaleString(); };

  document.querySelectorAll('rect.bar').forEach(function (bar) {
    bar.addEventListener('mousemove', function (ev) {
      var p = data[Number(bar.getAttribute('data-i'))];
      if (!p) { return; }
      var lines = ['<b>' + p.d + '</b>'];
      lines.push(n(p.w) + ' written · ' + n(p.e) + ' expected');
      if (p.w > 0) { lines.push(n(p.i) + ' ins / ' + n(p.u) + ' upd / ' + n(p.x) + ' del'); }
      lines.push(p.r + ' run(s)' + (p.f > 0 ? ', ' + p.f + ' failed' : ''));
      if (p.b > 0) { lines.push(p.b + ' backfill run(s) excluded'); }
      if (p.t) { lines.push('Trimmed as reprocessing for fitting'); }
      if (p.n) { lines.push('Expected a load, got none'); }
      else if (p.a && p.s) { lines.push(p.s); }
      if (p.m) { lines.push('Still arriving; not flagged'); }
      tip.innerHTML = lines.join('<br>');
      tip.hidden = false;
      var x = ev.clientX + 14;
      var y = ev.clientY + 14;
      if (x + tip.offsetWidth > window.innerWidth - 8) { x = ev.clientX - tip.offsetWidth - 14; }
      if (y + tip.offsetHeight > window.innerHeight - 8) { y = ev.clientY - tip.offsetHeight - 14; }
      tip.style.left = x + 'px';
      tip.style.top = y + 'px';
    });
    bar.addEventListener('mouseleave', function () { tip.hidden = true; });
  });
})();
`;
}
