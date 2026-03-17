<template>
  <div class="page-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>知识基础设施</span>
          <div class="header-actions">
            <el-input v-model="workspaceId" placeholder="workspaceId" style="width: 200px" />
            <el-button :icon="Refresh" circle @click="loadAll" :loading="loading" />
          </div>
        </div>
      </template>

      <el-row :gutter="16" class="summary-row">
        <el-col :span="8">
          <el-statistic title="知识文档数" :value="docs.length" />
        </el-col>
        <el-col :span="8">
          <el-statistic title="图谱实体数" :value="graphStats?.entities ?? 0" />
        </el-col>
        <el-col :span="8">
          <el-statistic title="存储对象数" :value="objects.length" />
        </el-col>
      </el-row>

      <el-divider />

      <el-row :gutter="16">
        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>知识文档</span>
            </template>
            <el-table :data="docs" size="small" max-height="360">
              <el-table-column prop="documentId" label="DocumentId" show-overflow-tooltip />
              <el-table-column prop="title" label="标题" width="180" show-overflow-tooltip />
              <el-table-column prop="indexedAt" label="索引时间" width="180">
                <template #default="{ row }">{{ formatTime(row.indexedAt) }}</template>
              </el-table-column>
            </el-table>
          </el-card>
        </el-col>

        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <div class="card-header">
                <span>知识检索</span>
                <div class="header-actions">
                  <el-input v-model="query" placeholder="输入检索词" style="width: 220px" @keyup.enter="doSearch" />
                  <el-button type="primary" @click="doSearch">搜索</el-button>
                </div>
              </div>
            </template>
            <el-table :data="searchResults" size="small" max-height="360">
              <el-table-column prop="documentId" label="DocumentId" width="180" show-overflow-tooltip />
              <el-table-column prop="score" label="分数" width="80" />
              <el-table-column label="片段" show-overflow-tooltip>
                <template #default="{ row }">{{ row.content }}</template>
              </el-table-column>
            </el-table>
          </el-card>
        </el-col>
      </el-row>

      <el-row :gutter="16" style="margin-top: 16px">
        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>图谱实体（前 20）</span>
            </template>
            <el-table :data="entities" size="small" max-height="300">
              <el-table-column prop="entityId" label="EntityId" width="180" show-overflow-tooltip />
              <el-table-column prop="type" label="类型" width="120" />
              <el-table-column prop="label" label="标签" show-overflow-tooltip />
            </el-table>
          </el-card>
        </el-col>

        <el-col :span="12">
          <el-card shadow="never" class="inner-card">
            <template #header>
              <span>存储对象（前 50）</span>
            </template>
            <el-table :data="objects" size="small" max-height="300">
              <el-table-column prop="objectId" label="ObjectId" width="150" show-overflow-tooltip />
              <el-table-column prop="path" label="路径" show-overflow-tooltip />
              <el-table-column prop="sizeBytes" label="大小" width="100" />
            </el-table>
          </el-card>
        </el-col>
      </el-row>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import {
  getKnowledgeDocuments,
  searchKnowledge,
  queryGraphEntities,
  getGraphStats,
  listStorageObjects,
  type KnowledgeDocument,
  type KnowledgeSearchResult,
  type GraphEntity,
  type StorageObjectMeta,
} from '@/api/knowledge'

const loading = ref(false)
const workspaceId = ref('default')
const query = ref('')

const docs = ref<KnowledgeDocument[]>([])
const searchResults = ref<KnowledgeSearchResult[]>([])
const entities = ref<GraphEntity[]>([])
const objects = ref<StorageObjectMeta[]>([])
const graphStats = ref<{ workspaceId: string; entities: number; relations: number } | null>(null)

function formatTime(v?: string) {
  if (!v) return '-'
  const d = new Date(v)
  return Number.isNaN(d.getTime()) ? v : d.toLocaleString()
}

async function loadAll() {
  loading.value = true
  try {
    const wid = workspaceId.value.trim() || 'default'
    const [d, s, e, o] = await Promise.all([
      getKnowledgeDocuments(wid),
      getGraphStats(wid),
      queryGraphEntities(wid, { limit: 20 }),
      listStorageObjects(wid),
    ])
    docs.value = d
    graphStats.value = s
    entities.value = e
    objects.value = o
  } catch (e: any) {
    ElMessage.error(e?.message || '加载失败')
  } finally {
    loading.value = false
  }
}

async function doSearch() {
  const q = query.value.trim()
  if (!q) {
    searchResults.value = []
    return
  }
  try {
    const wid = workspaceId.value.trim() || 'default'
    searchResults.value = await searchKnowledge(wid, q, 8)
  } catch (e: any) {
    ElMessage.error(e?.message || '检索失败')
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
.summary-row {
  margin-bottom: 8px;
}
.inner-card {
  border: 1px solid var(--el-border-color-lighter);
}
</style>
