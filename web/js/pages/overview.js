// Page 1 – Tổng quan SLA (managers): KPIs, late-rate trend by delivery day, by carrier, by region.
import { get } from '../api.js';
import { icon } from '../icons.js';
import { esc, fmt, CARRIERS, skeleton, errorState, emptyState, freshness, markUpdated, cssVar } from '../ui.js';

let charts = [];
const filters = { days: 30, carrier: '' };

export const title = 'Tổng quan SLA';

export function destroy() { charts.forEach(c => c.destroy()); charts = []; }

export async function render(main) {
  main.innerHTML = `
    <header class="page-head">
      <div>
        <h1>Tổng quan SLA</h1>
        <p class="muted">Tỉ lệ giao đúng hạn theo hãng và khu vực · dữ liệu Olist 2016–2018 được phát lại</p>
      </div>
      <div class="toolbar">
        <label class="field field--inline"><span>Dữ liệu nạp trong</span>
          <select id="f-days">${[7, 30, 90, 365].map(d => `<option value="${d}" ${d === filters.days ? 'selected' : ''}>${d} ngày</option>`).join('')}</select>
        </label>
        <label class="field field--inline"><span>Hãng</span>
          <select id="f-carrier"><option value="">Tất cả</option>${Object.entries(CARRIERS).map(([k, v]) =>
            `<option value="${k}" ${k === filters.carrier ? 'selected' : ''}>${v}</option>`).join('')}</select>
        </label>
        ${freshness()}
      </div>
    </header>
    <div id="ov-body">${skeleton(6)}</div>`;

  main.querySelector('#f-days').addEventListener('change', e => { filters.days = +e.target.value; load(main); });
  main.querySelector('#f-carrier').addEventListener('change', e => { filters.carrier = e.target.value; load(main); });
  main.querySelector('#refresh').addEventListener('click', () => load(main));
  await load(main);
}

async function load(main) {
  const body = main.querySelector('#ov-body');
  destroy();
  body.innerHTML = skeleton(6);
  body.setAttribute('aria-busy', 'true');
  try {
    const [byCarrier, byRegion, trend] = await Promise.all([
      get('/sla/summary', { days: filters.days, groupBy: 'carrier' }),
      get('/sla/summary', { days: filters.days, groupBy: 'region' }),
      get('/sla/trend', { days: filters.days, carrier: filters.carrier })
    ]);
    body.innerHTML = view(byCarrier.rows, byRegion.rows, trend.rows);
    // Bar widths via CSSOM (not inline style attributes) so the page works with a strict CSP.
    body.querySelectorAll('.bar__fill[data-w]').forEach(el => { el.style.width = `${el.dataset.w}%`; });
    if (trend.rows.length) drawTrend(body.querySelector('#c-trend'), weekly(trend.rows));
    if (byCarrier.rows.length) drawCarriers(body.querySelector('#c-carrier'), byCarrier.rows);
    markUpdated();
  } catch (err) {
    body.innerHTML = errorState(err.message, 'ov-retry');
    body.querySelector('#ov-retry')?.addEventListener('click', () => load(main));
  } finally {
    body.removeAttribute('aria-busy');
  }
}

function view(carrierRows, regionRows, trendRows) {
  if (!carrierRows.length) {
    return emptyState('Chưa có đơn nào được giao trong khoảng thời gian này.',
      'Hãy chọn khoảng thời gian dài hơn, hoặc chờ Replayer nạp thêm dữ liệu (15 phút/lần).', 'package');
  }
  const scope = filters.carrier ? carrierRows.filter(r => r.group === filters.carrier) : carrierRows;
  const delivered = scope.reduce((s, r) => s + r.delivered, 0);
  const late = scope.reduce((s, r) => s + r.late, 0);
  const onTime = delivered ? 100 * (1 - late / delivered) : null;
  const daysWeighted = trendRows.filter(r => r.avgDeliveryDays != null);
  const totalW = daysWeighted.reduce((s, r) => s + r.delivered, 0);
  const avgDays = totalW ? daysWeighted.reduce((s, r) => s + r.avgDeliveryDays * r.delivered, 0) / totalW : null;
  const scopeLabel = filters.carrier ? CARRIERS[filters.carrier] : 'tất cả hãng';

  const topRegions = regionRows.filter(r => r.delivered >= 3).sort((a, b) => b.latePercent - a.latePercent).slice(0, 8);
  const maxRegionPct = Math.max(1, ...topRegions.map(r => r.latePercent));

  return `
    <section class="kpis" aria-label="Chỉ số chính (${esc(scopeLabel)})">
      ${kpi('Giao đúng hạn', fmt.pct(onTime), 'circle-check', onTime != null && onTime < 90 ? 'late' : 'ok')}
      ${kpi('Đã giao', fmt.int(delivered), 'package-check')}
      ${kpi('Giao trễ', fmt.int(late), 'clock', late ? 'late' : null)}
      ${kpi('Thời gian giao TB', fmt.days(avgDays), 'truck')}
    </section>

    <section class="card">
      <div class="card__head"><h2>Tỉ lệ trễ theo tuần giao hàng</h2><span class="muted">${esc(scopeLabel)} · cột: số đơn đã giao · tuần dưới ${MIN_WEEK} đơn không tính tỉ lệ</span></div>
      ${trendRows.length ? `<div class="chart chart--wide"><canvas id="c-trend" role="img" aria-label="Biểu đồ tỉ lệ giao trễ theo tuần giao hàng"></canvas></div>
        <p class="small muted chart-note">${icon('info', { size: 14 })} Các tuần cuối thường có tỉ lệ trễ cao bất thường: Replayer mới phát tới các đơn đặt gần đây,
          nên trong những tuần đó mới chỉ có các đơn giao chậm của lần đặt trước. Số liệu sẽ ổn định dần khi dữ liệu được phát thêm.</p>
        <details class="data-alt"><summary>Xem dạng bảng</summary>${trendTable(weekly(trendRows))}</details>`
        : emptyState('Không có dữ liệu xu hướng cho bộ lọc này.')}
    </section>

    <div class="grid-2">
      <section class="card">
        <div class="card__head"><h2>Tỉ lệ trễ theo hãng</h2></div>
        <div class="chart"><canvas id="c-carrier" role="img" aria-label="Biểu đồ tỉ lệ giao trễ theo hãng"></canvas></div>
        <details class="data-alt"><summary>Xem dạng bảng</summary>${summaryTable(carrierRows, 'Hãng', g => CARRIERS[g] ?? g)}</details>
      </section>
      <section class="card">
        <div class="card__head"><h2>Bang trễ nhiều nhất</h2><span class="muted">bang có ≥ 3 đơn đã giao</span></div>
        ${topRegions.length ? `<table class="table table--compact">
          <thead><tr><th scope="col">Bang</th><th scope="col" class="num">Đã giao</th><th scope="col" class="num">Trễ</th><th scope="col">Tỉ lệ trễ</th></tr></thead>
          <tbody>${topRegions.map(r => `<tr>
            <th scope="row">${esc(r.group)}</th><td class="num">${fmt.int(r.delivered)}</td><td class="num">${fmt.int(r.late)}</td>
            <td><div class="bar"><span class="bar__fill ${r.latePercent > 0 ? 'bar__fill--late' : ''}" data-w="${(100 * r.latePercent / maxRegionPct).toFixed(1)}"></span><span class="num">${fmt.pct(r.latePercent)}</span></div></td>
          </tr>`).join('')}</tbody></table>`
          : emptyState('Chưa bang nào đủ 3 đơn đã giao.')}
      </section>
    </div>`;
}

function kpi(label, value, iconName, tone) {
  return `<div class="kpi ${tone ? `kpi--${tone}` : ''}">
    <div class="kpi__label">${icon(iconName, { size: 16 })}<span>${esc(label)}</span></div>
    <div class="kpi__value num">${esc(value)}</div></div>`;
}

function summaryTable(rows, head, name) {
  return `<table class="table table--compact"><thead><tr><th scope="col">${head}</th><th scope="col" class="num">Đã giao</th>
    <th scope="col" class="num">Trễ</th><th scope="col" class="num">Tỉ lệ trễ</th></tr></thead><tbody>
    ${rows.map(r => `<tr><th scope="row">${esc(name(r.group))}</th><td class="num">${fmt.int(r.delivered)}</td>
      <td class="num">${fmt.int(r.late)}</td><td class="num">${fmt.pct(r.latePercent)}</td></tr>`).join('')}</tbody></table>`;
}

function trendTable(rows) {
  return `<table class="table table--compact"><thead><tr><th scope="col">Tuần từ</th><th scope="col" class="num">Đã giao</th>
    <th scope="col" class="num">Trễ</th><th scope="col" class="num">Tỉ lệ trễ</th><th scope="col" class="num">Giao TB</th></tr></thead><tbody>
    ${rows.map(r => `<tr><th scope="row">${fmt.date(r.day)}</th><td class="num">${fmt.int(r.delivered)}</td><td class="num">${fmt.int(r.late)}</td>
      <td class="num">${fmt.pct(r.latePercent)}</td><td class="num">${fmt.days(r.avgDeliveryDays)}</td></tr>`).join('')}</tbody></table>`;
}

// ---------------- weekly buckets ----------------
// Daily rates are noisy (1 late out of 1 delivery = 100 %). Group delivery days into ISO weeks
// (Monday start) and leave the rate empty for weeks with too few deliveries to mean anything.
const MIN_WEEK = 5;

function weekly(rows) {
  const weeks = new Map();
  for (const r of rows) {
    const [y, m, d] = r.day.split('-').map(Number);
    const date = new Date(Date.UTC(y, m - 1, d));
    date.setUTCDate(date.getUTCDate() - ((date.getUTCDay() + 6) % 7));       // back to Monday
    const key = date.toISOString().slice(0, 10);
    const w = weeks.get(key) ?? { day: key, delivered: 0, late: 0, daysSum: 0, daysW: 0 };
    w.delivered += r.delivered;
    w.late += r.late;
    if (r.avgDeliveryDays != null) { w.daysSum += r.avgDeliveryDays * r.delivered; w.daysW += r.delivered; }
    weeks.set(key, w);
  }
  return [...weeks.values()].sort((a, b) => a.day.localeCompare(b.day)).map(w => ({
    day: w.day,
    delivered: w.delivered,
    late: w.late,
    latePercent: w.delivered >= MIN_WEEK ? 100 * w.late / w.delivered : null,
    avgDeliveryDays: w.daysW ? w.daysSum / w.daysW : null
  }));
}

// ---------------- charts (Chart.js, colors follow the theme) ----------------

function baseOptions() {
  const text = cssVar('--color-muted-fg'), grid = cssVar('--color-border');
  return {
    locale: 'vi-VN',   // 0,5 instead of 0.5 on the axes
    responsive: true, maintainAspectRatio: false, animation: matchMedia('(prefers-reduced-motion: reduce)').matches ? false : { duration: 250 },
    interaction: { mode: 'index', intersect: false },
    plugins: { legend: { labels: { color: text, boxWidth: 12, font: { family: 'Fira Sans' } } },
               tooltip: { titleFont: { family: 'Fira Sans' }, bodyFont: { family: 'JetBrains Mono' } } },
    scales: { x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 16 }, grid: { display: false } },
              y: { ticks: { color: text }, grid: { color: grid } } }
  };
}

function drawTrend(canvas, rows) {
  const o = baseOptions();
  o.scales.y = { ...o.scales.y, position: 'left', min: 0, title: { display: true, text: 'Tỉ lệ trễ (%)', color: o.scales.y.ticks.color } };
  o.scales.y2 = { position: 'right', min: 0, grid: { display: false }, ticks: { color: o.scales.y.ticks.color, precision: 0 },
                  title: { display: true, text: 'Số đơn', color: o.scales.y.ticks.color } };
  o.plugins.tooltip.callbacks = {
    title: items => `Tuần từ ${items[0].label}`,
    label: c => c.dataset.yAxisID === 'y2' ? ` Đã giao: ${fmt.int(c.raw)}`
      : ` Tỉ lệ trễ: ${c.raw == null ? `– (dưới ${MIN_WEEK} đơn)` : fmt.pct(c.raw)}`
  };
  charts.push(new Chart(canvas, {
    data: {
      labels: rows.map(r => fmt.date(r.day)),
      datasets: [
        { type: 'line', label: 'Tỉ lệ trễ (%)', data: rows.map(r => r.latePercent), yAxisID: 'y',
          borderColor: cssVar('--color-late'), backgroundColor: cssVar('--color-late'), borderWidth: 2, pointRadius: rows.length > 60 ? 0 : 3, tension: 0.2 },
        { type: 'bar', label: 'Đã giao', data: rows.map(r => r.delivered), yAxisID: 'y2',
          backgroundColor: cssVar('--color-chart-muted'), borderRadius: 2 }
      ]
    },
    options: o
  }));
}

function drawCarriers(canvas, rows) {
  const o = baseOptions();
  o.indexAxis = 'y';
  o.plugins.legend.display = false;
  o.scales = { x: { ...o.scales.y, min: 0, title: { display: true, text: 'Tỉ lệ trễ (%)', color: o.scales.x.ticks.color } },
               y: { ...o.scales.x, grid: { display: false } } };
  o.plugins.tooltip.callbacks = { label: c => ` ${fmt.pct(c.raw)} · ${fmt.int(rows[c.dataIndex].late)}/${fmt.int(rows[c.dataIndex].delivered)} đơn trễ` };
  charts.push(new Chart(canvas, {
    type: 'bar',
    data: { labels: rows.map(r => CARRIERS[r.group] ?? r.group),
            datasets: [{ data: rows.map(r => r.latePercent), backgroundColor: cssVar('--color-primary'), borderRadius: 3, maxBarThickness: 28 }] },
    options: o
  }));
}

