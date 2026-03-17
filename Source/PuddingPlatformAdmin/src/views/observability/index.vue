<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>可观测性面板</span>
          <div class="header-actions">
            <el-input v-model="sessionId" placeholder="SessionId" style="width: 180px" />
            <el-button type="primary" @click="loadSessionDebug">会话调试</el-button>
            <el-input v-model="messageId" placeholder="MessageId" style="width: 180px" />
            <el-button type="primary" plain @click="loadMessageDebug">消息调试</el-button>
            <el-input v-model="workspaceId" placeholder="WorkspaceId" style="width: 140px" />
            <el-button type="primary" plain @click="loadWorkspaceDebug">空间调试</el-button>
            <el-button :icon="Refresh" circle :loading="loading" @click="loadAll" />
          </div>
        </div>
      </template>

      <el-row :gutter="16">
        <el-col :span="6"><el-statistic title="总会话" :value="metrics?.session.total ?? 0" /></el-col>
        <el-col :span="6"><el-statistic title="活跃会话" :value="metrics?.session.active ?? 0" /></el-col>
        <el-col :span="6"><el-statistic title="待审批" :value="metrics?.approval.pending ?? 0" /></el-col>
        <el-col :span="6"><el-statistic title="在线 Runtime" :value="metrics?.runtime.onlineNodes ?? 0" /></el-col>
      </el-row>

      <el-row :gutter="16" style="margin-top: 16px">
        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header><span>审计事件类型分布（最近窗口）</span></template>
            <el-table :data="auditRows" size="small" max-height="300">
              <el-table-column prop="eventType" label="事件类型" />
              <el-table-column prop="count" label="数量" width="100" />
            </el-table>
          </el-card>
        </el-col>

        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header><span>调试摘要（最近10条审计）</span></template>
            <el-table :data="summary?.recentAuditEvents ?? []" size="small" max-height="300">
              <el-table-column prop="eventType" label="类型" width="160" />
              <el-table-column prop="sessionId" label="SessionId" show-overflow-tooltip />
              <el-table-column prop="timestamp" label="时间" width="180">
                <template #default="{ row }">{{ formatTime(row.timestamp) }}</template>
              </el-table-column>
            </el-table>
          </el-card>
        </el-col>
      </el-row>

      <el-row style="margin-top:16px" v-if="sessionDebug">
        <el-col :span="24">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>Session 调试快照：{{ sessionDebug.session?.sessionId }}</span>
            </template>
            <el-descriptions :column="4" border>
              <el-descriptions-item label="Workspace">{{ sessionDebug.session?.workspaceId }}</el-descriptions-item>
              <el-descriptions-item label="Agent 模板">{{ sessionDebug.session?.agentTemplateId }}</el-descriptions-item>
              <el-descriptions-item label="状态">{{ sessionDebug.session?.status }}</el-descriptions-item>
              <el-descriptions-item label="最近审计">{{ sessionDebug.auditCount }}</el-descriptions-item>
            </el-descriptions>
            <el-table :data="sessionDebug.recentAudit" size="small" style="margin-top: 12px">
              <el-table-column prop="eventType" label="事件" width="180" />
              <el-table-column prop="detail" label="详情" show-overflow-tooltip />
              <el-table-column prop="timestamp" label="时间" width="180">
                <template #default="{ row }">{{ formatTime(row.timestamp) }}</template>
              </el-table-column>
            </el-table>
          </el-card>
        </el-col>
      </el-row>

      <el-row style="margin-top:16px" v-if="messageDebug">
        <el-col :span="24">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>Message 调试快照：{{ messageDebug.messageId }}</span>
            </template>
            <el-descriptions :column="4" border>
              <el-descriptions-item label="路由成功">{{ messageDebug.diagnosis?.routeSuccess }}</el-descriptions-item>
              <el-descriptions-item label="权限拒绝">{{ messageDebug.diagnosis?.hasPermissionDenied }}</el-descriptions-item>
              <el-descriptions-item label="Runtime 失败">{{ messageDebug.diagnosis?.hasRuntimeFailure }}</el-descriptions-item>
              <el-descriptions-item label="审计条数">{{ messageDebug.auditCount }}</el-descriptions-item>
            </el-descriptions>
            <el-alert
              v-if="messageDebug.diagnosis?.routeFailureReason"
              type="warning"
              :title="`路由失败原因: ${messageDebug.diagnosis.routeFailureReason}`"
              show-icon
              style="margin-top: 12px"
            />
            <el-table :data="messageDebug.auditTimeline" size="small" style="margin-top: 12px">
              <el-table-column prop="eventType" label="事件" width="180" />
              <el-table-column prop="detail" label="详情" show-overflow-tooltip />
              <el-table-column prop="timestamp" label="时间" width="180">
                <template #default="{ row }">{{ formatTime(row.timestamp) }}</template>
              </el-table-column>
            </el-table>
          </el-card>
        </el-col>
      </el-row>

      <el-row style="margin-top:16px" v-if="workspaceDebug">
        <el-col :span="24">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>Workspace 调试快照：{{ workspaceDebug.workspace.workspaceId }}</span>
            </template>
            <el-descriptions :column="4" border>
              <el-descriptions-item label="启用">{{ workspaceDebug.workspace.isEnabled }}</el-descriptions-item>
              <el-descriptions-item label="冻结">{{ workspaceDebug.workspace.isFrozen }}</el-descriptions-item>
              <el-descriptions-item label="会话活跃数">{{ workspaceDebug.session.active }}</el-descriptions-item>
              <el-descriptions-item label="待审批">{{ workspaceDebug.approval.pending }}</el-descriptions-item>
              <el-descriptions-item label="路由失败">{{ workspaceDebug.routing.recentFailures }}</el-descriptions-item>
              <el-descriptions-item label="权限拒绝">{{ workspaceDebug.audit.permissionDeniedCount }}</el-descriptions-item>
              <el-descriptions-item label="Runtime失败">{{ workspaceDebug.audit.runtimeFailedCount }}</el-descriptions-item>
              <el-descriptions-item label="绑定Workflow">{{ workspaceDebug.workflow.boundWorkflows }}</el-descriptions-item>
            </el-descriptions>
            <el-alert
              v-if="workspaceDebug.workflow.potentialBlockerHint"
              type="warning"
              :title="workspaceDebug.workflow.potentialBlockerHint"
              show-icon
              style="margin-top: 12px"
            />
            <el-table :data="workspaceDebug.routing.topFailureReasons" size="small" style="margin-top: 12px">
              <el-table-column prop="reason" label="高频失败原因" />
              <el-table-column prop="count" label="次数" width="100" />
            </el-table>
          </el-card>
        </el-col>
      </el-row>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import {
  getDebugSummary,
  getDebugMetrics,
  getSessionDebug,
  getMessageDebug,
  getWorkspaceDebug,
  type DebugSummary,
  type DebugMetrics,
  type SessionDebugSnapshot,
  type MessageDebugSnapshot,
  type WorkspaceDebugSnapshot,
} from '@/api/debug'

const loading = ref(false)
const sessionId = ref('')
const messageId = ref('')
const workspaceId = ref('default')
const summary = ref<DebugSummary | null>(null)
const metrics = ref<DebugMetrics | null>(null)
const sessionDebug = ref<SessionDebugSnapshot | null>(null)
const messageDebug = ref<MessageDebugSnapshot | null>(null)
const workspaceDebug = ref<WorkspaceDebugSnapshot | null>(null)

const auditRows = computed(() => {
  const byType = metrics.value?.audit.byType ?? {}
  return Object.keys(byType).map((k) => ({ eventType: k, count: byType[k] }))
})

function formatTime(v?: string) {
  if (!v) return '-'
  const d = new Date(v)
  return Number.isNaN(d.getTime()) ? v : d.toLocaleString()
}

async function loadAll() {
  loading.value = true
  try {
    const [s, m] = await Promise.all([getDebugSummary(), getDebugMetrics()])
    summary.value = s
    metrics.value = m
  } catch (e: any) {
    ElMessage.error(e?.message || '加载可观测数据失败')
  } finally {
    loading.value = false
  }
}

async function loadSessionDebug() {
  const sid = sessionId.value.trim()
  if (!sid) return
  try {
    sessionDebug.value = await getSessionDebug(sid)
  } catch (e: any) {
    ElMessage.error(e?.message || 'Session 调试查询失败')
  }
}

async function loadMessageDebug() {
  const mid = messageId.value.trim()
  if (!mid) return
  try {
    messageDebug.value = await getMessageDebug(mid)
  } catch (e: any) {
    ElMessage.error(e?.message || 'Message 调试查询失败')
  }
}

async function loadWorkspaceDebug() {
  const wid = workspaceId.value.trim()
  if (!wid) return
  try {
    workspaceDebug.value = await getWorkspaceDebug(wid)
  } catch (e: any) {
    ElMessage.error(e?.message || 'Workspace 调试查询失败')
  }
}

onMounted(loadAll)
</script>

<style scoped lang="scss">
.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}
.inner-card {
  border: 1px solid var(--el-border-color-lighter);
}
</style>
