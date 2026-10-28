<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { useStaffAuth } from '@/stores/staffAuth'

const auth = useStaffAuth()
const router = useRouter()
const route = useRoute()

const formRef = ref<FormInstance>()
const loading = ref(false)
const form = reactive({ phone: '', password: '' })

const rules: FormRules = {
  phone: [
    { required: true, message: '请输入手机号', trigger: 'blur' },
    { pattern: /^1\d{10}$/, message: '手机号格式不正确', trigger: 'blur' }
  ],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }]
}

// Ruling F2：Teacher 暂落地 dashboard，T7 周课表页就绪后切换为 /admin/schedule
function defaultPath(): string {
  return '/admin/dashboard'
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  loading.value = true
  try {
    await auth.login(form.phone, form.password)
    ElMessage.success('登录成功')
    const redirect = (route.query.redirect as string) || defaultPath()
    router.replace(redirect)
  } catch {
    // 错误 toast 已由 http 拦截器统一处理（403 机构已停用等）
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <el-card class="login-card" shadow="always">
    <div class="login-title">🎓 机构管理后台</div>
    <el-form ref="formRef" :model="form" :rules="rules" size="large" @keyup.enter="submit">
      <el-form-item prop="phone">
        <el-input v-model="form.phone" placeholder="手机号" maxlength="11" autocomplete="username" />
      </el-form-item>
      <el-form-item prop="password">
        <el-input
          v-model="form.password"
          type="password"
          placeholder="密码"
          show-password
          autocomplete="current-password"
        />
      </el-form-item>
      <el-button type="primary" size="large" style="width: 100%" :loading="loading" @click="submit">
        登 录
      </el-button>
    </el-form>
    <div class="login-tip">初始密码为 123456，请联系机构管理员重置</div>
  </el-card>
</template>

<style scoped>
.login-card {
  width: 380px;
}
.login-title {
  text-align: center;
  font-size: 18px;
  font-weight: 600;
  margin-bottom: 24px;
}
.login-tip {
  margin-top: 16px;
  text-align: center;
  font-size: 12px;
  color: #909399;
}
</style>
