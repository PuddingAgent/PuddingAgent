import { validateAnnotations, validatePanelSpec } from './spec';

/**
 * 面板 spec 校验：**只接受声明式结构**。
 * 这组用例的重点不是"合法能过"，而是"违规必须被拒"——
 * 面板是模型输出直接进 UI 的入口，校验漏一条就是一条数据外带/XSS 面。
 */
const survey = () => ({
  v: 1,
  id: 'panel-1',
  kind: 'survey',
  title: '需求对齐',
  fields: [
    { key: 'scope', label: '范围', type: 'radio', options: [{ value: 'a', label: 'A' }] },
    { key: 'note', label: '补充', type: 'textarea', maxLength: 500 },
  ],
});

const variants = () => ({
  v: 1,
  id: 'panel-2',
  kind: 'variants',
  layout: 3,
  variants: [
    { id: 'A', title: '极简', html: '<div style="padding:8px">A</div>' },
    { id: 'B', title: '品牌', svg: '<svg viewBox="0 0 10 10"><rect width="10" height="10"/></svg>' },
  ],
});

describe('validatePanelSpec', () => {
  it('合法 survey 通过', () => {
    const result = validatePanelSpec(survey());
    expect(result.ok).toBe(true);
    expect(result.issues.filter((i) => i.severity === 'error')).toHaveLength(0);
  });

  it('合法 variants 通过', () => {
    expect(validatePanelSpec(variants()).ok).toBe(true);
  });

  it('拒绝未知版本与未知类型', () => {
    expect(validatePanelSpec({ ...survey(), v: 2 }).ok).toBe(false);
    expect(validatePanelSpec({ ...survey(), kind: 'iframe' }).ok).toBe(false);
  });

  it('survey 缺字段 / key 重复 / 选项类型缺 options 都报错', () => {
    expect(validatePanelSpec({ ...survey(), fields: [] }).ok).toBe(false);

    const dup = survey();
    dup.fields[1].key = 'scope';
    expect(validatePanelSpec(dup).ok).toBe(false);

    const noOptions = survey();
    delete (noOptions.fields[0] as Record<string, unknown>).options;
    const result = validatePanelSpec(noOptions);
    expect(result.ok).toBe(false);
    expect(result.issues.some((i) => i.path.includes('.options'))).toBe(true);
  });

  it('variants 布局只接受 1/2/3/4', () => {
    expect(validatePanelSpec({ ...variants(), layout: 5 }).ok).toBe(false);
    expect(validatePanelSpec({ ...variants(), layout: 0 }).ok).toBe(false);
  });

  it('variants 每项必须 html/svg 二选一', () => {
    const both = variants();
    both.variants[0] = { ...both.variants[0], svg: '<svg/>' } as never;
    expect(validatePanelSpec(both).ok).toBe(false);

    const neither = variants();
    neither.variants[0] = { id: 'A', title: '空' } as never;
    expect(validatePanelSpec(neither).ok).toBe(false);
  });

  it('拒绝外链资源（图片/字体/CDN）', () => {
    const externalImg = variants();
    externalImg.variants[0] = {
      id: 'A',
      title: '外链',
      html: '<img src="https://evil.example/tracker.gif">',
    } as never;
    const result = validatePanelSpec(externalImg);
    expect(result.ok).toBe(false);
    expect(result.issues.some((i) => i.message.includes('外链'))).toBe(true);

    const externalCss = variants();
    externalCss.variants[0] = {
      id: 'A',
      title: '外链 CSS',
      html: '<div style="background:url(https://evil.example/a.png)">x</div>',
    } as never;
    expect(validatePanelSpec(externalCss).ok).toBe(false);

    const protocolRelative = variants();
    protocolRelative.variants[0] = {
      id: 'A',
      title: '协议相对',
      html: '<img src="//evil.example/a.gif">',
    } as never;
    expect(validatePanelSpec(protocolRelative).ok).toBe(false);
  });

  it('允许内联 data: 图片（不误伤自包含内容）', () => {
    const inline = variants();
    inline.variants[0] = {
      id: 'A',
      title: '内联',
      html: '<img src="data:image/png;base64,AAAA">',
    } as never;
    expect(validatePanelSpec(inline).ok).toBe(true);
  });

  it('SVG 内不允许 <script>', () => {
    const bad = variants();
    bad.variants[1] = {
      id: 'B',
      title: '脚本',
      svg: '<svg><script>alert(1)</script></svg>',
    } as never;
    expect(validatePanelSpec(bad).ok).toBe(false);
  });

  it('draft 超过体积上限被拒', () => {
    const big = {
      v: 1,
      id: 'd',
      kind: 'draft',
      format: 'html',
      source: 'x'.repeat(256 * 1024 + 1),
    };
    expect(validatePanelSpec(big).ok).toBe(false);
  });

  it('非对象输入直接失败', () => {
    expect(validatePanelSpec(null).ok).toBe(false);
    expect(validatePanelSpec('<div/>').ok).toBe(false);
    expect(validatePanelSpec([]).ok).toBe(false);
  });
});

describe('validateAnnotations', () => {
  it('合法图元通过', () => {
    const issues = validateAnnotations([
      { type: 'rect', x: 0.1, y: 0.2, w: 0.3, h: 0.1, text: '太挤' },
      { type: 'arrow', from: { x: 0, y: 0 }, to: { x: 1, y: 1 } },
      { type: 'note', at: { x: 0.5, y: 0.5 }, text: '整体偏商务' },
    ]);
    expect(issues).toHaveLength(0);
  });

  it('坐标越界被拒（必须归一化 0..1）', () => {
    const issues = validateAnnotations([{ type: 'rect', x: 120, y: 0.2, w: 0.3, h: 0.1 }]);
    expect(issues.some((i) => i.path.includes('.x'))).toBe(true);
  });

  it('未知类型与空 note 被拒', () => {
    expect(validateAnnotations([{ type: 'circle' }]).length).toBeGreaterThan(0);
    expect(validateAnnotations([{ type: 'note', at: { x: 0.1, y: 0.1 }, text: '' }]).length).toBe(1);
  });

  it('缺省不报错（面板可以没有标注）', () => {
    expect(validateAnnotations(undefined)).toHaveLength(0);
  });
});
