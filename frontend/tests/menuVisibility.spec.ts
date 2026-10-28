import { describe, it, expect } from 'vitest'
import { menuForRole, type AdminMenuItem } from '@/layouts/adminMenu'
import type { Role } from '@/api/types'

function paths(role: Role): string[] {
  return menuForRole(role).map((m: AdminMenuItem) => m.path)
}

describe('AdminLayout 菜单可见性（spec §8 权限矩阵）', () => {
  it('Admin 看到全部 11 项', () => {
    expect(paths('Admin')).toEqual([
      '/admin/dashboard',
      '/admin/products',
      '/admin/intents',
      '/admin/students',
      '/admin/orders',
      '/admin/templates',
      '/admin/schedule',
      '/admin/consumptions',
      '/admin/reports',
      '/admin/staff',
      '/admin/profile'
    ])
  })

  it('Sales：工作台/产品/线索/学员/订单/课表/流水/报表（无班级、员工、设置）', () => {
    expect(paths('Sales')).toEqual([
      '/admin/dashboard',
      '/admin/products',
      '/admin/intents',
      '/admin/students',
      '/admin/orders',
      '/admin/schedule',
      '/admin/consumptions',
      '/admin/reports'
    ])
  })

  it('Teacher：工作台/学员/课表/流水/报表', () => {
    expect(paths('Teacher')).toEqual([
      '/admin/dashboard',
      '/admin/students',
      '/admin/schedule',
      '/admin/consumptions',
      '/admin/reports'
    ])
  })

  it('FrontDesk：工作台/线索/学员/订单/课表/流水', () => {
    expect(paths('FrontDesk')).toEqual([
      '/admin/dashboard',
      '/admin/intents',
      '/admin/students',
      '/admin/orders',
      '/admin/schedule',
      '/admin/consumptions'
    ])
  })

  it('每个菜单项都有标题与图标', () => {
    for (const item of menuForRole('Admin')) {
      expect(item.title, item.path).toBeTruthy()
      expect(item.icon, item.path).toBeTruthy()
    }
  })
})
