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
    // 文案已随「工作区 Agent 设置」UX 改版迁移：该部分说明文案原住在 workspace/[id]/index.tsx，
    // 现已收进独立抽屉组件 WorkspaceAgentSettingsDrawer.tsx（实例职责 / 来源模板 / 模板快照…）。
    // 旧的「模板默认值预览 / 个性化覆盖 / 覆盖头像 / 实例只保存工作区内身份…」已生产内消失——
    // 全库检索只命中本测试文件，git log -S 显示最后触碰于 6e2fd05、4b6a3d7 两次改版 ⇒ 是有意改写。
    // ⇒ 读新归属文件，保留本用例原意：**工作区侧编辑的是「实例身份」，不提供模型覆盖**。
    const workspaceDetail = read('..', 'workspace', '[id]', 'WorkspaceAgentSettingsDrawer.tsx');

    expect(workspaceDetail).toContain('实例职责');
    expect(workspaceDetail).toContain('模板只在创建时提供初始快照；Agent 创建后独立演进。');
    expect(workspaceDetail).toContain('来源模板');
    expect(workspaceDetail).not.toContain('模型覆盖');
    expect(workspaceDetail).not.toContain('高级 Prompt 覆盖');
  });
});
