import type { Role } from '@/api/types'

export interface AdminMenuItem {
  path: string
  title: string
  icon: string
  roles: Role[]
}

const ALL: Role[] = ['Admin', 'Sales', 'Teacher', 'FrontDesk']

/**
 * 机构后台菜单（spec §8 权限矩阵；Ruling F3：无员工自助改密项）。
 * 顺序即菜单显示顺序，menuVisibility.spec.ts 钉死。
 */
const MENU: AdminMenuItem[] = [
  { path: '/admin/dashboard', title: '工作台', icon: 'Odometer', roles: ALL },
  { path: '/admin/products', title: '产品管理', icon: 'Goods', roles: ['Admin', 'Sales'] },
  { path: '/admin/intents', title: '线索管理', icon: 'Bell', roles: ['Admin', 'Sales', 'FrontDesk'] },
  { path: '/admin/students', title: '学员管理', icon: 'User', roles: ALL },
  { path: '/admin/orders', title: '订单管理', icon: 'Tickets', roles: ['Admin', 'Sales', 'FrontDesk'] },
  { path: '/admin/templates', title: '班级管理', icon: 'Calendar', roles: ['Admin'] },
  { path: '/admin/schedule', title: '周课表', icon: 'Clock', roles: ALL },
  { path: '/admin/consumptions', title: '核销记录', icon: 'List', roles: ALL },
  { path: '/admin/reports', title: '业绩报表', icon: 'TrendCharts', roles: ['Admin', 'Sales', 'Teacher'] },
  { path: '/admin/staff', title: '员工管理', icon: 'Avatar', roles: ['Admin'] },
  { path: '/admin/profile', title: '机构页设置', icon: 'Setting', roles: ['Admin'] }
]

export function menuForRole(role: Role): AdminMenuItem[] {
  return MENU.filter((m) => m.roles.includes(role))
}
