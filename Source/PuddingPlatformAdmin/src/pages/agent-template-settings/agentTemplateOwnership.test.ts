import fs from 'node:fs';
import path from 'node:path';

const read = (...segments: string[]) => fs.readFileSync(path.join(__dirname, ...segments), 'utf8');

describe('Agent template and instance field ownership copy', () => {
  it('frames global template settings as reusable role blueprints', () => {
    const sections = read('types.ts');
    const promptSection = read('sections', 'PromptPersonaSection.tsx');
    const basicSection = read('sections', 'BasicSection.tsx');
    const modelSection = read('sections', 'ModelMemorySection.tsx');

    expect(sections).toContain("label: '角色定义'");
    expect(sections).toContain("label: '默认模型策略'");
    expect(promptSection).toContain('模板角色定义');
    expect(promptSection).toContain('默认语气与边界');
    expect(basicSection).toContain('模板名称');
    expect(basicSection).toContain('默认头像');
    expect(modelSection).toContain('默认服务商');
  });

  it('frames workspace Agent settings as instance identity without model overrides', () => {
    const workspaceDetail = read('..', 'workspace', '[id]', 'index.tsx');

    expect(workspaceDetail).toContain('实例职责');
    expect(workspaceDetail).toContain('模板默认值预览');
    expect(workspaceDetail).toContain('个性化覆盖');
    expect(workspaceDetail).toContain('覆盖头像');
    expect(workspaceDetail).toContain('实例只保存工作区内身份、头像和启停状态');
    expect(workspaceDetail).not.toContain('模型覆盖');
    expect(workspaceDetail).not.toContain('高级 Prompt 覆盖');
  });
});
