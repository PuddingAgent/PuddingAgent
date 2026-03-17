<template>
  <div class="tags-view-container">
    <div class="tags-view-wrapper">
      <router-link
        v-for="tag in visitedViews"
        :key="tag.path"
        :to="{ path: tag.path, query: {} }"
        class="tags-view-item"
        :class="isActive(tag) ? 'active' : ''"
        @click.middle="!isAffix(tag) && closeSelectedTag(tag)"
        @contextmenu.prevent="openMenu(tag, $event)"
      >
        {{ tag.title }}
        <el-icon
          v-if="!isAffix(tag)"
          class="el-icon-close"
          @click.prevent.stop="closeSelectedTag(tag)"
        >
          <Close />
        </el-icon>
      </router-link>
    </div>

    <!-- 右键菜单 -->
    <ul v-show="visible" :style="{ left: menuLeft + 'px', top: menuTop + 'px' }" class="contextmenu">
      <li @click="refreshSelectedTag(selectedTag!)">刷新</li>
      <li v-if="!isAffix(selectedTag)" @click="closeSelectedTag(selectedTag!)">关闭</li>
      <li @click="closeOthersTags">关闭其他</li>
      <li @click="closeAllTags">关闭所有</li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted, nextTick } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useTagsViewStore, type TagView } from '@/stores/tagsView'
import { constantRoutes } from '@/router'
import type { RouteRecordRaw } from 'vue-router'

const route = useRoute()
const router = useRouter()
const tagsViewStore = useTagsViewStore()

const visible = ref(false)
const menuLeft = ref(0)
const menuTop = ref(0)
const selectedTag = ref<TagView | null>(null)
const affixTags = ref<TagView[]>([])

const visitedViews = computed(() => tagsViewStore.visitedViews)

function isActive(tag: TagView) {
  return tag.path === route.path
}

function isAffix(tag: TagView | null) {
  return tag?.affix
}

function filterAffixTags(routes: RouteRecordRaw[], basePath = ''): TagView[] {
  let tags: TagView[] = []
  routes.forEach((r) => {
    const tagPath = basePath + '/' + (r.path || '')
    if (r.meta?.affix) {
      tags.push({
        name: r.name as string,
        path: tagPath.replace(/\/+/g, '/'),
        fullPath: tagPath.replace(/\/+/g, '/'),
        title: (r.meta?.title as string) || 'no-name',
        affix: true,
        meta: r.meta as Record<string, unknown>,
      })
    }
    if (r.children) {
      const childTags = filterAffixTags(r.children, tagPath)
      if (childTags.length) {
        tags = [...tags, ...childTags]
      }
    }
  })
  return tags
}

function initTags() {
  affixTags.value = filterAffixTags(constantRoutes)
  for (const tag of affixTags.value) {
    if (tag.name) {
      tagsViewStore.addVisitedView({
        name: tag.name,
        path: tag.path,
        fullPath: tag.fullPath,
        meta: tag.meta || {},
      } as any)
    }
  }
}

function addTags() {
  if (route.name) {
    tagsViewStore.addView(route)
  }
}

function closeSelectedTag(view: TagView) {
  tagsViewStore.delView(view)
  if (isActive(view)) {
    toLastView(tagsViewStore.visitedViews, view)
  }
}

function closeOthersTags() {
  if (selectedTag.value) {
    router.push(selectedTag.value.path)
    tagsViewStore.delOthersViews(selectedTag.value)
  }
}

function closeAllTags() {
  tagsViewStore.delAllViews()
  const lastAffixTag = affixTags.value[affixTags.value.length - 1]
  if (lastAffixTag) {
    router.push(lastAffixTag.path)
  } else {
    router.push('/')
  }
}

function toLastView(views: TagView[], _view: TagView) {
  const lastView = views[views.length - 1]
  if (lastView) {
    router.push(lastView.fullPath)
  } else {
    router.push('/')
  }
}

function refreshSelectedTag(view: TagView) {
  tagsViewStore.delCachedView(view)
  nextTick(() => {
    router.replace({ path: '/redirect' + view.path })
  })
}

function openMenu(tag: TagView, e: MouseEvent) {
  menuLeft.value = e.clientX
  menuTop.value = e.clientY
  visible.value = true
  selectedTag.value = tag
}

function closeMenu() {
  visible.value = false
}

watch(visible, (val) => {
  if (val) {
    document.body.addEventListener('click', closeMenu)
  } else {
    document.body.removeEventListener('click', closeMenu)
  }
})

watch(
  () => route.path,
  () => {
    addTags()
  },
)

onMounted(() => {
  initTags()
  addTags()
})
</script>

<style lang="scss" scoped>
.tags-view-container {
  height: 34px;
  width: 100%;
  background: #fff;
  border-bottom: 1px solid #d8dce5;
  box-shadow: 0 1px 3px 0 rgba(0, 0, 0, 0.12), 0 0 3px 0 rgba(0, 0, 0, 0.04);

  .tags-view-wrapper {
    display: flex;
    align-items: center;
    height: 100%;
    padding: 0 4px;
    overflow-x: auto;

    .tags-view-item {
      display: inline-flex;
      align-items: center;
      position: relative;
      cursor: pointer;
      height: 26px;
      line-height: 26px;
      border: 1px solid #d8dce5;
      color: #495060;
      background: #fff;
      padding: 0 8px;
      font-size: 12px;
      margin-left: 5px;
      text-decoration: none;
      border-radius: 3px;
      white-space: nowrap;

      &:first-of-type {
        margin-left: 8px;
      }

      &:last-of-type {
        margin-right: 8px;
      }

      &.active {
        background-color: #409eff;
        color: #fff;
        border-color: #409eff;

        &::before {
          content: '';
          background: #fff;
          display: inline-block;
          width: 8px;
          height: 8px;
          border-radius: 50%;
          position: relative;
          margin-right: 4px;
        }
      }

      .el-icon-close {
        margin-left: 4px;
        font-size: 12px;
        border-radius: 50%;

        &:hover {
          background-color: #b4bccc;
          color: #fff;
        }
      }
    }
  }
}

.contextmenu {
  margin: 0;
  background: #fff;
  z-index: 3000;
  position: fixed;
  list-style-type: none;
  padding: 5px 0;
  border-radius: 4px;
  font-size: 12px;
  font-weight: 400;
  color: #333;
  box-shadow: 2px 2px 3px 0 rgba(0, 0, 0, 0.3);

  li {
    margin: 0;
    padding: 7px 16px;
    cursor: pointer;

    &:hover {
      background: #eee;
    }
  }
}
</style>
