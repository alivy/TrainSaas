<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import * as Icons from '@element-plus/icons-vue'
import { useStaffAuth } from '@/stores/staffAuth'
import { enumLabel } from '@/utils/format'
import { useMediaNarrow } from '@/utils/useMediaNarrow'
import { menuForRole } from './adminMenu'

const staffAuth = useStaffAuth()
const route = useRoute()
const router = useRouter()

const menus = computed(() => (staffAuth.role ? menuForRole(staffAuth.role) : []))
// /admin/students/3 → /admin/students（详情页保持父菜单高亮）
const activeMenu = computed(() => '/' + route.path.split('/').slice(1, 3).join('/'))

// C1：窄屏（<768px，教师手机点名场景）侧栏改为抽屉式，内容区占满全宽
const isNarrow = useMediaNarrow()
const drawerOpen = ref(false)
watch(isNarrow, (narrow) => {
  if (!narrow) drawerOpen.value = false
})
watch(
  () => route.path,
  () => {
    drawerOpen.value = false // 导航后自动收起抽屉
  }
)

function logout() {
  staffAuth.logout()
  router.push('/admin/login')
}
</script>

<template>
  <el-container class="admin-layout">
    <el-aside width="220px" class="aside" :class="{ narrow: isNarrow, open: drawerOpen }">
      <div class="brand">🎓 {{ staffAuth.profile?.institutionName ?? '培训机构' }}</div>
      <el-menu router :default-active="activeMenu" class="side-menu">
        <el-menu-item v-for="m in menus" :key="m.path" :index="m.path">
          <el-icon><component :is="(Icons as Record<string, unknown>)[m.icon]" /></el-icon>
          <span>{{ m.title }}</span>
        </el-menu-item>
      </el-menu>
    </el-aside>
    <div v-if="isNarrow && drawerOpen" class="drawer-mask" @click="drawerOpen = false"></div>
    <el-container class="body-container">
      <el-header class="topbar">
        <div class="topbar-left">
          <button v-if="isNarrow" type="button" class="hamburger" aria-label="打开菜单" @click="drawerOpen = !drawerOpen">
            ☰
          </button>
          <div class="page-title">{{ route.meta.title ?? '' }}</div>
        </div>
        <div class="user-area">
          <el-tag v-if="staffAuth.role" size="small" type="info">
            {{ enumLabel('role', staffAuth.role) }}
          </el-tag>
          <span v-if="!isNarrow" class="username">{{ staffAuth.profile?.name }}</span>
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
.body-container {
  min-width: 0; /* 允许内容区在窄屏收缩 */
}
.topbar {
  background: #fff;
  border-bottom: 1px solid #e4e7ed;
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.topbar-left {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}
.page-title {
  font-size: 16px;
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.hamburger {
  min-width: 40px;
  min-height: 40px; /* 触控目标 */
  border: none;
  background: transparent;
  font-size: 20px;
  cursor: pointer;
  color: #303133;
}
.user-area {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
}
.username {
  font-size: 14px;
  color: #303133;
}
.main {
  background: #f5f7fa;
  overflow-y: auto;
}

/* ===== 窄屏：侧栏改抽屉（C1） ===== */
@media (max-width: 767px) {
  .aside.narrow {
    position: fixed;
    top: 0;
    bottom: 0;
    left: 0;
    z-index: 1001;
    width: 220px;
    transform: translateX(-100%);
    transition: transform 0.2s ease;
    box-shadow: none;
  }
  .aside.narrow.open {
    transform: translateX(0);
    box-shadow: 2px 0 12px rgba(0, 0, 0, 0.15);
  }
  .drawer-mask {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.35);
    z-index: 1000;
  }
  .main {
    padding: 12px 8px; /* 给点名页等内容留出全宽 */
  }
}
</style>
