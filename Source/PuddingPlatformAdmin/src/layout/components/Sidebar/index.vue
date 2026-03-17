<template>
  <div class="sidebar">
    <Logo v-if="showLogo" :collapse="!appStore.sidebar.opened" />
    <el-scrollbar wrap-class="scrollbar-wrapper">
      <el-menu
        :default-active="activeMenu"
        :collapse="!appStore.sidebar.opened"
        :collapse-transition="false"
        background-color="#304156"
        text-color="#bfcbd9"
        active-text-color="#409eff"
        :unique-opened="false"
        router
      >
        <SidebarItem
          v-for="route in routes"
          :key="route.path"
          :item="route"
          :base-path="route.path"
        />
      </el-menu>
    </el-scrollbar>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { useAppStore } from '@/stores/app'
import { useUserStore } from '@/stores/user'
import settings from '@/settings'
import Logo from './Logo.vue'
import SidebarItem from './SidebarItem.vue'

const route = useRoute()
const appStore = useAppStore()
const userStore = useUserStore()

const showLogo = settings.sidebarLogo

const routes = computed(() => {
  return userStore.routes.filter((r) => !r.meta?.hidden)
})

const activeMenu = computed(() => {
  const { meta, path } = route
  if (meta?.activeMenu) {
    return meta.activeMenu as string
  }
  return path
})
</script>

<style lang="scss" scoped>
.sidebar {
  height: 100%;
  display: flex;
  flex-direction: column;
}

:deep(.scrollbar-wrapper) {
  overflow-x: hidden !important;
}

:deep(.el-scrollbar__bar.is-horizontal) {
  display: none;
}

.el-menu {
  border: none;
  height: 100%;
  width: 100% !important;
}
</style>
