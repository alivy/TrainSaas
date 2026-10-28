import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { fetchAuthedImage, revokeImage } from '@/utils/blobAuth'

describe('blobAuth：受保护图片加载（Review Focus 3）', () => {
  const originalFetch = globalThis.fetch
  const originalCreate = URL.createObjectURL
  const originalRevoke = URL.revokeObjectURL

  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:mock-url')
    URL.revokeObjectURL = vi.fn()
  })
  afterEach(() => {
    globalThis.fetch = originalFetch
    URL.createObjectURL = originalCreate
    URL.revokeObjectURL = originalRevoke
    vi.clearAllMocks()
  })

  it('带 Authorization 头 fetch，成功返回 objectURL', async () => {
    const blob = new Blob(['x'], { type: 'image/png' })
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true, blob: () => Promise.resolve(blob) }) as never
    const url = await fetchAuthedImage('/api/files/vouchers/1', 'tok-abc')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/files/vouchers/1', {
      headers: { Authorization: 'Bearer tok-abc' }
    })
    expect(url).toBe('blob:mock-url')
  })

  it('非 2xx → 抛出带状态码的错误（404 跨租户凭据场景）', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 404,
      json: () => Promise.resolve({ detail: 'not found' })
    }) as never
    await expect(fetchAuthedImage('/api/files/vouchers/9', 'tok')).rejects.toThrow('404')
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })

  it('revokeImage 释放 objectURL', () => {
    revokeImage('blob:mock-url')
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:mock-url')
  })
})
