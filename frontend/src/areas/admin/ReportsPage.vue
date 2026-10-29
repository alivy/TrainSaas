<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useStaffAuth } from '@/stores/staffAuth'
import { orgSalesReport, orgTeachersReport } from '@/api/org'
import { formatMoney } from '@/utils/format'
import type { SalesReportRow, TeacherReportRow } from '@/api/types'

const auth = useStaffAuth()
const role = computed(() => auth.role)

function dayStr(d: Date): string {
  const p = (n: number) => (n < 10 ? `0${n}` : String(n))
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}
const now = new Date()
const range = ref<[string, string]>([dayStr(new Date(now.getFullYear(), now.getMonth(), 1)), dayStr(now)])

const activeTab = ref(role.value === 'Teacher' ? 'teachers' : 'sales')
const salesRows = ref<SalesReportRow[]>([])
const teacherRows = ref<TeacherReportRow[]>([])
const loading = ref(false)

const salesTotal = computed(() => salesRows.value.reduce((s, r) => s + r.totalAmount, 0))
const salesCount = computed(() => salesRows.value.reduce((s, r) => s + r.orderCount, 0))
const teacherSessions = computed(() => teacherRows.value.reduce((s, r) => s + r.completedSessions, 0))

async function load() {
  if (!range.value) return
  loading.value = true
  try {
    const [from, to] = range.value
    // 后端已按角色裁剪（Sales 仅自己行、Teacher 仅自己行），前端直接渲染
    if (role.value !== 'Teacher') salesRows.value = await orgSalesReport(from, to)
    if (role.value !== 'Sales') teacherRows.value = await orgTeachersReport(from, to)
  } finally {
    loading.value = false
  }
}
onMounted(load)
</script>

<template>
  <el-card shadow="never" v-loading="loading">
    <div class="toolbar">
      <el-date-picker
        v-model="range"
        type="daterange"
        value-format="YYYY-MM-DD"
        start-placeholder="开始"
        end-placeholder="结束"
        :clearable="false"
        style="width: 260px"
      />
      <el-button type="primary" @click="load">查询</el-button>
    </div>

    <el-tabs v-model="activeTab">
      <el-tab-pane v-if="role !== 'Teacher'" label="销售业绩" name="sales">
        <el-row :gutter="16" style="margin-bottom: 16px">
          <el-col :span="8">
            <el-card shadow="never"><el-statistic title="成单数" :value="salesCount" /></el-card>
          </el-col>
          <el-col :span="8">
            <el-card shadow="never">
              <div class="stat-title">成单金额（已确认订单）</div>
              <div class="stat-money">{{ formatMoney(salesTotal) }}</div>
            </el-card>
          </el-col>
        </el-row>
        <el-table :data="salesRows" stripe>
          <el-table-column prop="staffName" label="销售" min-width="120" />
          <el-table-column prop="orderCount" label="成单数" width="100" />
          <el-table-column label="成单金额" width="140">
            <template #default="{ row }">{{ formatMoney(row.totalAmount) }}</template>
          </el-table-column>
        </el-table>
        <el-empty v-if="salesRows.length === 0" description="该时间段没有已确认的订单" :image-size="60" />
      </el-tab-pane>

      <el-tab-pane v-if="role !== 'Sales'" label="教师课时" name="teachers">
        <el-row :gutter="16" style="margin-bottom: 16px">
          <el-col :span="8">
            <el-card shadow="never"><el-statistic title="已完成课次" :value="teacherSessions" /></el-card>
          </el-col>
        </el-row>
        <el-table :data="teacherRows" stripe>
          <el-table-column prop="teacherName" label="老师" min-width="120" />
          <el-table-column prop="completedSessions" label="已完成课次" width="120" />
          <el-table-column prop="presentMarks" label="到课点名数" width="120" />
        </el-table>
        <el-empty v-if="teacherRows.length === 0" description="该时间段没有已完成的课程" :image-size="60" />
      </el-tab-pane>
    </el-tabs>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}
.stat-title {
  font-size: 13px;
  color: #909399;
  margin-bottom: 8px;
}
.stat-money {
  font-size: 24px;
  font-weight: 600;
}
</style>
