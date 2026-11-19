// Calls to the Shipment SLA API with the user's Cognito access token.
import { CONFIG } from './config.js';
import { accessToken, clearTokens, login } from './auth.js';

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

/** GET a JSON resource. `params` with null/undefined/false values are left out. */
export async function get(path, params = {}) {
  const token = await accessToken();
  if (!token) { await login(); throw new ApiError(401, 'Cần đăng nhập'); }

  const qs = new URLSearchParams(
    Object.entries(params).filter(([, v]) => v !== null && v !== undefined && v !== false && v !== '')
  ).toString();

  let res;
  try {
    res = await fetch(`${CONFIG.apiUrl}${path}${qs ? `?${qs}` : ''}`, {
      headers: { Authorization: `Bearer ${token}` }
    });
  } catch {
    throw new ApiError(0, 'Không kết nối được tới máy chủ. Kiểm tra mạng rồi thử lại.');
  }

  if (res.status === 401) {            // token revoked / expired despite refresh → log in again
    clearTokens();
    await login();
    throw new ApiError(401, 'Phiên đăng nhập đã hết hạn');
  }
  if (!res.ok) {
    let detail = '';
    try { detail = (await res.json()).error ?? ''; } catch { /* body is not JSON */ }
    throw new ApiError(res.status, messageFor(res.status, detail));
  }
  return res.json();
}

function messageFor(status, detail) {
  switch (status) {
    case 400: return `Bộ lọc không hợp lệ: ${detail}`;
    case 403: return 'Tài khoản của bạn không có quyền xem mục này.';
    case 404: return 'Không tìm thấy dữ liệu.';
    case 504: return 'Truy vấn mất quá nhiều thời gian. Hãy thử lại hoặc thu hẹp khoảng thời gian.';
    default: return `Máy chủ gặp lỗi (${status}). Hãy thử lại sau ít phút.`;
  }
}
