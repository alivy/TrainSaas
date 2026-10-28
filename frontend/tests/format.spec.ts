import { describe, it, expect } from 'vitest'
import { formatMoney, formatDateTime, formatDate, enumLabel } from '@/utils/format'
import { enumValues } from '@/api/types'

describe('formatMoney', () => {
  it('人民币格式，两位小数', () => {
    expect(formatMoney(1980)).toBe('¥1,980.00')
    expect(formatMoney(299.5)).toBe('¥299.50')
    expect(formatMoney(0)).toBe('¥0.00')
  })
})

describe('formatDateTime / formatDate', () => {
  it('截到分钟', () => {
    expect(formatDateTime('2026-10-28T18:00:00')).toBe('2026-10-28 18:00')
  })
  it('带小数秒的后端本地 ISO 串同样截到分钟', () => {
    expect(formatDateTime('2026-10-28T13:06:39.0589337')).toBe('2026-10-28 13:06')
  })
  it('空值返回空串', () => {
    expect(formatDateTime(null)).toBe('')
    expect(formatDateTime(undefined)).toBe('')
    expect(formatDate(null)).toBe('')
  })
  it('formatDate 只取日期部分', () => {
    expect(formatDate('2026-10-28T18:00:00')).toBe('2026-10-28')
  })
})

describe('enumLabel', () => {
  it('常用标签', () => {
    expect(enumLabel('orderStatus', 'Pending')).toBe('待确认')
    expect(enumLabel('mark', 'Present')).toBe('到课')
    expect(enumLabel('consumptionKind', 'Reversal')).toBe('冲正')
    expect(enumLabel('role', 'FrontDesk')).toBe('前台')
  })
  it('未知值原样返回', () => {
    expect(enumLabel('orderStatus', 'Weird')).toBe('Weird')
  })
  it('每个枚举的每个成员都有中文标签（完整性）', () => {
    for (const [kind, values] of Object.entries(enumValues)) {
      for (const v of values) {
        const label = enumLabel(kind as never, v)
        expect(label, `${kind}.${v} 缺少标签`).toBeTruthy()
        expect(label, `${kind}.${v} 标签未翻译`).not.toBe(v)
      }
    }
  })
})
