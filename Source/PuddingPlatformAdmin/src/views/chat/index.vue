<template>
  <div class="chat-container">
    <el-row :gutter="16" class="chat-layout">
      <!-- 左侧配置面板 -->
      <el-col :span="6">
        <el-card shadow="never" class="config-card">
          <template #header>
            <span>连接配置</span>
          </template>
          <el-form label-position="top" size="small">
            <el-form-item label="Workspace ID">
              <el-input v-model="form.workspaceId" placeholder="default" clearable />
            </el-form-item>
            <el-form-item label="Channel ID">
              <el-input v-model="form.channelId" placeholder="cli" clearable />
            </el-form-item>
            <el-form-item label="用户 ID">
              <el-input v-model="form.userExternalId" placeholder="admin" clearable />
            </el-form-item>
            <el-form-item label="Session ID（可选，留空则自动创建）">
              <el-input v-model="form.sessionId" placeholder="留空则自动创建 Session" clearable />
            </el-form-item>
            <el-divider />
            <div class="session-info" v-if="currentSessionId">
              <el-text size="small" type="info">当前 Session</el-text>
              <el-text size="small" class="session-id">{{ currentSessionId }}</el-text>
              <el-button size="small" text @click="clearSession">新建会话</el-button>
            </div>
          </el-form>
        </el-card>
      </el-col>

      <!-- 右侧对话面板 -->
      <el-col :span="18">
        <el-card shadow="never" class="chat-card">
          <template #header>
            <div class="chat-header">
              <span>对话测试</span>
              <el-text size="small" type="info">
                链路: PlatformAdmin → Controller → Workspace → Runtime → LLM
              </el-text>
              <el-button size="small" text type="danger" @click="clearMessages">清空</el-button>
            </div>
          </template>

          <!-- 消息列表 -->
          <div ref="messagesEl" class="messages-area">
            <div v-if="messages.length === 0" class="empty-hint">
              <el-empty description="发送消息开始对话" :image-size="80" />
            </div>
            <div
              v-for="(msg, idx) in messages"
              :key="idx"
              :class="['message-item', msg.role]"
            >
              <div class="message-meta">
                <el-tag size="small" :type="msg.role === 'user' ? 'primary' : 'success'">
                  {{ msg.role === 'user' ? '用户' : 'Agent' }}
                </el-tag>
                <el-text size="small" type="info">{{ msg.time }}</el-text>
                <el-tag v-if="msg.sessionId" size="small" type="info" effect="plain">
                  Session: {{ msg.sessionId.slice(0, 8) }}…
                </el-tag>
              </div>
              <div class="message-body">
                <pre v-if="msg.role === 'agent'">{{ msg.text }}</pre>
                <span v-else>{{ msg.text }}</span>
              </div>
              <div v-if="msg.error" class="message-error">
                <el-alert :title="msg.error" type="error" :closable="false" show-icon />
              </div>
            </div>
            <div v-if="loading" class="message-item agent">
              <div class="message-meta">
                <el-tag size="small" type="success">Agent</el-tag>
              </div>
              <div class="message-body typing">
                <el-icon class="is-loading"><Loading /></el-icon>
                正在思考…
              </div>
            </div>
          </div>

          <!-- 输入区 -->
          <div class="input-area">
            <el-input
              v-model="inputText"
              type="textarea"
              :rows="3"
              placeholder="输入消息，Enter 发送，Shift+Enter 换行"
              @keydown.enter.exact.prevent="handleSend"
              :disabled="loading"
              resize="none"
            />
            <div class="input-actions">
              <el-text size="small" type="info">Enter 发送 · Shift+Enter 换行</el-text>
              <el-button
                type="primary"
                :loading="loading"
                :disabled="!inputText.trim()"
                @click="handleSend"
              >
                发送
              </el-button>
            </div>
          </div>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { ref, nextTick } from 'vue'
import { ElMessage } from 'element-plus'
import { Loading } from '@element-plus/icons-vue'
import { sendMessage } from '@/api/message'

interface ChatMessage {
  role: 'user' | 'agent'
  text: string
  time: string
  sessionId?: string
  error?: string
}

const form = ref({
  workspaceId: 'default',
  channelId: 'cli',
  userExternalId: 'admin',
  sessionId: '',
})

const inputText = ref('')
const messages = ref<ChatMessage[]>([])
const loading = ref(false)
const currentSessionId = ref('')
const messagesEl = ref<HTMLElement | null>(null)

function now() {
  return new Date().toLocaleTimeString('zh-CN', { hour12: false })
}

async function scrollToBottom() {
  await nextTick()
  if (messagesEl.value) {
    messagesEl.value.scrollTop = messagesEl.value.scrollHeight
  }
}

function clearSession() {
  currentSessionId.value = ''
  form.value.sessionId = ''
  messages.value = []
}

function clearMessages() {
  messages.value = []
}

async function handleSend() {
  const text = inputText.value.trim()
  if (!text || loading.value) return

  messages.value.push({ role: 'user', text, time: now() })
  inputText.value = ''
  loading.value = true
  await scrollToBottom()

  try {
    const resp = await sendMessage({
      channelId: form.value.channelId || 'cli',
      userExternalId: form.value.userExternalId || 'admin',
      messageText: text,
      workspaceId: form.value.workspaceId || 'default',
      sessionId: currentSessionId.value || form.value.sessionId || undefined,
    })

    // 更新 session id 以保持对话粘性
    if (resp.sessionId) {
      currentSessionId.value = resp.sessionId
    }

    if (resp.isSuccess && resp.reply) {
      messages.value.push({
        role: 'agent',
        text: resp.reply,
        time: now(),
        sessionId: resp.sessionId,
      })
    } else {
      messages.value.push({
        role: 'agent',
        text: '',
        time: now(),
        sessionId: resp.sessionId,
        error: resp.errorMessage ?? '未知错误',
      })
    }
  } catch (e: any) {
    ElMessage.error('请求失败：' + (e?.message ?? String(e)))
    messages.value.push({
      role: 'agent',
      text: '',
      time: now(),
      error: e?.message ?? '网络错误',
    })
  } finally {
    loading.value = false
    await scrollToBottom()
  }
}
</script>

<style scoped>
.chat-container {
  padding: 16px;
  height: calc(100vh - 100px);
  box-sizing: border-box;
}

.chat-layout {
  height: 100%;
}

.config-card {
  height: 100%;
}

.session-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.session-id {
  font-family: monospace;
  font-size: 12px;
  word-break: break-all;
}

.chat-card {
  height: 100%;
  display: flex;
  flex-direction: column;
}

.chat-card :deep(.el-card__body) {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 0;
}

.chat-header {
  display: flex;
  align-items: center;
  gap: 12px;
}

.messages-area {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.empty-hint {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
}

.message-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.message-item.user {
  align-items: flex-end;
}

.message-item.agent {
  align-items: flex-start;
}

.message-meta {
  display: flex;
  align-items: center;
  gap: 6px;
}

.message-body {
  max-width: 70%;
  padding: 10px 14px;
  border-radius: 8px;
  line-height: 1.6;
  word-break: break-word;
}

.message-item.user .message-body {
  background: #409eff;
  color: #fff;
}

.message-item.agent .message-body {
  background: #f5f7fa;
  color: #303133;
}

.message-body pre {
  margin: 0;
  white-space: pre-wrap;
  font-family: inherit;
}

.typing {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #909399;
}

.message-error {
  max-width: 70%;
  margin-top: 4px;
}

.input-area {
  border-top: 1px solid var(--el-border-color);
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.input-actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
</style>
