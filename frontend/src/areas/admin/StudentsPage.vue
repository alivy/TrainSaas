<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgStudentsList,
  orgStudentCreate,
  orgStudentUpdate,
  orgStaffList,
  orgParentResetPassword
} from '@/api/org'
import { formatDateTime } from '@/utils/format'
import type { StudentItem, StaffListItem } from '@/api/types'

const auth = useStaffAuth()
const router = useRouter()
const role = computed(() => auth.role)

const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)
const search = reactive({ q: '', phone: '' })

function loader(page: number, pageSize: number) {
  return orgStudentsList({
    q: search.q || undefined,
    phone: search.phone || undefined,
    page,
    pageSize
  })
}

// 销售下拉（Admin/FrontDesk 建学员时可指定归属）
const salesOptions = ref<StaffListItem[]>([])
onMounted(async () => {
  if (role.value === 'Admin' || role.value === 'FrontDesk') {
    const res = await orgStaffList({ role: 'Sales', page: 1, pageSize: 100 })
    salesOptions.value = res.items.filter((s) => s.isActive)
  }
})

// ===== 新建 / 编辑 =====
const dialogVisible = ref(false)
const saving = ref(false)
const editing = ref<StudentItem | null>(null)
const formRef = ref<FormInstance>()
const form = reactive({
  name: '',
  phone: '',
  gender: '',
  birthday: '' as string | null,
  assignedSalesId: null as number | null,
  notes: ''
})

const rules: FormRules = {
  name: [
    { required: true, message: '请输入学员姓名', trigger: 'blur' },
    { max: 50, message: '不超过 50 字', trigger: 'blur' }
  ],
  phone: [
    { required: true, message: '请输入联系手机号（家长登录账号）', trigger: 'blur' },
    { pattern: /^1\d{10}$/, message: '手机号格式不正确', trigger: 'blur' }
  ]
}

// 角色可编辑字段（后端同规则：Sales 仅 gender/birthday/notes，FrontDesk 仅 notes）
const canEditName = computed(() => role.value === 'Admin' || !editing.value)
const canEditPhone = computed(() => role.value === 'Admin' || !editing.value)
const canEditSales = computed(() => role.value === 'Admin' || !editing.value)
const canEditBasics = computed(() => role.value === 'Admin' || role.value === 'Sales' || !editing.value)

function openCreate() {
  editing.value = null
  Object.assign(form, { name: '', phone: '', gender: '', birthday: null, assignedSalesId: null, notes: '' })
  dialogVisible.value = true
}

function openEdit(row: StudentItem) {
  editing.value = row
  Object.assign(form, {
    name: row.name,
    phone: row.phone,
    gender: row.gender ?? '',
    birthday: row.birthday,
    assignedSalesId: row.assignedSalesId,
    notes: ''
  })
  dialogVisible.value = true
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  saving.value = true
  try {
    const payload = {
      name: form.name || undefined,
      phone: form.phone || undefined,
      gender: form.gender || undefined,
      birthday: form.birthday || undefined,
      assignedSalesId: form.assignedSalesId ?? undefined,
      notes: form.notes || undefined
    }
    if (editing.value) {
      // 按角色裁剪可提交字段，避免后端 403
      const body =
        role.value === 'FrontDesk'
          ? { notes: payload.notes }
          : role.value === 'Sales'
            ? { gender: payload.gender, birthday: payload.birthday, notes: payload.notes }
            : payload
      await orgStudentUpdate(editing.value.id, body)
      ElMessage.success('已保存')
    } else {
      await orgStudentCreate({
        name: form.name,
        phone: form.phone,
        gender: payload.gender,
        birthday: payload.birthday,
        assignedSalesId: payload.assignedSalesId,
        notes: payload.notes
      })
      ElMessage.success('学员已创建')
    }
    dialogVisible.value = false
    tableRef.value?.reload(!editing.value)
  } catch {
    // toast 已由拦截器处理
  } finally {
    saving.value = false
  }
}

async function resetParentPassword(row: StudentItem) {
  try {
    await ElMessageBox.confirm(
      `将学员「${row.name}」的家长账号（${row.phone}）密码重置为 123456？`,
      '重置家长密码',
      { type: 'warning' }
    )
  } catch {
    return
  }
  try {
    await orgParentResetPassword(row.phone)
    ElMessage.success('已重置为 123456')
  } catch {
    // 404（无家长账号）等由拦截器 toast
  }
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-input v-model="search.q" placeholder="姓名搜索" clearable style="width: 180px" @keyup.enter="tableRef?.reload()" @clear="tableRef?.reload()" />
      <el-input v-model="search.phone" placeholder="手机号精确查找" clearable maxlength="11" style="width: 200px" @keyup.enter="tableRef?.reload()" @clear="tableRef?.reload()" />
      <el-button type="primary" @click="tableRef?.reload()">查询</el-button>
      <el-button v-if="role !== 'Teacher'" type="success" @click="openCreate">新建学员</el-button>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column prop="name" label="姓名" min-width="110" />
      <el-table-column prop="phone" label="联系手机号" width="140" />
      <el-table-column label="性别" width="70">
        <template #default="{ row }">{{ row.gender ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="归属销售" width="110">
        <template #default="{ row }">{{ row.assignedSalesName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="状态" width="80">
        <template #default="{ row }">
          <el-tag :type="row.isActive ? 'success' : 'info'" size="small">{{ row.isActive ? '在读' : '停用' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="230" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="router.push(`/admin/students/${row.id}`)">详情</el-button>
          <el-button v-if="role !== 'Teacher'" link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button v-if="role !== 'Teacher'" link type="warning" @click="resetParentPassword(row)">重置家长密码</el-button>
        </template>
      </el-table-column>
    </PagedTable>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑学员' : '新建学员'" width="480px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="120px">
        <el-form-item label="姓名" prop="name">
          <el-input v-model="form.name" :disabled="editing !== null && !canEditName" maxlength="50" />
        </el-form-item>
        <el-form-item label="联系手机号" prop="phone">
          <el-input v-model="form.phone" :disabled="editing !== null && !canEditPhone" maxlength="11" placeholder="家长登录账号；兄弟姐妹可共用" />
        </el-form-item>
        <el-form-item v-if="canEditBasics" label="性别">
          <el-radio-group v-model="form.gender">
            <el-radio value="男">男</el-radio>
            <el-radio value="女">女</el-radio>
            <el-radio value="">不填</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="canEditBasics" label="生日">
          <el-date-picker v-model="form.birthday" type="date" value-format="YYYY-MM-DD" placeholder="选填" />
        </el-form-item>
        <el-form-item v-if="!editing && canEditSales && role !== 'Sales'" label="归属销售">
          <el-select v-model="form.assignedSalesId" clearable placeholder="选填" style="width: 100%">
            <el-option v-for="s in salesOptions" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="form.notes" type="textarea" :rows="2" />
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
