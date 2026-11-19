// Shared UI helpers: Vietnamese formatting, status chips (icon + text + color, never color alone),
// loading / empty / error states. Every value coming from the API goes through esc().
import { icon } from './icons.js';

export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const nf = new Intl.NumberFormat('vi-VN');
const pf = new Intl.NumberFormat('vi-VN', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const dtf = new Intl.DateTimeFormat('vi-VN', {
  timeZone: 'Asia/Ho_Chi_Minh', day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false
});
const tf = new Intl.DateTimeFormat('vi-VN', { timeZone: 'Asia/Ho_Chi_Minh', hour: '2-digit', minute: '2-digit', hour12: false });

export const fmt = {
  int: n => (n == null ? '–' : nf.format(n)),
  pct: n => (n == null || Number.isNaN(n) ? '–' : `${pf.format(n)} %`),
  days: n => (n == null ? '–' : `${pf.format(n)} ngày`),
  /** 'yyyy-MM-dd' (a calendar date, no time zone) → dd/MM/yyyy */
  date: s => (s ? s.slice(0, 10).split('-').reverse().join('/') : '–'),
  /** ISO timestamp → dd/MM/yyyy HH:mm in Vietnam time (vi-VN alone would print the time first) */
  dateTime: s => {
    if (!s) return '–';
    const p = Object.fromEntries(dtf.formatToParts(new Date(s)).map(x => [x.type, x.value]));
    return `${p.day}/${p.month}/${p.year} ${p.hour}:${p.minute}`;
  },
  time: d => tf.format(d)
};

export const CARRIERS = { ghn: 'GHN', ghtk: 'GHTK', jt: 'J&T' };

// tone → CSS class; icon + Vietnamese label so status never depends on color alone.
export const STATUS = {
  Created:        { label: 'Đã tạo đơn',      icon: 'package',        tone: 'neutral' },
  PickedUp:       { label: 'Đã lấy hàng',     icon: 'package-check',  tone: 'neutral' },
  InTransit:      { label: 'Đang vận chuyển', icon: 'truck',          tone: 'info' },
  OutForDelivery: { label: 'Đang giao',       icon: 'truck',          tone: 'info' },
  FailedAttempt:  { label: 'Giao thất bại',   icon: 'triangle-alert', tone: 'warn' },
  Delivered:      { label: 'Đã giao',         icon: 'circle-check',   tone: 'ok' },
  Returned:       { label: 'Hoàn hàng',       icon: 'undo-2',         tone: 'warn' },
  Cancelled:      { label: 'Đã huỷ',          icon: 'ban',            tone: 'neutral' }
};

export function chip(text, tone, iconName) {
  return `<span class="chip chip--${tone}">${iconName ? icon(iconName, { size: 14 }) : ''}<span>${esc(text)}</span></span>`;
}
export function statusChip(status) {
  const s = STATUS[status] ?? { label: status ?? 'Không rõ', icon: 'info', tone: 'neutral' };
  return chip(s.label, s.tone, s.icon);
}
export function lateChip(isLate) {
  if (isLate === true) return chip('Trễ hạn', 'late', 'clock');
  if (isLate === false) return chip('Đúng hạn', 'ok', 'circle-check');
  return '<span class="muted">–</span>';
}

/** Why an event did not change the state (server reasons are English, shown here in Vietnamese). */
export function outcomeText(outcome, reason = '') {
  if (outcome === 'Applied') return '';
  if (outcome === 'Stale') return 'Sự kiện cũ đến muộn – chỉ lưu lịch sử, không đổi trạng thái';
  if (outcome === 'Duplicate') return 'Bản gửi trùng – bỏ qua';
  if (reason.startsWith('backward transition')) return 'Bị chặn: trạng thái đi lùi (thường do quét nhầm)';
  if (reason.startsWith('shipment already')) return 'Bị chặn: đơn đã kết thúc trước đó';
  if (reason.startsWith('cannot cancel')) return 'Bị chặn: không thể huỷ sau khi đã lấy hàng';
  return 'Bị chặn – chỉ lưu lịch sử';
}

// ---------------- page states ----------------

export function skeleton(lines = 3) {
  return `<div class="skeleton" aria-hidden="true">${'<div class="skeleton__line"></div>'.repeat(lines)}</div>`;
}
export function emptyState(title, hint = '', iconName = 'search') {
  return `<div class="state">${icon(iconName, { size: 28 })}<p class="state__title">${esc(title)}</p>${hint ? `<p class="state__hint">${hint}</p>` : ''}</div>`;
}
export function errorState(message, retryId) {
  return `<div class="state state--error" role="alert">${icon('circle-x', { size: 28 })}
    <p class="state__title">${esc(message)}</p>
    ${retryId ? `<button class="btn" id="${retryId}" type="button">${icon('refresh-cw')}<span>Thử lại</span></button>` : ''}</div>`;
}

/** "Cập nhật lúc 14:05" + refresh button in the page header. */
export function freshness(id = 'refresh') {
  return `<div class="freshness"><span class="muted" id="${id}-time" aria-live="polite"></span>
    <button class="btn btn--ghost" id="${id}" type="button" title="Tải lại dữ liệu">${icon('refresh-cw')}<span class="sr-only">Tải lại dữ liệu</span></button></div>`;
}
export function markUpdated(id = 'refresh') {
  const el = document.getElementById(`${id}-time`);
  if (el) el.textContent = `Cập nhật lúc ${fmt.time(new Date())}`;
}

/** Read a CSS custom property (charts follow the light/dark theme). */
export function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}
