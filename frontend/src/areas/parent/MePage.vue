<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { useParentAuth } from '@/stores/parentAuth'
import { parentChangePassword } from '@/api/parent'

const auth = useParentAuth()
const router = useRouter()
const formRef = ref<FormInstance>()
const saving = ref(false)
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
  saving.value = true
  try {
    await parentChangePassword({ oldPassword: form.oldPassword, newPassword: form.newPassword })
    ElMessage.success('密码已修改，请重新登录')
    auth.logout()
    router.push('/parent/login')
  } catch {
    // 旧密码错误等由拦截器 toast
  } finally {
    saving.value = false
  }
}

function logout() {
  auth.logout()
  router.push('/parent/login')
}
</script>

<template>
  <div>
    <el-card shadow="never" style="margin-bottom: 12px">
      <div class="me-head">
        <div class="avatar">👤</div>
        <div>
          <div class="phone">{{ auth.phone || '家长账号' }}</div>
          <div class="muted">初始密码 123456，建议尽快修改</div>
        </div>
      </div>
    </el-card>

    <el-card shadow="never" header="修改密码" style="margin-bottom: 12px">
      <el-form ref="formRef" :model="form" :rules="rules" label-position="top">
        <el-form-item label="当前密码" prop="oldPassword">
          <el-input v-model="form.oldPassword" type="password" show-password />
        </el-form-item>
        <el-form-item label="新密码（至少 6 位）" prop="newPassword">
          <el-input v-model="form.newPassword" type="password" show-password />
        </el-form-item>
        <el-form-item label="确认新密码" prop="confirm">
          <el-input v-model="form.confirm" type="password" show-password />
        </el-form-item>
        <el-button type="primary" style="width: 100%" :loading="saving" @click="submit">
          修改密码
        </el-button>
      </el-form>
    </el-card>

    <el-button style="width: 100%" size="large" @click="logout">退出登录</el-button>
  </div>
</template>

<style scoped>
.me-head {
  display: flex;
  align-items: center;
  gap: 12px;
}
.avatar {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: #ecf5ff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 22px;
}
.phone {
  font-size: 16px;
  font-weight: 600;
}
.muted {
  font-size: 12px;
  color: #909399;
  margin-top: 2px;
}
</style>
