import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import Placeholder from '@/areas/Placeholder.vue'
import { AREA_TOKEN_KEY, type ApiArea } from '@/api/http'
import type { Role } from '@/api/types'
import { useStaffAuth } from '@/stores/staffAuth'

declare module 'vue-router' {
  interface RouteMeta {
    area?: ApiArea
    roles?: Role[]
    title?: string
  }
}

// T1：全部路由先注册（占位组件），后续任务逐个替换为真实页面；T2 引入布局嵌套。
export const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/admin/login' },
  { path: '/forbidden', name: 'forbidden', component: Placeholder, meta: { area: 'public', title: '无权访问' } },

  // ===== 平台超管 =====
  { path: '/platform/login', name: 'platform-login', component: Placeholder, meta: { area: 'public', title: '平台登录' } },
  { path: '/platform/institutions', name: 'platform-institutions', component: Placeholder, meta: { area: 'platform', title: '机构管理' } },
  { path: '/platform/change-password', name: 'platform-change-password', component: Placeholder, meta: { area: 'platform', title: '修改密码' } },

  // ===== 机构后台 =====
  { path: '/admin/login', name: 'admin-login', component: Placeholder, meta: { area: 'public', title: '机构登录' } },
  { path: '/admin/dashboard', name: 'admin-dashboard', component: Placeholder, meta: { area: 'staff', title: '工作台' } },
  { path: '/admin/products', name: 'admin-products', component: Placeholder, meta: { area: 'staff', roles: ['Admin', 'Sales'], title: '产品管理' } },
  { path: '/admin/staff', name: 'admin-staff', component: Placeholder, meta: { area: 'staff', roles: ['Admin'], title: '员工管理' } },
  { path: '/admin/students', name: 'admin-students', component: Placeholder, meta: { area: 'staff', title: '学员管理' } },
  { path: '/admin/students/:id', name: 'admin-student-detail', component: Placeholder, meta: { area: 'staff', title: '学员详情' } },
  { path: '/admin/intents', name: 'admin-intents', component: Placeholder, meta: { area: 'staff', roles: ['Admin', 'Sales', 'FrontDesk'], title: '线索管理' } },
  { path: '/admin/orders', name: 'admin-orders', component: Placeholder, meta: { area: 'staff', roles: ['Admin', 'Sales', 'FrontDesk'], title: '订单管理' } },
  { path: '/admin/orders/:id', name: 'admin-order-detail', component: Placeholder, meta: { area: 'staff', roles: ['Admin', 'Sales', 'FrontDesk'], title: '订单详情' } },
  { path: '/admin/templates', name: 'admin-templates', component: Placeholder, meta: { area: 'staff', title: '班级管理' } },
  { path: '/admin/schedule', name: 'admin-schedule', component: Placeholder, meta: { area: 'staff', title: '周课表' } },
  { path: '/admin/sessions/:id', name: 'admin-session-attendance', component: Placeholder, meta: { area: 'staff', title: '课程点名' } },
  { path: '/admin/consumptions', name: 'admin-consumptions', component: Placeholder, meta: { area: 'staff', title: '核销记录' } },
  { path: '/admin/reports', name: 'admin-reports', component: Placeholder, meta: { area: 'staff', roles: ['Admin', 'Sales', 'Teacher'], title: '业绩报表' } },
  { path: '/admin/profile', name: 'admin-profile', component: Placeholder, meta: { area: 'staff', roles: ['Admin'], title: '机构页设置' } },

  // ===== 公开介绍页 =====
  { path: '/org/:code', name: 'public-org', component: Placeholder, meta: { area: 'public', title: '机构介绍' } },

  // ===== 家长端 =====
  { path: '/parent/login', name: 'parent-login', component: Placeholder, meta: { area: 'public', title: '家长登录' } },
  { path: '/parent/children', name: 'parent-children', component: Placeholder, meta: { area: 'parent', title: '我的孩子' } },
  { path: '/parent/packs', name: 'parent-packs', component: Placeholder, meta: { area: 'parent', title: '课时包' } },
  { path: '/parent/records', name: 'parent-records', component: Placeholder, meta: { area: 'parent', title: '课时记录' } },
  { path: '/parent/schedule', name: 'parent-schedule', component: Placeholder, meta: { area: 'parent', title: '课表' } },
  { path: '/parent/me', name: 'parent-me', component: Placeholder, meta: { area: 'parent', title: '我的' } }
]

export const router = createRouter({
  history: createWebHistory(),
  routes
})

const LOGIN_PATH: Record<Exclude<ApiArea, 'public'>, string> = {
  platform: '/platform/login',
  staff: '/admin/login',
  parent: '/parent/login'
}

// 守卫规则（计划 Task 1）：public 直接放行；无 token → 本区域登录页（带 redirect）；
// meta.roles 不含当前角色 → /forbidden。401 时 http 层处理器负责清 store 并跳登录（main.ts 注册）。
router.beforeEach((to) => {
  const area = to.meta.area ?? 'public'
  if (area === 'public') return true

  const token = localStorage.getItem(AREA_TOKEN_KEY[area])
  if (!token) {
    return { path: LOGIN_PATH[area], query: { redirect: to.fullPath } }
  }
  if (area === 'staff' && to.meta.roles) {
    const role = useStaffAuth().role
    if (!role || !to.meta.roles.includes(role)) {
      return { path: '/forbidden' }
    }
  }
  return true
})
