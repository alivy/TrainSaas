<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { parentChildren } from '@/api/parent'
import { useParentAuth } from '@/stores/parentAuth'
import type { ParentChildItem } from '@/api/types'

const router = useRouter()
const auth = useParentAuth()
const children = ref<ParentChildItem[]>([])
const loading = ref(true)

onMounted(async () => {
  try {
    children.value = await parentChildren()
  } finally {
    loading.value = false
  }
})

function pick(child: ParentChildItem) {
  auth.selectChild(child.studentId)
  router.push('/parent/packs')
}
</script>

<template>
  <div v-loading="loading">
    <div v-for="c in children" :key="`${c.institutionId}-${c.studentId}`" class="child-card" @click="pick(c)">
      <div class="avatar">{{ c.name.slice(0, 1) }}</div>
      <div class="info">
        <div class="name">{{ c.name }}</div>
        <div class="inst">🏫 {{ c.institutionName ?? c.institutionCode }}</div>
      </div>
      <div class="arrow">›</div>
    </div>
    <el-empty
      v-if="!loading && children.length === 0"
      description="还没有关联的学员。报名成功后，机构会用您的手机号开通家长账号。"
    />
  </div>
</template>

<style scoped>
.child-card {
  display: flex;
  align-items: center;
  gap: 12px;
  background: #fff;
  border-radius: 12px;
  padding: 16px;
  margin-bottom: 12px;
  cursor: pointer;
  min-height: 44px;
}
.avatar {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: linear-gradient(135deg, #a0cfff, #409eff);
  color: #fff;
  font-size: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}
.info {
  flex: 1;
}
.name {
  font-size: 16px;
  font-weight: 600;
}
.inst {
  font-size: 12px;
  color: #909399;
  margin-top: 2px;
}
.arrow {
  color: #c0c4cc;
  font-size: 22px;
}
</style>
