const ALLOWED: Record<string, string[]> = {
  'image/jpeg': ['.jpg', '.jpeg'],
  'image/png': ['.png'],
  'image/webp': ['.webp']
}
const MAX_BYTES = 5 * 1024 * 1024

/**
 * 图片上传前端预检（与后端 FileStorageService 校验一致：类型白名单、扩展名匹配、≤5MB）。
 * 返回 null 表示通过；否则返回用户可见的错误文案。
 */
export function validateImageFile(file: File): string | null {
  const allowedExts = ALLOWED[file.type]
  if (!allowedExts) {
    return '仅支持 jpg/png/webp 图片'
  }
  if (file.size > MAX_BYTES) {
    return '图片不能超过 5MB'
  }
  const lower = file.name.toLowerCase()
  if (!allowedExts.some((ext) => lower.endsWith(ext))) {
    return '文件扩展名与图片类型不匹配'
  }
  return null
}
