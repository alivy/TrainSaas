import { describe, it, expect, vi, beforeEach } from 'vitest'

vi.mock('element-plus', () => ({
  ElMessage: { error: vi.fn(), success: vi.fn(), warning: vi.fn() }
}))

import { ElMessage } from 'element-plus'
import {
  TOKEN_KEYS,
  makeRequestInterceptor,
  makeResponseErrorInterceptor,
  registerUnauthorizedHandler,
  clearUnauthorizedHandlers
} from '@/api/http'

function axiosError(status: number, data: unknown) {
  return {
    isAxiosError: true,
    response: { status, data },
    config: { headers: {} }
  } as never
}

describe('http 层：token 注入隔离（Review Focus 1）', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
    clearUnauthorizedHandlers()
  })

  it('orgApi 拦截器只附带 staff token', () => {
    localStorage.setItem(TOKEN_KEYS.staff, 'staff-tok')
    localStorage.setItem(TOKEN_KEYS.parent, 'parent-tok')
    localStorage.setItem(TOKEN_KEYS.platform, 'platform-tok')
    const interceptor = makeRequestInterceptor('staff')
    const config = interceptor({ headers: {} } as never) as { headers: Record<string, string> }
    expect(config.headers.Authorization).toBe('Bearer staff-tok')
  })

  it('parentApi 拦截器只附带 parent token', () => {
    localStorage.setItem(TOKEN_KEYS.staff, 'staff-tok')
    localStorage.setItem(TOKEN_KEYS.parent, 'parent-tok')
    const interceptor = makeRequestInterceptor('parent')
    const config = interceptor({ headers: {} } as never) as { headers: Record<string, string> }
    expect(config.headers.Authorization).toBe('Bearer parent-tok')
  })

  it('publicApi 拦截器不附带任何 Authorization', () => {
    localStorage.setItem(TOKEN_KEYS.staff, 'staff-tok')
    const interceptor = makeRequestInterceptor(null)
    const config = interceptor({ headers: {} } as never) as { headers: Record<string, string> }
    expect(config.headers.Authorization).toBeUndefined()
  })
})

describe('http 层：错误响应处理', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.clearAllMocks()
    clearUnauthorizedHandlers()
  })

  it('401 → 提示过期 + 只调用本区域的 unauthorized 处理器并 reject', async () => {
    const staffHandler = vi.fn()
    const parentHandler = vi.fn()
    registerUnauthorizedHandler('staff', staffHandler)
    registerUnauthorizedHandler('parent', parentHandler)
    const onError = makeResponseErrorInterceptor('staff')
    await expect(onError(axiosError(401, { title: 'unauthorized' }))).rejects.toBeTruthy()
    expect(staffHandler).toHaveBeenCalledOnce()
    expect(parentHandler).not.toHaveBeenCalled()
    expect(ElMessage.error).toHaveBeenCalledWith('登录已过期，请重新登录')
  })

  it('429 → 专用限流文案', async () => {
    const onError = makeResponseErrorInterceptor('public')
    await expect(onError(axiosError(429, { title: 'too many' }))).rejects.toBeTruthy()
    expect(ElMessage.error).toHaveBeenCalledWith('操作过于频繁，请稍后再试')
  })

  it('ProblemDetails → toast detail 优先于 title，并继续 reject', async () => {
    const onError = makeResponseErrorInterceptor('staff')
    await expect(
      onError(axiosError(409, { status: 409, title: 'Conflict', detail: '订单已处理' }))
    ).rejects.toBeTruthy()
    expect(ElMessage.error).toHaveBeenCalledWith('订单已处理')
  })

  it('无 detail 时回退 title，再无则通用文案', async () => {
    const onError = makeResponseErrorInterceptor('staff')
    await expect(onError(axiosError(400, { title: 'Bad Request' }))).rejects.toBeTruthy()
    expect(ElMessage.error).toHaveBeenCalledWith('Bad Request')
    await expect(onError(axiosError(500, {}))).rejects.toBeTruthy()
    expect(ElMessage.error).toHaveBeenCalledWith('请求失败，请稍后重试')
  })

  it('网络错误（无 response）→ 通用文案', async () => {
    const onError = makeResponseErrorInterceptor('staff')
    await expect(onError({ isAxiosError: true, config: {} } as never)).rejects.toBeTruthy()
    expect(ElMessage.error).toHaveBeenCalledWith('网络异常，请检查后端服务是否运行')
  })
})
