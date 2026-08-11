import { Image, Space, Typography } from 'antd';
import React from 'react';
import { getVisionArtifactUrl } from './api';
import type { OrchestrationFlowNodeData } from './graphViewModel';
import ImageGenerateNodeSettings from './ImageGenerateNodeSettings';
import SubAgentNodeSettings from './SubAgentNodeSettings';
import type {
  OrchestrationExecutorBinding,
  OrchestrationNodeDefinition,
  OrchestrationNodeRunSnapshot,
  OrchestrationValueEnvelope,
} from './types';

const { Paragraph, Text } = Typography;

export const IMAGE_GENERATE_COMPONENT = 'pudding.media.image-generate';
export const IMAGE_PREVIEW_COMPONENT = 'pudding.media.image-preview';
export const SUB_AGENT_COMPONENT = 'pudding.agent.subagent';

interface ComponentUiDefinition {
  renderNodeOutput?: (data: OrchestrationFlowNodeData) => React.ReactNode;
  renderInspectorSettings?: (
    props: ComponentInspectorSettingsProps,
  ) => React.ReactNode;
  renderInspectorOutput?: (
    props: ComponentInspectorOutputProps,
  ) => React.ReactNode;
}

export interface ComponentInspectorSettingsProps {
  node: OrchestrationNodeDefinition;
  workspaceId: string;
  disabled: boolean;
  onConfigurationChange: (configuration: Record<string, unknown>) => void;
  onExecutorChange: (executor: OrchestrationExecutorBinding) => void;
}

export interface ComponentInspectorOutputProps {
  componentType: string;
  workspaceId: string;
  run: OrchestrationNodeRunSnapshot;
}

function ArtifactImage({
  workspaceId,
  artifactReference,
  compact,
}: {
  workspaceId: string;
  artifactReference: string;
  compact?: boolean;
}) {
  return (
    <Image
      src={getVisionArtifactUrl(workspaceId, artifactReference)}
      alt={`Image artifact ${artifactReference}`}
      preview={!compact}
      draggable={false}
      style={{
        display: 'block',
        width: '100%',
        maxHeight: compact ? 132 : 320,
        objectFit: 'contain',
        borderRadius: 6,
        background: 'rgba(128,128,128,.08)',
      }}
    />
  );
}

function renderImageNodeOutput(data: OrchestrationFlowNodeData) {
  if (!data.artifactReference && !data.outputSummary) return null;
  return (
    <div
      className="nodrag"
      style={{
        marginTop: 9,
        paddingTop: 8,
        borderTop: '1px solid rgba(128,128,128,.22)',
      }}
    >
      {data.artifactReference ? (
        <ArtifactImage
          workspaceId={data.workspaceId}
          artifactReference={data.artifactReference}
          compact
        />
      ) : null}
      {data.outputSummary ? (
        <Text
          type="secondary"
          ellipsis={{ tooltip: data.outputSummary }}
          style={{ display: 'block', marginTop: 6, fontSize: 10 }}
        >
          {data.outputSummary}
        </Text>
      ) : null}
    </div>
  );
}

function renderImageInspectorOutput({
  workspaceId,
  run,
}: ComponentInspectorOutputProps) {
  return (
    <Space direction="vertical" style={{ width: '100%', marginTop: 12 }}>
      {run.outputSummary ? (
        <Paragraph style={{ marginBottom: 0 }}>{run.outputSummary}</Paragraph>
      ) : null}
      {run.artifactReference ? (
        <>
          <ArtifactImage
            workspaceId={workspaceId}
            artifactReference={run.artifactReference}
          />
          <Text code copyable>
            {run.artifactReference}
          </Text>
        </>
      ) : null}
    </Space>
  );
}

function inlineText(envelope?: OrchestrationValueEnvelope): string | undefined {
  if (typeof envelope?.inlineValue === 'string') return envelope.inlineValue;
  if (envelope?.inlineValue === undefined) return undefined;
  return JSON.stringify(envelope.inlineValue, null, 2);
}

function renderSubAgentNodeOutput(data: OrchestrationFlowNodeData) {
  const result = inlineText(data.outputs?.result);
  if (!result && !data.outputSummary) return null;
  return (
    <div
      className="nodrag"
      style={{
        marginTop: 9,
        paddingTop: 8,
        borderTop: '1px solid rgba(128,128,128,.22)',
      }}
    >
      <Text
        style={{
          display: '-webkit-box',
          overflow: 'hidden',
          WebkitBoxOrient: 'vertical',
          WebkitLineClamp: 5,
          whiteSpace: 'pre-wrap',
          fontSize: 11,
          lineHeight: 1.45,
        }}
        title={result ?? data.outputSummary}
      >
        {result ?? data.outputSummary}
      </Text>
    </div>
  );
}

function renderSubAgentInspectorOutput({ run }: ComponentInspectorOutputProps) {
  const result = inlineText(run.outputs?.result);
  if (!result && !run.outputSummary) return null;
  return (
    <Space direction="vertical" style={{ width: '100%', marginTop: 12 }}>
      <Text strong>Result</Text>
      <Paragraph copyable style={{ marginBottom: 0, whiteSpace: 'pre-wrap' }}>
        {result ?? run.outputSummary}
      </Paragraph>
    </Space>
  );
}

const componentUiRegistry: Record<string, ComponentUiDefinition> = {
  [SUB_AGENT_COMPONENT]: {
    renderNodeOutput: renderSubAgentNodeOutput,
    renderInspectorSettings: (props) => <SubAgentNodeSettings {...props} />,
    renderInspectorOutput: renderSubAgentInspectorOutput,
  },
  [IMAGE_GENERATE_COMPONENT]: {
    renderNodeOutput: renderImageNodeOutput,
    renderInspectorSettings: (props) => (
      <ImageGenerateNodeSettings {...props} />
    ),
    renderInspectorOutput: renderImageInspectorOutput,
  },
  [IMAGE_PREVIEW_COMPONENT]: {
    renderNodeOutput: renderImageNodeOutput,
    renderInspectorOutput: renderImageInspectorOutput,
  },
};

export function ComponentNodeOutput({
  data,
}: {
  data: OrchestrationFlowNodeData;
}) {
  return (
    componentUiRegistry[data.componentType]?.renderNodeOutput?.(data) ?? null
  );
}

export function ComponentInspectorSettings(
  props: ComponentInspectorSettingsProps,
) {
  return (
    componentUiRegistry[
      props.node.component.componentType
    ]?.renderInspectorSettings?.(props) ?? null
  );
}

export function ComponentInspectorOutput(props: ComponentInspectorOutputProps) {
  const renderer =
    componentUiRegistry[props.componentType]?.renderInspectorOutput;
  if (renderer) return renderer(props);
  if (!props.run.outputSummary && !props.run.artifactReference) return null;
  return (
    <Space direction="vertical" style={{ width: '100%', marginTop: 12 }}>
      {props.run.outputSummary ? (
        <Paragraph style={{ marginBottom: 0 }}>
          {props.run.outputSummary}
        </Paragraph>
      ) : null}
      {props.run.artifactReference ? (
        <Text code copyable>
          {props.run.artifactReference}
        </Text>
      ) : null}
    </Space>
  );
}

export function hasComponentNodeOutput(componentType: string): boolean {
  return Boolean(componentUiRegistry[componentType]?.renderNodeOutput);
}
