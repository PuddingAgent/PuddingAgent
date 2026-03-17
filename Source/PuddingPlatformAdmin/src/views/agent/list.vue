<template>
  <div class="agent-container">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>智能体列表</span>
          <el-button type="primary" :icon="Plus">新建智能体</el-button>
        </div>
      </template>

      <el-table :data="tableData" stripe style="width: 100%">
        <el-table-column prop="name" label="名称" />
        <el-table-column prop="type" label="类型" width="120" />
        <el-table-column prop="status" label="状态" width="120">
          <template #default="scope">
            <el-tag :type="scope.row.status === 'online' ? 'success' : 'danger'">
              {{ scope.row.status === 'online' ? '在线' : '离线' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="workspace" label="工作空间" />
        <el-table-column prop="createdAt" label="创建时间" width="180" />
        <el-table-column label="操作" width="200">
          <template #default>
            <el-button size="small" type="primary" link>详情</el-button>
            <el-button size="small" type="danger" link>停止</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { Plus } from '@element-plus/icons-vue'

const tableData = ref([
  { name: 'CodingAgent', type: 'coding', status: 'online', workspace: '默认工作空间', createdAt: '2024-01-01' },
])
</script>

<style lang="scss" scoped>
.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
