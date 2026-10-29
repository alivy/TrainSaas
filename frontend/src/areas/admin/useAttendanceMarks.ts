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

  /**
   * 提交后 load() 会以服务器态重置本状态机（I1 修复）：
   * 用提交前的 payload 恢复失败项的用户所选标记（服务器未写入，本地保留便于修正后重交），
   * 重新记录失败原因，并把失败的名单外学员行加回列表（previousRows 提供其姓名/手机号）。
   */
  function restoreFailed(payload: MarkItemWire[], results: MarkResult[], previousRows?: AttendanceRow[]) {
    const failed = results.filter((r) => !r.ok)
    if (failed.length === 0) return
    const nextMarks = { ...marks.value }
    const nextErrors = { ...errors.value }
    for (const f of failed) {
      const chosen = payload.find((p) => p.studentId === f.studentId)
      if (!previousRows?.some((r) => r.studentId === f.studentId) && !rows.value.some((r) => r.studentId === f.studentId)) {
        // 名单外且行已丢失、又没给 previousRows —— 无法恢复行，仅记录错误
        if (f.error) nextErrors[f.studentId] = f.error
        continue
      }
      if (!rows.value.some((r) => r.studentId === f.studentId)) {
        const prev = previousRows!.find((r) => r.studentId === f.studentId)!
        rows.value = [...rows.value, prev]
      }
      if (chosen) nextMarks[f.studentId] = chosen.mark
      if (f.error) nextErrors[f.studentId] = f.error
    }
    marks.value = nextMarks
    errors.value = nextErrors
  }

  return { rows, marks, serverMarks, errors, init, setMark, addAdhoc, dirtyIds, isDirty, buildPayload, applyResults, restoreFailed }
}
