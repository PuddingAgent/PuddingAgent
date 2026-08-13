import {
  Alert,
  Button,
  Descriptions,
  Empty,
  Form,
  Input,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useEffect, useState } from 'react';
import {
  appendEdgeBindingDraft,
  buildEdgeReachabilityDiagnostics,
  buildEdgeRoutingPreview,
  patchEdgeBindingDraft,
  patchEdgeDraft,
  patchEdgePredicateDraft,
  predicateFormToModel,
  predicateModelToForm,
  removeEdgeBindingDraft,
  removeEdgeFromDraft,
  removeEdgePredicateFromDraft,
  resolveEdgeSourceContract,
} from './edgeEditor';
import type {
  OrchestrationCatalog,
  OrchestrationDataContract,
  OrchestrationEdgeDefinition,
  OrchestrationEdgePredicate,
  OrchestrationGraphDefinition,
} from './types';

const { Text } = Typography;

interface EdgeInspectorProps {
  definition: OrchestrationGraphDefinition;
  edgeId?: string;
  disabled?: boolean;
  catalog?: OrchestrationCatalog;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
  onDeleted: () => void;
}

function formatContract(contract: OrchestrationDataContract | undefined): string {
  if (!contract) return '—';
  return `${contract.dataType} · ${contract.cardinality} · ${contract.deliveries.join('/')}${contract.mediaTypes.length > 0 ? ` · ${contract.mediaTypes.join(',')}` : ''}`;
}

/**
 * Field-level editable predicate picker (doc 84 §8.4:281-292, doc 83 §12.3).
 * Structured six-field input with basic format validation; no registry API and
 * no free string expressions (doc 82 §10). Every edit is written through
 * patchEdgePredicateDraft (draft CRUD).
 */
const PredicatePicker: React.FC<{
  definition: OrchestrationGraphDefinition;
  edge: OrchestrationEdgeDefinition;
  disabled?: boolean;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
}> = ({ definition, edge, disabled, onDefinitionChange }) => {
  const predicate = edge.predicate;
  const form = predicateModelToForm(predicate);
  const [parametersText, setParametersText] = useState(form.parametersText);
  const parametersKey = JSON.stringify(predicate?.parameters ?? {});

  // Re-sync the JSON textarea only when the *committed* parameters change, so
  // an in-progress invalid JSON draft is never clobbered by unrelated edits.
  useEffect(() => {
    setParametersText(
      JSON.stringify(predicate?.parameters ?? {}, null, 2) || '{}',
    );
  }, [parametersKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const patchPredicate = (patch: Partial<OrchestrationEdgePredicate>) => {
    onDefinitionChange(
      patchEdgePredicateDraft(definition, edge.edgeId, patch),
    );
  };

  const handleParametersChange = (text: string) => {
    setParametersText(text);
    const candidate = predicateFormToModel({
      ...form,
      parametersText: text,
    });
    if (candidate.issues.length === 0) {
      patchPredicate({ parameters: candidate.predicate.parameters });
    }
  };

  const result = predicateFormToModel({
    ...form,
    parametersText,
  });

  return (
    <>
      <Alert
        type="info"
        showIcon
        message="受管谓词（字段级编辑）"
        description="谓词由注册表治理；这里只做结构化字段输入与格式校验，不支持任意脚本或表达式。Registry/版本化解析由 S4 阶段开放。"
        style={{ marginTop: 10 }}
      />
      <Form layout="vertical" style={{ marginTop: 10 }}>
        <Form.Item label="evaluatorId（必填）" style={{ marginBottom: 8 }}>
          <Input
            value={predicate?.evaluatorId ?? ''}
            disabled={disabled}
            placeholder="例如 pudding.schema.gate"
            onChange={(event) => patchPredicate({ evaluatorId: event.target.value })}
          />
        </Form.Item>
        <Form.Item label="version（必填）" style={{ marginBottom: 8 }}>
          <Input
            value={predicate?.version ?? ''}
            disabled={disabled}
            placeholder="例如 1"
            onChange={(event) => patchPredicate({ version: event.target.value })}
          />
        </Form.Item>
        <Form.Item label="contractHash（可选）" style={{ marginBottom: 8 }}>
          <Input
            value={predicate?.contractHash ?? ''}
            disabled={disabled}
            placeholder="由注册表冻结，可留空"
            onChange={(event) =>
              patchPredicate({
                contractHash: event.target.value.trim() || undefined,
              })
            }
          />
        </Form.Item>
        <Form.Item label="sourcePortId（必填）" style={{ marginBottom: 8 }}>
          <Input
            value={predicate?.sourcePortId ?? ''}
            disabled={disabled}
            placeholder="上游输出端口 id"
            onChange={(event) =>
              patchPredicate({ sourcePortId: event.target.value })
            }
          />
        </Form.Item>
        <Form.Item label="sourcePath（必填）" style={{ marginBottom: 8 }}>
          <Input
            value={predicate?.sourcePath ?? '$'}
            disabled={disabled}
            placeholder="$"
            onChange={(event) =>
              patchPredicate({ sourcePath: event.target.value })
            }
          />
        </Form.Item>
        <Form.Item label="parameters（JSON 对象）" style={{ marginBottom: 8 }}>
          <Input.TextArea
            rows={4}
            value={parametersText}
            disabled={disabled}
            onChange={(event) => handleParametersChange(event.target.value)}
            style={{ fontFamily: 'monospace', fontSize: 12 }}
          />
        </Form.Item>
      </Form>
      {result.issues.length > 0 ? (
        <Alert
          type="warning"
          showIcon
          message="谓词格式校验未通过"
          description={
            <ul style={{ margin: 0, paddingInlineStart: 20 }}>
              {result.issues.map((issue) => (
                <li key={issue.code}>
                  <Text type="secondary">
                    {issue.code} · {issue.message}
                  </Text>
                </li>
              ))}
            </ul>
          }
          style={{ marginTop: 8 }}
        />
      ) : null}
      <Button
        size="small"
        danger
        disabled={disabled}
        style={{ marginTop: 8 }}
        onClick={() =>
          onDefinitionChange(
            removeEdgePredicateFromDraft(definition, edge.edgeId),
          )
        }
      >
        移除谓词
      </Button>
    </>
  );
};

/**
 * Data edge form (doc 84 §8.3:271-279): editable binding fields plus a read-only
 * resolved contract summary and a sample-value preview that never reads
 * sensitive Artifact content.
 */
const DataEdgeForm: React.FC<{
  definition: OrchestrationGraphDefinition;
  edge: OrchestrationEdgeDefinition;
  disabled?: boolean;
  catalog?: OrchestrationCatalog;
  onDefinitionChange: (definition: OrchestrationGraphDefinition) => void;
}> = ({ definition, edge, disabled, catalog, onDefinitionChange }) => {
  const resolved = resolveEdgeSourceContract(definition, catalog, edge);
  const patchBinding = (
    bindingIndex: number,
    patch: Parameters<typeof patchEdgeBindingDraft>[3],
  ) =>
    onDefinitionChange(
      patchEdgeBindingDraft(definition, edge.edgeId, bindingIndex, patch),
    );

  return (
    <>
      {edge.bindings.length === 0 ? (
        <Alert
          type="warning"
          showIcon
          message="该 data edge 尚无绑定"
          description="请添加绑定并填写源/目标端口。"
          style={{ marginTop: 10 }}
        />
      ) : null}
      {edge.bindings.map((binding, index) => (
        <div
          key={`${edge.edgeId}-binding-${index}`}
          style={{
            border: '1px solid rgba(5,5,5,0.06)',
            borderRadius: 6,
            padding: 8,
            marginTop: 10,
          }}
        >
          <Space
            align="center"
            style={{ marginBottom: 6, justifyContent: 'space-between', width: '100%' }}
          >
            <Text strong>绑定 #{index + 1}</Text>
            <Button
              size="small"
              danger
              disabled={disabled || edge.bindings.length <= 1}
              onClick={() =>
                onDefinitionChange(
                  removeEdgeBindingDraft(definition, edge.edgeId, index),
                )
              }
            >
              移除
            </Button>
          </Space>
          <Form layout="vertical" style={{ marginTop: 4 }}>
            <Form.Item label="sourcePortId" style={{ marginBottom: 8 }}>
              <Input
                value={binding.sourcePortId}
                disabled={disabled}
                onChange={(event) =>
                  patchBinding(index, { sourcePortId: event.target.value })
                }
              />
            </Form.Item>
            <Form.Item label="sourcePath" style={{ marginBottom: 8 }}>
              <Input
                value={binding.sourcePath}
                disabled={disabled}
                onChange={(event) =>
                  patchBinding(index, { sourcePath: event.target.value })
                }
              />
            </Form.Item>
            <Form.Item label="targetPortId" style={{ marginBottom: 8 }}>
              <Input
                value={binding.targetPortId}
                disabled={disabled}
                onChange={(event) =>
                  patchBinding(index, { targetPortId: event.target.value })
                }
              />
            </Form.Item>
            <Form.Item label="targetKey（可选）" style={{ marginBottom: 8 }}>
              <Input
                value={binding.targetKey ?? ''}
                disabled={disabled}
                onChange={(event) =>
                  patchBinding(index, {
                    targetKey: event.target.value.trim() || undefined,
                  })
                }
              />
            </Form.Item>
            <Form.Item label="聚合" style={{ marginBottom: 8 }}>
              <Select
                value={binding.aggregation}
                disabled={disabled}
                options={[
                  { value: 'replace', label: 'replace（单值覆盖）' },
                  { value: 'append', label: 'append（追加到多值）' },
                ]}
                onChange={(aggregation) => patchBinding(index, { aggregation })}
              />
            </Form.Item>
          </Form>
        </div>
      ))}
      <Button
        size="small"
        disabled={disabled}
        style={{ marginTop: 8 }}
        onClick={() =>
          onDefinitionChange(appendEdgeBindingDraft(definition, edge.edgeId))
        }
      >
        添加绑定
      </Button>
      <Descriptions
        size="small"
        column={1}
        bordered
        style={{ marginTop: 12 }}
      >
        <Descriptions.Item label="解析后的源契约">
          {resolved.source
            ? `${formatContract(resolved.source)}${resolved.sourcePortName ? `（${resolved.sourcePortName}）` : ''}`
            : '—（Catalog 中未解析）'}
        </Descriptions.Item>
        <Descriptions.Item label="解析后的目标契约">
          {resolved.target
            ? `${formatContract(resolved.target)}${resolved.targetPortName ? `（${resolved.targetPortName}）` : ''}`
            : '—（Catalog 中未解析）'}
        </Descriptions.Item>
        <Descriptions.Item label="示例值预览">
          <Text type="secondary">
            示例值在运行后由输出快照提供；草稿/定义预览不读取敏感 Artifact
            内容。
          </Text>
        </Descriptions.Item>
      </Descriptions>
    </>
  );
};

const EdgeInspector: React.FC<EdgeInspectorProps> = ({
  definition,
  edgeId,
  disabled,
  catalog,
  onDefinitionChange,
  onDeleted,
}) => {
  const edge = definition.edges.find((item) => item.edgeId === edgeId);
  if (!edge) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="选择画布中的连线"
      />
    );
  }
  const isControl = edge.kind === 'control';
  const reachabilityIssues = isControl
    ? buildEdgeReachabilityDiagnostics(definition, edge)
    : [];
  const routingPreview = isControl ? buildEdgeRoutingPreview(edge) : [];

  return (
    <>
      <Space wrap style={{ marginBottom: 10 }}>
        <Tag color={edge.kind === 'data' ? 'purple' : 'blue'}>{edge.kind}</Tag>
        <Text code>{edge.edgeId}</Text>
      </Space>
      <Descriptions size="small" column={1} bordered>
        <Descriptions.Item label="源节点">{edge.fromNodeId}</Descriptions.Item>
        <Descriptions.Item label="目标节点">{edge.toNodeId}</Descriptions.Item>
      </Descriptions>
      <Form layout="vertical" style={{ marginTop: 12 }}>
        <Form.Item label="触发条件" style={{ marginBottom: 10 }}>
          <Select
            value={edge.condition}
            disabled={disabled}
            options={[
              { value: 'onSuccess', label: '上游成功（onSuccess）' },
              { value: 'onCompletion', label: '上游完成（onCompletion）' },
              { value: 'always', label: '始终（always）' },
            ]}
            onChange={(condition) =>
              onDefinitionChange(
                patchEdgeDraft(definition, edge.edgeId, { condition }),
              )
            }
          />
        </Form.Item>
      </Form>
      {isControl ? (
        <>
          {edge.predicate ? (
            <PredicatePicker
              key={`${edge.edgeId}:predicate`}
              definition={definition}
              edge={edge}
              disabled={disabled}
              onDefinitionChange={onDefinitionChange}
            />
          ) : (
            <Button
              size="small"
              disabled={disabled}
              style={{ marginTop: 10 }}
              onClick={() =>
                onDefinitionChange(
                  patchEdgePredicateDraft(definition, edge.edgeId, {}),
                )
              }
            >
              添加谓词
            </Button>
          )}
          {routingPreview.length > 0 ? (
            <Alert
              type="info"
              showIcon
              message="失败/跳过说明预览"
              description={
                <ul style={{ margin: 0, paddingInlineStart: 20 }}>
                  {routingPreview.map((line, index) => (
                    <li key={`${edge.edgeId}-preview-${index}`}>
                      <Text type="secondary">{line}</Text>
                    </li>
                  ))}
                </ul>
              }
              style={{ marginTop: 10 }}
            />
          ) : null}
          <Alert
            type={reachabilityIssues.length > 0 ? 'warning' : 'success'}
            showIcon
            message="可达性诊断"
            description={
              reachabilityIssues.length > 0 ? (
                <ul style={{ margin: 0, paddingInlineStart: 20 }}>
                  {reachabilityIssues.map((issue) => (
                    <li key={issue.code}>
                      <Text type="secondary">
                        {issue.code} · {issue.message}
                      </Text>
                    </li>
                  ))}
                </ul>
              ) : (
                <Text type="secondary">端点均从根节点可达。</Text>
              )
            }
            style={{ marginTop: 10 }}
          />
        </>
      ) : (
        <DataEdgeForm
          definition={definition}
          edge={edge}
          disabled={disabled}
          catalog={catalog}
          onDefinitionChange={onDefinitionChange}
        />
      )}
      <Form layout="vertical" style={{ marginTop: 12 }}>
        <Button
          danger
          size="small"
          disabled={disabled}
          onClick={() => {
            onDefinitionChange(removeEdgeFromDraft(definition, edge.edgeId));
            onDeleted();
          }}
        >
          删除连线
        </Button>
      </Form>
    </>
  );
};

export default EdgeInspector;
