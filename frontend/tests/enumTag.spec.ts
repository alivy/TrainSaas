import { describe, it, expect } from 'vitest'
import { enumTagType } from '@/utils/format'
import { enumValues } from '@/api/types'

const VALID_TAG_TYPES = ['primary', 'success', 'info', 'warning', 'danger']

describe('enumTagType（EnumTag 颜色映射）', () => {
  it('每个枚举的每个成员都有合法 tag 类型（完整性）', () => {
    for (const [kind, values] of Object.entries(enumValues)) {
      for (const v of values) {
        expect(VALID_TAG_TYPES, `${kind}.${v}`).toContain(enumTagType(kind as never, v))
      }
    }
  })

  it('关键语义色钉死', () => {
    expect(enumTagType('orderStatus', 'Pending')).toBe('info')
    expect(enumTagType('orderStatus', 'Active')).toBe('success')
    expect(enumTagType('orderStatus', 'Voided')).toBe('danger')
    expect(enumTagType('mark', 'Present')).toBe('success')
    expect(enumTagType('mark', 'Absent')).toBe('danger')
    expect(enumTagType('consumptionKind', 'Reversal')).toBe('danger')
    expect(enumTagType('intentStatus', 'New')).toBe('warning')
  })

  it('未知值回退 info', () => {
    expect(enumTagType('orderStatus', 'Weird')).toBe('info')
  })
})
