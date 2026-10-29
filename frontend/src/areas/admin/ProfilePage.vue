<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, type UploadFile } from 'element-plus'
import ImagePreview from '@/components/ImagePreview.vue'
import { orgProfileGet, orgProfilePut, orgUploadImage } from '@/api/org'
import { validateImageFile } from '@/utils/fileCheck'

const loading = ref(false)
const saving = ref(false)
const code = ref('')
const form = reactive({
  name: '',
  logoPath: '' as string,
  introText: '',
  contactPhone: '',
  address: ''
})

async function load() {
  loading.value = true
  try {
    const p = await orgProfileGet()
    code.value = p.code
    Object.assign(form, {
      name: p.name,
      logoPath: p.logoPath ?? '',
      introText: p.introText ?? '',
      contactPhone: p.contactPhone ?? '',
      address: p.address ?? ''
    })
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function onLogoChange(file: UploadFile) {
  if (!file.raw) return
  const err = validateImageFile(file.raw)
  if (err) {
    ElMessage.error(err)
    return
  }
  const res = await orgUploadImage(file.raw, 'logo')
  form.logoPath = res.path
  ElMessage.success('Logo 已上传，保存后生效')
}

async function save() {
  if (!form.name.trim() || form.name.trim().length > 50) {
    ElMessage.warning('机构名称必填且不超过 50 字')
    return
  }
  saving.value = true
  try {
    await orgProfilePut({
      name: form.name.trim(),
      logoPath: form.logoPath || undefined,
      introText: form.introText || undefined,
      contactPhone: form.contactPhone || undefined,
      address: form.address || undefined
    })
    ElMessage.success('已保存，介绍页即刻生效')
  } finally {
    saving.value = false
  }
}

function openPublicPage() {
  window.open(`/org/${code.value}`, '_blank')
}
</script>

<template>
  <el-card shadow="never" v-loading="loading" style="max-width: 640px">
    <template #header>
      <div style="display: flex; justify-content: space-between; align-items: center">
        <span>机构介绍页设置</span>
        <el-button link type="primary" @click="openPublicPage">查看介绍页 /org/{{ code }}</el-button>
      </div>
    </template>

    <el-form label-width="90px">
      <el-form-item label="机构名称" required>
        <el-input v-model="form.name" maxlength="50" />
      </el-form-item>
      <el-form-item label="Logo">
        <div style="display: flex; align-items: center; gap: 12px">
          <ImagePreview :path="form.logoPath || null" :size="72" />
          <el-upload :show-file-list="false" accept="image/jpeg,image/png,image/webp" :auto-upload="false" :on-change="onLogoChange">
            <el-button>上传 Logo</el-button>
          </el-upload>
        </div>
      </el-form-item>
      <el-form-item label="机构简介">
        <el-input v-model="form.introText" type="textarea" :rows="4" placeholder="展示在公开介绍页顶部" />
      </el-form-item>
      <el-form-item label="联系电话">
        <el-input v-model="form.contactPhone" />
      </el-form-item>
      <el-form-item label="地址">
        <el-input v-model="form.address" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </el-form-item>
    </el-form>
  </el-card>
</template>
