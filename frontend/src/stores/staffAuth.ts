import { defineStore } from 'pinia'
import { orgLogin } from '@/api/org'
import { TOKEN_KEYS } from '@/api/http'
import type { Role, StaffProfile } from '@/api/types'

function loadProfile(): StaffProfile | null {
  const raw = localStorage.getItem(TOKEN_KEYS.staffProfile)
  if (!raw) return null
  try {
    return JSON.parse(raw) as StaffProfile
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
      const { token, ...profile } = res
      this.token = token
      this.profile = profile
      localStorage.setItem(TOKEN_KEYS.staff, token)
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
