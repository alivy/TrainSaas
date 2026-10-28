import { defineStore } from 'pinia'
import { orgLogin, orgMe } from '@/api/org'
import { TOKEN_KEYS } from '@/api/http'
import type { Role, StaffProfile } from '@/api/types'

/** 持久化的员工档案：登录响应 + staffId（登录响应不含 staffId，登录后经 /me 补齐） */
export type StoredStaffProfile = StaffProfile & { staffId: number | null }

function loadProfile(): StoredStaffProfile | null {
  const raw = localStorage.getItem(TOKEN_KEYS.staffProfile)
  if (!raw) return null
  try {
    const parsed = JSON.parse(raw) as Partial<StoredStaffProfile>
    return { ...parsed, staffId: parsed.staffId ?? null } as StoredStaffProfile
  } catch {
    return null
  }
}

export const useStaffAuth = defineStore('staffAuth', {
  state: () => ({
    token: localStorage.getItem(TOKEN_KEYS.staff) as string | null,
    profile: loadProfile()
  }),
  getters: {
    role: (state): Role | null => state.profile?.role ?? null,
    is: (state) => (r: Role): boolean => state.profile?.role === r
  },
  actions: {
    async login(phone: string, password: string) {
      const res = await orgLogin(phone, password)
      const { token, ...rest } = res
      // 先落 token（orgMe 需要 Authorization 头），再补 staffId
      this.token = token
      localStorage.setItem(TOKEN_KEYS.staff, token)
      let staffId: number | null = null
      try {
        const me = await orgMe()
        staffId = me.staffId
      } catch {
        // /me 失败不阻断登录；staffId 缺失时页面按无 id 降级（自停用防护仍由后端兜底）
      }
      const profile: StoredStaffProfile = { ...rest, staffId }
      this.profile = profile
      localStorage.setItem(TOKEN_KEYS.staffProfile, JSON.stringify(profile))
    },
    logout() {
      this.token = null
      this.profile = null
      localStorage.removeItem(TOKEN_KEYS.staff)
      localStorage.removeItem(TOKEN_KEYS.staffProfile)
    }
  }
})
