<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import EnumTag from '@/components/EnumTag.vue'
import AuthedImage from '@/components/AuthedImage.vue'
import VoucherUploader from '@/components/VoucherUploader.vue'
import { useStaffAuth } from '@/stores/staffAuth'
import { orgOrderDetail, orgOrderConfirm, orgOrderCancel, orgOrderVoid } from '@/api/org'
import { formatMoney, formatDateTime } from '@/utils/format'
import type { OrderDetail } from '@/api/types'

const auth = useStaffAuth()
const route = useRoute()
const router = useRouter()

const detail = ref<OrderDetail | null>(null)
const loading = ref(false)
const acting = ref(false)

const isAdmin = computed(() => auth.role === 'Admin')
const isCreator = computed(() => detail.value?.createdById === auth.profile?.staffId)
const canCancel = computed(
  () => detail.value?.status === 'Pending' && (isAdmin.value || isCreator.value)
)

// 作废对话框
const voidDialog = reactive({ visible: false, reason: '', refundAmount: null as number | null })

async function load() {
  loading.value = true
  try {
    detail.value = await orgOrderDetail(Number(route.params.id))
  } finally {
    loading.value = false
  }
}
onMounted(load)

async function confirmOrder() {
  try {
    await ElMessageBox.confirm(
      '确认已收到款项？确认后课时包立即生效，并自动为家长手机号开通账号（初始密码 123456）。',
      '确认收款',
      { type: 'warning' }
    )
  } catch {
    return
  }
  acting.value = true
  try {
    await orgOrderConfirm(detail.value!.id)
    ElMessage.success('订单已生效')
    await load() // 409（他人已确认）时拦截器 toast，这里刷新拿最新状态
  } catch {
    await load()
  } finally {
    acting.value = false
  }
}

async function cancelOrder() {
  try {
    await ElMessageBox.confirm('取消该待确认订单？', '取消订单', { type: 'warning' })
  } catch {
    return
  }
  acting.value = true
  try {
    await orgOrderCancel(detail.value!.id)
    ElMessage.success('订单已取消')
    await load()
  } finally {
    acting.value = false
  }
}

function openVoid() {
  voidDialog.reason = ''
  voidDialog.refundAmount = null
  voidDialog.visible = true
}

async function submitVoid() {
  const reason = voidDialog.reason.trim()
  if (!reason || reason.length > 200) {
    ElMessage.warning('请填写作废原因（不超过 200 字）')
    return
  }
  if (voidDialog.refundAmount != null && voidDialog.refundAmount < 0) {
    ElMessage.warning('退款金额不能为负')
    return
  }
  acting.value = true
  try {
    await orgOrderVoid(detail.value!.id, {
      reason,
      refundAmount: voidDialog.refundAmount ?? undefined
    })
    voidDialog.visible = false
    ElMessage.success('订单已作废，剩余课时已失效')
    await load()
  } finally {
    acting.value = false
  }
}
</script>

<template>
  <div v-loading="loading">
    <el-page-header style="margin-bottom: 16px" @back="router.push('/admin/orders')">
      <template #content>订单详情</template>
    </el-page-header>

    <template v-if="detail">
      <el-card shadow="never" style="margin-bottom: 16px">
        <div class="head-row">
          <div>
            <span class="order-no">{{ detail.orderNo }}</span>
            <EnumTag kind="orderStatus" :value="detail.status" style="margin-left: 12px" />
          </div>
          <div class="actions">
            <el-button
              v-if="isAdmin && detail.status === 'Pending'"
              type="primary"
              :loading="acting"
              @click="confirmOrder"
            >
              确认收款
            </el-button>
            <el-button v-if="canCancel" :loading="acting" @click="cancelOrder">取消订单</el-button>
            <el-button v-if="isAdmin && detail.status === 'Active'" type="danger" @click="openVoid">
              作废/退费
            </el-button>
          </div>
        </div>

        <el-descriptions :column="3" border style="margin-top: 16px">
          <el-descriptions-item label="学员">
            {{ detail.student.name }}（{{ detail.student.phone }}）
          </el-descriptions-item>
          <el-descriptions-item label="产品">
            {{ detail.product.name }}（{{ detail.product.type === 'Fixed' ? '固定排课' : '灵活核销' }}）
          </el-descriptions-item>
          <el-descriptions-item label="实收金额">
            <strong>{{ formatMoney(detail.amount) }}</strong>
          </el-descriptions-item>
          <el-descriptions-item label="创建时间">{{ formatDateTime(detail.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="确认时间">{{ formatDateTime(detail.confirmedAt) || '—' }}</el-descriptions-item>
          <el-descriptions-item label="备注">{{ detail.remark ?? '—' }}</el-descriptions-item>
          <template v-if="detail.status === 'Voided'">
            <el-descriptions-item label="作废时间">{{ formatDateTime(detail.voidedAt) }}</el-descriptions-item>
            <el-descriptions-item label="作废原因" :span="2">{{ detail.voidReason }}</el-descriptions-item>
            <el-descriptions-item label="退款金额">
              {{ detail.refundAmount != null ? formatMoney(detail.refundAmount) : '—' }}
            </el-descriptions-item>
          </template>
          <el-descriptions-item v-if="detail.pack" label="课时包" :span="2">
            剩余 <strong>{{ detail.pack.remainingSessions }}</strong> / {{ detail.pack.totalSessions }}
            <EnumTag kind="packStatus" :value="detail.pack.status" style="margin-left: 8px" />
          </el-descriptions-item>
        </el-descriptions>
      </el-card>

      <el-card shadow="never">
        <template #header>
          <div class="head-row">
            <span>支付凭据（{{ detail.vouchers.length }}）</span>
            <VoucherUploader
              v-if="detail.status === 'Pending' || detail.status === 'Active'"
              :order-id="detail.id"
              @uploaded="load"
            />
          </div>
        </template>
        <el-empty v-if="detail.vouchers.length === 0" description="尚未上传凭据" :image-size="60" />
        <div v-else class="voucher-list">
          <AuthedImage v-for="v in detail.vouchers" :key="v.id" :url="v.url" :size="100" />
        </div>
      </el-card>
    </template>

    <el-dialog v-model="voidDialog.visible" title="作废订单 / 退费登记" width="460px">
      <el-alert
        type="warning"
        :closable="false"
        show-icon
        title="作废后剩余课时立即失效；已消耗课时保留，实际退款线下进行。"
        style="margin-bottom: 16px"
      />
      <el-form label-width="90px">
        <el-form-item label="作废原因" required>
          <el-input v-model="voidDialog.reason" type="textarea" :rows="3" maxlength="200" show-word-limit placeholder="必填，如：家长申请退费" />
        </el-form-item>
        <el-form-item label="退款金额">
          <el-input-number v-model="voidDialog.refundAmount" :min="0" :precision="2" :step="100" placeholder="选填" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="voidDialog.visible = false">返回</el-button>
        <el-button type="danger" :loading="acting" @click="submitVoid">确认作废</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.head-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.order-no {
  font-size: 16px;
  font-weight: 600;
}
.actions {
  display: flex;
  gap: 8px;
}
.voucher-list {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
</style>
