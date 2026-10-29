<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { fetchAuthedImage, revokeImage } from '@/utils/blobAuth'
import { useStaffAuth } from '@/stores/staffAuth'

// 受保护图片（凭据）：<img> 无法带 Authorization，走 blobAuth 工具（Review Focus 3）
const props = defineProps<{ url: string; size?: number }>()
const auth = useStaffAuth()
const objectUrl = ref('')
const failed = ref(false)
let disposed = false // M2：卸载竞态防护——fetch 晚于卸载返回时立即释放

onMounted(async () => {
  try {
    const url = await fetchAuthedImage(props.url, auth.token ?? '')
    if (disposed) {
      revokeImage(url)
    } else {
      objectUrl.value = url
    }
  } catch {
    failed.value = true
  }
})

onUnmounted(() => {
  disposed = true
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
