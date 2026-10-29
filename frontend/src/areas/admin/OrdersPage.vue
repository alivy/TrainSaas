<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import EnumTag from '@/components/EnumTag.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgOrdersList,
  orgOrderCreate,
  orgStudentsList,
  orgProductsList,
  orgStaffList
} from '@/api/org'
import { formatMoney, formatDateTime } from '@/utils/format'
import type { OrderStatus, StudentItem, ProductItem, StaffListItem } from '@/api/types'

const auth = useStaffAuth()
const router = useRouter()
const role = computed(() => auth.role)

const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)
const filters = reactive<{ status: OrderStatus | ''; studentId: number | null }>({ status: '', studentId: null })

function loader(page: number, pageSize: number) {
  return orgOrdersList({
    status: filters.status || undefined,
    studentId: filters.studentId ?? undefined,
    page,
    pageSize
  })
}

// ===== 新建订单 =====
const dialogVisible = ref(false)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({
  studentId: null as number | null,
  productId: null as number | null,
  amount: 0,
  salesId: null as number | null,
  remark: ''
})
const rules: FormRules = {
  studentId: [{ required: true, message: '请选择学员', trigger: 'change' }],
  productId: [{ required: true, message: '请选择产品', trigger: 'change' }],
  amount: [{ required: true, message: '请输入实收金额', trigger: 'blur' }]
}

// 学员远程搜索
const studentOptions = ref<StudentItem[]>([])
const studentLoading = ref(false)
async function searchStudents(q: string) {
  if (!q) return
  studentLoading.value = true
  try {
    const res = await orgStudentsList({ q, page: 1, pageSize: 20 })
    studentOptions.value = res.items
  } finally {
    studentLoading.value = false
  }
}

const productOptions = ref<ProductItem[]>([])
const salesOptions = ref<StaffListItem[]>([])

onMounted(async () => {
  const products = await orgProductsList({ page: 1, pageSize: 100 })
  productOptions.value = products.items
  if (role.value === 'Admin') {
    const sales = await orgStaffList({ role: 'Sales', page: 1, pageSize: 100 })
    salesOptions.value = sales.items.filter((s) => s.isActive)
  }
})

function openCreate() {
  Object.assign(form, { studentId: null, productId: null, amount: 0, salesId: null, remark: '' })
  studentOptions.value = []
  dialogVisible.value = true
}

function onProductChange(productId: number) {
  const p = productOptions.value.find((x) => x.id === productId)
  if (p) form.amount = p.price // 默认产品价，可改（议价）
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  if (form.amount < 0) {
    ElMessage.warning('金额不能为负')
    return
  }
  saving.value = true
  try {
    const created = await orgOrderCreate({
      studentId: form.studentId!,
      productId: form.productId!,
      amount: form.amount,
      salesId: form.salesId ?? undefined,
      remark: form.remark || undefined
    })
    dialogVisible.value = false
    ElMessage.success(`订单 ${created.orderNo} 已创建，请上传支付凭据`)
    router.push(`/admin/orders/${created.id}`) // 直接进详情传凭据
  } catch {
    // toast 已由拦截器处理
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select v-model="filters.status" placeholder="状态" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option label="待确认" value="Pending" />
        <el-option label="已生效" value="Active" />
        <el-option label="已取消" value="Cancelled" />
        <el-option label="已作废" value="Voided" />
      </el-select>
      <el-select
        v-model="filters.studentId"
        filterable
        remote
        clearable
        reserve-keyword
        placeholder="按学员筛选（搜索）"
        :remote-method="searchStudents"
        :loading="studentLoading"
        style="width: 220px"
        @change="tableRef?.reload()"
      >
        <el-option v-for="s in studentOptions" :key="s.id" :label="`${s.name}（${s.phone}）`" :value="s.id" />
      </el-select>
      <el-button type="primary" @click="openCreate">新建订单</el-button>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column prop="orderNo" label="订单号" width="190" />
      <el-table-column prop="studentName" label="学员" width="110" />
      <el-table-column prop="productName" label="产品" min-width="140" />
      <el-table-column label="实收金额" width="120">
        <template #default="{ row }">{{ formatMoney(row.amount) }}</template>
      </el-table-column>
      <el-table-column label="归属销售" width="110">
        <template #default="{ row }">{{ row.salesName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="状态" width="100">
        <template #default="{ row }"><EnumTag kind="orderStatus" :value="row.status" /></template>
      </el-table-column>
      <el-table-column label="创建时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column label="操作" width="80" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="router.push(`/admin/orders/${row.id}`)">详情</el-button>
        </template>
      </el-table-column>
    </PagedTable>

    <el-dialog v-model="dialogVisible" title="新建订单" width="520px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="100px">
        <el-form-item label="学员" prop="studentId">
          <el-select
            v-model="form.studentId"
            filterable
            remote
            reserve-keyword
            placeholder="输入姓名/手机号搜索"
            :remote-method="searchStudents"
            :loading="studentLoading"
            style="width: 100%"
          >
            <el-option
              v-for="s in studentOptions"
              :key="s.id"
              :label="`${s.name}（${s.phone}）`"
              :value="s.id"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="产品" prop="productId">
          <el-select v-model="form.productId" style="width: 100%" @change="onProductChange">
            <el-option
              v-for="p in productOptions"
              :key="p.id"
              :label="`${p.name}（${p.sessionCount} 课时 / ${formatMoney(p.price)}）`"
              :value="p.id"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="实收金额" prop="amount">
          <el-input-number v-model="form.amount" :min="0" :precision="2" :step="100" />
          <span class="muted" style="margin-left: 8px">默认产品价，可按实际议价修改</span>
        </el-form-item>
        <el-form-item v-if="role === 'Admin'" label="归属销售">
          <el-select v-model="form.salesId" clearable placeholder="默认为创建人" style="width: 100%">
            <el-option v-for="s in salesOptions" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="form.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">创建并上传凭据</el-button>
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
.muted {
  color: #909399;
  font-size: 12px;
}
</style>
