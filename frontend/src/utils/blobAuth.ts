/**
 * 受保护图片加载：凭据文件 GET /api/files/vouchers/{id} 需要 Authorization 头，
 * <img src> 无法带头 —— 必须 fetch → blob → objectURL，组件卸载时 revokeImage 释放。
 * 公开图片（logo/cover）走 /api/files/images/{path}，可直接作为 <img src>，不经过这里。
 */
export async function fetchAuthedImage(url: string, token: string): Promise<string> {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } })
  if (!res.ok) {
    throw new Error(`图片加载失败: ${res.status}`)
  }
  const blob = await res.blob()
  return URL.createObjectURL(blob)
}

export function revokeImage(objectUrl: string): void {
  URL.revokeObjectURL(objectUrl)
}
