import { computed, ref } from 'vue'
import type { Mark, MarkItemWire, MarkResult } from '@/api/types'

export interface AttendanceRow {
  studentId: number
  name: string
  phone: string
  adhoc?: boolean
}

/**
 * 点名状态机（SessionAttendancePage 的核心逻辑，独立可测）：
 * - init：合并班级名单与服务器已有点名（回显）
 * - setMark/addAdhoc：本地标记；dirtyIds 只含与服务器状态不同的学员
 * - buildPayload：提交所有已标记行（含未变化的——后端 Present→Present 幂等无操作、
 *   Leave→Leave 仅 upsert，改判由后端自动冲正，前端无需区分）
 * - applyResults：记录逐项错误（部分成功语义），成功项清除错误
 */
export function useAttendanceMarks() {
  const rows = ref<AttendanceRow[]>([])
  const marks = ref<Record<number, Mark>>({})
  const serverMarks = ref<Record<number, Mark>>({})
  const errors = ref<Record<number, string>>({})

  function init(
    roster: { studentId: number; name: string; phone: string }[],
    existing: { studentId: number; mark: Mark }[]
  ) {
    rows.value = roster.map((r) => ({ ...r }))
    const sm: Record<number, Mark> = {}
    for (const e of existing) sm[e.studentId] = e.mark
    serverMarks.value = sm
    marks.value = { ...sm }
    errors.value = {}
  }

  function setMark(studentId: number, mark: Mark) {
    marks.value = { ...marks.value, [studentId]: mark }
  }

  function addAdhoc(student: { studentId: number; name: string; phone: string }) {
    if (rows.value.some((r) => r.studentId === student.studentId)) return
    rows.value = [...rows.value, { ...student, adhoc: true }]
  }

  const dirtyIds = computed(() =>
    rows.value
      .map((r) => r.studentId)
      .filter((id) => marks.value[id] !== undefined && marks.value[id] !== serverMarks.value[id])
  )

  const isDirty = computed(() => dirtyIds.value.length > 0)

  function buildPayload(): MarkItemWire[] {
    return rows.value
      .filter((r) => marks.value[r.studentId] !== undefined)
      .map((r) => ({ studentId: r.studentId, mark: marks.value[r.studentId] }))
  }

  function applyResults(results: MarkResult[]) {
    const next: Record<number, string> = {}
    for (const r of results) {
      if (!r.ok && r.error) next[r.studentId] = r.error
    }
    errors.value = next
  }

  return { rows, marks, serverMarks, errors, init, setMark, addAdhoc, dirtyIds, isDirty, buildPayload, applyResults }
}
