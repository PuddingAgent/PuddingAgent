<template>
  <div class="app-wrapper" :class="classObj">
    <div v-if="device === 'mobile' && sidebar.opened" class="drawer-bg" @click="handleClickOutside" />
    <Sidebar class="sidebar-container" />
    <div class="main-container" :class="{ hasTagsView: showTagsView }">
      <div :class="{ 'fixed-header': fixedHeader }">
        <Navbar />
        <TagsView v-if="showTagsView" />
      </div>
      <AppMain />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useAppStore } from '@/stores/app'
import settings from '@/settings'
import Sidebar from './components/Sidebar/index.vue'
import Navbar from './components/Navbar.vue'
import TagsView from './components/TagsView/index.vue'
import AppMain from './components/AppMain.vue'

const appStore = useAppStore()

const sidebar = computed(() => appStore.sidebar)
const device = computed(() => appStore.device)
const fixedHeader = settings.fixedHeader
const showTagsView = settings.tagsView

const classObj = computed(() => ({
  hideSidebar: !sidebar.value.opened,
  openSidebar: sidebar.value.opened,
  withoutAnimation: sidebar.value.withoutAnimation,
  mobile: device.value === 'mobile',
}))

function handleClickOutside() {
  appStore.closeSidebar(false)
}
</script>

<style lang="scss" scoped>
.app-wrapper {
  position: relative;
  height: 100%;
  width: 100%;
  display: flex;
}

.drawer-bg {
  background: #000;
  opacity: 0.3;
  width: 100%;
  top: 0;
  height: 100%;
  position: absolute;
  z-index: 999;
}

.main-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 100%;
  overflow: hidden;
  transition: margin-left 0.28s;
}

.sidebar-container {
  width: 210px;
  height: 100%;
  flex-shrink: 0;
  transition: width 0.28s;
  background-color: #304156;
  overflow: hidden;
}

.hideSidebar .sidebar-container {
  width: 54px;
}

.fixed-header {
  position: sticky;
  top: 0;
  z-index: 9;
}

.mobile .sidebar-container {
  position: fixed;
  z-index: 1001;
  transition: transform 0.28s;
}

.mobile.hideSidebar .sidebar-container {
  pointer-events: none;
  transform: translateX(-210px);
}
</style>
