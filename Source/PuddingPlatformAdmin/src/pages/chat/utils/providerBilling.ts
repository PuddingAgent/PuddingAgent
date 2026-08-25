// ── 服务商计费展示适配器注册表（前端侧） ────────────────────────
//
// 与后端 ILlmBalanceProvider 查询适配器（Source/PuddingPlatform/Services/）对应：
// 后端负责各厂商 balance 端点的查询与响应归一化（DTO 已含通用 Currency/BalanceInfos 字段），
// 前端只需按 providerId 决定「是否展示徽标 + 展示名 + 货币符号」。
// 新增服务商（Qwen/GLM/Kimi 等）扩展步骤见
// Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md。

/** 前端余额展示适配器：命中 providerId 才在 UI 渲染余额徽标。 */
export interface ProviderBillingDisplayAdapter {
  /** 适配器标识（与后端厂商对应，如 'deepseek'）。 */
  id: string;
  /** providerId 是否由该适配器展示（providerId 为 llm.providers.json 中的自由字符串）。 */
  match: (providerId: string) => boolean;
  /** 徽标展示名（同时决定 ProviderBalanceIndicator 的品牌图标）。 */
  displayName: string;
  /** 后端未返回币种时的兜底货币符号。 */
  fallbackCurrencySymbol: string;
}

const CURRENCY_SYMBOLS: Record<string, string> = {
  CNY: '¥',
  USD: '$',
};

const billingDisplayAdapters: ProviderBillingDisplayAdapter[] = [
  {
    id: 'deepseek',
    match: (providerId) => providerId.toLowerCase().includes('deepseek'),
    displayName: 'DeepSeek',
    fallbackCurrencySymbol: '¥',
  },
];

/** 按当前主代理的 providerId 解析展示适配器；未命中返回 undefined（UI 不渲染徽标）。 */
export function resolveBillingAdapter(
  providerId: string | undefined,
): ProviderBillingDisplayAdapter | undefined {
  if (!providerId) return undefined;
  return billingDisplayAdapters.find((adapter) => adapter.match(providerId));
}

/** ISO 货币代码 → 符号；未知代码回退到适配器兜底符号。 */
export function currencySymbolFor(
  currencyCode: string | undefined,
  adapter: ProviderBillingDisplayAdapter,
): string {
  if (!currencyCode) return adapter.fallbackCurrencySymbol;
  return CURRENCY_SYMBOLS[currencyCode.toUpperCase()] ?? adapter.fallbackCurrencySymbol;
}
