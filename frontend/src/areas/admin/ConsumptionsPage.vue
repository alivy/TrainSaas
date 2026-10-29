<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import EnumTag from '@/components/EnumTag.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgConsumptionsList,
  orgReverseConsumption,
  orgStudentsList,
  orgStudentDetail,
  orgWalkIn
} from '@/api/org'
import { formatDateTime } from '@/utils/format'
import type { ConsumptionKind, ConsumptionItem, StudentItem, StudentPackItem } from '@/api/types'

const auth = useStaffAuth()
const isAdmin = computed(() => auth.role === 'Admin')
const canWalkIn = computed(() => auth.role === 'Admin' || auth.role === 'FrontDesk')

const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)
const filters = reactive<{ studentId: number | null; range: [string, string] | null; kind: ConsumptionKind | '' }>({
  studentId: null,
  range: null,
  kind: ''
})

function loader(page: number, pageSize: number) {
  return orgConsumptionsList({
    studentId: filters.studentId ?? undefined,
    from: filters.range?.[0] || undefined,
    to: filters.range?.[1] || undefined,
    kind: filters.kind || undefined,
    page,
    pageSize
  })
}

// 学员筛选（远程搜索）
const studentFilter = reactive({ q: '', loading: false, options: [] as StudentItem[] })
async function searchFilterStudents(q: string) {
  if (!q) return
  studentFilter.loading = true
  try {
    const res = await orgStudentsList({ q, page: 1, pageSize: 20 })
    studentFilter.options = res.items
  } finally {
    studentFilter.loading = false
  }
}

/** 本地日期串（后端 CreatedAt 为本地时间，不能用 toISOString 的 UTC 日期） */
function localToday(): string {
  const d = new Date()
  const p = (n: number) => (n < 10 ? `0${n}` : String(n))
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

/**
 * 撤销按钮可见性（启发式，后端强校验兜底）：
 * direction=+1 且（Admin 或 本人当天——前端无 operatorId，用 operatorName+当天近似）。
 */
function canReverse(row: ConsumptionItem): boolean {
  if (row.direction !== 1) return false
  if (isAdmin.value) return true
  const sameDay = row.createdAt.slice(0, 10) === localToday()
  return sameDay && row.operatorName === auth.profile?.name
}

const reverseDialog = reactive({ visible: false, target: null as ConsumptionItem | null, reason: '', busy: false })
function openReverse(row: ConsumptionItem) {
  reverseDialog.target = row
  reverseDialog.reason = ''
  reverseDialog.visible = true
}
async function submitReverse() {
  const reason = reverseDialog.reason.trim()
  if (!reason || reason.length > 200) {
    ElMessage.warning('请填写撤销原因（不超过 200 字）')
    return
  }
  reverseDialog.busy = true
  try {
    await orgReverseConsumption(reverseDialog.target!.id, reason)
    reverseDialog.visible = false
    ElMessage.success('已撤销，课时已恢复')
    tableRef.value?.reload(false)
  } catch {
    // 403/409 由拦截器 toast
  } finally {
    reverseDialog.busy = false
  }
}

// ===== 到店核销对话框 =====
const walkIn = reactive({
  visible: false,
  phone: '',
  searching: false,
  busy: false,
  studentId: null as number | null,
  studentName: '',
  packs: [] as StudentPackItem[],
  packId: null as number | null
})
function openWalkIn() {
  Object.assign(walkIn, { visible: true, phone: '', studentId: null, studentName: '', packs: [], packId: null })
}
async function walkInSearch() {
  if (!/^1\d{10}$/.test(walkIn.phone)) {
    ElMessage.warning('请输入正确的手机号')
    return
  }
  walkIn.searching = true
  try {
    const res = await orgStudentsList({ phone: walkIn.phone, page: 1, pageSize: 1 })
    const s = res.items[0]
    if (!s) {
      ElMessage.warning('未找到该手机号的学员')
      return
    }
    const detail = await orgStudentDetail(s.id)
    walkIn.studentId = s.id
    walkIn.studentName = detail.student.name
    walkIn.packs = detail.packs.filter((p) => p.status === 'Active' && p.remaining > 0)
    walkIn.packId = walkIn.packs[0]?.packId ?? null
    if (walkIn.packs.length === 0) ElMessage.warning('该学员没有可用课时包')
  } finally {
    walkIn.searching = false
  }
}
async function submitWalkIn() {
  if (!walkIn.studentId || !walkIn.packId) return
  walkIn.busy = true
  try {
    const res = await orgWalkIn(walkIn.studentId, walkIn.packId)
    ElMessage.success(`核销成功，剩余 ${res.remainingSessions} 节`)
    walkIn.visible = false
    tableRef.value?.reload()
  } catch {
    // 400（课时不足/已作废）由拦截器 toast
  } finally {
    walkIn.busy = false
  }
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select
        v-model="filters.studentId"
        filterable
        remote
        clearable
        reserve-keyword
        placeholder="按学员筛选（搜索）"
        :remote-method="searchFilterStudents"
        :loading="studentFilter.loading"
        style="width: 220px"
        @change="tableRef?.reload()"
      >
        <el-option v-for="s in studentFilter.options" :key="s.id" :label="`${s.name}（${s.phone}）`" :value="s.id" />
      </el-select>
      <el-date-picker
        v-model="filters.range"
        type="daterange"
        value-format="YYYY-MM-DD"
        start-placeholder="开始日期"
        end-placeholder="结束日期"
        style="width: 260px"
        @change="tableRef?.reload()"
      />
      <el-select v-model="filters.kind" placeholder="类型" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option label="课堂点名" value="RollCall" />
        <el-option label="到店核销" value="WalkIn" />
        <el-option label="冲正" value="Reversal" />
      </el-select>
      <el-button v-if="canWalkIn" type="success" @click="openWalkIn">到店核销</el-button>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column label="时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="学员" width="110">
        <template #default="{ row }">{{ row.studentName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="产品" min-width="140">
        <template #default="{ row }">{{ row.productName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="类型" width="110">
        <template #default="{ row }"><EnumTag kind="consumptionKind" :value="row.kind" /></template>
      </el-table-column>
      <el-table-column label="课时变化" width="100">
        <template #default="{ row }">
          <span :style="{ color: row.direction === 1 ? '#67c23a' : '#f56c6c', fontWeight: 600 }">
            {{ row.direction === 1 ? '-1' : '+1' }}
          </span>
        </template>
      </el-table-column>
      <el-table-column label="操作人" width="100">
        <template #default="{ row }">{{ row.operatorName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="原因" min-width="120" show-overflow-tooltip>
        <template #default="{ row }">{{ row.reason ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="操作" width="80" fixed="right">
        <template #default="{ row }">
          <el-button v-if="canReverse(row)" link type="danger" @click="openReverse(row)">撤销</el-button>
        </template>
      </el-table-column>
    </PagedTable>

    <!-- 撤销对话框 -->
    <el-dialog v-model="reverseDialog.visible" title="撤销核销" width="440px">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="撤销将生成一条冲正流水并恢复 1 课时，原流水保留（账目可审计）。"
        style="margin-bottom: 12px"
      />
      <el-input
        v-model="reverseDialog.reason"
        type="textarea"
        :rows="3"
        maxlength="200"
        show-word-limit
        placeholder="必填，如：点错学员"
      />
      <template #footer>
        <el-button @click="reverseDialog.visible = false">返回</el-button>
        <el-button type="danger" :loading="reverseDialog.busy" @click="submitReverse">确认撤销</el-button>
      </template>
    </el-dialog>

    <!-- 到店核销对话框 -->
    <el-dialog v-model="walkIn.visible" title="到店核销" width="440px" destroy-on-close>
      <div style="display: flex; gap: 8px; margin-bottom: 12px">
        <el-input v-model="walkIn.phone" placeholder="学员手机号" maxlength="11" @keyup.enter="walkInSearch" />
        <el-button type="primary" :loading="walkIn.searching" @click="walkInSearch">查找</el-button>
      </div>
      <template v-if="walkIn.studentId">
        <div style="margin-bottom: 8px">学员：<strong>{{ walkIn.studentName }}</strong></div>
        <el-select v-model="walkIn.packId" placeholder="选择课时包" style="width: 100%" :disabled="walkIn.packs.length === 0">
          <el-option
            v-for="p in walkIn.packs"
            :key="p.packId"
            :value="p.packId"
            :label="`${p.productName ?? '课时包'}（剩余 ${p.remaining}/${p.total}）`"
          />
        </el-select>
      </template>
      <template #footer>
        <el-button @click="walkIn.visible = false">取消</el-button>
        <el-button type="success" :disabled="!walkIn.packId" :loading="walkIn.busy" @click="submitWalkIn">
          核销一次
        </el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
</style>
