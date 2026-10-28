<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import {
  platformListInstitutions,
  platformCreateInstitution,
  platformUpdateInstitution,
  platformSetInstitutionStatus,
  platformResetAdminPassword
} from '@/api/platform'
import { formatDateTime } from '@/utils/format'
import type { InstitutionListItem, InstitutionStatus } from '@/api/types'

const loading = ref(false)
const items = ref<InstitutionListItem[]>([])
const total = ref(0)
const query = reactive<{ status: InstitutionStatus | ''; page: number; pageSize: number }>({
  status: '',
  page: 1,
  pageSize: 20
})

async function load() {
  loading.value = true
  try {
    const res = await platformListInstitutions({
      status: query.status || undefined,
      page: query.page,
      pageSize: query.pageSize
    })
    items.value = res.items
    total.value = res.total
  } finally {
    loading.value = false
  }
}

onMounted(load)

// ===== 新建 / 编辑 =====
const dialogVisible = ref(false)
const saving = ref(false)
const editing = ref<InstitutionListItem | null>(null)
const formRef = ref<FormInstance>()
const form = reactive({
  code: '',
  name: '',
  adminName: '',
  adminPhone: '',
  contactPhone: '',
  address: '',
  introText: ''
})

const rules: FormRules = {
  code: [
    { required: true, message: '请输入机构编号', trigger: 'blur' },
    {
      pattern: /^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$/,
      message: '3-32 位小写字母/数字/中划线，首尾为字母或数字',
      trigger: 'blur'
    }
  ],
  name: [
    { required: true, message: '请输入机构名称', trigger: 'blur' },
    { max: 50, message: '不超过 50 字', trigger: 'blur' }
  ],
  adminName: [{ required: true, message: '请输入管理员姓名', trigger: 'blur' }],
  adminPhone: [
    { required: true, message: '请输入管理员手机号', trigger: 'blur' },
    { pattern: /^1\d{10}$/, message: '手机号格式不正确', trigger: 'blur' }
  ]
}

function openCreate() {
  editing.value = null
  Object.assign(form, { code: '', name: '', adminName: '', adminPhone: '', contactPhone: '', address: '', introText: '' })
  dialogVisible.value = true
}

function openEdit(row: InstitutionListItem) {
  editing.value = row
  Object.assign(form, {
    code: row.code,
    name: row.name,
    adminName: '',
    adminPhone: '',
    contactPhone: '',
    address: '',
    introText: ''
  })
  dialogVisible.value = true
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  saving.value = true
  try {
    if (editing.value) {
      await platformUpdateInstitution(editing.value.id, {
        name: form.name,
        contactPhone: form.contactPhone || undefined,
        address: form.address || undefined,
        introText: form.introText || undefined
      })
      ElMessage.success('已保存')
    } else {
      await platformCreateInstitution({
        code: form.code,
        name: form.name,
        adminName: form.adminName,
        adminPhone: form.adminPhone,
        contactPhone: form.contactPhone || undefined,
        address: form.address || undefined,
        introText: form.introText || undefined
      })
      ElMessage.success('机构已开通，管理员初始密码 123456')
    }
    dialogVisible.value = false
    load()
  } catch {
    // toast 已由拦截器处理（409 重复 code/手机号等）
  } finally {
    saving.value = false
  }
}

// ===== 行操作 =====
async function toggleStatus(row: InstitutionListItem) {
  const disable = row.status === 'Active'
  try {
    await ElMessageBox.confirm(
      disable
        ? `停用「${row.name}」后，该机构所有员工将无法登录。确认停用？`
        : `启用「${row.name}」？`,
      '提示',
      { type: 'warning' }
    )
  } catch {
    return
  }
  await platformSetInstitutionStatus(row.id, disable ? 'disable' : 'enable')
  ElMessage.success(disable ? '已停用' : '已启用')
  load()
}

async function resetPassword(row: InstitutionListItem) {
  try {
    await ElMessageBox.confirm(
      `将「${row.name}」的管理员密码重置为 123456？`,
      '重置管理员密码',
      { type: 'warning' }
    )
  } catch {
    return
  }
  await platformResetAdminPassword(row.id)
  ElMessage.success('已重置，初始密码 123456')
}
</script>

<template>
  <div>
    <el-card shadow="never">
      <div class="toolbar">
        <el-select v-model="query.status" placeholder="状态" clearable style="width: 140px" @change="query.page = 1; load()">
          <el-option label="启用" value="Active" />
          <el-option label="停用" value="Disabled" />
        </el-select>
        <el-button type="primary" @click="openCreate">开通机构</el-button>
      </div>

      <el-table v-loading="loading" :data="items" stripe>
        <el-table-column prop="code" label="编号" width="140" />
        <el-table-column prop="name" label="名称" min-width="180" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.status === 'Active' ? 'success' : 'danger'" size="small">
              {{ row.status === 'Active' ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="staffCount" label="员工数" width="90" />
        <el-table-column prop="studentCount" label="学员数" width="90" />
        <el-table-column label="创建时间" width="160">
          <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="240" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button link :type="row.status === 'Active' ? 'warning' : 'success'" @click="toggleStatus(row)">
              {{ row.status === 'Active' ? '停用' : '启用' }}
            </el-button>
            <el-button link type="danger" @click="resetPassword(row)">重置管理员密码</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-pagination
        v-model:current-page="query.page"
        v-model:page-size="query.pageSize"
        :total="total"
        :page-sizes="[20, 50, 100]"
        layout="total, sizes, prev, pager, next"
        style="margin-top: 16px; justify-content: flex-end"
        @change="load"
      />
    </el-card>

    <el-dialog
      v-model="dialogVisible"
      :title="editing ? '编辑机构' : '开通机构'"
      width="520px"
      destroy-on-close
    >
      <el-form ref="formRef" :model="form" :rules="rules" label-width="110px">
        <el-form-item label="机构编号" prop="code">
          <el-input v-model="form.code" :disabled="!!editing" placeholder="如 demo（用于介绍页 /org/{编号}）" />
        </el-form-item>
        <el-form-item label="机构名称" prop="name">
          <el-input v-model="form.name" maxlength="50" />
        </el-form-item>
        <template v-if="!editing">
          <el-form-item label="管理员姓名" prop="adminName">
            <el-input v-model="form.adminName" />
          </el-form-item>
          <el-form-item label="管理员手机号" prop="adminPhone">
            <el-input v-model="form.adminPhone" maxlength="11" placeholder="登录账号，初始密码 123456" />
          </el-form-item>
        </template>
        <el-form-item label="联系电话">
          <el-input v-model="form.contactPhone" />
        </el-form-item>
        <el-form-item label="地址">
          <el-input v-model="form.address" />
        </el-form-item>
        <el-form-item label="机构简介">
          <el-input v-model="form.introText" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}
</style>
