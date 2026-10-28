<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import * as Icons from '@element-plus/icons-vue'
import { useStaffAuth } from '@/stores/staffAuth'
import { enumLabel } from '@/utils/format'
import { menuForRole } from './adminMenu'

const staffAuth = useStaffAuth()
const route = useRoute()
const router = useRouter()

const menus = computed(() => (staffAuth.role ? menuForRole(staffAuth.role) : []))
// /admin/students/3 → /admin/students（详情页保持父菜单高亮）
const activeMenu = computed(() => '/' + route.path.split('/').slice(1, 3).join('/'))

function logout() {
  staffAuth.logout()
  router.push('/admin/login')
}
</script>

<template>
  <el-container class="admin-layout">
    <el-aside width="220px" class="aside">
      <div class="brand">🎓 {{ staffAuth.profile?.institutionName ?? '培训机构' }}</div>
      <el-menu router :default-active="activeMenu" class="side-menu">
        <el-menu-item v-for="m in menus" :key="m.path" :index="m.path">
          <el-icon><component :is="(Icons as Record<string, unknown>)[m.icon]" /></el-icon>
          <span>{{ m.title }}</span>
        </el-menu-item>
      </el-menu>
    </el-aside>
    <el-container>
      <el-header class="topbar">
        <div class="page-title">{{ route.meta.title ?? '' }}</div>
        <div class="user-area">
          <el-tag v-if="staffAuth.role" size="small" type="info">
            {{ enumLabel('role', staffAuth.role) }}
          </el-tag>
          <span class="username">{{ staffAuth.profile?.name }}</span>
          <el-button link type="danger" @click="logout">退出</el-button>
        </div>
      </el-header>
      <el-main class="main"><router-view /></el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.admin-layout {
  height: 100vh;
}
.aside {
  background: #fff;
  border-right: 1px solid #e4e7ed;
  display: flex;
  flex-direction: column;
}
.brand {
  height: 56px;
  line-height: 56px;
  padding: 0 16px;
  font-weight: 600;
  font-size: 15px;
  border-bottom: 1px solid #e4e7ed;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.side-menu {
  border-right: none;
  flex: 1;
  overflow-y: auto;
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
.username {
  font-size: 14px;
  color: #303133;
}
.main {
  background: #f5f7fa;
  overflow-y: auto;
}
</style>
