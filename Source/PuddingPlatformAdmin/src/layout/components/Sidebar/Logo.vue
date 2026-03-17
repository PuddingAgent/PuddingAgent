<template>
  <div class="sidebar-logo-container" :class="{ collapse: collapse }">
    <router-link to="/" class="sidebar-logo-link">
      <h1 class="sidebar-title">{{ collapse ? 'P' : title }}</h1>
    </router-link>
  </div>
</template>

<script setup lang="ts">
import settings from '@/settings'

defineProps<{
  collapse: boolean
}>()

const title = settings.title
</script>

<style lang="scss" scoped>
.sidebar-logo-container {
  position: relative;
  width: 100%;
  height: 50px;
  line-height: 50px;
  background: #2b2f3a;
  text-align: center;
  overflow: hidden;

  .sidebar-logo-link {
    text-decoration: none;
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    width: 100%;
  }

  .sidebar-title {
    display: inline-block;
    margin: 0;
    color: #fff;
    font-weight: 600;
    line-height: 50px;
    font-size: 14px;
    font-family: Avenir, Helvetica Neue, Arial, Helvetica, sans-serif;
    vertical-align: middle;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  &.collapse {
    .sidebar-title {
      font-size: 20px;
    }
  }
}
</style>
