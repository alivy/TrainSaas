<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import ChildSwitcher from './ChildSwitcher.vue'
import { useParentAuth } from '@/stores/parentAuth'
import { parentSchedule } from '@/api/parent'
import type { ParentScheduleItem } from '@/api/types'

const auth = useParentAuth()
const items = ref<ParentScheduleItem[]>([])
const loading = ref(false)

async function load() {
  if (!auth.selectedChildId) {
    items.value = []
    return
  }
  loading.value = true
  try {
    items.value = await parentSchedule(auth.selectedChildId, 14)
  } finally {
    loading.value = false
  }
}

watch(() => auth.selectedChildId, load, { immediate: true })

// 按日期分组
const groups = computed(() => {
  const map = new Map<string, ParentScheduleItem[]>()
  for (const s of items.value) {
    const date = s.startAt.slice(0, 10)
    if (!map.has(date)) map.set(date, [])
    map.get(date)!.push(s)
  }
  return [...map.entries()].map(([date, list]) => ({ date, list }))
})

const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
function weekOf(dateStr: string): string {
  const d = new Date(dateStr + 'T00:00:00')
  return WEEK[d.getDay()]
}
</script>

<template>
  <div>
    <ChildSwitcher />
    <div v-loading="loading">
      <div v-for="g in groups" :key="g.date" class="day-group">
        <div class="day-head">{{ g.date }} {{ weekOf(g.date) }}</div>
        <div v-for="(s, i) in g.list" :key="i" class="session-card">
          <div class="time">{{ s.startAt.slice(11, 16) }} – {{ s.endAt.slice(11, 16) }}</div>
          <div class="detail">
            <div class="product">{{ s.productName }}</div>
            <div class="meta">
              {{ s.teacherName ?? '' }}<template v-if="s.room"> · {{ s.room }}</template>
              <template v-if="s.institutionName"> · {{ s.institutionName }}</template>
            </div>
          </div>
        </div>
      </div>
      <el-empty
        v-if="!loading && items.length === 0"
        description="未来两周没有课程。报读固定排课班级后，课表会自动出现在这里。"
      />
    </div>
  </div>
</template>

<style scoped>
.day-group {
  margin-bottom: 16px;
}
.day-head {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  padding: 4px 2px 8px;
}
.session-card {
  display: flex;
  gap: 12px;
  background: #fff;
  border-radius: 10px;
  padding: 12px 14px;
  margin-bottom: 8px;
  border-left: 3px solid #409eff;
}
.time {
  font-weight: 700;
  font-size: 14px;
  white-space: nowrap;
}
.product {
  font-size: 14px;
  font-weight: 500;
}
.meta {
  font-size: 12px;
  color: #909399;
  margin-top: 2px;
}
</style>
