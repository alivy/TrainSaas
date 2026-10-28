import { defineStore } from 'pinia'
import { platformLogin } from '@/api/platform'
import { TOKEN_KEYS } from '@/api/http'

export const usePlatformAuth = defineStore('platformAuth', {
  state: () => ({
    token: localStorage.getItem(TOKEN_KEYS.platform) as string | null,
    name: '' as string
  }),
  actions: {
    async login(username: string, password: string) {
      const res = await platformLogin(username, password)
      this.token = res.token
      this.name = res.name
      localStorage.setItem(TOKEN_KEYS.platform, res.token)
    },
    logout() {
      this.token = null
      this.name = ''
      localStorage.removeItem(TOKEN_KEYS.platform)
    }
  }
})
