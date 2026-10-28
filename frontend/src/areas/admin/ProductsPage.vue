<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import EnumTag from '@/components/EnumTag.vue'
import ImagePreview from '@/components/ImagePreview.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import {
  orgProductsList,
  orgProductCreate,
  orgProductUpdate,
  orgProductSetPublished,
  orgUploadImage
} from '@/api/org'
import { formatMoney, formatDateTime } from '@/utils/format'
import type { ProductItem, ProductType } from '@/api/types'

const auth = useStaffAuth()
const isAdmin = computed(() => auth.role === 'Admin')

const tableRef = ref<InstanceType<typeof PagedTable>>()
const filters = reactive<{ type: ProductType | ''; published: string }>({ type: '', published: '' })

function loader(page: number, pageSize: number) {
  return orgProductsList({
    type: filters.type || undefined,
    published: filters.published === '' ? undefined : filters.published === 'true',
    page,
    pageSize
  })
}

// ===== 新建 / 编辑 =====
const dialogVisible = ref(false)
const saving = ref(false)
const editing = ref<ProductItem | null>(null)
const formRef = ref<FormInstance>()
const form = reactive({
  name: '',
  type: 'Fixed' as ProductType,
  price: 0,
  sessionCount: 1,
  description: '',
  coverImagePath: '' as string
})

const rules: FormRules = {
  name: [
    { required: true, message: '请输入产品名称', trigger: 'blur' },
    { max: 50, message: '不超过 50 字', trigger: 'blur' }
  ],
  price: [{ required: true, message: '请输入价格', trigger: 'blur' }],
  sessionCount: [{ required: true, message: '请输入课时数', trigger: 'blur' }]
}

function openCreate() {
  editing.value = null
  Object.assign(form, { name: '', type: 'Fixed', price: 0, sessionCount: 1, description: '', coverImagePath: '' })
  dialogVisible.value = true
}

function openEdit(row: ProductItem) {
  editing.value = row
  Object.assign(form, {
    name: row.name,
    type: row.type,
    price: row.price,
    sessionCount: row.sessionCount,
    description: row.description ?? '',
    coverImagePath: row.coverImagePath ?? ''
  })
  dialogVisible.value = true
}

async function onCoverChange(file: File) {
  const res = await orgUploadImage(file, 'cover')
  form.coverImagePath = res.path
  ElMessage.success('封面已上传，保存后生效')
}

async function submit() {
  const ok = await formRef.value?.validate().catch(() => false)
  if (!ok) return
  if (form.price < 0) {
    ElMessage.warning('价格不能为负')
    return
  }
  if (!Number.isInteger(form.sessionCount) || form.sessionCount < 1) {
    ElMessage.warning('课时数必须为不小于 1 的整数')
    return
  }
  saving.value = true
  try {
    if (editing.value) {
      await orgProductUpdate(editing.value.id, {
        name: form.name,
        type: form.type,
        price: form.price,
        sessionCount: form.sessionCount,
        description: form.description || undefined,
        coverImagePath: form.coverImagePath || undefined
      })
      ElMessage.success('已保存')
    } else {
      await orgProductCreate({
        name: form.name,
        type: form.type,
        price: form.price,
        sessionCount: form.sessionCount,
        description: form.description || undefined
      })
      ElMessage.success('产品已创建（未上架），可继续编辑封面并上架')
    }
    dialogVisible.value = false
    tableRef.value?.reload()
  } catch {
    // toast 已由拦截器处理
  } finally {
    saving.value = false
  }
}

async function togglePublish(row: ProductItem) {
  await orgProductSetPublished(row.id, !row.isPublished)
  ElMessage.success(row.isPublished ? '已下架' : '已上架')
  tableRef.value?.reload()
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select v-model="filters.type" placeholder="类型" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option label="固定排课" value="Fixed" />
        <el-option label="灵活核销" value="Flexible" />
      </el-select>
      <el-select v-model="filters.published" placeholder="上架状态" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option label="已上架" value="true" />
        <el-option label="未上架" value="false" />
      </el-select>
      <el-button v-if="isAdmin" type="primary" @click="openCreate">新建产品</el-button>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column label="封面" width="80">
        <template #default="{ row }">
          <ImagePreview :path="row.coverImagePath" :size="44" />
        </template>
      </el-table-column>
      <el-table-column prop="name" label="名称" min-width="160" />
      <el-table-column label="类型" width="110">
        <template #default="{ row }"><EnumTag kind="productType" :value="row.type" /></template>
      </el-table-column>
      <el-table-column label="价格" width="120">
        <template #default="{ row }">{{ formatMoney(row.price) }}</template>
      </el-table-column>
      <el-table-column prop="sessionCount" label="课时数" width="90" />
      <el-table-column label="上架" width="90">
        <template #default="{ row }">
          <el-tag :type="row.isPublished ? 'success' : 'info'" size="small">
            {{ row.isPublished ? '已上架' : '未上架' }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="创建时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column v-if="isAdmin" label="操作" width="150" fixed="right">
        <template #default="{ row }">
          <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
          <el-button link :type="row.isPublished ? 'warning' : 'success'" @click="togglePublish(row)">
            {{ row.isPublished ? '下架' : '上架' }}
          </el-button>
        </template>
      </el-table-column>
    </PagedTable>

    <el-dialog v-model="dialogVisible" :title="editing ? '编辑产品' : '新建产品'" width="520px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="90px">
        <el-form-item label="名称" prop="name">
          <el-input v-model="form.name" maxlength="50" />
        </el-form-item>
        <el-form-item label="类型" prop="type">
          <el-radio-group v-model="form.type" :disabled="!!editing">
            <el-radio value="Fixed">固定排课</el-radio>
            <el-radio value="Flexible">灵活核销</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="价格(元)" prop="price">
          <el-input-number v-model="form.price" :min="0" :precision="2" :step="100" />
        </el-form-item>
        <el-form-item label="课时数" prop="sessionCount">
          <el-input-number v-model="form.sessionCount" :min="1" :step="1" step-strictly />
        </el-form-item>
        <el-form-item label="介绍">
          <el-input v-model="form.description" type="textarea" :rows="3" />
        </el-form-item>
        <el-form-item v-if="editing" label="封面">
          <div style="display: flex; align-items: center; gap: 12px">
            <ImagePreview :path="form.coverImagePath || null" :size="64" />
            <el-upload
              :show-file-list="false"
              accept="image/jpeg,image/png,image/webp"
              :auto-upload="false"
              :on-change="(f: { raw: File }) => onCoverChange(f.raw)"
            >
              <el-button>上传封面</el-button>
            </el-upload>
          </div>
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
