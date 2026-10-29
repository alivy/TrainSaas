<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useStaffAuth } from '@/stores/staffAuth'
import { orgSessionDetail, orgMarkAttendance, orgStudentsList } from '@/api/org'
import { formatDateTime, toHHmm, dayOfWeekLabel } from '@/utils/format'
import { useAttendanceMarks } from './useAttendanceMarks'
import type { SessionDetail, Mark, StudentItem } from '@/api/types'

const auth = useStaffAuth()
const route = useRoute()
const router = useRouter()

const detail = ref<SessionDetail | null>(null)
const loading = ref(false)
const submitting = ref(false)
const submitted = ref(false)

const marks = useAttendanceMarks()

// 权限（与后端一致）：Admin 或本节课老师
const canMark = computed(
  () => auth.role === 'Admin' || (detail.value != null && detail.value.teacherId === auth.profile?.staffId)
)

async function load() {
  loading.value = true
  try {
    detail.value = await orgSessionDetail(Number(route.params.id))
    marks.init(
      detail.value.roster.map((r) => ({ studentId: r.studentId, name: r.name, phone: r.phone })),
      detail.value.attendance.map((a) => ({ studentId: a.studentId, mark: a.mark }))
    )
    submitted.value = detail.value.status === 'Completed'
  } finally {
    loading.value = false
  }
}
onMounted(load)

const MARK_OPTIONS: { value: Mark; label: string; cls: string }[] = [
  { value: 'Present', label: '到课', cls: 'mark-present' },
  { value: 'Leave', label: '请假', cls: 'mark-leave' },
  { value: 'Absent', label: '缺勤', cls: 'mark-absent' }
]

// 名单外临时添加（插班/补课；后端校验须持该产品课时包）
const adhoc = reactive({ visible: false, q: '', loading: false, options: [] as StudentItem[] })
async function searchAdhoc() {
  if (!adhoc.q) return
  adhoc.loading = true
  try {
    const res = await orgStudentsList({ q: adhoc.q, page: 1, pageSize: 10 })
    adhoc.options = res.items.filter(
      (s) => !marks.rows.value.some((r) => r.studentId === s.id)
    )
  } finally {
    adhoc.loading = false
  }
}
function addAdhoc(s: StudentItem) {
  marks.addAdhoc({ studentId: s.id, name: s.name, phone: s.phone })
  adhoc.visible = false
  adhoc.q = ''
  adhoc.options = []
}

async function submit() {
  if (!detail.value) return
  const payload = marks.buildPayload()
  if (payload.length === 0) {
    ElMessage.warning('请先标记至少一名学员')
    return
  }
  submitting.value = true
  try {
    const res = await orgMarkAttendance(detail.value.id, payload)
    marks.applyResults(res.results)
    const failed = res.results.filter((r) => !r.ok)
    if (failed.length === 0) {
      ElMessage.success('点名已提交，课时已扣减')
    } else {
      ElMessage.warning(`${failed.length} 名学员处理失败，详见列表红字`)
    }
    submitted.value = true
    await load() // 刷新回显与状态
  } catch {
    // toast 已由拦截器处理
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div v-loading="loading" class="attendance-page">
    <el-page-header style="margin-bottom: 12px" @back="router.back()">
      <template #content>课程点名</template>
    </el-page-header>

    <template v-if="detail">
      <!-- 课程卡 -->
      <el-card shadow="never" class="course-card">
        <div class="course-title">{{ detail.template?.productName ?? '课程' }}</div>
        <div class="course-meta">
          {{ formatDateTime(detail.startAt) }} – {{ detail.endAt.slice(11, 16) }}
          <template v-if="detail.template">
            · {{ dayOfWeekLabel(detail.template.dayOfWeek) }} {{ toHHmm(detail.template.startTime) }}班
          </template>
        </div>
        <div class="course-meta">
          老师：{{ detail.teacherName ?? '—' }}
          <template v-if="detail.template?.room"> · 教室：{{ detail.template.room }}</template>
          ·
          <el-tag size="small" :type="detail.status === 'Completed' ? 'success' : 'info'">
            {{ detail.status === 'Completed' ? '已点名' : '待上课' }}
          </el-tag>
        </div>
      </el-card>

      <!-- 名单 -->
      <el-card shadow="never" style="margin-top: 12px">
        <template #header>
          <div class="roster-head">
            <span>学员名单（{{ marks.rows.value.length }}）</span>
            <el-button v-if="canMark" size="small" @click="adhoc.visible = true">临时添加</el-button>
          </div>
        </template>

        <div v-for="row in marks.rows.value" :key="row.studentId" class="roster-row">
          <div class="stu">
            <div class="stu-name">
              {{ row.name }}
              <el-tag v-if="row.adhoc" size="small" type="warning">临时</el-tag>
            </div>
            <div class="stu-phone">{{ row.phone }}</div>
            <div v-if="marks.errors.value[row.studentId]" class="stu-error">
              {{ marks.errors.value[row.studentId] }}
            </div>
          </div>
          <div class="mark-group">
            <button
              v-for="opt in MARK_OPTIONS"
              :key="opt.value"
              type="button"
              class="mark-btn"
              :class="[opt.cls, { active: marks.marks.value[row.studentId] === opt.value }]"
              :disabled="!canMark"
              @click="marks.setMark(row.studentId, opt.value)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <el-empty v-if="marks.rows.value.length === 0" description="班级还没有学员，请先在班级管理中分班" :image-size="60" />
      </el-card>

      <!-- 底部固定提交栏 -->
      <div v-if="canMark && marks.rows.value.length > 0" class="submit-bar">
        <div class="submit-info">
          已标记 {{ Object.keys(marks.marks.value).length }} 人
          <span v-if="marks.isDirty.value" class="dirty">· 有未提交改动</span>
          <span v-else-if="submitted" class="saved">· 与服务器一致</span>
        </div>
        <el-button
          type="primary"
          size="large"
          :loading="submitting"
          :disabled="Object.keys(marks.marks.value).length === 0"
          @click="submit"
        >
          {{ submitted ? '重新提交点名' : '提交点名' }}
        </el-button>
      </div>
      <el-alert
        v-if="submitted"
        style="margin-top: 12px"
        type="info"
        :closable="false"
        title="改判说明：到课改请假/缺勤会自动冲正已扣课时；请假改到课会补扣。全部以流水为准。"
      />
    </template>

    <!-- 临时添加对话框 -->
    <el-dialog v-model="adhoc.visible" title="临时添加学员（插班/补课）" width="400px" destroy-on-close>
      <div style="display: flex; gap: 8px; margin-bottom: 12px">
        <el-input v-model="adhoc.q" placeholder="姓名/手机号" @keyup.enter="searchAdhoc" />
        <el-button type="primary" :loading="adhoc.loading" @click="searchAdhoc">搜索</el-button>
      </div>
      <el-alert type="info" :closable="false" title="名单外学员须持有本课程产品的有效课时包，否则点名会失败。" style="margin-bottom: 8px" />
      <div v-for="s in adhoc.options" :key="s.id" class="adhoc-row" @click="addAdhoc(s)">
        {{ s.name }}（{{ s.phone }}）
      </div>
      <el-empty v-if="adhoc.options.length === 0 && adhoc.q" description="无匹配学员" :image-size="50" />
    </el-dialog>
  </div>
</template>

<style scoped>
.attendance-page {
  max-width: 720px;
  margin: 0 auto;
  padding-bottom: 84px; /* 给底部固定栏留位 */
}
.course-card .course-title {
  font-size: 17px;
  font-weight: 600;
}
.course-meta {
  margin-top: 4px;
  font-size: 13px;
  color: #606266;
}
.roster-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.roster-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  padding: 10px 0;
  border-bottom: 1px solid #f0f2f5;
}
.roster-row:last-child {
  border-bottom: none;
}
.stu-name {
  font-weight: 600;
  font-size: 15px;
}
.stu-phone {
  font-size: 12px;
  color: #909399;
}
.stu-error {
  font-size: 12px;
  color: #f56c6c;
  margin-top: 2px;
}
.mark-group {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
}
.mark-btn {
  min-width: 56px;
  min-height: 40px; /* 触控目标 ≥40px（Review Focus 5） */
  border-radius: 8px;
  border: 1px solid #dcdfe6;
  background: #fff;
  font-size: 14px;
  cursor: pointer;
  color: #606266;
}
.mark-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.mark-btn.mark-present.active {
  background: #67c23a;
  border-color: #67c23a;
  color: #fff;
}
.mark-btn.mark-leave.active {
  background: #e6a23c;
  border-color: #e6a23c;
  color: #fff;
}
.mark-btn.mark-absent.active {
  background: #f56c6c;
  border-color: #f56c6c;
  color: #fff;
}
.submit-bar {
  position: fixed;
  bottom: 0;
  left: 0;
  right: 0;
  background: #fff;
  border-top: 1px solid #e4e7ed;
  padding: 10px 16px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  z-index: 30;
}
.submit-info {
  font-size: 13px;
  color: #606266;
}
.dirty {
  color: #e6a23c;
}
.saved {
  color: #67c23a;
}
.adhoc-row {
  padding: 10px 8px;
  border-bottom: 1px solid #f0f2f5;
  cursor: pointer;
}
.adhoc-row:hover {
  background: #f5f7fa;
}
</style>
