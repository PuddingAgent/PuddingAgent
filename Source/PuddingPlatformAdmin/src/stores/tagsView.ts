import { defineStore } from 'pinia'
import type { RouteLocationNormalized } from 'vue-router'

export interface TagView {
  name?: string
  path: string
  fullPath: string
  title: string
  affix?: boolean
  meta?: Record<string, unknown>
}

interface TagsViewState {
  visitedViews: TagView[]
  cachedViews: string[]
}

export const useTagsViewStore = defineStore('tagsView', {
  state: (): TagsViewState => ({
    visitedViews: [],
    cachedViews: [],
  }),

  actions: {
    addView(view: RouteLocationNormalized) {
      this.addVisitedView(view)
      this.addCachedView(view)
    },

    addVisitedView(view: RouteLocationNormalized) {
      if (this.visitedViews.some((v) => v.path === view.path)) return
      this.visitedViews.push({
        name: view.name as string,
        path: view.path,
        fullPath: view.fullPath,
        title: (view.meta?.title as string) || 'no-name',
        affix: view.meta?.affix as boolean,
        meta: view.meta as Record<string, unknown>,
      })
    },

    addCachedView(view: RouteLocationNormalized) {
      const name = view.name as string
      if (!name) return
      if (this.cachedViews.includes(name)) return
      if (view.meta?.noCache !== true) {
        this.cachedViews.push(name)
      }
    },

    delView(view: TagView) {
      this.delVisitedView(view)
      this.delCachedView(view)
    },

    delVisitedView(view: TagView) {
      const i = this.visitedViews.findIndex((v) => v.path === view.path)
      if (i > -1) this.visitedViews.splice(i, 1)
    },

    delCachedView(view: TagView) {
      const name = view.name
      if (!name) return
      const i = this.cachedViews.indexOf(name)
      if (i > -1) this.cachedViews.splice(i, 1)
    },

    delOthersViews(view: TagView) {
      this.visitedViews = this.visitedViews.filter(
        (v) => v.affix || v.path === view.path,
      )
      const name = view.name
      if (name) {
        this.cachedViews = [name]
      } else {
        this.cachedViews = []
      }
    },

    delAllViews() {
      this.visitedViews = this.visitedViews.filter((v) => v.affix)
      this.cachedViews = []
    },
  },
})
