import { describe, it, expect, vi, beforeEach } from 'vitest'

// 直接 mock http 层的 axios 实例，断言 api 模块构造的参数契约（Review Focus 4）
vi.mock('@/api/http', () => ({
  orgApi: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  parentApi: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  platformApi: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  publicApi: { get: vi.fn(), post: vi.fn(), put: vi.fn(), delete: vi.fn() },
  TOKEN_KEYS: { platform: 'ts_platform_token', staff: 'ts_staff_token', staffProfile: 'ts_staff_profile', parent: 'ts_parent_token' },
  makeRequestInterceptor: vi.fn(),
  makeResponseErrorInterceptor: vi.fn(),
  registerUnauthorizedHandler: vi.fn(),
  clearUnauthorizedHandlers: vi.fn()
}))

import { orgApi, parentApi } from '@/api/http'
import { orgOrdersList, orgSessionsList, orgMarkAttendance } from '@/api/org'
import { parentRecords } from '@/api/parent'

describe('api 参数构造契约', () => {
  beforeEach(() => vi.clearAllMocks())

  it('订单列表：枚举传名称字符串、分页默认 pageSize=20', async () => {
    vi.mocked(orgApi.get).mockResolvedValue({ data: { total: 0, items: [] } })
    await orgOrdersList({ status: 'Pending', page: 2 })
    expect(orgApi.get).toHaveBeenCalledWith('/api/org/orders', {
      params: { status: 'Pending', page: 2, pageSize: 20, studentId: undefined }
    })
  })

  it('课表列表：日期参数为 yyyy-MM-dd 原样传递', async () => {
    vi.mocked(orgApi.get).mockResolvedValue({ data: [] })
    await orgSessionsList('2026-10-28', '2026-11-04')
    expect(orgApi.get).toHaveBeenCalledWith('/api/org/sessions', {
      params: { from: '2026-10-28', to: '2026-11-04' }
    })
  })

  it('点名：mark 大小写原样进 body（后端大小写敏感）', async () => {
    vi.mocked(orgApi.post).mockResolvedValue({ data: { results: [] } })
    await orgMarkAttendance(5, [
      { studentId: 1, mark: 'Present' },
      { studentId: 2, mark: 'Leave' }
    ])
    expect(orgApi.post).toHaveBeenCalledWith('/api/org/sessions/5/attendance', {
      marks: [
        { studentId: 1, mark: 'Present' },
        { studentId: 2, mark: 'Leave' }
      ]
    })
  })

  it('家长流水：studentId 必带、分页参数齐全', async () => {
    vi.mocked(parentApi.get).mockResolvedValue({ data: { total: 0, items: [] } })
    await parentRecords(7, { page: 1 })
    expect(parentApi.get).toHaveBeenCalledWith('/api/parent/records', {
      params: { studentId: 7, page: 1, pageSize: 20, from: undefined, to: undefined }
    })
  })
})
