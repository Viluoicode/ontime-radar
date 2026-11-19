// Page 2 – Tra cứu đơn (CSKH + managers): one shipment's state and full timeline.
// Deep link: #/lookup/<carrier>/<tracking code>
import { get } from '../api.js';
import { icon } from '../icons.js';
import { esc, fmt, CARRIERS, STATUS, statusChip, lateChip, outcomeText, skeleton, errorState, emptyState } from '../ui.js';

export const title = 'Tra cứu đơn';
export function destroy() {}

export async function render(main, [carrier = 'ghn', code = ''] = []) {
  carrier = CARRIERS[carrier] ? carrier : 'ghn';
  main.innerHTML = `
    <header class="page-head">
      <div><h1>Tra cứu đơn</h1><p class="muted">Trạng thái hiện tại và toàn bộ lịch sử của một vận đơn</p></div>
    </header>
    <form class="card search" id="lk-form" role="search">
      <label class="field"><span>Hãng vận chuyển</span>
        <select name="carrier">${Object.entries(CARRIERS).map(([k, v]) => `<option value="${k}" ${k === carrier ? 'selected' : ''}>${v}</option>`).join('')}</select>
      </label>
      <label class="field field--grow"><span>Mã vận đơn</span>
        <input name="code" class="mono" required autocomplete="off" spellcheck="false" maxlength="64"
               placeholder="VD: GHN0407BE2C" value="${esc(code)}" aria-describedby="lk-hint">
      </label>
      <button class="btn btn--primary" type="submit">${icon('search')}<span>Tra cứu</span></button>
      <p class="field__hint" id="lk-hint">Mã đơn có thể lấy từ trang “Cần xử lý”.</p>
    </form>
    <div id="lk-result" aria-live="polite"></div>`;

  main.querySelector('#lk-form').addEventListener('submit', e => {
    e.preventDefault();
    const f = new FormData(e.target);
    const c = String(f.get('code')).trim();
    if (c) location.hash = `#/lookup/${encodeURIComponent(f.get('carrier'))}/${encodeURIComponent(c)}`;
  });

  const result = main.querySelector('#lk-result');
  if (!code) {
    result.innerHTML = emptyState('Nhập mã vận đơn để xem hành trình của đơn.', 'Kết quả gồm trạng thái hiện tại, hạn giao và mọi sự kiện hãng đã gửi.');
    return;
  }
  await load(result, carrier, code);
}

async function load(result, carrier, code) {
  result.innerHTML = skeleton(5);
  result.setAttribute('aria-busy', 'true');
  try {
    const data = await get(`/shipments/${encodeURIComponent(carrier)}/${encodeURIComponent(code)}`);
    result.innerHTML = view(data);
  } catch (err) {
    result.innerHTML = err.status === 404
      ? emptyState(`Không tìm thấy vận đơn ${code} của ${CARRIERS[carrier]}.`, 'Kiểm tra lại mã và hãng vận chuyển.', 'search')
      : errorState(err.message, 'lk-retry');
    result.querySelector('#lk-retry')?.addEventListener('click', () => load(result, carrier, code));
  } finally {
    result.removeAttribute('aria-busy');
  }
}

function view({ state: s, events }) {
  const ordered = [...events].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));   // newest first
  return `
    <section class="card shipment">
      <div class="shipment__head">
        <div>
          <p class="muted">${esc(CARRIERS[s.carrier] ?? s.carrier)}</p>
          <h2 class="mono">${esc(s.trackingCode)}</h2>
        </div>
        <div class="chips">${statusChip(s.status)} ${s.status === 'Delivered' ? lateChip(s.isLate) : ''}</div>
      </div>
      <dl class="facts">
        <div><dt>Hẹn giao</dt><dd>${fmt.date(s.estimatedDelivery)}</dd></div>
        <div><dt>Đã giao lúc</dt><dd>${fmt.dateTime(s.deliveredAt)}</dd></div>
        <div><dt>Sự kiện mới nhất</dt><dd>${fmt.dateTime(s.lastEventAt)}</dd></div>
        <div><dt>Giao tới</dt><dd>${esc(s.region ?? '–')}</dd></div>
        <div><dt>Mã đơn hàng</dt><dd class="mono small">${esc(s.orderId ?? '–')}</dd></div>
      </dl>
    </section>

    <section class="card">
      <div class="card__head"><h2>Hành trình</h2><span class="muted">${fmt.int(events.length)} sự kiện · mới nhất ở trên</span></div>
      <ol class="timeline">
        ${ordered.map(e => {
          const meta = { ...(STATUS[e.status] ?? { icon: 'info', tone: 'neutral' }) };
          if (e.status === 'Delivered' && e.outcome === 'Applied' && s.isLate) meta.tone = 'late';   // late delivery → red dot
          const note = outcomeText(e.outcome, e.reason);
          return `<li class="timeline__item ${e.outcome === 'Applied' ? '' : 'timeline__item--ignored'}">
            <span class="timeline__dot timeline__dot--${meta.tone}">${icon(meta.icon, { size: 14 })}</span>
            <div class="timeline__body">
              <div class="timeline__row">${statusChip(e.status)}<time class="mono small" datetime="${esc(e.occurredAt)}">${fmt.dateTime(e.occurredAt)}</time></div>
              <p class="small muted">Mã gốc của hãng: <span class="mono">${esc(e.rawStatus)}</span>${e.location ? ` · ${icon('map-pin', { size: 12 })} ${esc(e.location)}` : ''}</p>
              ${note ? `<p class="small timeline__note">${icon('info', { size: 12 })} ${esc(note)}</p>` : ''}
            </div>
          </li>`;
        }).join('')}
      </ol>
    </section>`;
}
