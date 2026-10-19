import { Button, Checkbox, Input, Radio, Select } from 'antd';
import React, { useState } from 'react';
import type { SurveyField, SurveySpec } from './spec';
import type { UiSubmissionPayload } from './submission';
import { isSurveyReady, validateSurveyValues } from './surveyValues';

/**
 * 问卷面板（`kind: 'survey'`）。
 *
 * 只用**已有依赖 antd**，不引入新库；range 用原生 input（antd Slider 依赖
 * ResizeObserver，在 jsdom 与沙箱环境里都不稳）。
 *
 * 提交语义：只有 **显式点提交** 才回传一次，输入过程中不产生任何消息——
 * 否则每敲一个字都是一次模型调用，既费 token 又打断。
 */
export interface SurveyPanelProps {
  spec: SurveySpec;
  onSubmit: (payload: UiSubmissionPayload) => void;
  disabled?: boolean;
}

const fieldId = (key: string) => `survey-field-${key}`;

export const SurveyPanel: React.FC<SurveyPanelProps> = ({ spec, onSubmit, disabled }) => {
  const [values, setValues] = useState<Record<string, unknown>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});

  const setValue = (key: string, value: unknown) => {
    setValues((previous) => ({ ...previous, [key]: value }));
    // 改了就清掉该字段的错误，避免"改完了还红着"。
    setErrors((previous) => {
      if (!previous[key]) return previous;
      const next = { ...previous };
      delete next[key];
      return next;
    });
  };

  const handleSubmit = () => {
    if (disabled) return;
    const result = validateSurveyValues(spec, values);
    setErrors(result.errors);
    if (!result.ok) return;
    onSubmit({ panelId: spec.id, kind: 'survey', values: result.values });
    if (spec.resetOnSubmit !== false) setValues({});
  };

  const ready = isSurveyReady(spec, values);

  return (
    <div data-testid="panel-survey">
      {spec.title ? <h4 style={{ margin: '0 0 4px' }}>{spec.title}</h4> : null}
      {spec.description ? (
        <p style={{ margin: '0 0 12px', fontSize: 12, opacity: 0.7 }}>{spec.description}</p>
      ) : null}

      {spec.fields.map((field) => (
        <div key={field.key} data-testid={fieldId(field.key)} style={{ marginBottom: 12 }}>
          <label style={{ display: 'block', fontSize: 12, marginBottom: 4 }}>
            {field.label}
            {field.required ? <span style={{ color: '#d4380d' }}> *</span> : null}
          </label>
          {renderControl(field, values[field.key], (value) => setValue(field.key, value))}
          {errors[field.key] ? (
            <div
              data-testid={`${fieldId(field.key)}-error`}
              style={{ color: '#d4380d', fontSize: 12, marginTop: 2 }}
            >
              {errors[field.key]}
            </div>
          ) : null}
        </div>
      ))}

      <Button
        type="primary"
        data-testid="panel-survey-submit"
        disabled={disabled || !ready}
        onClick={handleSubmit}
      >
        {spec.submitLabel ?? '提交'}
      </Button>
    </div>
  );
};

function renderControl(
  field: SurveyField,
  value: unknown,
  onChange: (value: unknown) => void,
): React.ReactNode {
  const options = field.options ?? [];
  switch (field.type) {
    case 'textarea':
      return (
        <Input.TextArea
          value={typeof value === 'string' ? value : ''}
          placeholder={field.placeholder}
          maxLength={field.maxLength}
          autoSize={{ minRows: 2, maxRows: 6 }}
          onChange={(event) => onChange(event.target.value)}
        />
      );
    case 'number':
      return (
        <Input
          type="number"
          value={typeof value === 'number' ? String(value) : ''}
          placeholder={field.placeholder}
          min={field.min}
          max={field.max}
          step={field.step}
          onChange={(event) => onChange(event.target.value === '' ? undefined : Number(event.target.value))}
        />
      );
    case 'range':
      return (
        <input
          type="range"
          style={{ width: '100%' }}
          min={field.min ?? 0}
          max={field.max ?? 100}
          step={field.step ?? 1}
          value={typeof value === 'number' ? value : field.min ?? 0}
          onChange={(event) => onChange(Number(event.target.value))}
        />
      );
    case 'select':
      return (
        <Select
          style={{ width: '100%' }}
          value={typeof value === 'string' ? value : undefined}
          placeholder={field.placeholder}
          options={options}
          onChange={onChange}
        />
      );
    case 'radio':
      return (
        <Radio.Group
          value={typeof value === 'string' ? value : undefined}
          options={options}
          onChange={(event) => onChange(event.target.value)}
        />
      );
    case 'checkbox':
      return (
        <Checkbox.Group
          value={Array.isArray(value) ? (value as string[]) : []}
          options={options}
          onChange={onChange}
        />
      );
    case 'text':
    default:
      return (
        <Input
          value={typeof value === 'string' ? value : ''}
          placeholder={field.placeholder}
          maxLength={field.maxLength}
          onChange={(event) => onChange(event.target.value)}
        />
      );
  }
}

export default SurveyPanel;
