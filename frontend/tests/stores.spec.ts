import { describe, it, expect, vi, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'

vi.mock('@/api/platform', () => ({
  platformLogin: vi.fn(),
  platformChangePassword: vi.fn()
}))
vi.mock('@/api/org', () => ({
  orgLogin: vi.fn(),
  orgMe: vi.fn()
}))
vi.mock('@/api/parent', () => ({
  parentLogin: vi.fn(),
  parentChangePassword: vi.fn()
}))

import * as platformApi from '@/api/platform'
import * as orgApi from '@/api/org'
import * as parentApi from '@/api/parent'
import { TOKEN_KEYS } from '@/api/http'
import { usePlatformAuth } from '@/stores/platformAuth'
import { useStaffAuth } from '@/stores/staffAuth'
import { useParentAuth } from '@/stores/parentAuth'

describe('认证 stores', () => {
  beforeEach(() => {
    localStorage.clear()
    sessionStorage.clear()
    setActivePinia(createPinia())
    vi.clearAllMocks()
  })

  it('staffAuth.login 写入精确的 localStorage 键与 profile（staffId 来自 /me）', async () => {
    vi.mocked(orgApi.orgLogin).mockResolvedValue({
      token: 'tok-1',
      name: '机构管理员',
      role: 'Admin',
      institutionId: 1,
      institutionName: '演示机构',
      institutionCode: 'demo'
    })
    vi.mocked(orgApi.orgMe).mockResolvedValue({
      staffId: 42,
      name: '机构管理员',
      role: 'Admin',
      institutionId: 1,
      institutionName: '演示机构',
      institutionCode: 'demo'
    })
    const store = useStaffAuth()
    await store.login('13900000001', '123456')
    expect(localStorage.getItem(TOKEN_KEYS.staff)).toBe('tok-1')
    expect(localStorage.getItem(TOKEN_KEYS.staffProfile)).toContain('"institutionCode":"demo"')
    expect(localStorage.getItem(TOKEN_KEYS.staffProfile)).toContain('"staffId":42')
    expect(store.token).toBe('tok-1')
    expect(store.role).toBe('Admin')
    expect(store.profile?.staffId).toBe(42)
    expect(store.is('Admin')).toBe(true)
    expect(store.is('Teacher')).toBe(false)
  })

  it('staffAuth.logout 清空 token 与 profile', async () => {
    vi.mocked(orgApi.orgLogin).mockResolvedValue({
      token: 'tok-1', name: 'n', role: 'Sales', institutionId: 1,
      institutionName: 'i', institutionCode: 'c'
    })
    vi.mocked(orgApi.orgMe).mockResolvedValue({
      staffId: 7, name: 'n', role: 'Sales', institutionId: 1,
      institutionName: 'i', institutionCode: 'c'
    })
    const store = useStaffAuth()
    await store.login('13900000002', '123456')
    store.logout()
    expect(localStorage.getItem(TOKEN_KEYS.staff)).toBeNull()
    expect(localStorage.getItem(TOKEN_KEYS.staffProfile)).toBeNull()
    expect(store.token).toBeNull()
    expect(store.role).toBeNull()
  })

  it('staffAuth 从 localStorage 恢复会话（刷新页面场景）', () => {
    localStorage.setItem(TOKEN_KEYS.staff, 'persisted')
    localStorage.setItem(
      TOKEN_KEYS.staffProfile,
      JSON.stringify({ staffId: 3, name: '李老师', role: 'Teacher', institutionId: 1, institutionName: '演示机构', institutionCode: 'demo' })
    )
    const store = useStaffAuth()
    expect(store.token).toBe('persisted')
    expect(store.role).toBe('Teacher')
    expect(store.profile?.name).toBe('李老师')
  })

  it('platformAuth.login/logout 使用平台专属键', async () => {
    vi.mocked(platformApi.platformLogin).mockResolvedValue({ token: 'p-tok', name: 'admin' })
    const store = usePlatformAuth()
    await store.login('admin', '123456')
    expect(localStorage.getItem(TOKEN_KEYS.platform)).toBe('p-tok')
    expect(localStorage.getItem(TOKEN_KEYS.staff)).toBeNull()
    store.logout()
    expect(localStorage.getItem(TOKEN_KEYS.platform)).toBeNull()
  })

  it('parentAuth.login/logout 使用家长专属键', async () => {
    vi.mocked(parentApi.parentLogin).mockResolvedValue({ token: 'pa-tok', phone: '13800001111' })
    const store = useParentAuth()
    await store.login('13800001111', '123456')
    expect(localStorage.getItem(TOKEN_KEYS.parent)).toBe('pa-tok')
    expect(store.phone).toBe('13800001111')
    store.logout()
    expect(localStorage.getItem(TOKEN_KEYS.parent)).toBeNull()
  })

  it('parentAuth.selectedChildId 切换持久化到 sessionStorage，logout 清除', () => {
    const store = useParentAuth()
    store.selectChild(7)
    expect(sessionStorage.getItem('ts_parent_child')).toBe('7')
    expect(store.selectedChildId).toBe(7)
    store.logout()
    expect(sessionStorage.getItem('ts_parent_child')).toBeNull()
    expect(store.selectedChildId).toBeNull()
  })

  it('parentAuth 从 sessionStorage 恢复选中孩子（刷新场景）', () => {
    sessionStorage.setItem('ts_parent_child', '9')
    const store = useParentAuth()
    expect(store.selectedChildId).toBe(9)
  })
})
