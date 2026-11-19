// Page 3 – Cần xử lý (CSKH + managers): lists of shipments that need attention.
import { get } from '../api.js';
import { icon } from '../icons.js';
import { esc, fmt, CARRIERS, statusChip, lateChip, skeleton, errorState, emptyState, freshness, markUpdated } from '../ui.js';

export const title = 'Cần xử lý';
export function destroy() {}

const VIEWS = {
  late:   { label: 'Giao trễ',             icon: 'clock',          params: { late: true },
            empty: 'Không có đơn giao trễ nào với bộ lọc này.' },
  failed: { label: 'Từng giao thất bại',   icon: 'triangle-alert', params: { hadFailedAttempt: true },
            empty: 'Không có đơn nào từng giao thất bại với bộ lọc này.' },
  overdue:{ label: 'Quá hạn, chưa giao',   icon: 'flag',           params: { overdue: true },
            empty: 'Không có đơn nào đang quá hạn mà chưa giao.' },
  all:    { label: 'Tất cả',               icon: 'package',        params: {},
            empty: 'Chưa có đơn nào trong khoảng thời gian này.' }
};
const LIMIT = 100;
const filters = { view: 'late', carrier: '', region: '', days: 30 };

export async function render(main) {
  main.innerHTML = `
    <header class="page-head">
      <div><h1>Đơn cần xử lý</h1><p class="muted">Bấm vào một đơn để xem hành trình chi tiết</p></div>
      <div class="toolbar">${freshness()}</div>
    </header>
    <section class="card filters" aria-label="Bộ lọc">
      <div class="segmented" role="radiogroup" aria-label="Loại đơn">
        ${Object.entries(VIEWS).map(([k, v]) => `<button type="button" role="radio" class="segmented__btn" data-view="${k}"
            aria-checked="${k === filters.view}">${icon(v.icon, { size: 16 })}<span>${v.label}</span></button>`).join('')}
      </div>
      <div class="filters__row">
        <label class="field"><span>Hãng</span>
          <select id="f-carrier"><option value="">Tất cả</option>${Object.entries(CARRIERS).map(([k, v]) =>
            `<option value="${k}" ${k === filters.carrier ? 'selected' : ''}>${v}</option>`).join('')}</select></label>
        <label class="field"><span>Bang (mã 2 chữ)</span>
          <input id="f-region" class="mono" maxlength="10" placeholder="VD: SP" value="${esc(filters.region)}"
                 pattern="[A-Za-z]{2,10}" aria-describedby="f-region-err"></label>
        <label class="field"><span>Dữ liệu nạp trong</span>
          <select id="f-days">${[7, 30, 90, 365].map(d => `<option value="${d}" ${d === filters.days ? 'selected' : ''}>${d} ngày</option>`).join('')}</select></label>
      </div>
      <p class="field__error" id="f-region-err" hidden>Mã bang gồm 2–10 chữ cái, ví dụ SP, RJ.</p>
    </section>
    <div id="at-body">${skeleton(6)}</div>`;

  main.querySelectorAll('.segmented__btn').forEach(btn => btn.addEventListener('click', () => {
    filters.view = btn.dataset.view;
    main.querySelectorAll('.segmented__btn').forEach(b => b.setAttribute('aria-checked', String(b === btn)));
    load(main);
  }));
  // Arrow keys move between the radio buttons (radiogroup keyboard pattern).
  main.querySelector('.segmented').addEventListener('keydown', e => {
    if (!['ArrowLeft', 'ArrowRight'].includes(e.key)) return;
    const btns = [...main.querySelectorAll('.segmented__btn')];
    const i = btns.indexOf(document.activeElement);
    const next = btns[(i + (e.key === 'ArrowRight' ? 1 : btns.length - 1)) % btns.length];
    next.focus(); next.click();
  });
  main.querySelector('#f-carrier').addEventListener('change', e => { filters.carrier = e.target.value; load(main); });
  main.querySelector('#f-days').addEventListener('change', e => { filters.days = +e.target.value; load(main); });
  const region = main.querySelector('#f-region');
  region.addEventListener('change', () => {
    const v = region.value.trim();
    const ok = v === '' || /^[A-Za-z]{2,10}$/.test(v);
    main.querySelector('#f-region-err').hidden = ok;
    region.setAttribute('aria-invalid', String(!ok));
    if (ok) { filters.region = v.toUpperCase(); load(main); }
  });
  main.querySelector('#refresh').addEventListener('click', () => load(main));
  await load(main);
}

async function load(main) {
  const body = main.querySelector('#at-body');
  body.innerHTML = skeleton(6);
  body.setAttribute('aria-busy', 'true');
  const v = VIEWS[filters.view];
  try {
    const data = await get('/shipments', {
      ...v.params, carrier: filters.carrier, region: filters.region, days: filters.days, limit: LIMIT
    });
    body.innerHTML = data.rows.length ? table(data.rows) : emptyState(v.empty, 'Thử chọn khoảng thời gian dài hơn hoặc bỏ bớt bộ lọc.', v.icon);
    // Whole row is clickable with the mouse; keyboard users use the link in the first cell.
    body.querySelector('tbody')?.addEventListener('click', e => {
      if (e.target.closest('a')) return;
      const a = e.target.closest('tr')?.querySelector('a.row-link');
      if (a) location.hash = a.getAttribute('href');
    });
    markUpdated();
  } catch (err) {
    body.innerHTML = errorState(err.message, 'at-retry');
    body.querySelector('#at-retry')?.addEventListener('click', () => load(main));
  } finally {
    body.removeAttribute('aria-busy');
  }
}

function table(rows) {
  const link = r => `#/lookup/${encodeURIComponent(r.carrier)}/${encodeURIComponent(r.trackingCode)}`;
  return `
    <p class="muted small" role="status">Hiển thị ${fmt.int(rows.length)} đơn${rows.length === LIMIT ? ` (tối đa ${LIMIT}, mới cập nhật nhất trước)` : ''}</p>
    <div class="card card--flush">
      <table class="table table--rows">
        <thead><tr>
          <th scope="col">Mã vận đơn</th><th scope="col">Hãng</th><th scope="col">Bang</th><th scope="col">Trạng thái</th>
          <th scope="col">Hẹn giao</th><th scope="col">Sự kiện mới nhất</th><th scope="col">Đúng hạn?</th><th scope="col" class="num">Lần giao lỗi</th>
        </tr></thead>
        <tbody>
          ${rows.map(r => `<tr>
            <td data-label="Mã vận đơn"><a class="mono row-link" href="${link(r)}">${esc(r.trackingCode)}${icon('chevron-right', { size: 14 })}</a></td>
            <td data-label="Hãng">${esc(CARRIERS[r.carrier] ?? r.carrier)}</td>
            <td data-label="Bang">${esc(r.region ?? '–')}</td>
            <td data-label="Trạng thái">${statusChip(r.status)}</td>
            <td data-label="Hẹn giao">${fmt.date(r.estimatedDelivery)}</td>
            <td data-label="Sự kiện mới nhất" class="mono small">${fmt.dateTime(r.lastEventAt)}</td>
            <td data-label="Đúng hạn?">${r.status === 'Delivered' ? lateChip(r.isLate) : '<span class="muted">Chưa giao</span>'}</td>
            <td data-label="Lần giao lỗi" class="num">${r.failedAttempts ? fmt.int(r.failedAttempts) : '–'}</td>
          </tr>`).join('')}
        </tbody>
      </table>
    </div>`;
}
