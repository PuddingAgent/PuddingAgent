<template>
  <div class="dashboard-container">
    <el-row :gutter="20">
      <el-col :span="24">
        <div class="welcome-card">
          <el-card shadow="hover">
            <h2>欢迎使用 Pudding 管理平台</h2>
            <p>当前用户：{{ userStore.name }}</p>
          </el-card>
        </div>
      </el-col>
    </el-row>

    <el-row :gutter="20" style="margin-top: 20px">
      <el-col :span="8">
        <el-card shadow="hover">
          <template #header>工作空间</template>
          <div class="stat-num">--</div>
          <div class="stat-label">活跃工作空间数</div>
        </el-card>
      </el-col>
      <el-col :span="8">
        <el-card shadow="hover">
          <template #header>智能体</template>
          <div class="stat-num">--</div>
          <div class="stat-label">已注册智能体</div>
        </el-card>
      </el-col>
      <el-col :span="8">
        <el-card shadow="hover">
          <template #header>用户</template>
          <div class="stat-num">--</div>
          <div class="stat-label">系统用户数</div>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { useUserStore } from '@/stores/user'

const userStore = useUserStore()
</script>

<style lang="scss" scoped>
.dashboard-container {
  padding: 0;
}

.welcome-card h2 {
  margin: 0 0 10px;
}

.stat-num {
  font-size: 32px;
  font-weight: bold;
  color: #409eff;
}

.stat-label {
  margin-top: 8px;
  color: #999;
  font-size: 14px;
}
</style>
