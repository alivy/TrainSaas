<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { usePlatformAuth } from '@/stores/platformAuth'

const auth = usePlatformAuth()
const router = useRouter()
const route = useRoute()

const formRef = ref<FormInstance>()
const loading = ref(false)
const form = reactive({ username: '', password: '' })

const rules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }]
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  loading.value = true
  try {
    await auth.login(form.username, form.password)
    ElMessage.success('登录成功')
    const redirect = (route.query.redirect as string) || '/platform/institutions'
    router.replace(redirect)
  } catch {
    // 错误 toast 已由 http 拦截器统一处理
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <el-card class="login-card" shadow="always">
    <div class="login-title">🏢 平台管理后台</div>
    <el-form ref="formRef" :model="form" :rules="rules" size="large" @keyup.enter="submit">
      <el-form-item prop="username">
        <el-input v-model="form.username" placeholder="用户名" autocomplete="username" />
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
</style>
