<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ path?: string | null; size?: number }>()
const px = computed(() => props.size ?? 64)
const url = computed(() => (props.path ? `/api/files/images/${props.path}` : ''))
</script>

<template>
  <el-image
    v-if="url"
    :src="url"
    fit="cover"
    :style="{ width: px + 'px', height: px + 'px', borderRadius: '6px' }"
    :preview-src-list="[url]"
    preview-teleported
  />
  <div
    v-else
    class="img-placeholder"
    :style="{ width: px + 'px', height: px + 'px', fontSize: px / 2.5 + 'px' }"
  >
    🖼
  </div>
</template>

<style scoped>
.img-placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, #dfe9f7, #eef2f8);
  border-radius: 6px;
}
</style>
