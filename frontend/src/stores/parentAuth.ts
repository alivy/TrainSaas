import { defineStore } from 'pinia'
import { parentLogin } from '@/api/parent'
import { TOKEN_KEYS } from '@/api/http'

export const useParentAuth = defineStore('parentAuth', {
  state: () => ({
    token: localStorage.getItem(TOKEN_KEYS.parent) as string | null,
    phone: '' as string,
    // T11 使用：当前选中的孩子（sessionStorage 持久化，刷新不丢）
    selectedChildId: Number(sessionStorage.getItem('ts_parent_child') ?? 0) || null
  }),
  actions: {
    async login(phone: string, password: string) {
      const res = await parentLogin(phone, password)
      this.token = res.token
      this.phone = res.phone
      localStorage.setItem(TOKEN_KEYS.parent, res.token)
    },
    selectChild(studentId: number) {
      this.selectedChildId = studentId
      sessionStorage.setItem('ts_parent_child', String(studentId))
    },
    logout() {
      this.token = null
      this.phone = ''
      this.selectedChildId = null
      localStorage.removeItem(TOKEN_KEYS.parent)
      sessionStorage.removeItem('ts_parent_child')
    }
  }
})
