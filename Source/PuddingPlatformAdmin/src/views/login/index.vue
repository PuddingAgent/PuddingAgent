<template>
  <div class="login-page">
    <!-- 左侧品牌区 -->
    <div class="brand-panel" aria-hidden="true">
      <div class="brand-bg-grid" />
      <div class="brand-content">
        <div class="brand-logo">
          <svg width="40" height="40" viewBox="0 0 40 40" fill="none" xmlns="http://www.w3.org/2000/svg">
            <rect width="40" height="40" rx="10" fill="white" fill-opacity="0.15" />
            <path d="M10 20C10 14.477 14.477 10 20 10C25.523 10 30 14.477 30 20C30 25.523 25.523 30 20 30" stroke="white" stroke-width="2.5" stroke-linecap="round" />
            <circle cx="20" cy="20" r="4" fill="white" />
            <path d="M14 26L10 30" stroke="white" stroke-width="2.5" stroke-linecap="round" />
          </svg>
          <span class="brand-name">Pudding</span>
        </div>
        <h1 class="brand-headline">智能体协作<br />管理平台</h1>
        <p class="brand-desc">统一管理工作空间、智能体与多智能体协作网络，构建下一代 AI 驱动的工作流。</p>
        <ul class="feature-list" role="list">
          <li v-for="feat in features" :key="feat.text" class="feature-item">
            <svg class="feature-icon" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clip-rule="evenodd" />
            </svg>
            <span>{{ feat.text }}</span>
          </li>
        </ul>
        <!-- 装饰圆圈 -->
        <div class="deco-circle deco-1" />
        <div class="deco-circle deco-2" />
      </div>
    </div>

    <!-- 右侧登录表单区 -->
    <div class="form-panel">
      <div class="form-wrapper">
        <div class="form-header">
          <h2 class="form-title">欢迎回来</h2>
          <p class="form-subtitle">登录以访问管理控制台</p>
        </div>

        <el-form
          ref="loginFormRef"
          :model="loginForm"
          :rules="loginRules"
          class="login-form"
          autocomplete="on"
          label-position="top"
          @submit.prevent="handleLogin"
        >
          <el-form-item label="用户名" prop="username">
            <el-input
              v-model="loginForm.username"
              placeholder="请输入用户名"
              name="username"
              type="text"
              autocomplete="username"
              size="large"
              :prefix-icon="UserIcon"
              :disabled="loading"
            />
          </el-form-item>

          <el-form-item label="密码" prop="password" style="margin-top: 20px">
            <el-input
              v-model="loginForm.password"
              :type="passwordVisible ? 'text' : 'password'"
              placeholder="请输入密码"
              name="password"
              autocomplete="current-password"
              size="large"
              :prefix-icon="LockIcon"
              :disabled="loading"
              @keyup.enter="handleLogin"
            >
              <template #suffix>
                <button
                  type="button"
                  class="pwd-toggle"
                  :aria-label="passwordVisible ? '隐藏密码' : '显示密码'"
                  @click="passwordVisible = !passwordVisible"
                >
                  <svg v-if="passwordVisible" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94" /><path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19" /><line x1="1" y1="1" x2="23" y2="23" />
                  </svg>
                  <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                    <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z" /><circle cx="12" cy="12" r="3" />
                  </svg>
                </button>
              </template>
            </el-input>
          </el-form-item>

          <div class="form-options">
            <el-checkbox v-model="rememberMe" label="记住我" size="small" />
          </div>

          <el-button
            native-type="submit"
            type="primary"
            size="large"
            class="submit-btn"
            :loading="loading"
            :disabled="loading"
            @click="handleLogin"
          >
            <span v-if="!loading">登录</span>
            <span v-else>登录中...</span>
          </el-button>
        </el-form>

        <p class="form-footer">
          Pudding Agent Network &copy; {{ new Date().getFullYear() }}
        </p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, watch, markRaw } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '@/stores/user'
import type { FormInstance, FormRules } from 'element-plus'
import { User, Lock } from '@element-plus/icons-vue'

const UserIcon = markRaw(User)
const LockIcon = markRaw(Lock)

const userStore = useUserStore()
const route = useRoute()
const router = useRouter()

const loginFormRef = ref<FormInstance>()
const loading = ref(false)
const passwordVisible = ref(false)
const rememberMe = ref(false)
const redirect = ref('')

const features = [
  { text: '多智能体协作与编排' },
  { text: '工作空间隔离与权限管理' },
  { text: '实时监控与可观测性' },
  { text: 'MCP 工具与插件生态' },
]

const loginForm = reactive({
  username: '',
  password: '',
})

const loginRules = reactive<FormRules>({
  username: [{ required: true, trigger: 'blur', message: '请输入用户名' }],
  password: [{ required: true, trigger: 'blur', message: '请输入密码（至少6位）', min: 6 }],
})

watch(
  () => route.query,
  (query) => {
    if (query.redirect) {
      redirect.value = query.redirect as string
    }
  },
  { immediate: true },
)

async function handleLogin() {
  const valid = await loginFormRef.value?.validate().catch(() => false)
  if (!valid) return

  loading.value = true
  try {
    await userStore.login(loginForm)
    router.push({ path: redirect.value || '/' })
  } finally {
    loading.value = false
  }
}
</script>

<style lang="scss" scoped>
@import url('https://fonts.googleapis.com/css2?family=Fira+Code:wght@400;600&family=Fira+Sans:wght@300;400;500;600&display=swap');

// Design tokens
$indigo-600: #6366f1;
$indigo-700: #4f46e5;
$indigo-900: #312e81;
$indigo-950: #1e1b4b;
$emerald-500: #10b981;
$slate-50:  #f8fafc;
$slate-100: #f1f5f9;
$slate-400: #94a3b8;
$slate-600: #475569;
$slate-900: #0f172a;

.login-page {
  display: flex;
  min-height: 100vh;
  font-family: 'Fira Sans', system-ui, sans-serif;
}

/* ── 左侧品牌面板 ─────────────────────────────── */
.brand-panel {
  position: relative;
  flex: 0 0 45%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, $indigo-950 0%, $indigo-900 40%, $indigo-700 100%);
  overflow: hidden;
  padding: 48px;

  @media (max-width: 768px) {
    display: none;
  }
}

.brand-bg-grid {
  position: absolute;
  inset: 0;
  background-image:
    linear-gradient(rgba(255,255,255,0.04) 1px, transparent 1px),
    linear-gradient(90deg, rgba(255,255,255,0.04) 1px, transparent 1px);
  background-size: 40px 40px;
}

.brand-content {
  position: relative;
  z-index: 1;
  max-width: 400px;
  width: 100%;
}

.brand-logo {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 48px;

  .brand-name {
    font-family: 'Fira Code', monospace;
    font-size: 22px;
    font-weight: 600;
    color: white;
    letter-spacing: 0.05em;
  }
}

.brand-headline {
  font-size: 36px;
  font-weight: 600;
  line-height: 1.25;
  color: white;
  margin: 0 0 20px;
  letter-spacing: -0.02em;
}

.brand-desc {
  font-size: 15px;
  line-height: 1.65;
  color: rgba(255,255,255,0.65);
  margin: 0 0 40px;
}

.feature-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.feature-item {
  display: flex;
  align-items: center;
  gap: 10px;
  color: rgba(255,255,255,0.85);
  font-size: 14px;
  font-weight: 400;

  .feature-icon {
    flex-shrink: 0;
    width: 18px;
    height: 18px;
    color: $emerald-500;
  }
}

.deco-circle {
  position: absolute;
  border-radius: 50%;
  background: rgba(255,255,255,0.04);
  pointer-events: none;
}
.deco-1 {
  width: 320px; height: 320px;
  bottom: -80px; right: -80px;
}
.deco-2 {
  width: 180px; height: 180px;
  top: 60px; right: 40px;
  background: rgba(255,255,255,0.03);
  border: 1px solid rgba(255,255,255,0.08);
}

/* ── 右侧表单面板 ─────────────────────────────── */
.form-panel {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  background-color: $slate-50;
  padding: 48px 32px;
}

.form-wrapper {
  width: 100%;
  max-width: 400px;
}

.form-header {
  margin-bottom: 36px;

  .form-title {
    font-size: 28px;
    font-weight: 600;
    color: $slate-900;
    margin: 0 0 8px;
    letter-spacing: -0.02em;
  }

  .form-subtitle {
    font-size: 14px;
    color: $slate-600;
    margin: 0;
  }
}

.login-form {
  :deep(.el-form-item__label) {
    font-size: 13px;
    font-weight: 500;
    color: $slate-600;
    padding-bottom: 6px;
    line-height: 1.4;
  }

  :deep(.el-input__wrapper) {
    border-radius: 8px;
    box-shadow: 0 0 0 1px #e2e8f0;
    background: white;
    transition: box-shadow 0.15s ease;

    &:hover {
      box-shadow: 0 0 0 1px #a5b4fc;
    }

    &.is-focus {
      box-shadow: 0 0 0 2px rgba(99, 102, 241, 0.2), 0 0 0 1px $indigo-600;
    }
  }

  :deep(.el-input__inner) {
    font-size: 14px;
    color: $slate-900;
    height: 42px;

    &::placeholder {
      color: $slate-400;
    }
  }

  :deep(.el-input__prefix-icon) {
    color: $slate-400;
  }

  :deep(.el-form-item__error) {
    font-size: 12px;
    padding-top: 4px;
  }
}

.form-options {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 16px 0 24px;

  :deep(.el-checkbox__label) {
    font-size: 13px;
    color: $slate-600;
  }
}

.pwd-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  background: transparent;
  cursor: pointer;
  color: $slate-400;
  padding: 0;
  transition: color 0.15s ease;

  &:hover {
    color: $indigo-600;
  }

  &:focus-visible {
    outline: 2px solid $indigo-600;
    border-radius: 4px;
  }

  svg {
    width: 16px;
    height: 16px;
  }
}

.submit-btn {
  width: 100%;
  height: 44px;
  border-radius: 8px;
  font-size: 15px;
  font-weight: 500;
  letter-spacing: 0.01em;
  background: $indigo-600;
  border-color: $indigo-600;
  transition: background 0.15s ease, transform 0.1s ease, box-shadow 0.15s ease;

  &:hover:not(:disabled) {
    background: $indigo-700;
    border-color: $indigo-700;
    box-shadow: 0 4px 12px rgba(99, 102, 241, 0.35);
    transform: translateY(-1px);
  }

  &:active:not(:disabled) {
    transform: translateY(0);
  }

  &:focus-visible {
    outline: 2px solid $indigo-600;
    outline-offset: 2px;
  }
}

.form-footer {
  margin-top: 40px;
  text-align: center;
  font-size: 12px;
  color: $slate-400;
}

/* ── 响应式 ──────────────────────────────────── */
@media (max-width: 480px) {
  .form-panel {
    padding: 32px 20px;
  }

  .form-wrapper {
    max-width: 100%;
  }

  .form-header .form-title {
    font-size: 24px;
  }
}
</style>
