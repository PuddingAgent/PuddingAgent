<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>Agent 模板</span>
          <el-button :icon="Refresh" circle @click="loadList" :loading="loading" />
        </div>
      </template>

      <el-table :data="list" stripe v-loading="loading" style="width: 100%">
        <el-table-column prop="templateId" label="Template ID" width="200" show-overflow-tooltip />
        <el-table-column prop="name" label="名称" width="180" />
        <el-table-column prop="description" label="描述" show-overflow-tooltip />
        <el-table-column prop="templateType" label="类型" width="100">
          <template #default="{ row }">
            <el-tag :type="typeTagMap[row.templateType] ?? 'info'" size="small">
              {{ row.templateType }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="Skills" width="80" align="center">
          <template #default="{ row }">{{ row.skillIds?.length ?? 0 }}</template>
        </el-table-column>
        <el-table-column label="系统提示词" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="prompt-preview">{{ row.systemPrompt || '(无)' }}</span>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { getAgentTemplateList, type AgentTemplateDefinition } from '@/api/agentTemplate'

const loading = ref(false)
const list = ref<AgentTemplateDefinition[]>([])

const typeTagMap: Record<string, string> = {
  Service: 'primary',
  Task: 'success',
  Audit: 'warning',
}

async function loadList() {
  loading.value = true
  try {
    list.value = await getAgentTemplateList()
  } catch (e: any) {
    ElMessage.error(e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

onMounted(loadList)
</script>

<style lang="scss" scoped>
.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.prompt-preview {
  font-family: monospace;
  font-size: 12px;
  color: #909399;
}
</style>
