import { currencySymbolFor, resolveBillingAdapter } from './providerBilling';

describe('providerBilling', () => {
  it('matches deepseek provider ids case-insensitively', () => {
    expect(resolveBillingAdapter('deepseek')?.id).toBe('deepseek');
    expect(resolveBillingAdapter('DeepSeek')?.id).toBe('deepseek');
    expect(resolveBillingAdapter('my-deepseek-proxy')?.id).toBe('deepseek');
  });

  it('returns undefined for unmatched or empty provider ids', () => {
    expect(resolveBillingAdapter('moonshot')).toBeUndefined();
    expect(resolveBillingAdapter('volcengine-ark')).toBeUndefined();
    expect(resolveBillingAdapter(undefined)).toBeUndefined();
    expect(resolveBillingAdapter('')).toBeUndefined();
  });

  it('maps currency codes to symbols with adapter fallback', () => {
    const adapter = resolveBillingAdapter('deepseek')!;
    expect(currencySymbolFor('CNY', adapter)).toBe('¥');
    expect(currencySymbolFor('usd', adapter)).toBe('$');
    // 未知代码与缺失代码回退到适配器兜底符号
    expect(currencySymbolFor('EUR', adapter)).toBe('¥');
    expect(currencySymbolFor(undefined, adapter)).toBe('¥');
  });
});
