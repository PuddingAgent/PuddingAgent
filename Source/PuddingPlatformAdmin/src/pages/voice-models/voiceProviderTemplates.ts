import type { UpsertVoiceProviderRequest, UpsertTtsModelRequest, UpsertAsrModelRequest } from '@/services/platform/api';

export interface TtsTemplateModel extends UpsertTtsModelRequest {}

export interface AsrTemplateModel extends UpsertAsrModelRequest {}

export interface VoiceProviderTemplate {
  value: string;
  label: string;
  provider: Omit<UpsertVoiceProviderRequest, 'apiKey'>;
  ttsModels: TtsTemplateModel[];
  asrModels: AsrTemplateModel[];
}

const DASHSCOPE_TTS_MODELS: TtsTemplateModel[] = [
  {
    modelId: 'cosyvoice-v3-flash',
    name: 'CosyVoice V3 Flash',
    path: '/api/v1/services/audio/tts/SpeechSynthesizer',
    voices: ['longanyang', 'longanhuan_v3'],
    audioFormats: ['wav', 'mp3'],
    sampleRates: [24000, 48000],
    supportsStreaming: true,
    supportsInstructions: true,
    supportsVoiceCloning: false,
    supportsVoiceDesign: false,
    isDeprecated: false,
    isDefault: true,
    sortOrder: 1,
  },
  {
    modelId: 'qwen3-tts-flash',
    name: 'Qwen3 TTS Flash',
    path: '/api/v1/services/aigc/multimodal-generation/generation',
    voices: ['Cherry', 'Stella', 'Vera'],
    audioFormats: ['wav', 'mp3'],
    sampleRates: [24000],
    supportsStreaming: true,
    supportsInstructions: false,
    supportsVoiceCloning: false,
    supportsVoiceDesign: false,
    isDeprecated: false,
    isDefault: false,
    sortOrder: 2,
  },
];

const DASHSCOPE_ASR_MODELS: AsrTemplateModel[] = [
  {
    modelId: 'qwen3-asr-flash-realtime',
    name: 'Qwen3 ASR Flash Realtime',
    path: '/api/v1/services/aigc/multimodal-generation/generation',
    languages: ['zh-CN', 'en-US'],
    sampleRates: [16000],
    supportsEmotion: true,
    supportsTimestamps: true,
    supportsHotWords: false,
    isDeprecated: false,
    isDefault: true,
    sortOrder: 1,
  },
];

const XUNFEI_ASR_MODELS: AsrTemplateModel[] = [
  {
    modelId: 'xunfei-lark-realtime',
    name: '讯飞 Lark 实时语音识别',
    path: '/v2/iat',
    languages: ['zh-CN', 'en-US'],
    sampleRates: [16000, 8000],
    supportsEmotion: false,
    supportsTimestamps: true,
    supportsHotWords: false,
    isDeprecated: false,
    isDefault: true,
    sortOrder: 1,
  },
];

export const VOICE_PROVIDER_TEMPLATES: VoiceProviderTemplate[] = [
  {
    value: 'dashscope',
    label: '阿里云百炼-语音',
    provider: {
      providerId: 'dashscope',
      name: '阿里云百炼-语音',
      endpoint: 'https://dashscope.aliyuncs.com',
      description: '支持 CosyVoice 和 Qwen-TTS/ASR。',
      isEnabled: true,
    },
    ttsModels: DASHSCOPE_TTS_MODELS,
    asrModels: DASHSCOPE_ASR_MODELS,
  },
  {
    value: 'xunfei',
    label: '讯飞语音',
    provider: {
      providerId: 'xunfei',
      name: '讯飞语音',
      endpoint: 'https://iat-api.xfyun.cn/v2',
      description: '讯飞语音，仅提供 ASR。',
      isEnabled: false,
    },
    ttsModels: [],
    asrModels: XUNFEI_ASR_MODELS,
  },
];

export function getVoiceProviderTemplateProviderValues(
  template: VoiceProviderTemplate,
): UpsertVoiceProviderRequest {
  return { ...template.provider, apiKey: '' };
}

export function getVoiceProviderTemplateTtsModels(
  template: VoiceProviderTemplate,
): UpsertTtsModelRequest[] {
  return template.ttsModels;
}

export function getVoiceProviderTemplateAsrModels(
  template: VoiceProviderTemplate,
): UpsertAsrModelRequest[] {
  return template.asrModels;
}
