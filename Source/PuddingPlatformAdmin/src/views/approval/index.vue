<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>审批管理</span>
          <el-button :icon="Refresh" circle @click="loadList" :loading="loading" />
        </div>
      </template>

      <el-table :data="list" stripe v-loading="loading" style="width: 100%">
        <el-table-column prop="approvalId" label="ApprovalId" width="220" show-overflow-tooltip />
        <el-table-column prop="sessionId" label="SessionId" show-overflow-tooltip />
        <el-table-column prop="workspaceId" label="WorkspaceId" width="160" show-overflow-tooltip />
        <el-table-column prop="actionDescription" label="操作描述" show-overflow-tooltip />
        <el-table-column prop="status" label="状态" width="100">
          <template #default="{ row }">
            <el-tag :type="statusTagMap[row.status] ?? 'info'" size="small">{{ row.status }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="createdAt" label="创建时间" width="180">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="160">
          <template #default="{ row }">
            <template v-if="row.status === 'Pending'">
              <el-button size="small" type="success" link @click="openConfirm(row)">批准</el-button>
              <el-popconfirm
                title="确定拒绝此审批？"
                confirm-button-text="拒绝"
                cancel-button-text="取消"
                @confirm="handleReject(row.approvalId)"
              >
                <template #reference>
                  <el-button size="small" type="danger" link>拒绝</el-button>
                </template>
              </el-popconfirm>
            </template>
            <span v-else class="resolved-label">{{ row.resolvedBy || '—' }}</span>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 确认对话框 -->
    <el-dialog v-model="confirmDialogVisible" title="批准审批" width="420px" destroy-on-close>
      <el-form ref="confirmFormRef" :model="confirmForm" :rules="confirmRules" label-width="100px">
        <el-form-item label="确认码" prop="confirmationCode">
          <el-input v-model="confirmForm.confirmationCode" placeholder="请输入确认码" />
        </el-form-item>
        <el-form-item label="操作人" prop="confirmedBy">
          <el-input v-model="confirmForm.confirmedBy" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="confirmDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="handleConfirm">确认批准</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { getPendingApprovals, confirmApproval, rejectApproval, type ApprovalRecord } from '@/api/approval'
import { useUserStore } from '@/stores/user'

const loading = ref(false)
const saving = ref(false)
const list = ref<ApprovalRecord[]>([])
const confirmDialogVisible = ref(false)
const confirmFormRef = ref<FormInstance>()
const currentApprovalId = ref('')
const userStore = useUserStore()

const confirmForm = reactive({
  confirmationCode: '',
  confirmedBy: userStore.name || 'Admin',
})

const confirmRules = {
  confirmationCode: [{ required: true, message: '请输入确认码', trigger: 'blur' }],
  confirmedBy: [{ required: true, message: '请输入操作人', trigger: 'blur' }],
}

const statusTagMap: Record<string, string> = {
  Pending: 'warning',
  Confirmed: 'success',
  Rejected: 'danger',
  Expired: 'info',
}

function formatTime(iso: string) {
  const d = new Date(iso)
  return isNaN(d.getTime()) ? iso : d.toLocaleString('zh-CN', { hour12: false })
}

async function loadList() {
  loading.value = true
  try {
    list.value = await getPendingApprovals()
  } catch (e: any) {
    ElMessage.error(e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

function openConfirm(row: ApprovalRecord) {
  currentApprovalId.value = row.approvalId
  confirmForm.confirmationCode = ''
  confirmForm.confirmedBy = userStore.name || 'Admin'
  confirmDialogVisible.value = true
}

async function handleConfirm() {
  await confirmFormRef.value?.validate()
  saving.value = true
  try {
    await confirmApproval(currentApprovalId.value, confirmForm.confirmationCode, confirmForm.confirmedBy)
    ElMessage.success('已批准')
    confirmDialogVisible.value = false
    await loadList()
  } catch (e: any) {
    ElMessage.error(e?.message || '批准失败')
  } finally {
    saving.value = false
  }
}

async function handleReject(approvalId: string) {
  try {
    await rejectApproval(approvalId, userStore.name || 'Admin')
    ElMessage.success('已拒绝')
    await loadList()
  } catch (e: any) {
    ElMessage.error(e?.message || '拒绝失败')
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
.resolved-label { color: #909399; font-size: 13px; }
</style>
