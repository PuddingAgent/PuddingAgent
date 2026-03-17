<script setup lang="ts">
import { onBeforeMount } from 'vue'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()

onBeforeMount(() => {
  const { params, query } = route
  const { path } = params
  const resolvedPath = Array.isArray(path) ? '/' + path.join('/') : '/' + path
  router.replace({ path: resolvedPath, query })
})
</script>

<template>
  <div />
</template>
