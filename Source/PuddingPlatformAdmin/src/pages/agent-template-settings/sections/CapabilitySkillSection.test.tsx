import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Form } from 'antd';
import * as React from 'react';
import CapabilitySkillSection from './CapabilitySkillSection';

jest.mock('../styles', () => {
  const styles = new Proxy({}, {
    get: (_target, prop) => String(prop),
  });
  return {
    useStyles: () => ({ styles }),
  };
});

jest.mock('antd', () => {
  const actual = jest.requireActual('antd');
  return {
    ...actual,
    Modal: ({ open, children }: any) => (open ? <div role="dialog">{children}</div> : null),
  };
});

describe('CapabilitySkillSection form persistence', () => {
  it('keeps selected capability and skill ids in validated form values', async () => {
    const saved: any[] = [];

    const Harness = () => {
      const [form] = Form.useForm();
      const [grantTargetKeys, setGrantTargetKeys] = React.useState<string[]>([]);
      const [skillTargetKeys, setSkillTargetKeys] = React.useState<string[]>([]);
      const defaultCapIds = ['cap-http-fetch'];

      return (
        <Form form={form}>
          <CapabilitySkillSection
            id="capabilities"
            capabilities={[
              {
                id: 1,
                capabilityId: 'cap-http-fetch',
                name: 'HTTP 请求',
                toolName: 'http_fetch',
                requiresShellExecution: false,
                requiresFileWrite: false,
                requiresNetworkAccess: true,
                isEnabled: true,
                sortOrder: 1,
                sourceKind: 'BuiltIn',
                runtimeStatus: 'Available',
                createdAt: '',
                updatedAt: '',
              },
              {
                id: 2,
                capabilityId: 'cap-python',
                name: 'Python 代码执行',
                toolName: 'python',
                requiresShellExecution: true,
                requiresFileWrite: false,
                requiresNetworkAccess: false,
                isEnabled: true,
                sortOrder: 2,
                sourceKind: 'BuiltIn',
                runtimeStatus: 'Available',
                createdAt: '',
                updatedAt: '',
              },
            ]}
            skillPackages={[
              {
                id: 1,
                skillPackageId: 'skill-a',
                name: 'Skill A',
                version: '1.0.0',
                fileName: 'skill-a.zip',
                fileSizeBytes: 1,
                contentType: 'application/zip',
                isEnabled: true,
                sortOrder: 1,
                createdAt: '',
                updatedAt: '',
              },
            ]}
            grantTargetKeys={grantTargetKeys}
            skillTargetKeys={skillTargetKeys}
            onGrantChange={(keys) => {
              setGrantTargetKeys(keys);
              form.setFieldsValue({ selectedCapabilityIds: [...defaultCapIds, ...keys] });
            }}
            onSkillChange={(keys) => {
              setSkillTargetKeys(keys);
              form.setFieldsValue({ selectedSkillPackageIds: keys });
            }}
            defaultCapIds={defaultCapIds}
            grantCapabilities={[
              {
                id: 2,
                capabilityId: 'cap-python',
                name: 'Python 代码执行',
                toolName: 'python',
                requiresShellExecution: true,
                requiresFileWrite: false,
                requiresNetworkAccess: false,
                isEnabled: true,
                sortOrder: 2,
                sourceKind: 'BuiltIn',
                runtimeStatus: 'Available',
                createdAt: '',
                updatedAt: '',
              },
            ]}
          />
          <button
            type="button"
            onClick={async () => saved.push(await form.validateFields())}
          >
            save
          </button>
        </Form>
      );
    };

    render(<Harness />);

    fireEvent.click(screen.getAllByRole('button', { name: /添加\/管理/ })[0]);
    await screen.findByRole('dialog');
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Python 代码执行' }));
    fireEvent.click(screen.getByTestId('resource-picker-apply'));

    fireEvent.click(screen.getAllByRole('button', { name: /添加\/管理/ })[1]);
    await screen.findByRole('dialog');
    fireEvent.click(await screen.findByRole('checkbox', { name: 'Skill A' }));
    fireEvent.click(screen.getByTestId('resource-picker-apply'));

    fireEvent.click(screen.getByText('save'));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0].selectedCapabilityIds).toEqual(['cap-http-fetch', 'cap-python']);
    expect(saved[0].selectedSkillPackageIds).toEqual(['skill-a']);
  });

  it('能力名与技术标识相同时 chip 只渲染一次', () => {
    // 真实能力目录里 name 常常就等于 toolName（例如 search_tools），
    // 早期实现无条件追加 code，会得到「search_tools search_tools」。
    const Harness = () => {
      const [form] = Form.useForm();
      return (
        <Form form={form}>
          <CapabilitySkillSection
            id="capabilities"
            capabilities={[
              {
                id: 1,
                capabilityId: 'cap-search',
                name: 'search_tools',
                toolName: 'search_tools',
                requiresShellExecution: false,
                requiresFileWrite: false,
                requiresNetworkAccess: false,
                isEnabled: true,
                sortOrder: 1,
                sourceKind: 'BuiltIn',
                runtimeStatus: 'Available',
                createdAt: '',
                updatedAt: '',
              },
              {
                id: 2,
                capabilityId: 'cap-http',
                name: 'HTTP 请求',
                toolName: 'http_fetch',
                requiresShellExecution: false,
                requiresFileWrite: false,
                requiresNetworkAccess: false,
                isEnabled: true,
                sortOrder: 2,
                sourceKind: 'BuiltIn',
                runtimeStatus: 'Available',
                createdAt: '',
                updatedAt: '',
              },
            ]}
            skillPackages={[]}
            grantTargetKeys={[]}
            skillTargetKeys={[]}
            onGrantChange={() => {}}
            onSkillChange={() => {}}
            defaultCapIds={['cap-search', 'cap-http']}
            grantCapabilities={[]}
          />
        </Form>
      );
    };

    render(<Harness />);

    // 相同时只有一个技术标识
    expect(screen.getAllByText('search_tools')).toHaveLength(1);
    // 中文名与技术标识不同时两者都保留
    expect(screen.getByText('HTTP 请求')).toBeTruthy();
    expect(screen.getByText('http_fetch')).toBeTruthy();
  });
});
