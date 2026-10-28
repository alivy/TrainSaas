import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import zhCn from 'element-plus/es/locale/lang/zh-cn'

import App from './App.vue'
import { router } from './router'
import { registerUnauthorizedHandler } from './api/http'
import { usePlatformAuth } from './stores/platformAuth'
import { useStaffAuth } from './stores/staffAuth'
import { useParentAuth } from './stores/parentAuth'

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
app.use(router)
app.use(ElementPlus, { locale: zhCn })

// 401 → 只清本区域 store 并跳本区域登录页（Review Focus 1：绝不跨区）
const platformAuth = usePlatformAuth(pinia)
const staffAuth = useStaffAuth(pinia)
const parentAuth = useParentAuth(pinia)

registerUnauthorizedHandler('platform', () => {
  platformAuth.logout()
  router.push('/platform/login')
})
registerUnauthorizedHandler('staff', () => {
  staffAuth.logout()
  router.push('/admin/login')
})
registerUnauthorizedHandler('parent', () => {
  parentAuth.logout()
  router.push('/parent/login')
})
registerUnauthorizedHandler('public', () => {
  /* 公开区域无 token，401 仅提示 */
})

app.mount('#app')
