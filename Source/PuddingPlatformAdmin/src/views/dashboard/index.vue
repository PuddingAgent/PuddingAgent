<template>
  <div class="dashboard-container">
    <el-row :gutter="20">
      <el-col :span="24">
        <el-card shadow="never" class="welcome-card">
          <div class="welcome-inner">
            <div>
              <h2>欢迎使用 Pudding 管理平台</h2>
              <p class="welcome-sub">当前用户：{{ userStore.name }} &nbsp;|&nbsp; 服务时间：{{ serverTime }}</p>
            </div>
            <el-tag v-if="runtimeOnline" type="success" effect="dark">Runtime 在线</el-tag>
            <el-tag v-else type="danger" effect="dark">Runtime 离线</el-tag>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <el-row :gutter="20" style="margin-top: 20px">
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-icon" style="background: #ecf5ff"><el-icon color="#409eff" :size="28"><OfficeBuilding /></el-icon></div>
          <div class="stat-body">
            <div class="stat-num">{{ workspaceCount }}</div>
            <div class="stat-label">活跃工作空间</div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-icon" style="background: #f0f9eb"><el-icon color="#67c23a" :size="28"><Cpu /></el-icon></div>
          <div class="stat-body">
            <div class="stat-num">{{ activeSessions }}</div>
            <div class="stat-label">活跃 Agent 会话</div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-icon" style="background: #fdf6ec"><el-icon color="#e6a23c" :size="28"><Document /></el-icon></div>
          <div class="stat-body">
            <div class="stat-num">{{ auditCount }}</div>
            <div class="stat-label">近期审计事件</div>
          </div>
        </el-card>
      </el-col>
      <el-col :xs="24" :sm="12" :md="6">
        <el-card shadow="hover" class="stat-card">
          <div class="stat-icon" style="background: #fef0f0"><el-icon color="#f56c6c" :size="28"><Warning /></el-icon></div>
          <div class="stat-body">
            <div class="stat-num">{{ totalRuntimeSessions }}</div>
            <div class="stat-label">Runtime 总会话</div>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <el-row :gutter="20" style="margin-top: 20px">
      <el-col :span="24">
        <el-card shadow="never">
          <template #header>
            <span>近期审计事件</span>
            <el-button style="float:right" size="small" link type="primary" @click="$router.push('/audit')">查看全部</el-button>
          </template>
          <el-table :data="recentAudit" stripe size="small" v-loading="loading">
            <el-table-column prop="eventType" label="事件类型" width="220" />
            <el-table-column prop="sessionId" label="SessionId" show-overflow-tooltip />
            <el-table-column prop="workspaceId" label="WorkspaceId" show-overflow-tooltip />
            <el-table-column prop="timestamp" label="时间" width="200">
              <template #default="{ row }">{{ formatTime(row.timestamp) }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { OfficeBuilding, Cpu, Document, Warning } from '@element-plus/icons-vue'
import { useUserStore } from '@/stores/user'
import { getWorkspaceList } from '@/api/workspace'
import { getDebugSummary } from '@/api/debug'
import { getRuntimeSummary } from '@/api/runtimeSession'

const userStore = useUserStore()
const loading = ref(false)
const serverTime = ref('--')
const workspaceCount = ref('--')
const activeSessions = ref<number | string>('--')
const auditCount = ref<number | string>('--')
const totalRuntimeSessions = ref<number | string>('--')
const runtimeOnline = ref(false)
const recentAudit = ref<any[]>([])

function formatTime(iso: string) {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString('zh-CN', { hour12: false })
}

async function loadStats() {
  loading.value = true
  try {
    const [workspaces, summary] = await Promise.allSettled([
      getWorkspaceList(),
      getDebugSummary(),
    ])
    if (workspaces.status === 'fulfilled') {
      workspaceCount.value = workspaces.value.filter(w => w.isEnabled && !w.isFrozen).length.toString()
    }
    if (summary.status === 'fulfilled') {
      serverTime.value = formatTime(summary.value.utcNow)
      auditCount.value = summary.value.recentAuditCount
      recentAudit.value = summary.value.recentAuditEvents.slice(0, 8)
    }
  } finally {
    loading.value = false
  }

  // Runtime 可能不在线，单独处理
  try {
    const rt = await getRuntimeSummary()
    runtimeOnline.value = true
    activeSessions.value = rt.activeSessions
    totalRuntimeSessions.value = rt.totalSessions
  } catch {
    runtimeOnline.value = false
    activeSessions.value = 'N/A'
    totalRuntimeSessions.value = 'N/A'
  }
}

onMounted(loadStats)
</script>

<style lang="scss" scoped>
.dashboard-container { padding: 0; }

.welcome-card .welcome-inner {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.welcome-inner h2 { margin: 0 0 6px; }
.welcome-sub { margin: 0; color: #909399; font-size: 13px; }

.stat-card {
  display: flex;
  align-items: center;
  :deep(.el-card__body) {
    display: flex;
    align-items: center;
    gap: 16px;
    width: 100%;
  }
}
.stat-icon {
  width: 56px; height: 56px;
  border-radius: 12px;
  display: flex; align-items: center; justify-content: center;
  flex-shrink: 0;
}
.stat-num { font-size: 28px; font-weight: 700; color: #303133; }
.stat-label { margin-top: 4px; color: #909399; font-size: 13px; }
</style>
