<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { useParentAuth } from '@/stores/parentAuth'

const auth = useParentAuth()
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

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  loading.value = true
  try {
    await auth.login(form.phone, form.password)
    ElMessage.success('登录成功')
    const redirect = (route.query.redirect as string) || '/parent/children'
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
    <div class="login-title">👨‍👩‍👧 家长中心</div>
    <el-form ref="formRef" :model="form" :rules="rules" size="large" @keyup.enter="submit">
      <el-form-item prop="phone">
        <el-input v-model="form.phone" placeholder="手机号" maxlength="11" autocomplete="username" />
      </el-form-item>
      <el-form-item prop="password">
        <el-input
          v-model="form.password"
          type="password"
          placeholder="密码（初始 123456）"
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
  width: min(380px, 100%);
}
.login-title {
  text-align: center;
  font-size: 18px;
  font-weight: 600;
  margin-bottom: 24px;
}
</style>
