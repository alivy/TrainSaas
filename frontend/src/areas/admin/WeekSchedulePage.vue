<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { orgSessionsList } from '@/api/org'
import { dayOfWeekLabel } from '@/utils/format'
import EnumTag from '@/components/EnumTag.vue'
import type { SessionListItem } from '@/api/types'

const router = useRouter()

function dayStr(d: Date): string {
  const p = (n: number) => (n < 10 ? `0${n}` : String(n))
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

// 周起点：周一
function mondayOf(d: Date): Date {
  const copy = new Date(d)
  const dow = copy.getDay() // 0=周日
  const diff = dow === 0 ? -6 : 1 - dow
  copy.setDate(copy.getDate() + diff)
  copy.setHours(0, 0, 0, 0)
  return copy
}

const weekStart = ref(mondayOf(new Date()))
const sessions = ref<SessionListItem[]>([])
const loading = ref(false)

const weekDays = computed(() => {
  return Array.from({ length: 7 }, (_, i) => {
    const d = new Date(weekStart.value)
    d.setDate(d.getDate() + i)
    return {
      date: dayStr(d),
      label: `${dayOfWeekLabel(i === 6 ? 0 : i + 1)} ${d.getMonth() + 1}/${d.getDate()}`,
      isToday: dayStr(d) === dayStr(new Date())
    }
  })
})

function sessionsOf(date: string): SessionListItem[] {
  return sessions.value
    .filter((s) => s.startAt.slice(0, 10) === date)
    .sort((a, b) => a.startAt.localeCompare(b.startAt))
}

async function load() {
  loading.value = true
  try {
    const from = dayStr(weekStart.value)
    const to = dayStr(new Date(weekStart.value.getTime() + 6 * 86400000))
    sessions.value = await orgSessionsList(from, to)
  } finally {
    loading.value = false
  }
}

function shiftWeek(days: number) {
  const d = new Date(weekStart.value)
  d.setDate(d.getDate() + days)
  weekStart.value = d
  load()
}

function goToday() {
  weekStart.value = mondayOf(new Date())
  load()
}

onMounted(load)
</script>

<template>
  <el-card shadow="never" v-loading="loading">
    <div class="toolbar">
      <el-button-group>
        <el-button @click="shiftWeek(-7)">上一周</el-button>
        <el-button @click="goToday">本周</el-button>
        <el-button @click="shiftWeek(7)">下一周</el-button>
      </el-button-group>
      <span class="muted">课程实例按需自动生成未来 4 周；点击课程卡进入点名</span>
    </div>

    <div class="week-grid">
      <div v-for="day in weekDays" :key="day.date" class="week-col" :class="{ today: day.isToday }">
        <div class="col-head" :class="{ today: day.isToday }">{{ day.label }}</div>
        <div class="col-body">
          <div
            v-for="s in sessionsOf(day.date)"
            :key="s.id"
            class="session-card"
            :class="{ completed: s.status === 'Completed' }"
            @click="router.push(`/admin/sessions/${s.id}`)"
          >
            <div class="time">{{ s.startAt.slice(11, 16) }}–{{ s.endAt.slice(11, 16) }}</div>
            <div class="product">{{ s.productName }}</div>
            <div class="meta">
              {{ s.teacherName ?? '' }}<template v-if="s.room"> · {{ s.room }}</template> · {{ s.enrolledCount }}人
            </div>
            <EnumTag kind="sessionStatus" :value="s.status" />
          </div>
          <div v-if="sessionsOf(day.date).length === 0" class="empty-day">—</div>
        </div>
      </div>
    </div>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
}
.muted {
  color: #909399;
  font-size: 12px;
}
.week-grid {
  display: grid;
  grid-template-columns: repeat(7, 1fr);
  gap: 8px;
}
.week-col {
  background: #fafbfc;
  border-radius: 8px;
  min-height: 300px;
  display: flex;
  flex-direction: column;
}
.week-col.today {
  outline: 2px solid #409eff33;
}
.col-head {
  padding: 8px;
  text-align: center;
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  border-bottom: 1px solid #ebeef5;
}
.col-head.today {
  color: #409eff;
}
.col-body {
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  flex: 1;
}
.session-card {
  background: #fff;
  border: 1px solid #ebeef5;
  border-left: 3px solid #409eff;
  border-radius: 6px;
  padding: 8px;
  cursor: pointer;
  transition: box-shadow 0.15s;
}
.session-card:hover {
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
}
.session-card.completed {
  border-left-color: #67c23a;
}
.time {
  font-weight: 600;
  font-size: 13px;
}
.product {
  font-size: 13px;
  margin: 2px 0;
}
.meta {
  font-size: 12px;
  color: #909399;
  margin-bottom: 4px;
}
.empty-day {
  text-align: center;
  color: #c0c4cc;
  padding-top: 24px;
}
/* 窄屏（教师手机）：横向滚动保持 7 列可读 */
@media (max-width: 900px) {
  .week-grid {
    grid-auto-flow: column;
    grid-auto-columns: 160px;
    grid-template-columns: none;
    overflow-x: auto;
  }
}
</style>
