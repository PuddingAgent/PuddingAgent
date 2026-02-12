import {
  formatPricePer1MTokensRmb,
  modelFormValuesToApiValues,
  modelToFormValues,
} from './pricing';
import {
  LLM_PROVIDER_TEMPLATES,
  getProviderTemplateModelValues,
  getProviderTemplateProviderValues,
} from './providerTemplates';
import type { LlmModelDto, UpsertLlmModelRequest } from '@/services/platform/api';

describe('LlmResourcePoolPage pricing units', () => {
  it('formats stored model prices as RMB for display', () => {
    expect(formatPricePer1MTokensRmb(5)).toBe('¥5.0000');
    expect(formatPricePer1MTokensRmb(0.5)).toBe('¥0.5000');
  });

  it('keeps model prices as RMB values across form and API payloads', () => {
    const model: LlmModelDto = {
      id: 1,
      providerId: 1,
      modelId: 'deepseek-chat',
      name: 'deepseek-chat',
      description: '',
      maxContextTokens: 8192,
      maxOutputTokens: 2048,
      inputPricePer1MTokens: 5,
      outputPricePer1MTokens: 15,
      cacheHitPricePer1MTokens: 1,
      capabilityTags: ['text'],
      isDefault: false,
      isDeprecated: false,
      sortOrder: 100,
      createdAt: '2026-06-03T00:00:00Z',
      updatedAt: '2026-06-03T00:00:00Z',
    };

    const formValues = modelToFormValues(model);
    expect(formValues.inputPricePer1MTokens).toBe(5);
    expect(formValues.outputPricePer1MTokens).toBe(15);
    expect(formValues.cacheHitPricePer1MTokens).toBe(1);

    const apiValues = modelFormValuesToApiValues({
      ...formValues,
      inputPricePer1MTokens: 72,
      outputPricePer1MTokens: 144,
      cacheHitPricePer1MTokens: 0,
    } as UpsertLlmModelRequest);

    expect(apiValues.inputPricePer1MTokens).toBe(72);
    expect(apiValues.outputPricePer1MTokens).toBe(144);
    expect(apiValues.cacheHitPricePer1MTokens).toBe(0);
  });
});

describe('LlmResourcePoolPage provider templates', () => {
  it('builds DeepSeek V4 provider and model values without embedding an API key', () => {
    const template = LLM_PROVIDER_TEMPLATES.find((item) => item.value === 'deepseek');
    expect(template).toBeDefined();

    const providerValues = getProviderTemplateProviderValues(template!);
    expect(providerValues).toEqual({
      providerId: 'deepseek',
      name: 'DeepSeek',
      protocol: 'openai',
      baseUrl: 'https://api.deepseek.com',
      description: 'DeepSeek API（OpenAI 兼容；Anthropic 格式地址为 https://api.deepseek.com/anthropic）',
      isEnabled: true,
      maxConcurrentRequests: 50,
    });
    expect(providerValues).not.toHaveProperty('apiKey');

    const modelValues = getProviderTemplateModelValues(template!);
    expect(modelValues).toHaveLength(2);
    expect(modelValues[0]).toMatchObject({
      modelId: 'deepseek-v4-flash',
      name: 'DeepSeek-V4-Flash',
      maxContextTokens: 1000000,
      maxOutputTokens: 384000,
      inputPricePer1MTokens: 1,
      outputPricePer1MTokens: 2,
      cacheHitPricePer1MTokens: 0.02,
      isDefault: true,
      capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
    });
    expect(modelValues[1]).toMatchObject({
      modelId: 'deepseek-v4-pro',
      name: 'DeepSeek-V4-Pro',
      maxContextTokens: 1000000,
      maxOutputTokens: 384000,
      inputPricePer1MTokens: 3,
      outputPricePer1MTokens: 6,
      cacheHitPricePer1MTokens: 0.025,
      isDefault: false,
      capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
    });
  });

  it('builds Xiaomi MiMo token-plan models with zero prices', () => {
    const template = LLM_PROVIDER_TEMPLATES.find((item) => item.value === 'xiaomimimo-tokenplan');
    expect(template).toBeDefined();

    expect(getProviderTemplateProviderValues(template!)).toEqual({
      providerId: 'xiaomimimo-tokenplan',
      name: 'xiaomimimo-tokenplan',
      protocol: 'openai',
      baseUrl: 'https://token-plan-cn.xiaomimimo.com/v1',
      description: '小米 MiMo Token Plan（预付 token 计划，模板内模型费用按 0 处理）',
      isEnabled: true,
      maxConcurrentRequests: 50,
    });

    const modelValues = getProviderTemplateModelValues(template!);
    expect(modelValues).toHaveLength(3);
    for (const model of modelValues) {
      expect(model).toMatchObject({
        maxContextTokens: 1000000,
        maxOutputTokens: 128000,
        inputPricePer1MTokens: 0,
        outputPricePer1MTokens: 0,
        cacheHitPricePer1MTokens: 0,
        capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
      });
    }
    expect(modelValues[0]).toMatchObject({
      modelId: 'mimo-v2.5-pro-ultraspeed',
      name: 'MiMo-V2.5-Pro-UltraSpeed',
      isDefault: false,
    });
    expect(modelValues[1]).toMatchObject({
      modelId: 'mimo-v2.5-pro',
      name: 'MiMo-V2.5-Pro',
      isDefault: true,
    });
    expect(modelValues[2]).toMatchObject({
      modelId: 'mimo-v2.5',
      name: 'MiMo-V2.5',
      isDefault: false,
    });
  });

  it('builds Xiaomi MiMo pay-as-you-go models with RMB API values', () => {
    const template = LLM_PROVIDER_TEMPLATES.find((item) => item.value === 'xiaomimimo-payg');
    expect(template).toBeDefined();

    expect(getProviderTemplateProviderValues(template!)).toEqual({
      providerId: 'xiaomimimo-payg',
      name: 'xiaomimimo-按量付费',
      protocol: 'openai',
      baseUrl: 'https://api.xiaomimimo.com/v1',
      description: '小米 MiMo 按量付费',
      isEnabled: true,
      maxConcurrentRequests: 50,
    });

    const modelValues = getProviderTemplateModelValues(template!);
    expect(modelValues).toHaveLength(3);
    expect(modelValues[0]).toMatchObject({
      modelId: 'mimo-v2.5-pro-ultraspeed',
      name: 'MiMo-V2.5-Pro-UltraSpeed',
      maxContextTokens: 1000000,
      maxOutputTokens: 128000,
      inputPricePer1MTokens: 9,
      outputPricePer1MTokens: 18,
      cacheHitPricePer1MTokens: 0.075,
      isDefault: false,
      capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
    });
    expect(modelValues[1]).toMatchObject({
      modelId: 'mimo-v2.5-pro',
      name: 'MiMo-V2.5-Pro',
      maxContextTokens: 1000000,
      maxOutputTokens: 128000,
      inputPricePer1MTokens: 3,
      outputPricePer1MTokens: 6,
      cacheHitPricePer1MTokens: 0.025,
      isDefault: true,
      capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
    });
    expect(modelValues[2]).toMatchObject({
      modelId: 'mimo-v2.5',
      name: 'MiMo-V2.5',
      maxContextTokens: 1000000,
      maxOutputTokens: 128000,
      inputPricePer1MTokens: 1,
      outputPricePer1MTokens: 2,
      cacheHitPricePer1MTokens: 0.02,
      isDefault: false,
      capabilityTags: ['text', 'function-calling', 'json-mode', 'streaming', 'long-context', 'code', 'reasoning'],
    });
  });
});
