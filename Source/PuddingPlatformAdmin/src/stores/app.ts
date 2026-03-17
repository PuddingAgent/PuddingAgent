import { defineStore } from 'pinia'

interface AppState {
  sidebar: {
    opened: boolean
    withoutAnimation: boolean
  }
  device: 'desktop' | 'mobile'
}

export const useAppStore = defineStore('app', {
  state: (): AppState => ({
    sidebar: {
      opened: true,
      withoutAnimation: false,
    },
    device: 'desktop',
  }),

  actions: {
    toggleSidebar() {
      this.sidebar.opened = !this.sidebar.opened
      this.sidebar.withoutAnimation = false
    },

    closeSidebar(withoutAnimation: boolean) {
      this.sidebar.opened = false
      this.sidebar.withoutAnimation = withoutAnimation
    },

    toggleDevice(device: 'desktop' | 'mobile') {
      this.device = device
    },
  },
})
