<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { orgStudentDetail } from '@/api/org'
import { formatMoney, formatDateTime, dayOfWeekLabel, toHHmm } from '@/utils/format'
import EnumTag from '@/components/EnumTag.vue'
import type { StudentDetail } from '@/api/types'

const route = useRoute()
const router = useRouter()
const detail = ref<StudentDetail | null>(null)
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    detail.value = await orgStudentDetail(Number(route.params.id))
  } finally {
    loading.value = false
  }
}
onMounted(load)
</script>

<template>
  <div v-loading="loading">
    <el-page-header style="margin-bottom: 16px" @back="router.push('/admin/students')">
      <template #content>学员详情</template>
    </el-page-header>

    <template v-if="detail">
      <el-card shadow="never" style="margin-bottom: 16px">
        <el-descriptions :column="4" border>
          <el-descriptions-item label="姓名">{{ detail.student.name }}</el-descriptions-item>
          <el-descriptions-item label="手机号">{{ detail.student.phone }}</el-descriptions-item>
          <el-descriptions-item label="性别">{{ detail.student.gender ?? '—' }}</el-descriptions-item>
          <el-descriptions-item label="生日">{{ detail.student.birthday ?? '—' }}</el-descriptions-item>
          <el-descriptions-item label="状态">
            <el-tag :type="detail.student.isActive ? 'success' : 'info'" size="small">
              {{ detail.student.isActive ? '在读' : '停用' }}
            </el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="备注" :span="3">{{ detail.student.notes ?? '—' }}</el-descriptions-item>
        </el-descriptions>
      </el-card>

      <el-card shadow="never">
        <el-tabs>
          <el-tab-pane :label="`课时包 (${detail.packs.length})`">
            <el-table :data="detail.packs" stripe>
              <el-table-column prop="productName" label="产品" min-width="150" />
              <el-table-column label="剩余/总数" width="120">
                <template #default="{ row }">
                  <strong :style="{ color: row.remaining === 0 ? '#f56c6c' : '#67c23a' }">{{ row.remaining }}</strong>
                  / {{ row.total }}
                </template>
              </el-table-column>
              <el-table-column label="状态" width="100">
                <template #default="{ row }"><EnumTag kind="packStatus" :value="row.status" /></template>
              </el-table-column>
            </el-table>
          </el-tab-pane>

          <el-tab-pane :label="`订单 (${detail.orders.length})`">
            <el-table :data="detail.orders" stripe>
              <el-table-column prop="orderNo" label="订单号" width="190" />
              <el-table-column prop="productName" label="产品" min-width="140" />
              <el-table-column label="金额" width="120">
                <template #default="{ row }">{{ formatMoney(row.amount) }}</template>
              </el-table-column>
              <el-table-column label="状态" width="100">
                <template #default="{ row }"><EnumTag kind="orderStatus" :value="row.status" /></template>
              </el-table-column>
              <el-table-column label="创建时间" width="160">
                <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column label="操作" width="80">
                <template #default="{ row }">
                  <el-button link type="primary" @click="router.push(`/admin/orders/${row.id}`)">详情</el-button>
                </template>
              </el-table-column>
            </el-table>
          </el-tab-pane>

          <el-tab-pane :label="`最近流水 (${detail.recentConsumptions.length})`">
            <el-table :data="detail.recentConsumptions" stripe>
              <el-table-column label="时间" width="160">
                <template #default="{ row }">{{ formatDateTime(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column label="类型" width="110">
                <template #default="{ row }"><EnumTag kind="consumptionKind" :value="row.kind" /></template>
              </el-table-column>
              <el-table-column label="方向" width="80">
                <template #default="{ row }">
                  <span :style="{ color: row.direction === 1 ? '#67c23a' : '#f56c6c' }">
                    {{ row.direction === 1 ? '-1 课时' : '+1 课时' }}
                  </span>
                </template>
              </el-table-column>
              <el-table-column prop="productName" label="产品" min-width="140" />
            </el-table>
          </el-tab-pane>

          <el-tab-pane :label="`班级 (${detail.enrollments.length})`">
            <el-table :data="detail.enrollments" stripe>
              <el-table-column label="周几" width="80">
                <template #default="{ row }">{{ dayOfWeekLabel(row.dayOfWeek) }}</template>
              </el-table-column>
              <el-table-column label="时间" width="140">
                <template #default="{ row }">{{ toHHmm(row.startTime) }} - {{ toHHmm(row.endTime) }}</template>
              </el-table-column>
              <el-table-column prop="teacherName" label="老师" width="110" />
              <el-table-column label="状态" width="100">
                <template #default="{ row }"><EnumTag kind="enrollmentStatus" :value="row.status" /></template>
              </el-table-column>
            </el-table>
          </el-tab-pane>
        </el-tabs>
      </el-card>
    </template>
  </div>
</template>
