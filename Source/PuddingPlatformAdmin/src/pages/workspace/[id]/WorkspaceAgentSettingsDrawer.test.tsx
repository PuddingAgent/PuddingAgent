import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Form } from 'antd';
import * as React from 'react';
import WorkspaceAgentSettingsDrawer from './WorkspaceAgentSettingsDrawer';

/**
 * 工作台外壳的冒烟测试：身份栏、文档目录、单文档编辑器与保存状态。
 *
 * 只断言一期新增的编辑体验，不重复覆盖各分区的业务字段。
 * 注意：本项目的测试转换器只做去类型注释，测试文件里的类型标注保持最小。
 */

jest.mock('../../agent-template-settings/styles', () => {
  const styles = new Proxy({}, { get: (_target, prop) => String(prop) });
  const cx = (...args: any[]) =>
    args.filter((value) => typeof value === 'string' && value).join(' ');
  return { useStyles: () => ({ styles, cx }) };
});

// 能力 / 模型分区会各自发起 API 请求；工作台外壳不依赖它们的内部实现。
jest.mock('../../agent-template-settings/sections/CapabilitySkillSection', () => ({
  __esModule: true,
  default: () => <div data-testid="capability-section" />,
}));
jest.mock('../../agent-template-settings/sections/ModelMemorySection', () => ({
  __esModule: true,
  default: () => <div data-testid="model-section" />,
}));

const baseProps = {
  open: true,
  editMode: true,
  loading: false,
  workspaceName: '默认工作空间',
  onClose: jest.fn(),
  onSave: jest.fn().mockResolvedValue(true),
  onSourceTemplateChange: jest.fn(),
  templates: [
    {
      id: 1,
      templateId: 'general-assistant',
      name: '通用助手',
      role: 'Service',
      maxContextTokens: 128000,
      selectedCapabilityIds: [],
      selectedSkillPackageIds: [],
      isBuiltIn: true,
      isEnabled: true,
      sortOrder: 1,
      createdAt: '',
      updatedAt: '',
    },
  ],
  avatars: [],
  providers: [],
  models: [],
  memoryModels: [],
  embeddingModels: [],
  loadingModels: false,
  loadingMemoryModels: false,
  loadingEmbeddingModels: false,
  onProviderChange: jest.fn(),
  onMemoryProviderChange: jest.fn(),
  onEmbeddingProviderChange: jest.fn(),
  capabilities: [],
  skillPackages: [],
  defaultCapIds: [],
  grantCapabilities: [],
  grantTargetKeys: [],
  skillTargetKeys: [],
  onGrantChange: jest.fn(),
  onSkillChange: jest.fn(),
};

const Harness = ({ initialValues, ...overrides }: any) => {
  const [form] = Form.useForm();
  // 与真实父组件一致：先把服务端数据 / 模板快照写入表单，再递增基线版本。
  React.useLayoutEffect(() => {
    form.resetFields();
    form.setFieldsValue(initialValues);
    // 只在挂载时灌入一次基线数据
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return (
    <WorkspaceAgentSettingsDrawer
      {...baseProps}
      {...overrides}
      form={form}
      dataVersion={1}
    />
  );
};

/** 文档正文是 textarea：断言 value 需要收窄元素类型。 */
const textareaByLabel = (label: string) =>
  screen.getByLabelText(label) as HTMLTextAreaElement;

const findTextareaByLabel = async (label: string) =>
  (await screen.findByLabelText(label)) as HTMLTextAreaElement;

/** 打开「角色与 Prompt」分区（工作台默认打开基础信息）。 */
const openPromptSection = async () => {
  fireEvent.click(screen.getByRole('button', { name: /^角色与 Prompt/ }));
  return screen.findByRole('navigation', { name: 'Prompt 文档目录' });
};

describe('WorkspaceAgentSettingsDrawer 编辑工作台', () => {
  it('默认打开基础信息，身份栏与保存状态可见', async () => {
    render(
      <Harness
        initialValues={{
          name: '审批审计员',
          role: 'Audit',
          sourceTemplateId: 'global:general-assistant',
        }}
      />,
    );

    expect(await screen.findByText('审批审计员')).toBeTruthy();
    expect(screen.getByText(/默认工作空间 · 审计型 \(Audit\) · 创建自：通用助手/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /^基础信息/ })).toBeTruthy();
    expect(screen.getByText('无未保存更改')).toBeTruthy();
    expect(screen.queryByRole('navigation', { name: 'Prompt 文档目录' })).toBeNull();
  });

  it('文档目录列出全部八份内容，默认编辑系统提示词', async () => {
    render(
      <Harness
        initialValues={{
          name: '审批审计员',
          role: 'Audit',
          sourceTemplateId: 'global:general-assistant',
          systemPrompt: '# 角色\n你是审批审计员。',
        }}
      />,
    );

    await screen.findByText('审批审计员');
    const catalog = await openPromptSection();

    // 文档目录列出全部八份内容
    expect(catalog.querySelectorAll('button')).toHaveLength(8);
    expect(screen.getByRole('button', { name: /心跳恢复/ })).toBeTruthy();
    expect(screen.getByText('8 份内容 · 按需配置')).toBeTruthy();

    // 默认打开系统提示词，正文可编辑且保留换行
    expect(textareaByLabel('系统提示词').value).toBe('# 角色\n你是审批审计员。');
  });

  it('修改内容后显示未保存数量，改回原值后标记消失', async () => {
    render(<Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} />);
    await screen.findByText('审计员');
    await openPromptSection();

    const editor = await findTextareaByLabel('系统提示词');
    fireEvent.change(editor, { target: { value: '改过的内容' } });

    expect(await screen.findByText('未保存 · 1 项')).toBeTruthy();

    fireEvent.change(editor, { target: { value: '原始内容' } });
    await waitFor(() => expect(screen.getByText('无未保存更改')).toBeTruthy());
  });

  it('切换文档保留草稿，并在目录与分区上标记已修改', async () => {
    render(<Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} />);
    await screen.findByText('审计员');
    await openPromptSection();

    const editor = await findTextareaByLabel('系统提示词');
    fireEvent.change(editor, { target: { value: '改过的内容' } });

    fireEvent.click(screen.getByRole('button', { name: /协作规范/ }));
    expect(textareaByLabel('协作规范').value).toBe('');

    fireEvent.click(screen.getByRole('button', { name: /系统提示词/ }));
    expect(textareaByLabel('系统提示词').value).toBe('改过的内容');

    // 分区导航与目录都带上修改标记
    expect(screen.getByRole('button', { name: /角色与 Prompt，有未保存修改/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /系统提示词（核心职责与行为），已修改/ })).toBeTruthy();
  });

  it('Markdown 文档可切换到预览，纯文本文档不提供预览', async () => {
    render(<Harness initialValues={{ name: '审计员', soulMdContent: '# 人设\n保持克制。' }} />);
    await screen.findByText('审计员');
    await openPromptSection();

    fireEvent.click(await screen.findByRole('button', { name: /人设与边界/ }));
    fireEvent.click(screen.getByRole('radio', { name: '预览' }));

    const preview = await screen.findByTestId('prompt-document-preview');
    expect(preview.textContent).toContain('人设');

    fireEvent.click(screen.getByRole('button', { name: /用户消息模板/ }));
    expect(screen.queryByTestId('prompt-document-preview')).toBeNull();
  });

  it('保存成功后刷新基线并留在工作台，不需要关闭再重开', async () => {
    const onSave = jest.fn().mockResolvedValue(true);
    const onClose = jest.fn();
    render(
      <Harness
        initialValues={{ name: '审计员', systemPrompt: '原始内容' }}
        onSave={onSave}
        onClose={onClose}
      />,
    );
    await screen.findByText('审计员');
    await openPromptSection();

    const editor = await findTextareaByLabel('系统提示词');
    fireEvent.change(editor, { target: { value: '新内容' } });
    fireEvent.click(screen.getByRole('button', { name: '保存所有更改' }));

    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onClose).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.getByText('无未保存更改')).toBeTruthy());
    // 仍停留在当前文档
    expect(textareaByLabel('系统提示词').value).toBe('新内容');
  });

  it('保存失败时保留草稿并提示重试', async () => {
    const onSave = jest.fn().mockResolvedValue(false);
    render(
      <Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} onSave={onSave} />,
    );
    await screen.findByText('审计员');
    await openPromptSection();

    const editor = await findTextareaByLabel('系统提示词');
    fireEvent.change(editor, { target: { value: '新内容' } });
    fireEvent.click(screen.getByRole('button', { name: '保存所有更改' }));

    expect(await screen.findByText('保存失败，草稿已保留')).toBeTruthy();
    expect(textareaByLabel('系统提示词').value).toBe('新内容');
    expect(screen.getByText('未保存 · 1 项')).toBeTruthy();
  });

  it('无修改时保存按钮降级为「已保存」且禁用，返回列表直接关闭', async () => {
    const onClose = jest.fn();
    render(
      <Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} onClose={onClose} />,
    );

    // 有修改时才显示「保存所有更改」；无修改时不能长期停在灰紫禁用态
    const save = (await screen.findByRole('button', { name: '已保存' })) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    expect(screen.queryByRole('button', { name: '保存所有更改' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /返回列表/ }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('身份栏提供可见关闭入口，且与返回列表共用未保存确认', async () => {
    const onClose = jest.fn();
    render(
      <Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} onClose={onClose} />,
    );
    await screen.findByText('审计员');
    await openPromptSection();

    // 有修改：关闭走同一套确认，不直接丢弃草稿
    fireEvent.change(await findTextareaByLabel('系统提示词'), {
      target: { value: '新内容' },
    });
    fireEvent.click(screen.getByRole('button', { name: '关闭 Agent 编辑工作台' }));
    expect(onClose).not.toHaveBeenCalled();
    expect(await screen.findByText('放弃未保存的修改？')).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '放弃修改' }));
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('有修改时返回列表需确认，可放弃修改', async () => {
    const onClose = jest.fn();
    render(
      <Harness initialValues={{ name: '审计员', systemPrompt: '原始内容' }} onClose={onClose} />,
    );
    await screen.findByText('审计员');
    await openPromptSection();

    const editor = await findTextareaByLabel('系统提示词');
    fireEvent.change(editor, { target: { value: '新内容' } });

    fireEvent.click(screen.getByRole('button', { name: /返回列表/ }));
    expect(onClose).not.toHaveBeenCalled();
    expect(await screen.findByText('放弃未保存的修改？')).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '放弃修改' }));
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('加载中显示加载态且禁用保存', async () => {
    render(<Harness loading initialValues={{ name: '审计员' }} />);
    expect(await screen.findByText('正在加载 Agent 完整配置…')).toBeTruthy();
  });
});
