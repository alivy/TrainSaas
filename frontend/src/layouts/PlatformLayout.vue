<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { OfficeBuilding, Lock } from '@element-plus/icons-vue'
import { usePlatformAuth } from '@/stores/platformAuth'

const platformAuth = usePlatformAuth()
const route = useRoute()
const router = useRouter()

const menus = [
  { path: '/platform/institutions', title: '机构管理', icon: OfficeBuilding },
  { path: '/platform/change-password', title: '修改密码', icon: Lock }
]
const activeMenu = computed(() => route.path)

function logout() {
  platformAuth.logout()
  router.push('/platform/login')
}
</script>

<template>
  <el-container class="platform-layout">
    <el-aside width="220px" class="aside">
      <div class="brand">🏢 培训机构 SaaS 平台</div>
      <el-menu router :default-active="activeMenu" class="side-menu">
        <el-menu-item v-for="m in menus" :key="m.path" :index="m.path">
          <el-icon><component :is="m.icon" /></el-icon>
          <span>{{ m.title }}</span>
        </el-menu-item>
      </el-menu>
    </el-aside>
    <el-container>
      <el-header class="topbar">
        <div class="page-title">{{ route.meta.title ?? '' }}</div>
        <div class="user-area">
          <el-tag size="small" type="warning">平台超管</el-tag>
          <span class="username">{{ platformAuth.name }}</span>
          <el-button link type="danger" @click="logout">退出</el-button>
        </div>
      </el-header>
      <el-main class="main"><router-view /></el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.platform-layout {
  height: 100vh;
}
.aside {
  background: #fff;
  border-right: 1px solid #e4e7ed;
}
.brand {
  height: 56px;
  line-height: 56px;
  padding: 0 16px;
  font-weight: 600;
  font-size: 15px;
  border-bottom: 1px solid #e4e7ed;
}
.side-menu {
  border-right: none;
}
.topbar {
  background: #fff;
  border-bottom: 1px solid #e4e7ed;
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.page-title {
  font-size: 16px;
  font-weight: 600;
}
.user-area {
  display: flex;
  align-items: center;
  gap: 10px;
}
.main {
  background: #f5f7fa;
  overflow-y: auto;
}
</style>
