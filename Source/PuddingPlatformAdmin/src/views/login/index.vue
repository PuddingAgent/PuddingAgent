<template>
  <div class="login-container">
    <el-form ref="loginFormRef" :model="loginForm" :rules="loginRules" class="login-form" autocomplete="on">
      <div class="title-container">
        <h3 class="title">Pudding 管理平台</h3>
      </div>

      <el-form-item prop="username">
        <el-input
          v-model="loginForm.username"
          placeholder="用户名"
          name="username"
          type="text"
          autocomplete="on"
          :prefix-icon="User"
        />
      </el-form-item>

      <el-form-item prop="password">
        <el-input
          v-model="loginForm.password"
          :type="passwordVisible ? 'text' : 'password'"
          placeholder="密码"
          name="password"
          autocomplete="on"
          :prefix-icon="Lock"
          @keyup.enter="handleLogin"
        >
          <template #suffix>
            <el-icon class="show-pwd" @click="passwordVisible = !passwordVisible">
              <View v-if="passwordVisible" />
              <Hide v-else />
            </el-icon>
          </template>
        </el-input>
      </el-form-item>

      <el-button :loading="loading" type="primary" style="width: 100%; margin-bottom: 30px" @click="handleLogin">
        登录
      </el-button>
    </el-form>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore } from '@/stores/user'
import type { FormInstance, FormRules } from 'element-plus'
import { User, Lock, View, Hide } from '@element-plus/icons-vue'

const userStore = useUserStore()
const route = useRoute()
const router = useRouter()

const loginFormRef = ref<FormInstance>()
const loading = ref(false)
const passwordVisible = ref(false)
const redirect = ref('')

const loginForm = reactive({
  username: '',
  password: '',
})

const loginRules = reactive<FormRules>({
  username: [{ required: true, trigger: 'blur', message: '请输入用户名' }],
  password: [{ required: true, trigger: 'blur', message: '请输入密码', min: 6 }],
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
.login-container {
  min-height: 100%;
  width: 100%;
  background-color: #2d3a4b;
  overflow: hidden;
  display: flex;
  align-items: center;
  justify-content: center;

  .login-form {
    width: 520px;
    max-width: 100%;
    padding: 160px 35px 0;
    overflow: hidden;
  }

  .title-container {
    position: relative;

    .title {
      font-size: 26px;
      color: #eee;
      margin: 0 auto 40px;
      text-align: center;
      font-weight: bold;
    }
  }

  .show-pwd {
    cursor: pointer;
    user-select: none;
  }

  :deep(.el-input__wrapper) {
    background-color: rgba(0, 0, 0, 0.1);
    border-radius: 5px;
    color: #fff;
  }

  :deep(.el-input__inner) {
    color: #fff;
    caret-color: #fff;

    &::placeholder {
      color: rgba(255, 255, 255, 0.5);
    }
  }
}
</style>
