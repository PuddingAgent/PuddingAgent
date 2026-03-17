<template>
  <component :is="type" v-bind="linkProps">
    <slot />
  </component>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { isExternal } from '@/utils/validate'

const props = defineProps<{
  to: string
}>()

const isExt = computed(() => isExternal(props.to))

const type = computed(() => (isExt.value ? 'a' : 'router-link'))

const linkProps = computed(() => {
  if (isExt.value) {
    return {
      href: props.to,
      target: '_blank',
      rel: 'noopener noreferrer',
    }
  }
  return {
    to: props.to,
  }
})
</script>
