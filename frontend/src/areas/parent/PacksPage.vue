<script setup lang="ts">
import { ref, watch } from 'vue'
import ChildSwitcher from './ChildSwitcher.vue'
import { useParentAuth } from '@/stores/parentAuth'
import { parentPacks } from '@/api/parent'
import { formatDate } from '@/utils/format'
import type { ParentPackItem } from '@/api/types'

const auth = useParentAuth()
const packs = ref<ParentPackItem[]>([])
const loading = ref(false)

async function load() {
  if (!auth.selectedChildId) {
    packs.value = []
    return
  }
  loading.value = true
  try {
    packs.value = await parentPacks(auth.selectedChildId)
  } finally {
    loading.value = false
  }
}

watch(() => auth.selectedChildId, load, { immediate: true })
</script>

<template>
  <div>
    <ChildSwitcher />
    <div v-loading="loading">
      <div v-for="p in packs" :key="p.packId" class="pack-card" :class="{ voided: p.status === 'Voided' }">
        <div class="pack-head">
          <span class="pname">{{ p.productName ?? '课时包' }}</span>
          <el-tag :type="p.status === 'Active' ? 'success' : 'danger'" size="small">
            {{ p.status === 'Active' ? '正常' : '已作废' }}
          </el-tag>
        </div>
        <div class="pack-numbers">
          <span class="remaining" :class="{ low: p.remainingSessions === 0 && p.status === 'Active' }">
            {{ p.remainingSessions }}
          </span>
          <span class="total">/ {{ p.totalSessions }} 课时</span>
        </div>
        <el-progress
          :percentage="p.totalSessions > 0 ? Math.round((p.remainingSessions / p.totalSessions) * 100) : 0"
          :status="p.remainingSessions === 0 ? 'exception' : undefined"
          :stroke-width="8"
        />
        <div class="pack-foot">
          <span>订单 {{ p.orderNo ?? '—' }}</span>
          <span>购买于 {{ formatDate(p.purchasedAt) }}</span>
        </div>
      </div>
      <el-empty
        v-if="!loading && packs.length === 0"
        description="暂无课时包。报名缴费后自动显示；如需续费请联系机构。"
      />
    </div>
  </div>
</template>

<style scoped>
.pack-card {
  background: #fff;
  border-radius: 12px;
  padding: 16px;
  margin-bottom: 12px;
}
.pack-card.voided {
  opacity: 0.65;
}
.pack-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.pname {
  font-weight: 600;
  font-size: 15px;
}
.pack-numbers {
  margin: 10px 0 8px;
}
.remaining {
  font-size: 34px;
  font-weight: 700;
  color: #67c23a;
}
.remaining.low {
  color: #f56c6c;
}
.total {
  font-size: 13px;
  color: #909399;
  margin-left: 6px;
}
.pack-foot {
  display: flex;
  justify-content: space-between;
  font-size: 12px;
  color: #909399;
  margin-top: 10px;
}
</style>
