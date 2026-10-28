<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { platformChangePassword } from '@/api/platform'
import { usePlatformAuth } from '@/stores/platformAuth'

const auth = usePlatformAuth()
const router = useRouter()
const formRef = ref<FormInstance>()
const loading = ref(false)
const form = reactive({ oldPassword: '', newPassword: '', confirm: '' })

const rules: FormRules = {
  oldPassword: [{ required: true, message: '请输入当前密码', trigger: 'blur' }],
  newPassword: [
    { required: true, message: '请输入新密码', trigger: 'blur' },
    { min: 6, message: '新密码至少 6 位', trigger: 'blur' }
  ],
  confirm: [
    { required: true, message: '请再次输入新密码', trigger: 'blur' },
    {
      validator: (_r, value: string, cb) =>
        value === form.newPassword ? cb() : cb(new Error('两次输入的新密码不一致')),
      trigger: 'blur'
    }
  ]
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  loading.value = true
  try {
    await platformChangePassword({ oldPassword: form.oldPassword, newPassword: form.newPassword })
    ElMessage.success('密码已修改，请重新登录')
    auth.logout()
    router.push('/platform/login')
  } catch {
    // toast 已由拦截器处理（401 旧密码错误等）
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <el-card shadow="never" style="max-width: 480px">
    <el-form ref="formRef" :model="form" :rules="rules" label-width="100px">
      <el-form-item label="当前密码" prop="oldPassword">
        <el-input v-model="form.oldPassword" type="password" show-password />
      </el-form-item>
      <el-form-item label="新密码" prop="newPassword">
        <el-input v-model="form.newPassword" type="password" show-password placeholder="至少 6 位" />
      </el-form-item>
      <el-form-item label="确认新密码" prop="confirm">
        <el-input v-model="form.confirm" type="password" show-password />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="loading" @click="submit">修改密码</el-button>
      </el-form-item>
    </el-form>
  </el-card>
</template>
