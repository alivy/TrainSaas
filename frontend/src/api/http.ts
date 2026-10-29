import axios, { type AxiosInstance, type InternalAxiosRequestConfig } from 'axios'
import { ElMessage } from 'element-plus'

/** localStorage 键名（Global Constraints 钉死，禁止更改） */
export const TOKEN_KEYS = {
  platform: 'ts_platform_token',
  staff: 'ts_staff_token',
  staffProfile: 'ts_staff_profile',
  parent: 'ts_parent_token'
} as const

export type TokenArea = 'platform' | 'staff' | 'parent'
export type ApiArea = TokenArea | 'public'

/** 区域 → token 存储键（router 守卫复用同一映射，避免两处硬编码） */
export const AREA_TOKEN_KEY: Record<TokenArea, string> = {
  platform: TOKEN_KEYS.platform,
  staff: TOKEN_KEYS.staff,
  parent: TOKEN_KEYS.parent
}

// 401 处理回调：由 main.ts 注册（store.logout + 跳本区域登录页），避免 http 层反向依赖 store/router
const unauthorizedHandlers = new Map<ApiArea, () => void>()

export function registerUnauthorizedHandler(area: ApiArea, fn: () => void): void {
  unauthorizedHandlers.set(area, fn)
}

export function clearUnauthorizedHandlers(): void {
  unauthorizedHandlers.clear()
}

/** 请求拦截器工厂：只附带本区域 token（Review Focus 1：token 绝不跨区） */
export function makeRequestInterceptor(area: TokenArea | null) {
  return (config: InternalAxiosRequestConfig): InternalAxiosRequestConfig => {
    if (area) {
      const token = localStorage.getItem(AREA_TOKEN_KEY[area])
      if (token) {
        config.headers.Authorization = `Bearer ${token}`
      }
    }
    return config
  }
}

/** 响应错误拦截器工厂：ProblemDetails → toast；401 → 本区域处理器；429 → 限流文案 */
export function makeResponseErrorInterceptor(area: ApiArea) {
  return (error: unknown): Promise<never> => {
    const ax = error as {
      isAxiosError?: boolean
      response?: { status: number; data?: { title?: string; detail?: string } }
      config?: { url?: string }
    }
    const response = ax?.response
    if (response) {
      const data = response.data ?? {}
      // 登录/改密端点的 401 是"凭据错误"业务语义：toast 后端 detail，绝不触发登出跳转（I2）
      const url = ax?.config?.url ?? ''
      const isCredentialCall = /\/auth\/login$|\/change-password$/.test(url)
      if (response.status === 401 && !isCredentialCall) {
        ElMessage.error('登录已过期，请重新登录')
        unauthorizedHandlers.get(area)?.()
      } else if (response.status === 429) {
        ElMessage.error('操作过于频繁，请稍后再试')
      } else {
        ElMessage.error(data.detail ?? data.title ?? '请求失败，请稍后重试')
      }
    } else {
      ElMessage.error('网络异常，请检查后端服务是否运行')
    }
    return Promise.reject(error ?? new Error('请求失败'))
  }
}

function createApi(area: ApiArea): AxiosInstance {
  const instance = axios.create({ timeout: 30000 })
  instance.interceptors.request.use(makeRequestInterceptor(area === 'public' ? null : area))
  instance.interceptors.response.use(undefined, makeResponseErrorInterceptor(area))
  return instance
}

export const platformApi = createApi('platform')
export const orgApi = createApi('staff')
export const parentApi = createApi('parent')
export const publicApi = createApi('public')
