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
    Transfer: ({ targetKeys = [], onChange, titles }: any) => (
      <button
        type="button"
        data-testid={`transfer-${titles?.[1] ?? 'target'}`}
        onClick={() => onChange([...targetKeys, titles?.[1] === '已授权' ? 'cap-python' : 'skill-a'])}
      >
        move
      </button>
    ),
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

    fireEvent.click(screen.getByTestId('transfer-已授权'));
    fireEvent.click(screen.getByTestId('transfer-已选'));
    fireEvent.click(screen.getByText('save'));

    await waitFor(() => expect(saved).toHaveLength(1));
    expect(saved[0].selectedCapabilityIds).toEqual(['cap-http-fetch', 'cap-python']);
    expect(saved[0].selectedSkillPackageIds).toEqual(['skill-a']);
  });
});
