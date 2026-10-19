import { render } from '@testing-library/react';
import * as React from 'react';
import { buildSandboxDocument, SANDBOX_CSP, SandboxedHtml } from './SandboxedHtml';

/**
 * 沙箱有**两道**闸：sandbox 属性管脚本，CSP 管网络。
 *
 * 只测 sandbox 是不够的——外部评审指出 `<img src="https://...">` 在
 * `sandbox=""` 里依然会发请求。所以这里必须同时钉住 CSP。
 */
describe('SandboxedHtml', () => {
  it('iframe 使用空 sandbox（最严格）', () => {
    const { getByTestId } = render(<SandboxedHtml html="<div>x</div>" title="t" />);
    expect(getByTestId('sandboxed-html').getAttribute('sandbox')).toBe('');
  });

  it('srcdoc 注入 CSP，禁止一切外部请求', () => {
    const { getByTestId } = render(<SandboxedHtml html="<div>x</div>" title="t" />);
    const srcDoc = getByTestId('sandboxed-html').getAttribute('srcdoc') ?? '';

    expect(srcDoc).toContain('Content-Security-Policy');
    expect(srcDoc).toContain("default-src 'none'");
    expect(srcDoc).toContain("script-src 'none'");
  });

  it('原始内容仍被保留（隔离不能把预览搞坏）', () => {
    const { getByTestId } = render(<SandboxedHtml html="<div>hello</div>" title="t" />);
    expect(getByTestId('sandboxed-html').getAttribute('srcdoc')).toContain('<div>hello</div>');
  });

  it('title 透传（无障碍）', () => {
    const { getByTestId } = render(<SandboxedHtml html="<div/>" title="方案 A" />);
    expect(getByTestId('sandboxed-html').getAttribute('title')).toBe('方案 A');
  });
});

describe('CSP 策略本身', () => {
  it('默认全禁，只放行 data: 资源与内联样式', () => {
    expect(SANDBOX_CSP).toContain("default-src 'none'");
    expect(SANDBOX_CSP).toContain('img-src data:');
    expect(SANDBOX_CSP).toContain('font-src data:');
    expect(SANDBOX_CSP).toContain("style-src 'unsafe-inline'");
    // 不允许任何 http(s) 来源
    expect(SANDBOX_CSP).not.toContain('http');
  });
});

describe('buildSandboxDocument', () => {
  it('产出完整文档（meta 只有在完整文档里才生效）', () => {
    const doc = buildSandboxDocument('<p>hi</p>');
    expect(doc.startsWith('<!doctype html>')).toBe(true);
    expect(doc).toContain('<head>');
    expect(doc).toContain('<body><p>hi</p></body>');
  });
});
