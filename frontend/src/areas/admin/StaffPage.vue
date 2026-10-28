<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import EnumTag from '@/components/EnumTag.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgStaffList,
  orgStaffCreate,
  orgStaffUpdate,
  orgStaffSetActive,
  orgStaffResetPassword
} from '@/api/org'
import { formatDateTime } from '@/utils/format'
import type { StaffListItem, Role } from '@/api/types'

const auth = useStaffAuth()
// 泛型 SFC 不支持 InstanceType——用结构化类型对接 PagedTable 的 defineExpose
const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)
const roleFilter = ref<Role | ''>('')

const ROLE_OPTIONS: { value: Role; label: string }[] = [
  { value: 'Admin', label: '管理员' },
  { value: 'Sales', label: '销售' },
  { value: 'Teacher', label: '老师' },
  { value: 'FrontDesk', label: '前台' }
]

function loader(page: number, pageSize: number) {
  return orgStaffList({ role: roleFilter.value || undefined, page, pageSize })
}

// ===== 新建 / 编辑 =====
const dialogVisible = ref(false)
const saving = ref(false)
const editing = ref<StaffListItem | null>(null)
const formRef = ref<FormInstance>()
const form = reactive({ name: '', phone: '', role: 'Sales' as Role })

const rules: FormRules = {
  name: [{ required: true, message: '请输入姓名', trigger: 'blur' }],
  phone: [
    { required: true, message: '请输入手机号', trigger: 'blur' },
    { pattern: /^1\d{10}$/, message: '手机号格式不正确', trigger: 'blur' }
  ],
  role: [{ required: true, message: '请选择角色', trigger: 'change' }]
}

function openCreate() {
  editing.value = null
  Object.assign(form, { name: '', phone: '', role: 'Sales' })
  dialogVisible.value = true
}

function openEdit(row: StaffListItem) {
  editing.value = row
  Object.assign(form, { name: row.name, phone: row.phone, role: row.role })
  dialogVisible.value = true
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  saving.value = true
  try {
    if (editing.value) {
      await orgStaffUpdate(editing.value.id, { name: form.name, role: form.role })
      ElMessage.success('已保存')
    } else {
      await orgStaffCreate({ name: form.name, phone: form.phone, role: form.role })
      ElMessage.success('员工已创建，初始密码 123456')
    }
    dialogVisible.value = false
    tableRef.value?.reload()
  } catch {
    // 409（手机号占用）/409（最后管理员降级）等由拦截器 toast
  } finally {
    saving.value = false
  }
}

async function toggleActive(row: StaffListItem) {
  if (row.id === auth.profile?.staffId) {
    ElMessage.warning('不能停用自己')
    return
  }
  try {
    await ElMessageBox.confirm(
      row.isActive ? `停用「${row.name}」后其将无法登录。确认？` : `启用「${row.name}」？`,
      '提示',
      { type: 'warning' }
    )
  } catch {
    return
  }
  await orgStaffSetActive(row.id, !row.isActive)
  ElMessage.success(row.isActive ? '已停用' : '已启用')
  tableRef.value?.reload()
}

async function resetPassword(row: StaffListItem) {
  try {
    await ElMessageBox.confirm(`将「${row.name}」的密码重置为 123456？`, '重置密码', { type: 'warning' })
  } catch {
    return
  }
  await orgStaffResetPassword(row.id)
  ElMessage.success('已重置，初始密码 123456')
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select v-model="roleFilter" placeholder="角色" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option v-for="r in ROLE_OPTIONS" :key="r.value" :label="r.label" :value="r.value" />
      </el-select>
      <el-button type="primary" @click="openCreate">新建员工</el-button>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column prop="name" label="姓名" min-width="120" />
      <el-table-column prop="phone" label="手机号(登录账号)" width="160" />
      <el-table-column label="角色" width="100">
        <template #default="{ row }"><EnumTag kind="role" :value="row.role" /></template>
      </el-table-column>
      <el-table-column label="状态" width="90">
        <template #default="{ row }">
          <el-tag :type="row.isActive ? 'success' : 'danger'" size="small">
            {{ row.isActive ? '在职' : '停用' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="220" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button
            link
            :type="row.isActive ? 'warning' : 'success'"
            :disabled="row.id === auth.profile?.staffId"
            @click="toggleActive(row)"
          >
            {{ row.isActive ? '停用' : '启用' }}
          </el-button>
          <el-button link type="danger" @click="resetPassword(row)">重置密码</el-button>
        </template>
      </el-table-column>
    </PagedTable>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑员工' : '新建员工'" width="440px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="80px">
        <el-form-item label="姓名" prop="name">
          <el-input v-model="form.name" />
        </el-form-item>
        <el-form-item label="手机号" prop="phone">
          <el-input v-model="form.phone" maxlength="11" :disabled="!!editing" placeholder="登录账号，初始密码 123456" />
        </el-form-item>
        <el-form-item label="角色" prop="role">
          <el-select v-model="form.role" style="width: 100%">
            <el-option v-for="r in ROLE_OPTIONS" :key="r.value" :label="r.label" :value="r.value" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">确定</el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}
</style>
