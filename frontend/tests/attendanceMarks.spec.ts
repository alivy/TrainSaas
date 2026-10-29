import { describe, it, expect } from 'vitest'
import { useAttendanceMarks } from '@/areas/admin/useAttendanceMarks'

const ROSTER = [
  { studentId: 1, name: '张小明', phone: '13800001111' },
  { studentId: 2, name: '张小红', phone: '13800001111' },
  { studentId: 3, name: '李华', phone: '13800002222' }
]

describe('useAttendanceMarks（点名状态机）', () => {
  it('init 合并名单与已有点名回显', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [{ studentId: 1, mark: 'Present' }])
    expect(m.rows.value).toHaveLength(3)
    expect(m.marks.value[1]).toBe('Present')
    expect(m.marks.value[2]).toBeUndefined()
  })

  it('未改动时 isDirty=false；改动后仅含变化的学员', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [{ studentId: 1, mark: 'Present' }])
    expect(m.isDirty.value).toBe(false)
    m.setMark(1, 'Present') // 与服务器一致
    expect(m.isDirty.value).toBe(false)
    m.setMark(2, 'Leave')
    expect(m.isDirty.value).toBe(true)
    expect(m.dirtyIds.value).toEqual([2])
    m.setMark(1, 'Absent') // 改判
    expect(m.dirtyIds.value.sort()).toEqual([1, 2])
  })

  it('buildPayload 输出所有已标记行，mark 大小写原样（后端敏感）', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [{ studentId: 1, mark: 'Present' }])
    m.setMark(2, 'Leave')
    const payload = m.buildPayload()
    expect(payload).toEqual([
      { studentId: 1, mark: 'Present' },
      { studentId: 2, mark: 'Leave' }
    ])
  })

  it('addAdhoc 名单外学员可加入并标记', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [])
    m.addAdhoc({ studentId: 9, name: '插班生', phone: '13800009999' })
    expect(m.rows.value).toHaveLength(4)
    expect(m.rows.value[3].adhoc).toBe(true)
    m.setMark(9, 'Present')
    expect(m.buildPayload()).toEqual([{ studentId: 9, mark: 'Present' }])
  })

  it('addAdhoc 重复添加幂等', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [])
    m.addAdhoc({ studentId: 1, name: '张小明', phone: '13800001111' })
    expect(m.rows.value).toHaveLength(3)
  })

  it('applyResults 记录逐项错误，成功项清空错误', () => {
    const m = useAttendanceMarks()
    m.init(ROSTER, [])
    m.setMark(1, 'Present')
    m.setMark(2, 'Present')
    m.applyResults([
      { studentId: 1, ok: true, error: null },
      { studentId: 2, ok: false, error: '无可用课时包' }
    ])
    expect(m.errors.value[2]).toBe('无可用课时包')
    expect(m.errors.value[1]).toBeUndefined()
    // 重试成功后错误清除
    m.applyResults([{ studentId: 2, ok: true, error: null }])
    expect(m.errors.value[2]).toBeUndefined()
  })
})
