<script setup lang="ts" generic="T">
import { onMounted, ref } from 'vue'
import type { Paged } from '@/api/types'

const props = defineProps<{
  loader: (page: number, pageSize: number) => Promise<Paged<T>>
}>()

const items = ref<T[]>([]) as { value: T[] }
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    const res = await props.loader(page.value, pageSize.value)
    items.value = res.items
    total.value = res.total
  } finally {
    loading.value = false
  }
}

/** 筛选条件变化时调用：回到第一页重新加载 */
function reload(resetPage = true) {
  if (resetPage) page.value = 1
  return load()
}

function onPageChange() {
  load()
}

onMounted(load)

defineExpose({ reload, items })
</script>

<template>
  <div>
    <el-table v-loading="loading" :data="items" stripe>
      <slot />
    </el-table>
    <el-pagination
      v-model:current-page="page"
      v-model:page-size="pageSize"
      :total="total"
      :page-sizes="[20, 50, 100]"
      layout="total, sizes, prev, pager, next"
      style="margin-top: 16px; justify-content: flex-end"
      @change="onPageChange"
    />
  </div>
</template>
