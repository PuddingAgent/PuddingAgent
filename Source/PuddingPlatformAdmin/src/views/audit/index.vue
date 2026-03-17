<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>审计事件</span>
          <el-button :icon="Refresh" circle @click="loadList" :loading="loading" />
        </div>
      </template>

      <!-- 过滤栏 -->
      <el-form :inline="true" :model="filter" @submit.prevent="loadList" style="margin-bottom: 16px">
        <el-form-item label="SessionId">
          <el-input v-model="filter.sessionId" placeholder="过滤 SessionId" clearable style="width: 200px" />
        </el-form-item>
        <el-form-item label="WorkspaceId">
          <el-input v-model="filter.workspaceId" placeholder="过滤 WorkspaceId" clearable style="width: 180px" />
        </el-form-item>
        <el-form-item label="MessageId">
          <el-input v-model="filter.messageId" placeholder="过滤 MessageId" clearable style="width: 180px" />
        </el-form-item>
        <el-form-item label="Limit">
          <el-input-number v-model="filter.limit" :min="10" :max="200" :step="10" style="width: 120px" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" native-type="submit">查询</el-button>
          <el-button @click="resetFilter">重置</el-button>
        </el-form-item>
      </el-form>

      <el-table :data="list" stripe v-loading="loading" style="width: 100%">
        <el-table-column prop="eventType" label="事件类型" width="220" />
        <el-table-column prop="sessionId" label="SessionId" show-overflow-tooltip />
        <el-table-column prop="workspaceId" label="WorkspaceId" show-overflow-tooltip />
        <el-table-column prop="messageId" label="MessageId" show-overflow-tooltip />
        <el-table-column prop="agentTemplateId" label="TemplateId" show-overflow-tooltip />
        <el-table-column prop="detail" label="详情" show-overflow-tooltip />
        <el-table-column prop="timestamp" label="时间" width="190">
          <template #default="{ row }">{{ formatTime(row.timestamp) }}</template>
        </el-table-column>
      </el-table>

      <div class="pagination-bar">
        <span class="total-tip">共 {{ list.length }} 条（最多显示 {{ filter.limit }} 条）</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { queryAuditEvents, type AuditEventRecord } from '@/api/audit'

const loading = ref(false)
const list = ref<AuditEventRecord[]>([])

const filter = reactive({
  sessionId: '',
  workspaceId: '',
  messageId: '',
  limit: 50,
})

function formatTime(iso: string) {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString('zh-CN', { hour12: false })
}

function resetFilter() {
  filter.sessionId = ''
  filter.workspaceId = ''
  filter.messageId = ''
  filter.limit = 50
  loadList()
}

async function loadList() {
  loading.value = true
  try {
    list.value = await queryAuditEvents({
      sessionId: filter.sessionId || undefined,
      workspaceId: filter.workspaceId || undefined,
      messageId: filter.messageId || undefined,
      limit: filter.limit,
    })
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
.pagination-bar {
  margin-top: 12px;
  text-align: right;
}
.total-tip { color: #909399; font-size: 13px; }
</style>
