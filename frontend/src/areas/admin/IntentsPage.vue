<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PagedTable from '@/components/PagedTable.vue'
import EnumTag from '@/components/EnumTag.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import { orgIntentsList, orgIntentFollow, orgIntentMarkConverted, orgIntentMarkInvalid } from '@/api/org'
import { formatDateTime } from '@/utils/format'
import type { IntentListItem, IntentStatus } from '@/api/types'

const auth = useStaffAuth()
// 写操作：Admin 或 Sales（FrontDesk 只读，Teacher 进不了本页）
const canAct = computed(() => auth.role === 'Admin' || auth.role === 'Sales')

const tableRef = ref<{ reload: (resetPage?: boolean) => void } | null>(null)
const filters = reactive<{ status: IntentStatus | '' }>({ status: '' })

function loader(page: number, pageSize: number) {
  return orgIntentsList({ status: filters.status || undefined, page, pageSize })
}

async function follow(row: IntentListItem) {
  try {
    await orgIntentFollow(row.id)
    ElMessage.success('已认领跟进')
    tableRef.value?.reload(false)
  } catch {
    // 409（他人已跟进）由拦截器 toast
  }
}

async function markConverted(row: IntentListItem) {
  try {
    await ElMessageBox.confirm(`将「${row.name ?? row.phone}」的线索标记为已成交？`, '提示', { type: 'success' })
  } catch {
    return
  }
  await orgIntentMarkConverted(row.id)
  ElMessage.success('已标记成交')
  tableRef.value?.reload(false)
}

async function markInvalid(row: IntentListItem) {
  let reason: string
  try {
    const res = await ElMessageBox.prompt('标记无效的原因（选填）', '标记无效', {
      inputPlaceholder: '如：空号、无意向',
      inputValidator: (v: string) => (v ?? '').length <= 200 || '不超过 200 字'
    })
    reason = res.value ?? ''
  } catch {
    return
  }
  await orgIntentMarkInvalid(row.id, reason || undefined)
  ElMessage.success('已标记无效')
  tableRef.value?.reload(false)
}
</script>

<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select v-model="filters.status" placeholder="状态" clearable style="width: 140px" @change="tableRef?.reload()">
        <el-option label="新线索" value="New" />
        <el-option label="跟进中" value="Followed" />
        <el-option label="已成交" value="Converted" />
        <el-option label="无效" value="Invalid" />
      </el-select>
    </div>

    <PagedTable ref="tableRef" :loader="loader">
      <el-table-column label="姓名" width="110">
        <template #default="{ row }">{{ row.name ?? '—' }}</template>
      </el-table-column>
      <el-table-column prop="phone" label="手机号" width="140" />
      <el-table-column label="意向产品" width="150">
        <template #default="{ row }">{{ row.productName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="留言" min-width="180" show-overflow-tooltip>
        <template #default="{ row }">{{ row.message ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="状态" width="100">
        <template #default="{ row }"><EnumTag kind="intentStatus" :value="row.status" /></template>
      </el-table-column>
      <el-table-column label="跟进人" width="100">
        <template #default="{ row }">{{ row.followedByName ?? '—' }}</template>
      </el-table-column>
      <el-table-column label="提交时间" width="160">
        <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column v-if="canAct" label="操作" width="200" fixed="right">
        <template #default="{ row }">
          <el-button
            link
            type="primary"
            :disabled="row.status !== 'New' && row.followedByStaffId !== auth.profile?.staffId || row.status === 'Converted' || row.status === 'Invalid'"
            @click="follow(row)"
          >
            跟进
          </el-button>
          <el-button link type="success" :disabled="row.status === 'Converted' || row.status === 'Invalid'" @click="markConverted(row)">成交</el-button>
          <el-button link type="danger" :disabled="row.status === 'Invalid'" @click="markInvalid(row)">无效</el-button>
        </template>
      </el-table-column>
    </PagedTable>
  </el-card>
</template>

<style scoped>
.toolbar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}
</style>
