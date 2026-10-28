<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgSessionsList,
  orgOrdersList,
  orgSalesReport,
  orgIntentsList,
  orgStudentsList,
  orgStudentDetail,
  orgWalkIn,
  orgConsumptionsList
} from '@/api/org'
import { formatMoney, formatDateTime } from '@/utils/format'
import EnumTag from '@/components/EnumTag.vue'
import type { SessionListItem, ConsumptionItem, StudentPackItem } from '@/api/types'

const auth = useStaffAuth()
const router = useRouter()
const role = computed(() => auth.role)

function dayStr(d: Date): string {
  const p = (n: number) => (n < 10 ? `0${n}` : String(n))
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}
const today = dayStr(new Date())
const monthStart = dayStr(new Date(new Date().getFullYear(), new Date().getMonth(), 1))

// ===== Admin =====
const adminStats = reactive({ todaySessions: 0, pendingOrders: 0, monthAmount: 0 })
// ===== Sales =====
const salesStats = reactive({ monthCount: 0, monthAmount: 0, newIntents: 0 })
// ===== Teacher =====
const todaySessions = ref<SessionListItem[]>([])
// ===== FrontDesk =====
const punch = reactive({
  phone: '',
  searching: false,
  busy: false,
  studentName: '' as string,
  studentId: 0 as number | null,
  packs: [] as StudentPackItem[],
  selectedPackId: null as number | null
})
const todayConsumptions = ref<ConsumptionItem[]>([])

async function loadAdmin() {
  const [sessions, pending, report] = await Promise.all([
    orgSessionsList(today, today),
    orgOrdersList({ status: 'Pending', page: 1, pageSize: 1 }),
    orgSalesReport(monthStart, today)
  ])
  adminStats.todaySessions = sessions.length
  adminStats.pendingOrders = pending.total
  adminStats.monthAmount = report.reduce((s, r) => s + r.totalAmount, 0)
}

async function loadSales() {
  const [report, intents] = await Promise.all([
    orgSalesReport(monthStart, today),
    orgIntentsList({ status: 'New', page: 1, pageSize: 1 })
  ])
  salesStats.monthCount = report[0]?.orderCount ?? 0
  salesStats.monthAmount = report[0]?.totalAmount ?? 0
  salesStats.newIntents = intents.total
}

async function loadTeacher() {
  todaySessions.value = await orgSessionsList(today, today)
}

async function loadFrontDesk() {
  const res = await orgConsumptionsList({ from: today, to: today, page: 1, pageSize: 10 })
  todayConsumptions.value = res.items
}

async function searchStudent() {
  if (!/^1\d{10}$/.test(punch.phone)) {
    ElMessage.warning('请输入正确的手机号')
    return
  }
  punch.searching = true
  try {
    const res = await orgStudentsList({ phone: punch.phone, page: 1, pageSize: 1 })
    const student = res.items[0]
    if (!student) {
      ElMessage.warning('未找到该手机号的学员')
      punch.studentName = ''
      punch.studentId = null
      punch.packs = []
      punch.selectedPackId = null
      return
    }
    const detail = await orgStudentDetail(student.id)
    punch.studentName = detail.student.name
    punch.studentId = student.id
    punch.packs = detail.packs.filter((p) => p.status === 'Active' && p.remaining > 0)
    punch.selectedPackId = punch.packs[0]?.packId ?? null
    if (punch.packs.length === 0) {
      ElMessage.warning('该学员没有可用课时包')
    }
  } finally {
    punch.searching = false
  }
}

async function doWalkIn() {
  if (!punch.studentId || !punch.selectedPackId) return
  punch.busy = true
  try {
    const res = await orgWalkIn(punch.studentId, punch.selectedPackId)
    ElMessage.success(`核销成功，剩余 ${res.remainingSessions} 节`)
    // 本地刷新所选包余额 + 今日流水
    const pack = punch.packs.find((p) => p.packId === punch.selectedPackId)
    if (pack) pack.remaining = res.remainingSessions
    if (pack && pack.remaining <= 0) {
      punch.packs = punch.packs.filter((p) => p.packId !== pack.packId)
      punch.selectedPackId = punch.packs[0]?.packId ?? null
    }
    loadFrontDesk()
  } catch {
    // toast 已由拦截器处理
  } finally {
    punch.busy = false
  }
}

onMounted(() => {
  if (role.value === 'Admin') loadAdmin()
  else if (role.value === 'Sales') loadSales()
  else if (role.value === 'Teacher') loadTeacher()
  else if (role.value === 'FrontDesk') loadFrontDesk()
})
</script>

<template>
  <div class="dashboard">
    <!-- ===== Admin ===== -->
    <template v-if="role === 'Admin'">
      <el-row :gutter="16">
        <el-col :span="8">
          <el-card shadow="never">
            <el-statistic title="今日课程" :value="adminStats.todaySessions" />
          </el-card>
        </el-col>
        <el-col :span="8">
          <el-card shadow="never">
            <el-statistic title="待确认订单" :value="adminStats.pendingOrders" />
            <el-button link type="primary" @click="router.push('/admin/orders')">去处理</el-button>
          </el-card>
        </el-col>
        <el-col :span="8">
          <el-card shadow="never">
            <div class="stat-title">本月成单金额</div>
            <div class="stat-money">{{ formatMoney(adminStats.monthAmount) }}</div>
          </el-card>
        </el-col>
      </el-row>
    </template>

    <!-- ===== Sales ===== -->
    <template v-else-if="role === 'Sales'">
      <el-row :gutter="16">
        <el-col :span="8">
          <el-card shadow="never">
            <el-statistic title="本月成单数" :value="salesStats.monthCount" />
          </el-card>
        </el-col>
        <el-col :span="8">
          <el-card shadow="never">
            <div class="stat-title">本月成单金额</div>
            <div class="stat-money">{{ formatMoney(salesStats.monthAmount) }}</div>
          </el-card>
        </el-col>
        <el-col :span="8">
          <el-card shadow="never">
            <el-statistic title="待跟进线索" :value="salesStats.newIntents" />
            <el-button link type="primary" @click="router.push('/admin/intents')">去跟进</el-button>
          </el-card>
        </el-col>
      </el-row>
    </template>

    <!-- ===== Teacher ===== -->
    <template v-else-if="role === 'Teacher'">
      <el-card shadow="never" header="今日我的课">
        <el-empty v-if="todaySessions.length === 0" description="今天没有课" />
        <div v-for="s in todaySessions" :key="s.id" class="session-row">
          <div>
            <strong>{{ formatDateTime(s.startAt) }}</strong>
            <span style="margin-left: 8px">{{ s.productName }}</span>
            <el-tag size="small" style="margin-left: 8px">{{ s.room ?? '—' }}</el-tag>
            <span class="muted" style="margin-left: 8px">在班 {{ s.enrolledCount }} 人</span>
          </div>
          <el-button type="primary" size="small" @click="router.push(`/admin/sessions/${s.id}`)">
            点名
          </el-button>
        </div>
      </el-card>
    </template>

    <!-- ===== FrontDesk ===== -->
    <template v-else-if="role === 'FrontDesk'">
      <el-row :gutter="16">
        <el-col :span="12">
          <el-card shadow="never" header="快捷核销（到店）">
            <div class="punch-search">
              <el-input
                v-model="punch.phone"
                placeholder="学员手机号"
                maxlength="11"
                style="width: 200px"
                @keyup.enter="searchStudent"
              />
              <el-button type="primary" :loading="punch.searching" @click="searchStudent">
                查找
              </el-button>
            </div>
            <template v-if="punch.studentId">
              <div style="margin: 12px 0">
                学员：<strong>{{ punch.studentName }}</strong>
              </div>
              <el-select
                v-model="punch.selectedPackId"
                placeholder="选择课时包"
                style="width: 100%"
                :disabled="punch.packs.length === 0"
              >
                <el-option
                  v-for="p in punch.packs"
                  :key="p.packId"
                  :value="p.packId"
                  :label="`${p.productName ?? '课时包'}（剩余 ${p.remaining}/${p.total}）`"
                />
              </el-select>
              <el-button
                type="success"
                size="large"
                style="width: 100%; margin-top: 12px"
                :disabled="!punch.selectedPackId"
                :loading="punch.busy"
                @click="doWalkIn"
              >
                核销一次
              </el-button>
            </template>
          </el-card>
        </el-col>
        <el-col :span="12">
          <el-card shadow="never" header="今日核销流水">
            <el-empty v-if="todayConsumptions.length === 0" description="今天还没有核销" :image-size="60" />
            <div v-for="c in todayConsumptions" :key="c.id" class="session-row">
              <span>
                {{ formatDateTime(c.createdAt) }} · {{ c.studentName }} · {{ c.productName }}
              </span>
              <EnumTag kind="consumptionKind" :value="c.kind" />
            </div>
          </el-card>
        </el-col>
      </el-row>
    </template>
  </div>
</template>

<style scoped>
.dashboard {
  min-height: 200px;
}
.stat-title {
  font-size: 13px;
  color: #909399;
  margin-bottom: 8px;
}
.stat-money {
  font-size: 24px;
  font-weight: 600;
  color: #303133;
}
.session-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 0;
  border-bottom: 1px solid #f0f2f5;
}
.session-row:last-child {
  border-bottom: none;
}
.muted {
  color: #909399;
  font-size: 12px;
}
.punch-search {
  display: flex;
  gap: 8px;
}
</style>
