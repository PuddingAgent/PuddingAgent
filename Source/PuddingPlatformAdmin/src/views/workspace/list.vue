<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>工作空间管理</span>
          <div class="header-actions">
            <el-button :icon="Refresh" circle @click="loadList" :loading="loading" />
            <el-button type="primary" :icon="Plus" @click="openCreate">新建工作空间</el-button>
          </div>
        </div>
      </template>

      <el-table :data="list" stripe v-loading="loading" style="width: 100%">
        <el-table-column prop="workspaceId" label="ID" width="200" show-overflow-tooltip />
        <el-table-column prop="name" label="名称" />
        <el-table-column prop="description" label="描述" show-overflow-tooltip />
        <el-table-column label="状态" width="160">
          <template #default="{ row }">
            <el-tag v-if="row.isFrozen" type="warning">已冻结</el-tag>
            <el-tag v-else-if="row.isEnabled" type="success">活跃</el-tag>
            <el-tag v-else type="info">停用</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="渠道数" width="90" align="center">
          <template #default="{ row }">{{ row.channelBindings?.length ?? 0 }}</template>
        </el-table-column>
        <el-table-column label="Agent 模板数" width="110" align="center">
          <template #default="{ row }">{{ row.agentTemplateIds?.length ?? 0 }}</template>
        </el-table-column>
        <el-table-column label="操作" width="160">
          <template #default="{ row }">
            <el-button size="small" type="primary" link @click="openEdit(row)">编辑</el-button>
            <el-popconfirm
              :title="`确定删除工作空间 ${row.name}？`"
              confirm-button-text="确认删除"
              cancel-button-text="取消"
              @confirm="handleDelete(row.workspaceId)"
            >
              <template #reference>
                <el-button size="small" type="danger" link :disabled="row.workspaceId === 'default'">删除</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新建/编辑对话框 -->
    <el-dialog v-model="dialogVisible" :title="isEdit ? '编辑工作空间' : '新建工作空间'" width="520px" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="100px">
        <el-form-item label="ID" prop="workspaceId">
          <el-input v-model="form.workspaceId" :disabled="isEdit" placeholder="如 my-workspace" />
        </el-form-item>
        <el-form-item label="名称" prop="name">
          <el-input v-model="form.name" placeholder="工作空间显示名称" />
        </el-form-item>
        <el-form-item label="描述">
          <el-input v-model="form.description" type="textarea" :rows="2" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="form.isEnabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="handleSave">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import type { FormInstance } from 'element-plus'
import { getWorkspaceList, upsertWorkspace, deleteWorkspace, type WorkspaceDefinition } from '@/api/workspace'

const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const isEdit = ref(false)
const list = ref<WorkspaceDefinition[]>([])
const formRef = ref<FormInstance>()

const form = reactive({
  workspaceId: '',
  name: '',
  description: '',
  isEnabled: true,
})

const rules = {
  workspaceId: [{ required: true, message: '请输入工作空间 ID', trigger: 'blur' }],
  name: [{ required: true, message: '请输入名称', trigger: 'blur' }],
}

async function loadList() {
  loading.value = true
  try {
    list.value = await getWorkspaceList()
  } catch (e: any) {
    ElMessage.error(e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

function openCreate() {
  isEdit.value = false
  Object.assign(form, { workspaceId: '', name: '', description: '', isEnabled: true })
  dialogVisible.value = true
}

function openEdit(row: WorkspaceDefinition) {
  isEdit.value = true
  Object.assign(form, {
    workspaceId: row.workspaceId,
    name: row.name,
    description: row.description ?? '',
    isEnabled: row.isEnabled,
  })
  dialogVisible.value = true
}

async function handleSave() {
  await formRef.value?.validate()
  saving.value = true
  try {
    await upsertWorkspace(form.workspaceId, {
      workspaceId: form.workspaceId,
      name: form.name,
      description: form.description,
      isEnabled: form.isEnabled,
      isFrozen: false,
      channelBindings: [],
      agentTemplateIds: [],
      auditAgentTemplateIds: [],
    })
    ElMessage.success('保存成功')
    dialogVisible.value = false
    await loadList()
  } catch (e: any) {
    ElMessage.error(e?.message || '保存失败')
  } finally {
    saving.value = false
  }
}

async function handleDelete(workspaceId: string) {
  try {
    await deleteWorkspace(workspaceId)
    ElMessage.success('已删除')
    await loadList()
  } catch (e: any) {
    ElMessage.error(e?.message || '删除失败')
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
.header-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}
</style>
