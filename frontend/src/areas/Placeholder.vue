<script setup lang="ts">
import { useRoute } from 'vue-router'

const route = useRoute()
</script>

<template>
  <div class="placeholder">
    <el-empty :description="`「${route.meta.title ?? route.path}」页面建设中（占位）`">
      <template #image><span style="font-size: 48px">🚧</span></template>
      <div style="color: #909399; font-size: 13px">{{ route.path }}</div>
    </el-empty>
  </div>
</template>

<style scoped>
.placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 60vh;
}
</style>
