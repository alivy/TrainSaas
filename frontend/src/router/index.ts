import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import Placeholder from '@/areas/Placeholder.vue'
import ForbiddenPage from '@/components/ForbiddenPage.vue'
import BlankLayout from '@/layouts/BlankLayout.vue'
import AdminLayout from '@/layouts/AdminLayout.vue'
import PlatformLayout from '@/layouts/PlatformLayout.vue'
import MobileLayout from '@/layouts/MobileLayout.vue'
import PlatformLoginPage from '@/areas/platform/LoginPage.vue'
import PlatformInstitutionsPage from '@/areas/platform/InstitutionsPage.vue'
import PlatformChangePasswordPage from '@/areas/platform/ChangePasswordPage.vue'
import AdminLoginPage from '@/areas/admin/LoginPage.vue'
import AdminDashboardPage from '@/areas/admin/DashboardPage.vue'
import AdminProductsPage from '@/areas/admin/ProductsPage.vue'
import AdminStaffPage from '@/areas/admin/StaffPage.vue'
import AdminStudentsPage from '@/areas/admin/StudentsPage.vue'
import AdminStudentDetailPage from '@/areas/admin/StudentDetailPage.vue'
import AdminIntentsPage from '@/areas/admin/IntentsPage.vue'
import AdminOrdersPage from '@/areas/admin/OrdersPage.vue'
import AdminOrderDetailPage from '@/areas/admin/OrderDetailPage.vue'
import AdminTemplatesPage from '@/areas/admin/TemplatesPage.vue'
import AdminWeekSchedulePage from '@/areas/admin/WeekSchedulePage.vue'
import AdminSessionAttendancePage from '@/areas/admin/SessionAttendancePage.vue'
import AdminConsumptionsPage from '@/areas/admin/ConsumptionsPage.vue'
import ParentLoginPage from '@/areas/parent/LoginPage.vue'
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

export const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/admin/login' },
  { path: '/forbidden', name: 'forbidden', component: ForbiddenPage, meta: { area: 'public', title: '无权访问' } },

  // ===== 平台超管 =====
  { path: '/platform/login', component: BlankLayout, meta: { area: 'public' }, children: [
    { path: '', name: 'platform-login', component: PlatformLoginPage, meta: { area: 'public', title: '平台登录' } }
  ] },
  { path: '/platform', component: PlatformLayout, meta: { area: 'platform' }, children: [
    { path: '', redirect: '/platform/institutions' },
    { path: 'institutions', name: 'platform-institutions', component: PlatformInstitutionsPage, meta: { title: '机构管理' } },
    { path: 'change-password', name: 'platform-change-password', component: PlatformChangePasswordPage, meta: { title: '修改密码' } }
  ] },

  // ===== 机构后台 =====
  { path: '/admin/login', component: BlankLayout, meta: { area: 'public' }, children: [
    { path: '', name: 'admin-login', component: AdminLoginPage, meta: { area: 'public', title: '机构登录' } }
  ] },
  { path: '/admin', component: AdminLayout, meta: { area: 'staff' }, children: [
    { path: '', redirect: '/admin/dashboard' },
    { path: 'dashboard', name: 'admin-dashboard', component: AdminDashboardPage, meta: { title: '工作台' } },
    { path: 'products', name: 'admin-products', component: AdminProductsPage, meta: { roles: ['Admin', 'Sales'], title: '产品管理' } },
    { path: 'staff', name: 'admin-staff', component: AdminStaffPage, meta: { roles: ['Admin'], title: '员工管理' } },
    { path: 'students', name: 'admin-students', component: AdminStudentsPage, meta: { title: '学员管理' } },
    { path: 'students/:id', name: 'admin-student-detail', component: AdminStudentDetailPage, meta: { title: '学员详情' } },
    { path: 'intents', name: 'admin-intents', component: AdminIntentsPage, meta: { roles: ['Admin', 'Sales', 'FrontDesk'], title: '线索管理' } },
    { path: 'orders', name: 'admin-orders', component: AdminOrdersPage, meta: { roles: ['Admin', 'Sales', 'FrontDesk'], title: '订单管理' } },
    { path: 'orders/:id', name: 'admin-order-detail', component: AdminOrderDetailPage, meta: { roles: ['Admin', 'Sales', 'FrontDesk'], title: '订单详情' } },
    { path: 'templates', name: 'admin-templates', component: AdminTemplatesPage, meta: { title: '班级管理' } },
    { path: 'schedule', name: 'admin-schedule', component: AdminWeekSchedulePage, meta: { title: '周课表' } },
    { path: 'sessions/:id', name: 'admin-session-attendance', component: AdminSessionAttendancePage, meta: { title: '课程点名' } },
    { path: 'consumptions', name: 'admin-consumptions', component: AdminConsumptionsPage, meta: { title: '核销记录' } },
    { path: 'reports', name: 'admin-reports', component: Placeholder, meta: { roles: ['Admin', 'Sales', 'Teacher'], title: '业绩报表' } },
    { path: 'profile', name: 'admin-profile', component: Placeholder, meta: { roles: ['Admin'], title: '机构页设置' } }
  ] },

  // ===== 公开介绍页 =====
  { path: '/org/:code', name: 'public-org', component: Placeholder, meta: { area: 'public', title: '机构介绍' } },

  // ===== 家长端 =====
  { path: '/parent/login', component: BlankLayout, meta: { area: 'public' }, children: [
    { path: '', name: 'parent-login', component: ParentLoginPage, meta: { area: 'public', title: '家长登录' } }
  ] },
  { path: '/parent', component: MobileLayout, meta: { area: 'parent' }, children: [
    { path: '', redirect: '/parent/children' },
    { path: 'children', name: 'parent-children', component: Placeholder, meta: { title: '我的孩子' } },
    { path: 'packs', name: 'parent-packs', component: Placeholder, meta: { title: '课时包' } },
    { path: 'records', name: 'parent-records', component: Placeholder, meta: { title: '课时记录' } },
    { path: 'schedule', name: 'parent-schedule', component: Placeholder, meta: { title: '课表' } },
    { path: 'me', name: 'parent-me', component: Placeholder, meta: { title: '我的' } }
  ] }
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

// 守卫规则（计划 Task 1/2）：public 放行；无 token → 本区域登录页（带 redirect）；
// meta.roles（含父级合并）不含当前角色 → /forbidden。401 时 http 层处理器负责清 store 并跳登录。
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
