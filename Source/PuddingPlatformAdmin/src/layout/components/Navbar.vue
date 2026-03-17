<template>
  <div class="navbar">
    <div class="hamburger-container" @click="appStore.toggleSidebar()">
      <el-icon :size="20" style="cursor: pointer">
        <Fold v-if="appStore.sidebar.opened" />
        <Expand v-else />
      </el-icon>
    </div>

    <Breadcrumb class="breadcrumb-container" />

    <div class="right-menu">
      <el-dropdown trigger="click" class="avatar-container">
        <div class="avatar-wrapper">
          <el-avatar :size="32" :src="userStore.avatar || undefined">
            {{ userStore.name?.charAt(0) || 'U' }}
          </el-avatar>
          <span class="user-name">{{ userStore.name }}</span>
          <el-icon><CaretBottom /></el-icon>
        </div>
        <template #dropdown>
          <el-dropdown-menu>
            <router-link to="/">
              <el-dropdown-item>首页</el-dropdown-item>
            </router-link>
            <el-dropdown-item divided @click="handleLogout">
              <span>退出登录</span>
            </el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>
  </div>
</template>

<script setup lang="ts">
import { useRouter } from 'vue-router'
import { useAppStore } from '@/stores/app'
import { useUserStore } from '@/stores/user'
import Breadcrumb from './Breadcrumb.vue'

const appStore = useAppStore()
const userStore = useUserStore()
const router = useRouter()

async function handleLogout() {
  await userStore.logout()
  router.push(`/login?redirect=${router.currentRoute.value.fullPath}`)
}
</script>

<style lang="scss" scoped>
.navbar {
  height: 50px;
  overflow: hidden;
  display: flex;
  align-items: center;
  background: #fff;
  box-shadow: 0 1px 4px rgba(0, 21, 41, 0.08);
}

.hamburger-container {
  padding: 0 15px;
  display: flex;
  align-items: center;
}

.breadcrumb-container {
  flex: 1;
}

.right-menu {
  display: flex;
  align-items: center;
  padding-right: 16px;
}

.avatar-container {
  cursor: pointer;

  .avatar-wrapper {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .user-name {
    font-size: 14px;
    color: #333;
  }
}
</style>
