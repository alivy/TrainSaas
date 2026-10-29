<script setup lang="ts">
import { ref, watch } from 'vue'
import ChildSwitcher from './ChildSwitcher.vue'
import EnumTag from '@/components/EnumTag.vue'
import { useParentAuth } from '@/stores/parentAuth'
import { parentRecords } from '@/api/parent'
import { formatDateTime } from '@/utils/format'
import type { ParentRecordItem } from '@/api/types'

const auth = useParentAuth()
const records = ref<ParentRecordItem[]>([])
const total = ref(0)
const page = ref(1)
const loading = ref(false)

async function load(reset = true) {
  if (!auth.selectedChildId) {
    records.value = []
    total.value = 0
    return
  }
  if (reset) page.value = 1
  loading.value = true
  try {
    const res = await parentRecords(auth.selectedChildId, { page: page.value, pageSize: 20 })
    records.value = reset ? res.items : [...records.value, ...res.items]
    total.value = res.total
  } finally {
    loading.value = false
  }
}

watch(() => auth.selectedChildId, () => load(true), { immediate: true })

function loadMore() {
  page.value += 1
  load(false)
}
</script>

<template>
  <div>
    <ChildSwitcher />
    <div v-loading="loading">
      <div v-for="r in records" :key="r.id" class="record-row">
        <div class="r-left">
          <div class="r-line1">
            <EnumTag kind="consumptionKind" :value="r.kind" />
            <span class="r-product">{{ r.productName ?? '' }}</span>
          </div>
          <div class="r-time">{{ formatDateTime(r.createdAt) }}<template v-if="r.reason"> · {{ r.reason }}</template></div>
        </div>
        <div class="r-delta" :class="r.direction === 1 ? 'minus' : 'plus'">
          {{ r.direction === 1 ? '-1' : '+1' }}
        </div>
      </div>
      <el-empty v-if="!loading && records.length === 0" description="暂无课时记录" :image-size="60" />
      <div v-if="records.length < total" class="load-more">
        <el-button :loading="loading" @click="loadMore">加载更多</el-button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.record-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  background: #fff;
  border-radius: 10px;
  padding: 12px 14px;
  margin-bottom: 8px;
}
.r-line1 {
  display: flex;
  align-items: center;
  gap: 8px;
}
.r-product {
  font-size: 14px;
  font-weight: 500;
}
.r-time {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}
.r-delta {
  font-size: 18px;
  font-weight: 700;
}
.r-delta.minus {
  color: #67c23a;
}
.r-delta.plus {
  color: #f56c6c;
}
.load-more {
  text-align: center;
  padding: 8px 0 16px;
}
</style>
