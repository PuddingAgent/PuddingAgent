import { displayName, extractDocumentRefs } from './documentRefs';

/**
 * 文档引用识别：核心是"宁可漏认，不可误认"。
 * 围栏代码块里的路径、裸文本里的路径、非文档扩展名，都不应该变成链接。
 */
describe('extractDocumentRefs', () => {
  it('识别行内 code 中的工作区路径', () => {
    const md = '详见 `Docs/12_features/方案.md`，以及 `Source/a/b.cs`。';
    const refs = extractDocumentRefs(md);

    expect(refs).toHaveLength(1);
    expect(refs[0]).toMatchObject({
      kind: 'workspace',
      target: 'Docs/12_features/方案.md',
      ext: 'md',
    });
  });

  it('识别 artifact:// 引用', () => {
    const refs = extractDocumentRefs('图片见 `artifact://vision-abc123`。');
    expect(refs).toHaveLength(1);
    expect(refs[0]).toMatchObject({ kind: 'artifact', target: 'vision-abc123' });
  });

  it('围栏代码块里的路径不被识别（那是示例，不是引用）', () => {
    // 注意：围栏内**必须带反引号**才算有效用例。原写法 `cat Docs/xx.md`
    // 本来就不匹配行内 code，即使删掉围栏跳过逻辑也会通过——那是假绿。
    const md = ['```md', '示例：`Docs/xx.md`', '```'].join('\n');
    expect(extractDocumentRefs(md)).toHaveLength(0);
  });

  it('裸文本里的路径不被识别', () => {
    expect(extractDocumentRefs('见 Docs/12_features/方案.md 这个文件')).toHaveLength(0);
  });

  it('非文档扩展名不被识别', () => {
    expect(extractDocumentRefs('看 `Source/Foo.cs`')).toHaveLength(0);
    expect(extractDocumentRefs('看 `package.json`')).toHaveLength(0);
  });

  it('含空格的句子不会被误判为路径', () => {
    expect(extractDocumentRefs('看 `请打开 Docs/a.md 这个`')).toHaveLength(0);
  });

  it('同一引用只出现一次', () => {
    const md = '`Docs/a.md` 与 `Docs/a.md`';
    expect(extractDocumentRefs(md)).toHaveLength(1);
  });

  it('支持 Windows 反斜杠路径与 pdf/html', () => {
    const refs = extractDocumentRefs('`temp\\out\\report.pdf` 和 `Docs\\x.HTML`');
    expect(refs).toHaveLength(2);
    expect(refs[0].ext).toBe('pdf');
    expect(refs[1].ext).toBe('html');
  });

  it('纯文件名也算引用', () => {
    const refs = extractDocumentRefs('见 `README.md`');
    expect(refs[0]).toMatchObject({ kind: 'workspace', target: 'README.md' });
  });
});

describe('displayName', () => {
  it('路径取最后一段', () => {
    const [ref] = extractDocumentRefs('`Docs/12_features/方案.md`');
    expect(displayName(ref)).toBe('方案.md');
  });

  it('反斜杠路径同样处理', () => {
    const [ref] = extractDocumentRefs('`temp\\out\\report.pdf`');
    expect(displayName(ref)).toBe('report.pdf');
  });
});
