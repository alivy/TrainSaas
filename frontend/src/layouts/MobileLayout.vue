<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const tabs = [
  { path: '/parent/children', label: '孩子', icon: '👶' },
  { path: '/parent/packs', label: '课时', icon: '🎫' },
  { path: '/parent/records', label: '记录', icon: '📋' },
  { path: '/parent/schedule', label: '课表', icon: '📅' },
  { path: '/parent/me', label: '我的', icon: '👤' }
]
const active = computed(() => route.path)
</script>

<template>
  <div class="mobile-layout">
    <header class="m-header">{{ route.meta.title ?? '' }}</header>
    <main class="m-main"><router-view /></main>
    <nav class="m-tabbar">
      <router-link
        v-for="t in tabs"
        :key="t.path"
        :to="t.path"
        class="m-tab"
        :class="{ active: active === t.path }"
      >
        <span class="m-tab-icon">{{ t.icon }}</span>
        <span class="m-tab-label">{{ t.label }}</span>
      </router-link>
    </nav>
  </div>
</template>

<style scoped>
.mobile-layout {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  background: #f5f7fa;
}
.m-header {
  position: sticky;
  top: 0;
  z-index: 10;
  height: 48px;
  line-height: 48px;
  text-align: center;
  font-size: 16px;
  font-weight: 600;
  background: #fff;
  border-bottom: 1px solid #e4e7ed;
}
.m-main {
  flex: 1;
  padding: 12px 12px 68px;
  overflow-y: auto;
}
.m-tabbar {
  position: fixed;
  bottom: 0;
  left: 0;
  right: 0;
  height: 56px;
  background: #fff;
  border-top: 1px solid #e4e7ed;
  display: flex;
  z-index: 20;
}
.m-tab {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 2px;
  text-decoration: none;
  color: #909399;
  min-height: 44px; /* touch target */
}
.m-tab.active {
  color: #409eff;
}
.m-tab-icon {
  font-size: 20px;
  line-height: 1;
}
.m-tab-label {
  font-size: 11px;
}
</style>
