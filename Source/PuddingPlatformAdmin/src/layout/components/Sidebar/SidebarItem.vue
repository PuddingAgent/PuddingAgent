<template>
  <div v-if="!item.meta?.hidden">
    <!-- 只有一个子路由或没有子路由 -->
    <template v-if="hasOneShowingChild(item.children, item) && (!onlyOneChild?.children || onlyOneChild.noShowingChildren) && !item.meta?.alwaysShow">
      <app-link v-if="onlyOneChild?.meta" :to="resolvePath(onlyOneChild.path)">
        <el-menu-item :index="resolvePath(onlyOneChild.path)">
          <el-icon v-if="onlyOneChild.meta?.icon || item.meta?.icon">
            <component :is="onlyOneChild.meta?.icon || item.meta?.icon" />
          </el-icon>
          <template #title>{{ onlyOneChild.meta?.title }}</template>
        </el-menu-item>
      </app-link>
    </template>

    <!-- 有多个子路由 -->
    <el-sub-menu v-else :index="resolvePath(item.path)">
      <template #title>
        <el-icon v-if="item.meta?.icon">
          <component :is="item.meta.icon" />
        </el-icon>
        <span>{{ item.meta?.title }}</span>
      </template>
      <SidebarItem
        v-for="child in item.children"
        :key="child.path"
        :item="child"
        :base-path="resolvePath(child.path)"
      />
    </el-sub-menu>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import type { RouteRecordRaw } from 'vue-router'
import { isExternal } from '@/utils/validate'
import AppLink from './Link.vue'

interface Props {
  item: RouteRecordRaw
  basePath?: string
}

const props = withDefaults(defineProps<Props>(), {
  basePath: '',
})

interface OnlyOneChild extends RouteRecordRaw {
  noShowingChildren?: boolean
}

const onlyOneChild = ref<OnlyOneChild | null>(null)

function hasOneShowingChild(children: RouteRecordRaw[] | undefined, parent: RouteRecordRaw): boolean {
  if (!children) {
    onlyOneChild.value = { ...parent, path: '', noShowingChildren: true } as OnlyOneChild
    return true
  }

  const showingChildren = children.filter((item) => {
    if (item.meta?.hidden) return false
    onlyOneChild.value = item as OnlyOneChild
    return true
  })

  if (showingChildren.length === 1) return true

  if (showingChildren.length === 0) {
    onlyOneChild.value = { ...parent, path: '', noShowingChildren: true } as OnlyOneChild
    return true
  }

  return false
}

function resolvePath(routePath: string): string {
  if (isExternal(routePath)) return routePath
  if (isExternal(props.basePath)) return props.basePath
  if (routePath.startsWith('/')) return routePath
  const base = props.basePath.endsWith('/') ? props.basePath : props.basePath + '/'
  return (base + routePath).replace(/\/+/g, '/')
}
</script>
