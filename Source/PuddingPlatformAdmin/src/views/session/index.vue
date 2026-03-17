<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>Runtime 会话</span>
          <div class="header-actions">
            <el-tag v-if="summary" type="success">
              活跃: {{ summary.activeSessions }} / 总计: {{ summary.totalSessions }}
            </el-tag>
            <el-button :icon="Refresh" circle @click="loadAll" :loading="loading" />
          </div>
        </div>
      </template>

      <el-table :data="list" stripe v-loading="loading" style="width: 100%">
        <el-table-column prop="sessionId" label="SessionId" show-overflow-tooltip />
        <el-table-column prop="agentInstanceId" label="AgentInstanceId" show-overflow-tooltip />
        <el-table-column prop="workspaceId" label="WorkspaceId" width="180" show-overflow-tooltip />
        <el-table-column prop="agentTemplateId" label="TemplateId" width="180" show-overflow-tooltip />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
              {{ row.isActive ? '活跃' : '已终止' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="turnCount" label="轮次" width="70" align="center" />
        <el-table-column prop="lastActiveAt" label="最后活跃" width="180">
          <template #default="{ row }">{{ formatTime(row.lastActiveAt) }}</template>
        </el-table-column>
        <el-table-column prop="terminationReason" label="终止原因" width="130" show-overflow-tooltip />
        <el-table-column label="操作" width="100">
          <template #default="{ row }">
            <el-popconfirm
              title="确定强制终止此会话？"
              confirm-button-text="终止"
              cancel-button-text="取消"
              @confirm="handleTerminate(row.sessionId)"
            >
              <template #reference>
                <el-button size="small" type="danger" link :disabled="!row.isActive">终止</el-button>
              </template>
            </el-popconfirm>
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
import {
  getRuntimeSessionList,
  terminateRuntimeSession,
  getRuntimeSummary,
  type SessionRuntimeRecord,
  type RuntimeSummary,
} from '@/api/runtimeSession'

const loading = ref(false)
const list = ref<SessionRuntimeRecord[]>([])
const summary = ref<RuntimeSummary | null>(null)

function formatTime(iso: string) {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString('zh-CN', { hour12: false })
}

async function loadAll() {
  loading.value = true
  try {
    const [sessions, sum] = await Promise.allSettled([
      getRuntimeSessionList(),
      getRuntimeSummary(),
    ])
    if (sessions.status === 'fulfilled') list.value = sessions.value
    if (sum.status === 'fulfilled') summary.value = sum.value
  } catch (e: any) {
    ElMessage.error(e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

async function handleTerminate(sessionId: string) {
  try {
    await terminateRuntimeSession(sessionId)
    ElMessage.success('会话已终止')
    await loadAll()
  } catch (e: any) {
    ElMessage.error(e?.message || '终止失败')
  }
}

onMounted(loadAll)
</script>

<style lang="scss" scoped>
.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.header-actions {
  display: flex;
  gap: 10px;
  align-items: center;
}
</style>
