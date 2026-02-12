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

const MIMO_V25_CAPABILITIES = [
  'text',
  'function-calling',
  'json-mode',
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
    value: 'deepseek',
        label: 'DeepSeek',
    provider: {
      providerId: 'deepseek',
      name: 'DeepSeek',
      protocol: 'openai',
      baseUrl: 'https://api.deepseek.com',
      description: 'DeepSeek API（OpenAI 兼容；Anthropic 格式地址为 https://api.deepseek.com/anthropic）',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'deepseek-v4-flash',
        name: 'DeepSeek-V4-Flash',
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
    value: 'xiaomimimo-tokenplan',
        label: 'xiaomimimo-tokenplan',
    provider: {
      providerId: 'xiaomimimo-tokenplan',
      name: 'xiaomimimo-tokenplan',
      protocol: 'openai',
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
      protocol: 'openai',
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
      protocol: 'openai',
      baseUrl: 'https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1',
      description: '阿里云百炼平台，OpenAI 兼容协议。支持 Qwen 系列对话模型和 text-embedding-v4。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'qwen-turbo',
        name: 'Qwen Turbo',
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
      protocol: 'openai',
      baseUrl: 'https://api.openai.com/v1',
      description: 'OpenAI API，支持 GPT 系列对话模型和 text-embedding-3-small。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'text-embedding-3-small',
        name: 'Embedding 3 Small',
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
      protocol: 'openai',
      baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
      description: '智谱 BigModel，支持 GLM 系列对话模型。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'glm-5.2',
        name: 'GLM 5.2',
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
      protocol: 'openai',
      baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
      description: '智谱 BigModel Embedding API，支持 1024/2048 维向量。',
      isEnabled: true,
      maxConcurrentRequests: 50,
    },
    models: [
      {
        modelId: 'embedding-3',
        name: 'Embedding 3',
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
    description: model.description,
    maxContextTokens: model.maxContextTokens,
    maxOutputTokens: model.maxOutputTokens,
    inputPricePer1MTokens: toApiPrice(model.inputPricePer1MTokensRmb),
    outputPricePer1MTokens: toApiPrice(model.outputPricePer1MTokensRmb),
    cacheHitPricePer1MTokens: toApiPrice(model.cacheHitPricePer1MTokensRmb),
    capabilityTags: [...(model.capabilityTags ?? [])],
    isDeprecated: model.isDeprecated,
    isDefault: model.isDefault,
    isEmbedding: model.isEmbedding ?? false,
    sortOrder: model.sortOrder,
  }));
