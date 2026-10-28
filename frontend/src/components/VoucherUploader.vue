<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage, type UploadFile } from 'element-plus'
import { orgOrderUploadVoucher } from '@/api/org'
import { validateImageFile } from '@/utils/fileCheck'
import type { VoucherCreatedResponse } from '@/api/types'

const props = defineProps<{ orderId: number }>()
const emit = defineEmits<{ uploaded: [voucher: VoucherCreatedResponse] }>()
const uploading = ref(false)

async function onChange(file: UploadFile) {
  if (!file.raw || uploading.value) return
  const err = validateImageFile(file.raw)
  if (err) {
    ElMessage.error(err)
    return
  }
  uploading.value = true
  try {
    const res = await orgOrderUploadVoucher(props.orderId, file.raw)
    ElMessage.success('凭据已上传')
    emit('uploaded', res)
  } catch {
    // toast 已由拦截器处理
  } finally {
    uploading.value = false
  }
}
</script>

<template>
  <el-upload
    :show-file-list="false"
    accept="image/jpeg,image/png,image/webp"
    :auto-upload="false"
    :on-change="onChange"
  >
    <el-button type="primary" plain :loading="uploading">上传支付凭据</el-button>
  </el-upload>
</template>
