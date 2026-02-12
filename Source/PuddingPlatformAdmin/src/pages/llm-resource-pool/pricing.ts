import type { LlmModelDto, UpsertLlmModelRequest } from '@/services/platform/api';

const roundPrice = (value: number) => Number(value.toFixed(6));

const toRmbPrice = (value?: number) => roundPrice(value ?? 0);

export const toApiPrice = (value?: number) => roundPrice(value ?? 0);

export const formatPricePer1MTokensRmb = (value?: number) => `¥${toRmbPrice(value).toFixed(4)}`;

export const modelToFormValues = (model: LlmModelDto): Partial<UpsertLlmModelRequest> => ({
  ...model,
  capabilityTags: model.capabilityTags,
  inputPricePer1MTokens: toRmbPrice(model.inputPricePer1MTokens),
  outputPricePer1MTokens: toRmbPrice(model.outputPricePer1MTokens),
  cacheHitPricePer1MTokens: toRmbPrice(model.cacheHitPricePer1MTokens),
});

export const modelFormValuesToApiValues = (
  values: UpsertLlmModelRequest,
): UpsertLlmModelRequest => ({
  ...values,
  inputPricePer1MTokens: toApiPrice(values.inputPricePer1MTokens),
  outputPricePer1MTokens: toApiPrice(values.outputPricePer1MTokens),
  cacheHitPricePer1MTokens: values.cacheHitPricePer1MTokens
    ? toApiPrice(values.cacheHitPricePer1MTokens)
    : 0,
});
