import { enumValues, type EnumKind } from '@/api/types'

const moneyFormatter = new Intl.NumberFormat('zh-CN', { style: 'currency', currency: 'CNY' })

export function formatMoney(n: number | null | undefined): string {
  return n == null ? '' : moneyFormatter.format(n)
}

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n)
}

export function formatDate(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return ''
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return `${formatDate(iso)} ${pad2(d.getHours())}:${pad2(d.getMinutes())}`
}

const labels: Record<EnumKind, Record<string, string>> = {
  role: { Admin: '管理员', Sales: '销售', Teacher: '老师', FrontDesk: '前台' },
  productType: { Fixed: '固定排课', Flexible: '灵活核销' },
  orderStatus: { Pending: '待确认', Active: '已生效', Cancelled: '已取消', Voided: '已作废' },
  packStatus: { Active: '正常', Voided: '已作废' },
  institutionStatus: { Active: '启用', Disabled: '已停用' },
  sessionStatus: { Scheduled: '待上课', Completed: '已完成', Cancelled: '已取消' },
  enrollmentStatus: { Enrolled: '在班', Withdrawn: '已退班' },
  mark: { Present: '到课', Leave: '请假', Absent: '缺勤' },
  consumptionKind: { RollCall: '课堂点名', WalkIn: '到店核销', Reversal: '冲正' },
  intentStatus: { New: '新线索', Followed: '跟进中', Converted: '已成交', Invalid: '无效' }
}

export function enumLabel(kind: EnumKind, value: string): string {
  return labels[kind]?.[value] ?? value
}

export type TagType = 'primary' | 'success' | 'info' | 'warning' | 'danger'

const tagTypes: Record<EnumKind, Record<string, TagType>> = {
  role: { Admin: 'danger', Sales: 'primary', Teacher: 'success', FrontDesk: 'info' },
  productType: { Fixed: 'primary', Flexible: 'success' },
  orderStatus: { Pending: 'info', Active: 'success', Cancelled: 'warning', Voided: 'danger' },
  packStatus: { Active: 'success', Voided: 'danger' },
  institutionStatus: { Active: 'success', Disabled: 'danger' },
  sessionStatus: { Scheduled: 'info', Completed: 'success', Cancelled: 'warning' },
  enrollmentStatus: { Enrolled: 'success', Withdrawn: 'info' },
  mark: { Present: 'success', Leave: 'warning', Absent: 'danger' },
  consumptionKind: { RollCall: 'primary', WalkIn: 'success', Reversal: 'danger' },
  intentStatus: { New: 'warning', Followed: 'primary', Converted: 'success', Invalid: 'info' }
}

export function enumTagType(kind: EnumKind, value: string): TagType {
  return tagTypes[kind]?.[value] ?? 'info'
}

/** 完整性自检用的枚举值表（重导出，方便页面直接遍历筛选下拉） */
export { enumValues }
export type { EnumKind }

/** "HH:mm:ss"（后端 TimeOnly 序列化）→ "HH:mm"（提交/显示） */
export function toHHmm(timeOnly: string | null | undefined): string {
  if (!timeOnly) return ''
  return timeOnly.slice(0, 5)
}
