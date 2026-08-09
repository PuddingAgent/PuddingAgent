import type { UpsertLlmModelRequest, UpsertLlmProviderRequest } from '@/services/platform/api';
import { toApiPrice } from './pricing';

interface LlmProviderTemplateModel
  extends Omit<
    UpsertLlmModelRequest,
    'inputPricePer1MTokens' | 'outputPricePer1MTokens' | 'cacheHitPricePer1MTokens'
  > {
  inputPricePer1MTokensRmb: number;
  outputPricePer1MTokensRmb: number;
  cacheHitPricePer1MTokensRmb: number;
  isEmbedding?: boolean;
}

export interface LlmProviderTemplate {
  value: string;
  label: string;
  provider: Omit<UpsertLlmProviderRequest, 'apiKey'>;
  models: LlmProviderTemplateModel[];
}

const DEEPSEEK_V4_CAPABILITIES = [
  'text',
  'function-calling',
  'json-mode',
  'streaming',
  'long-context',
  'code',
  'reasoning',
];

const KIMI_K3_CAPABILITIES = [
  'text',
  'function-calling',
  'json-mode',
  'streaming',
  'long-context',
  'code',
  'reasoning',
];

const MIMO_V25_CAPABILITIES = [
  'text',
  'function-calling',
  'json-mode',
  'streaming',
  'long-context',
  'code',
  'reasoning',
];

const OPENCODE_GO_CAPABILITIES = [
  'text',
  'function-calling',
  'streaming',
  'long-context',
  'code',
  'reasoning',
];

const createMimoModels = (
  ultraSpeedPrices: Pick<
    LlmProviderTemplateModel,
    'inputPricePer1MTokensRmb' | 'outputPricePer1MTokensRmb' | 'cacheHitPricePer1MTokensRmb'
  >,
  proPrices: Pick<
    LlmProviderTemplateModel,
    'inputPricePer1MTokensRmb' | 'outputPricePer1MTokensRmb' | 'cacheHitPricePer1MTokensRmb'
  >,
  standardPrices: Pick<
    LlmProviderTemplateModel,
    'inputPricePer1MTokensRmb' | 'outputPricePer1MTokensRmb' | 'cacheHitPricePer1MTokensRmb'
  >,
): LlmProviderTemplateModel[] => [
  {
    modelId: 'mimo-v2.5-pro-ultraspeed',
    name: 'MiMo-V2.5-Pro-UltraSpeed',
    protocol: 'openai',
    description: '小米 MiMo V2.5 Pro UltraSpeed，1M 上下文窗口，最大输出 128K tokens，输出 TPS 约 500-1000。',
    maxContextTokens: 1000000,
    maxOutputTokens: 128000,
    ...ultraSpeedPrices,
    capabilityTags: MIMO_V25_CAPABILITIES,
    isDeprecated: false,
    isDefault: false,
    sortOrder: 0,
  },
  {
    modelId: 'mimo-v2.5-pro',
    name: 'MiMo-V2.5-Pro',
    protocol: 'openai',
    description: '小米 MiMo V2.5 Pro，1M 上下文窗口，最大输出 128K tokens。',
    maxContextTokens: 1000000,
    maxOutputTokens: 128000,
    ...proPrices,
    capabilityTags: MIMO_V25_CAPABILITIES,
    isDeprecated: false,
    isDefault: true,
    sortOrder: 1,
  },
  {
    modelId: 'mimo-v2.5',
    name: 'MiMo-V2.5',
    protocol: 'openai',
    description: '小米 MiMo V2.5，1M 上下文窗口，最大输出 128K tokens。',
    maxContextTokens: 1000000,
    maxOutputTokens: 128000,
    ...standardPrices,
    capabilityTags: MIMO_V25_CAPABILITIES,
    isDeprecated: false,
    isDefault: false,
    sortOrder: 2,
  },
];

export const LLM_PROVIDER_TEMPLATES: LlmProviderTemplate[] = [
  {
    value: 'opencode-go',
    label: 'OpenCode Go',
    provider: {
      providerId: 'opencode',
      name: 'OpenCode Go',
      baseUrl: 'https://opencode.ai/zen/go/v1',
      description: 'OpenCode Go 订阅服务；不同模型分别使用 Chat Completions、Responses 或 Anthropic Messages。',
      isEnabled: true,
      maxConcurrentRequests: 300,
    },
    models: [
      {
        modelId: 'gpt-5.6-luna',
        name: 'GPT-5.6 Luna',
        protocol: 'responses',
        description: 'OpenCode Go GPT-5.6 Luna，Responses API，1.05M 上下文窗口。',
        maxContextTokens: 1050000,
        maxInputTokens: 922000,
        maxOutputTokens: 128000,
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [...OPENCODE_GO_CAPABILITIES, 'vision'],
        isDeprecated: false,
        isDefault: false,
        sortOrder: 0,
      },
      {
        modelId: 'grok-4.5',
        name: 'Grok 4.5',
        protocol: 'openai',
        description: 'OpenCode Go Grok 4.5，Chat Completions，500K 上下文窗口。',
        maxContextTokens: 500000,
        maxOutputTokens: 500000,
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [...OPENCODE_GO_CAPABILITIES, 'vision'],
        isDeprecated: false,
        isDefault: false,
        sortOrder: 1,
      },
      {
        modelId: 'glm-5.2',
        name: 'GLM-5.2',
        protocol: 'openai',
        description: 'OpenCode Go GLM-5.2，Chat Completions，1M 上下文窗口。',
        maxContextTokens: 1000000,
        maxOutputTokens: 131072,
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: OPENCODE_GO_CAPABILITIES,
        isDeprecated: false,
        isDefault: false,
        sortOrder: 2,
      },
      {
        modelId: 'kimi-k3',
        name: 'Kimi K3',
        protocol: 'openai',
        description: 'OpenCode Go Kimi K3，Chat Completions，1,048,576 tokens 上下文窗口。',
        maxContextTokens: 1048576,
        maxOutputTokens: 131072,
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [...OPENCODE_GO_CAPABILITIES, 'vision'],
        isDeprecated: false,
        isDefault: false,
        sortOrder: 3,
      },
      {
        modelId: 'qwen3.8-max',
        name: 'Qwen3.8 Max',
        protocol: 'anthropic',
        description: 'OpenCode Go Qwen3.8 Max，Anthropic Messages，1M 上下文窗口。',
        maxContextTokens: 1000000,
        maxOutputTokens: 131072,
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [...OPENCODE_GO_CAPABILITIES, 'vision'],
        isDeprecated: false,
        isDefault: false,
        sortOrder: 4,
      },
    ],
  },
  {
    value: 'deepseek',
    label: 'DeepSeek',
    provider: {
      providerId: 'deepseek',
      name: 'DeepSeek',
      baseUrl: 'https://api.deepseek.com',
      description: 'DeepSeek API（OpenAI 兼容；Anthropic 格式地址为 https://api.deepseek.com/anthropic）',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'deepseek-v4-flash',
        name: 'DeepSeek-V4-Flash',
        protocol: 'openai',
        description: 'DeepSeek V4 Flash，支持非思考与思考模式、Json Output、Tool Calls、对话前缀续写和非思考模式 FIM。',
        maxContextTokens: 1000000,
        maxOutputTokens: 384000,
        inputPricePer1MTokensRmb: 1,
        outputPricePer1MTokensRmb: 2,
        cacheHitPricePer1MTokensRmb: 0.02,
        capabilityTags: DEEPSEEK_V4_CAPABILITIES,
        isDeprecated: false,
        isDefault: true,
        sortOrder: 0,
      },
      {
        modelId: 'deepseek-v4-pro',
        name: 'DeepSeek-V4-Pro',
        protocol: 'openai',
        description: 'DeepSeek V4 Pro，支持非思考与思考模式、Json Output、Tool Calls、对话前缀续写和非思考模式 FIM。',
        maxContextTokens: 1000000,
        maxOutputTokens: 384000,
        inputPricePer1MTokensRmb: 3,
        outputPricePer1MTokensRmb: 6,
        cacheHitPricePer1MTokensRmb: 0.025,
        capabilityTags: DEEPSEEK_V4_CAPABILITIES,
        isDeprecated: false,
        isDefault: false,
        sortOrder: 1,
      },
    ],
  },
  {
    value: 'moonshot',
    label: 'Moonshot（Kimi，按量付费）',
    provider: {
      providerId: 'moonshot',
      name: 'Moonshot（Kimi，按量付费）',
      baseUrl: 'https://api.moonshot.cn/v1',
      description: 'Moonshot Kimi K3 按量付费；额度：并发 50、TPM 2,000,000、RPM 200。',
      isEnabled: true,
      maxConcurrentRequests: 50,
      tokensPerMinute: 2000000,
      requestsPerMinute: 200,
    },
    models: [
      {
        modelId: 'kimi-k3',
        name: 'Kimi K3',
        protocol: 'openai',
        description: 'Moonshot Kimi K3，1,048,576 tokens 上下文窗口，最大输出 131,072 tokens。',
        maxContextTokens: 1048576,
        maxOutputTokens: 131072,
        inputPricePer1MTokensRmb: 20,
        outputPricePer1MTokensRmb: 100,
        cacheHitPricePer1MTokensRmb: 2,
        capabilityTags: KIMI_K3_CAPABILITIES,
        isDeprecated: false,
        isDefault: true,
        sortOrder: 0,
        maxConcurrentRequests: 50,
      },
    ],
  },
  {
    value: 'xiaomimimo-tokenplan',
    label: 'xiaomimimo-tokenplan',
    provider: {
      providerId: 'xiaomimimo-tokenplan',
      name: 'xiaomimimo-tokenplan',
      baseUrl: 'https://token-plan-cn.xiaomimimo.com/v1',
      description: '小米 MiMo Token Plan（预付 token 计划，模板内模型费用按 0 处理）',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: createMimoModels(
      {
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
      },
      {
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
      },
      {
        inputPricePer1MTokensRmb: 0,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
      },
    ),
  },
  {
    value: 'xiaomimimo-payg',
        label: 'xiaomimimo-按量付费',
    provider: {
      providerId: 'xiaomimimo-payg',
      name: 'xiaomimimo-按量付费',
      baseUrl: 'https://api.xiaomimimo.com/v1',
      description: '小米 MiMo 按量付费',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: createMimoModels(
      {
        inputPricePer1MTokensRmb: 9,
        outputPricePer1MTokensRmb: 18,
        cacheHitPricePer1MTokensRmb: 0.075,
      },
      {
        inputPricePer1MTokensRmb: 3,
        outputPricePer1MTokensRmb: 6,
        cacheHitPricePer1MTokensRmb: 0.025,
      },
      {
        inputPricePer1MTokensRmb: 1,
        outputPricePer1MTokensRmb: 2,
        cacheHitPricePer1MTokensRmb: 0.02,
      },
    ),
  },
  {
    value: 'dashscope',
        label: '阿里云百炼 (DashScope)',
    provider: {
      providerId: 'dashscope',
      name: '阿里云百炼',
      baseUrl: 'https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1',
      description: '阿里云百炼平台，OpenAI 兼容协议。支持 Qwen 系列对话模型和 text-embedding-v4。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'qwen-turbo',
        name: 'Qwen Turbo',
        protocol: 'openai',
        description: 'Qwen Turbo，轻量高性能对话模型。',
        maxContextTokens: 131072,
        maxOutputTokens: 8192,
        inputPricePer1MTokensRmb: 0.3,
        outputPricePer1MTokensRmb: 0.6,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: ['text', 'function-calling', 'streaming'],
        isDeprecated: false,
        isDefault: true,
        sortOrder: 1,
      },
      {
        modelId: 'text-embedding-v4',
        name: 'Qwen3 Embedding V4',
        protocol: 'openai',
        description: '阿里云 text-embedding-v4，1024 维向量，中文 CMTEB 70.14。',
        maxContextTokens: 8192,
        maxOutputTokens: 1,
        inputPricePer1MTokensRmb: 0.0005,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [],
        isEmbedding: true,
        isDeprecated: false,
        isDefault: false,
        sortOrder: 30,
      },
    ],
  },
  {
    value: 'openai',
        label: 'OpenAI',
    provider: {
      providerId: 'openai',
      name: 'OpenAI',
      baseUrl: 'https://api.openai.com/v1',
      description: 'OpenAI API，支持 GPT 系列对话模型和 text-embedding-3-small。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'text-embedding-3-small',
        name: 'Embedding 3 Small',
        protocol: 'openai',
        description: 'OpenAI text-embedding-3-small，1536 维向量。',
        maxContextTokens: 8191,
        maxOutputTokens: 1,
        inputPricePer1MTokensRmb: 0.144,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [],
        isEmbedding: true,
        isDeprecated: false,
        isDefault: false,
        sortOrder: 31,
      },
    ],
  },
  {
    value: 'bigmodel',
        label: '智谱 BigModel',
    provider: {
      providerId: 'bigmodel',
      name: '智谱 BigModel',
      baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
      description: '智谱 BigModel，支持 GLM 系列对话模型。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'glm-5.2',
        name: 'GLM 5.2',
        protocol: 'openai',
        description: '最新一代 GLM 模型，1M 上下文窗口。',
        maxContextTokens: 1048576,
        maxOutputTokens: 131072,
        inputPricePer1MTokensRmb: 1.0,
        outputPricePer1MTokensRmb: 4.0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: ['text', 'function-calling', 'streaming'],
        isDeprecated: false,
        isDefault: true,
        sortOrder: 1,
      },
    ],
  },
  {
    value: 'bigmodel-embeddings',
        label: '智谱 Embedding',
    provider: {
      providerId: 'bigmodel-embeddings',
      name: '智谱 Embedding',
      baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
      description: '智谱 BigModel Embedding API，支持 1024/2048 维向量。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'embedding-3',
        name: 'Embedding 3',
        protocol: 'openai',
        description: '智谱 embedding-3，支持自定义维度（512/1024/2048），默认 1024。',
        maxContextTokens: 8192,
        maxOutputTokens: 1,
        inputPricePer1MTokensRmb: 0.0005,
        outputPricePer1MTokensRmb: 0,
        cacheHitPricePer1MTokensRmb: 0,
        capabilityTags: [],
        isEmbedding: true,
        isDeprecated: false,
        isDefault: true,
        sortOrder: 31,
      },
    ],
  },
];

export const getProviderTemplateProviderValues = (
  template: LlmProviderTemplate,
): Omit<UpsertLlmProviderRequest, 'apiKey'> => ({
  ...template.provider,
});

export const getProviderTemplateModelValues = (
  template: LlmProviderTemplate,
): UpsertLlmModelRequest[] =>
  template.models.map((model) => ({
    modelId: model.modelId,
    name: model.name,
    protocol: model.protocol,
    description: model.description,
    maxContextTokens: model.maxContextTokens,
    maxInputTokens: model.maxInputTokens,
    maxOutputTokens: model.maxOutputTokens,
    inputPricePer1MTokens: toApiPrice(model.inputPricePer1MTokensRmb),
    outputPricePer1MTokens: toApiPrice(model.outputPricePer1MTokensRmb),
    cacheHitPricePer1MTokens: toApiPrice(model.cacheHitPricePer1MTokensRmb),
    capabilityTags: [...(model.capabilityTags ?? [])],
    isDeprecated: model.isDeprecated,
    isDefault: model.isDefault,
    isEmbedding: model.isEmbedding ?? false,
    sortOrder: model.sortOrder,
    maxConcurrentRequests: model.maxConcurrentRequests,
  }));
