<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { fetchAuthedImage, revokeImage } from '@/utils/blobAuth'
import { useStaffAuth } from '@/stores/staffAuth'

// 受保护图片（凭据）：<img> 无法带 Authorization，走 blobAuth 工具（Review Focus 3）
const props = defineProps<{ url: string; size?: number }>()
const auth = useStaffAuth()
const objectUrl = ref('')
const failed = ref(false)

onMounted(async () => {
  try {
    objectUrl.value = await fetchAuthedImage(props.url, auth.token ?? '')
  } catch {
    failed.value = true
  }
})

onUnmounted(() => {
  if (objectUrl.value) revokeImage(objectUrl.value)
})
</script>

<template>
  <el-image
    v-if="objectUrl"
    :src="objectUrl"
    fit="cover"
    :style="{ width: (props.size ?? 80) + 'px', height: (props.size ?? 80) + 'px', borderRadius: '4px' }"
    :preview-src-list="[objectUrl]"
    preview-teleported
  />
  <el-tag v-else-if="failed" type="danger" size="small">图片不可用</el-tag>
  <el-skeleton v-else animated style="width: 80px; height: 80px" />
</template>
