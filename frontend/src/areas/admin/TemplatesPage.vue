<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgTemplatesList,
  orgTemplateCreate,
  orgTemplateUpdate,
  orgTemplateSetActive,
  orgTemplateRoster,
  orgTemplateEnroll,
  orgTemplateWithdraw,
  orgProductsList,
  orgStaffList,
  orgStudentsList
} from '@/api/org'
import { dayOfWeekLabel, toHHmm } from '@/utils/format'
import type { TemplateListItem, ProductItem, StaffListItem, RosterItem, StudentItem } from '@/api/types'

const auth = useStaffAuth()
const isAdmin = computed(() => auth.role === 'Admin')

const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)

function loader(page: number, pageSize: number) {
  return orgTemplatesList({ page, pageSize })
}

// ===== 下拉数据源 =====
const fixedProducts = ref<ProductItem[]>([])
const teacherOptions = ref<StaffListItem[]>([])
onMounted(async () => {
  if (isAdmin.value) {
    const [products, staff] = await Promise.all([
      orgProductsList({ type: 'Fixed', page: 1, pageSize: 100 }),
      orgStaffList({ page: 1, pageSize: 100 })
    ])
    fixedProducts.value = products.items
    teacherOptions.value = staff.items.filter(
      (s) => s.isActive && (s.role === 'Teacher' || s.role === 'Admin')
    )
  }
})

const DAY_OPTIONS = [1, 2, 3, 4, 5, 6, 0].map((d) => ({ value: d, label: dayOfWeekLabel(d) }))

// ===== 新建 / 编辑班级 =====
const dialogVisible = ref(false)
const saving = ref(false)
const editing = ref<TemplateListItem | null>(null)
const formRef = ref<FormInstance>()
const form = reactive({
  productId: null as number | null,
  dayOfWeek: 2,
  startTime: '18:00',
  endTime: '19:30',
  teacherId: null as number | null,
  room: '',
  capacity: null as number | null
})
const rules: FormRules = {
  productId: [{ required: true, message: '请选择固定排课产品', trigger: 'change' }],
  teacherId: [{ required: true, message: '请选择授课老师', trigger: 'change' }]
}

function openCreate() {
  editing.value = null
  Object.assign(form, { productId: null, dayOfWeek: 2, startTime: '18:00', endTime: '19:30', teacherId: null, room: '', capacity: null })
  dialogVisible.value = true
}

function openEdit(row: TemplateListItem) {
  editing.value = row
  Object.assign(form, {
    productId: row.productId,
    dayOfWeek: row.dayOfWeek,
    startTime: toHHmm(row.startTime),
    endTime: toHHmm(row.endTime),
    teacherId: row.teacherId,
    room: row.room ?? '',
    capacity: row.capacity
  })
  dialogVisible.value = true
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  if (form.endTime <= form.startTime) {
    ElMessage.warning('结束时间必须晚于开始时间')
    return
  }
  if (form.capacity != null && form.capacity < 1) {
    ElMessage.warning('容量至少为 1')
    return
  }
  saving.value = true
  try {
    const payload = {
      productId: form.productId!,
      dayOfWeek: form.dayOfWeek,
      startTime: form.startTime,
      endTime: form.endTime,
      teacherId: form.teacherId!,
      room: form.room || undefined,
      capacity: form.capacity ?? undefined
    }
    if (editing.value) {
      await orgTemplateUpdate(editing.value.id, payload)
      ElMessage.success('已保存（不影响已生成课程，仅对之后生成的实例生效）')
    } else {
      await orgTemplateCreate(payload)
      ElMessage.success('班级已创建')
    }
    dialogVisible.value = false
    tableRef.value?.reload()
  } catch {
    // toast 已由拦截器处理
  } finally {
    saving.value = false
  }
}

async function toggleActive(row: TemplateListItem) {
  await orgTemplateSetActive(row.id, !row.isActive)
  ElMessage.success(row.isActive ? '已停用（不再生成新课）' : '已启用')
  tableRef.value?.reload(false)
}

// ===== 名单抽屉 =====
const rosterVisible = ref(false)
const rosterTemplate = ref<TemplateListItem | null>(null)
const roster = ref<RosterItem[]>([])
const rosterLoading = ref(false)

async function openRoster(row: TemplateListItem) {
  rosterTemplate.value = row
  rosterVisible.value = true
  await loadRoster()
}

async function loadRoster() {
  if (!rosterTemplate.value) return
  rosterLoading.value = true
  try {
    roster.value = await orgTemplateRoster(rosterTemplate.value.id)
  } finally {
    rosterLoading.value = false
  }
}

// 添加学员：远程搜索对话框
const enrollDialog = reactive({ visible: false, options: [] as StudentItem[], studentId: null as number | null, loading: false, busy: false })
async function searchForEnroll(q: string) {
  if (!q) return
  enrollDialog.loading = true
  try {
    const res = await orgStudentsList({ q, page: 1, pageSize: 20 })
    enrollDialog.options = res.items
  } finally {
    enrollDialog.loading = false
  }
}
function openEnroll() {
  enrollDialog.visible = true
  enrollDialog.options = []
  enrollDialog.studentId = null
}
async function submitEnroll() {
  if (!enrollDialog.studentId || !rosterTemplate.value) return
  enrollDialog.busy = true
  try {
    await orgTemplateEnroll(rosterTemplate.value.id, enrollDialog.studentId)
    ElMessage.success('已加入班级')
    enrollDialog.visible = false
    await loadRoster()
    tableRef.value?.reload(false)
  } catch {
    // 409 已在班 / 400 班级已满 —— 拦截器 toast
  } finally {
    enrollDialog.busy = false
  }
}

async function withdraw(row: RosterItem) {
  try {
    await ElMessageBox.confirm(`将「${row.name}」移出班级？（历史点名与课时不受影响）`, '退班', { type: 'warning' })
  } catch {
    return
  }
  await orgTemplateWithdraw(rosterTemplate.value!.id, row.studentId)
  ElMessage.success('已退班')
  await loadRoster()
  tableRef.value?.reload(false)
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-button v-if="isAdmin" type="primary" @click="openCreate">新建班级</el-button>
      <span class="muted">班级 = 每周固定时段的课表模板；修改模板只影响之后生成的课程</span>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column prop="productName" label="产品" min-width="150" />
      <el-table-column label="周几" width="80">
        <template #default="{ row }">{{ dayOfWeekLabel(row.dayOfWeek) }}</template>
      </el-table-column>
      <el-table-column label="时间" width="130">
        <template #default="{ row }">{{ toHHmm(row.startTime) }} - {{ toHHmm(row.endTime) }}</template>
      </el-table-column>
      <el-table-column label="老师" width="100">
        <template #default="{ row }">{{ row.teacherName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="教室" width="90">
        <template #default="{ row }">{{ row.room ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="在班/容量" width="100">
        <template #default="{ row }">
          {{ row.enrolledCount }}{{ row.capacity != null ? ` / ${row.capacity}` : '' }}
        </template>
      </el-table-column>
      <el-table-column label="状态" width="80">
        <template #default="{ row }">
          <el-tag :type="row.isActive ? 'success' : 'info'" size="small">{{ row.isActive ? '启用' : '停用' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column label="操作" :width="isAdmin ? 220 : 80" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openRoster(row)">名单</el-button>
          <template v-if="isAdmin">
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button link :type="row.isActive ? 'warning' : 'success'" @click="toggleActive(row)">
              {{ row.isActive ? '停用' : '启用' }}
            </el-button>
          </template>
        </template>
      </el-table-column>
    </PagedTable>

    <!-- 新建/编辑班级 -->
    <el-dialog v-model="dialogVisible" :title="editing ? '编辑班级' : '新建班级'" width="480px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="90px">
        <el-form-item label="产品" prop="productId">
          <el-select v-model="form.productId" style="width: 100%" placeholder="仅固定排课型产品">
            <el-option v-for="p in fixedProducts" :key="p.id" :label="`${p.name}（${p.sessionCount} 课时）`" :value="p.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="周几">
          <el-select v-model="form.dayOfWeek" style="width: 100%">
            <el-option v-for="d in DAY_OPTIONS" :key="d.value" :label="d.label" :value="d.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="时间">
          <div style="display: flex; gap: 8px; align-items: center">
            <el-time-select v-model="form.startTime" start="06:00" end="22:00" step="00:15" style="width: 130px" />
            <span>—</span>
            <el-time-select v-model="form.endTime" :start="form.startTime" end="23:00" step="00:15" style="width: 130px" />
          </div>
        </el-form-item>
        <el-form-item label="授课老师" prop="teacherId">
          <el-select v-model="form.teacherId" style="width: 100%">
            <el-option v-for="t in teacherOptions" :key="t.id" :label="`${t.name}（${t.role === 'Admin' ? '管理员' : '老师'}）`" :value="t.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="教室">
          <el-input v-model="form.room" placeholder="选填" />
        </el-form-item>
        <el-form-item label="容量">
          <el-input-number v-model="form.capacity" :min="1" :step="1" step-strictly placeholder="选填" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">确定</el-button>
      </template>
    </el-dialog>

    <!-- 名单抽屉 -->
    <el-drawer v-model="rosterVisible" :title="`班级名单 · ${rosterTemplate?.productName ?? ''} ${rosterTemplate ? dayOfWeekLabel(rosterTemplate.dayOfWeek) : ''}`" size="420px">
      <div v-if="isAdmin" style="margin-bottom: 12px">
        <el-button type="primary" @click="openEnroll">添加学员</el-button>
      </div>
      <el-table v-loading="rosterLoading" :data="roster" stripe>
        <el-table-column prop="name" label="姓名" width="100" />
        <el-table-column prop="phone" label="手机号" width="130" />
        <el-table-column v-if="isAdmin" label="操作" width="80">
          <template #default="{ row }">
            <el-button link type="danger" @click="withdraw(row)">退班</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!rosterLoading && roster.length === 0" description="还没有学员，点击「添加学员」分班" :image-size="60" />
    </el-drawer>

    <!-- 添加学员 -->
    <el-dialog v-model="enrollDialog.visible" title="添加学员到班级" width="420px" destroy-on-close append-to-body>
      <el-select
        v-model="enrollDialog.studentId"
        filterable
        remote
        reserve-keyword
        placeholder="输入姓名/手机号搜索"
        :remote-method="searchForEnroll"
        :loading="enrollDialog.loading"
        style="width: 100%"
      >
        <el-option v-for="s in enrollDialog.options" :key="s.id" :label="`${s.name}（${s.phone}）`" :value="s.id" />
      </el-select>
      <template #footer>
        <el-button @click="enrollDialog.visible = false">取消</el-button>
        <el-button type="primary" :disabled="!enrollDialog.studentId" :loading="enrollDialog.busy" @click="submitEnroll">
          加入班级
        </el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
}
.muted {
  color: #909399;
  font-size: 12px;
}
</style>
