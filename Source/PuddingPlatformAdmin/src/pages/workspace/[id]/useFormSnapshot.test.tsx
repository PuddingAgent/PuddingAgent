import { act, render, screen } from '@testing-library/react';
import { Form } from 'antd';
import * as React from 'react';

/**
 * 工作台的脏值判断依赖 `Form.useWatch([], { form, preserve: true })`：
 * Prompt 文档字段由受控编辑器写入、不渲染 Form.Item，默认的 getFieldsValue()
 * 不会返回它们。这里锁定该行为，避免升级 rc-field-form / antd 后脏值静默失效。
 */
describe('Form.useWatch 全量草稿快照', () => {
  const Harness: React.FC<{ onChange: (values: Record<string, unknown>) => void }> = ({
    onChange,
  }) => {
    const [form] = Form.useForm();
    const values = Form.useWatch([], { form, preserve: true }) as
      | Record<string, unknown>
      | undefined;
    React.useEffect(() => {
      if (values) onChange(values);
    }, [onChange, values]);

    return (
      <Form form={form}>
        <Form.Item name="name" label="name">
          <input aria-label="name" />
        </Form.Item>
        <button
          type="button"
          onClick={() => form.setFieldValue('soulMdContent', '未挂载字段的草稿')}
        >
          set unregistered
        </button>
        <output data-testid="snapshot">{JSON.stringify(values ?? {})}</output>
      </Form>
    );
  };

  it('包含未挂载字段，并在 setFieldValue 后更新', async () => {
    const snapshots: Record<string, unknown>[] = [];
    render(<Harness onChange={(values) => snapshots.push(values)} />);

    await act(async () => {
      screen.getByRole('button', { name: 'set unregistered' }).click();
    });

    const latest = snapshots[snapshots.length - 1];
    expect(latest.soulMdContent).toBe('未挂载字段的草稿');
    expect(screen.getByTestId('snapshot').textContent).toContain('未挂载字段的草稿');
  });
});
