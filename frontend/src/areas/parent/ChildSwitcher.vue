<script setup lang="ts">
// 孩子切换器：家长端 packs/records/schedule 三页共用。
// 选中孩子存 parentAuth.selectedChildId（sessionStorage 持久化）。
import { onMounted, ref } from 'vue'
import { useParentAuth } from '@/stores/parentAuth'
import { parentChildren } from '@/api/parent'
import type { ParentChildItem } from '@/api/types'

const emit = defineEmits<{ loaded: [children: ParentChildItem[]] }>()
const auth = useParentAuth()
const children = ref<ParentChildItem[]>([])

onMounted(async () => {
  children.value = await parentChildren()
  // 恢复/默认选中第一个孩子
  const ids = children.value.map((c) => c.studentId)
  if (!auth.selectedChildId || !ids.includes(auth.selectedChildId)) {
    if (ids.length > 0) auth.selectChild(ids[0])
  }
  emit('loaded', children.value)
})
</script>

<template>
  <div v-if="children.length > 1" class="child-switcher">
    <button
      v-for="c in children"
      :key="c.studentId"
      type="button"
      class="child-chip"
      :class="{ active: auth.selectedChildId === c.studentId }"
      @click="auth.selectChild(c.studentId)"
    >
      {{ c.name }}<span class="inst">{{ c.institutionName }}</span>
    </button>
  </div>
</template>

<style scoped>
.child-switcher {
  display: flex;
  gap: 8px;
  overflow-x: auto;
  padding: 4px 0 12px;
}
.child-chip {
  flex-shrink: 0;
  min-height: 40px;
  padding: 6px 16px;
  border-radius: 20px;
  border: 1px solid #dcdfe6;
  background: #fff;
  font-size: 14px;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 6px;
}
.child-chip.active {
  background: #409eff;
  border-color: #409eff;
  color: #fff;
}
.inst {
  font-size: 11px;
  opacity: 0.75;
}
</style>
