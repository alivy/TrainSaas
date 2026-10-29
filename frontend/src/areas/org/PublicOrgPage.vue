<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { publicOrgPage, publicSubmitIntent } from '@/api/public'
import { formatMoney } from '@/utils/format'
import type { PublicOrgResponse, PublicOrgProductItem } from '@/api/types'

const route = useRoute()
const code = String(route.params.code ?? '')

const org = ref<PublicOrgResponse | null>(null)
const notFound = ref(false)
const loading = ref(true)

onMounted(async () => {
  try {
    org.value = await publicOrgPage(code)
  } catch {
    notFound.value = true // 404：不存在或已停用（后端同一响应，不泄露存在性）
  } finally {
    loading.value = false
  }
})

function imageUrl(path: string | null | undefined): string {
  return path ? `/api/files/images/${path}` : ''
}

// ===== 报名意向 =====
const intent = reactive({
  visible: false,
  product: null as PublicOrgProductItem | null,
  name: '',
  phone: '',
  message: '',
  busy: false,
  cooldown: 0 // 429 冷却秒数（Review Focus 5）
})
let cooldownTimer: ReturnType<typeof setInterval> | null = null

onUnmounted(() => {
  if (cooldownTimer) clearInterval(cooldownTimer) // M1：卸载清理定时器
})

function openIntent(product: PublicOrgProductItem | null) {
  intent.product = product
  intent.visible = true
}

function startCooldown() {
  intent.cooldown = 60
  cooldownTimer = setInterval(() => {
    intent.cooldown -= 1
    if (intent.cooldown <= 0 && cooldownTimer) {
      clearInterval(cooldownTimer)
      cooldownTimer = null
    }
  }, 1000)
}

const submitted = ref(false)

async function submitIntent() {
  if (!/^1\d{10}$/.test(intent.phone)) {
    window.alert('请输入正确的手机号')
    return
  }
  if (intent.message.length > 200) {
    window.alert('留言不超过 200 字')
    return
  }
  if (intent.cooldown > 0) return
  intent.busy = true
  try {
    await publicSubmitIntent(code, {
      name: intent.name || undefined,
      phone: intent.phone,
      productId: intent.product?.id,
      message: intent.message || undefined
    })
    intent.visible = false
    submitted.value = true
  } catch (e) {
    // 拦截器已 toast；429 时进入冷却
    const status = (e as { response?: { status?: number } })?.response?.status
    if (status === 429) startCooldown()
  } finally {
    intent.busy = false
  }
}
</script>

<template>
  <div v-loading="loading" class="org-page">
    <!-- 机构不存在/停用 -->
    <div v-if="notFound" class="not-found">
      <div class="nf-icon">🏫</div>
      <h2>机构不存在或已停止服务</h2>
      <p>请确认链接是否正确，或联系机构获取最新地址。</p>
    </div>

    <template v-else-if="org">
      <!-- 提交成功全屏态 -->
      <div v-if="submitted" class="success-state">
        <div class="nf-icon">✅</div>
        <h2>报名意向已提交</h2>
        <p>{{ org.name }} 将尽快与您联系，请保持手机畅通。</p>
        <el-button @click="submitted = false">返回机构页</el-button>
      </div>

      <template v-else>
        <!-- 头部 -->
        <header class="org-head">
          <img v-if="org.logoPath" :src="imageUrl(org.logoPath)" class="org-logo" alt="logo" />
          <div v-else class="org-logo placeholder">🎓</div>
          <h1 class="org-name">{{ org.name }}</h1>
          <p v-if="org.introText" class="org-intro">{{ org.introText }}</p>
          <div class="org-contact">
            <a v-if="org.contactPhone" :href="`tel:${org.contactPhone}`" class="contact-link">
              📞 {{ org.contactPhone }}
            </a>
            <span v-if="org.address" class="contact-addr">📍 {{ org.address }}</span>
          </div>
        </header>

        <!-- 课程产品 -->
        <section class="products">
          <h2 class="section-title">课程产品</h2>
          <div v-for="p in org.products" :key="p.id" class="product-card">
            <img v-if="p.coverImagePath" :src="imageUrl(p.coverImagePath)" class="cover" alt="" />
            <div v-else class="cover gradient"></div>
            <div class="p-body">
              <div class="p-name">{{ p.name }}</div>
              <div class="p-tags">
                <span class="tag" :class="p.type === 'Fixed' ? 'fixed' : 'flex'">
                  {{ p.type === 'Fixed' ? '固定排课' : '灵活核销' }}
                </span>
                <span class="sessions">{{ p.sessionCount }} 课时</span>
              </div>
              <p v-if="p.description" class="p-desc">{{ p.description }}</p>
              <div class="p-foot">
                <span class="price">{{ formatMoney(p.price) }}</span>
                <button class="enroll-btn" @click="openIntent(p)">我要报名</button>
              </div>
            </div>
          </div>
          <el-empty v-if="org.products.length === 0" description="机构暂未发布课程" />
        </section>

        <div class="bottom-cta">
          <button class="enroll-btn wide" @click="openIntent(null)">咨询报名</button>
        </div>
      </template>
    </template>

    <!-- 报名弹层 -->
    <el-dialog v-model="intent.visible" title="报名 / 咨询" width="min(420px, 92vw)">
      <el-form label-position="top">
        <el-form-item v-if="intent.product" label="意向课程">
          <el-input :model-value="intent.product.name" disabled />
        </el-form-item>
        <el-form-item label="称呼（选填）">
          <el-input v-model="intent.name" maxlength="20" placeholder="如：王妈妈" />
        </el-form-item>
        <el-form-item label="手机号（必填）" required>
          <el-input v-model="intent.phone" maxlength="11" placeholder="机构将通过此号码联系您" />
        </el-form-item>
        <el-form-item label="留言（选填）">
          <el-input v-model="intent.message" type="textarea" :rows="3" maxlength="200" show-word-limit />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="intent.visible = false">取消</el-button>
        <el-button
          type="primary"
          :loading="intent.busy"
          :disabled="intent.cooldown > 0"
          @click="submitIntent"
        >
          {{ intent.cooldown > 0 ? `提交过于频繁（${intent.cooldown}s）` : '提交' }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.org-page {
  max-width: 640px;
  margin: 0 auto;
  min-height: 100vh;
  background: #fff;
  padding-bottom: 88px;
}
.not-found,
.success-state {
  text-align: center;
  padding: 96px 24px;
  color: #606266;
}
.nf-icon {
  font-size: 56px;
  margin-bottom: 12px;
}
.org-head {
  padding: 28px 20px 20px;
  background: linear-gradient(160deg, #eaf3ff 0%, #ffffff 100%);
  text-align: center;
}
.org-logo {
  width: 72px;
  height: 72px;
  border-radius: 16px;
  object-fit: cover;
}
.org-logo.placeholder {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 34px;
  background: #dfe9f7;
}
.org-name {
  font-size: 22px;
  margin: 10px 0 6px;
}
.org-intro {
  font-size: 14px;
  color: #606266;
  white-space: pre-wrap;
  margin: 0 12px 10px;
}
.org-contact {
  display: flex;
  gap: 16px;
  justify-content: center;
  flex-wrap: wrap;
  font-size: 13px;
}
.contact-link {
  color: #409eff;
  text-decoration: none;
}
.contact-addr {
  color: #909399;
}
.section-title {
  font-size: 17px;
  padding: 20px 20px 8px;
}
.products {
  padding-bottom: 8px;
}
.product-card {
  display: flex;
  gap: 12px;
  margin: 12px 16px;
  padding: 12px;
  border: 1px solid #ebeef5;
  border-radius: 12px;
}
.cover {
  width: 96px;
  height: 96px;
  border-radius: 8px;
  object-fit: cover;
  flex-shrink: 0;
}
.cover.gradient {
  background: linear-gradient(135deg, #a0cfff, #c2e7b0);
}
.p-body {
  flex: 1;
  min-width: 0;
}
.p-name {
  font-size: 15px;
  font-weight: 600;
}
.p-tags {
  margin: 4px 0;
  display: flex;
  gap: 8px;
  align-items: center;
}
.tag {
  font-size: 11px;
  padding: 1px 8px;
  border-radius: 10px;
}
.tag.fixed {
  background: #ecf5ff;
  color: #409eff;
}
.tag.flex {
  background: #f0f9eb;
  color: #67c23a;
}
.sessions {
  font-size: 12px;
  color: #909399;
}
.p-desc {
  font-size: 12px;
  color: #606266;
  margin: 4px 0;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.p-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 6px;
}
.price {
  color: #f56c6c;
  font-weight: 700;
  font-size: 16px;
}
.enroll-btn {
  min-height: 40px; /* 触控目标（Review Focus 5） */
  padding: 0 18px;
  border: none;
  border-radius: 20px;
  background: #409eff;
  color: #fff;
  font-size: 14px;
  cursor: pointer;
}
.enroll-btn.wide {
  width: 100%;
}
.enroll-btn:disabled {
  opacity: 0.6;
}
.bottom-cta {
  position: fixed;
  bottom: 0;
  left: 0;
  right: 0;
  padding: 12px 16px calc(12px + env(safe-area-inset-bottom));
  background: #fff;
  border-top: 1px solid #ebeef5;
  max-width: 640px;
  margin: 0 auto;
}
</style>
