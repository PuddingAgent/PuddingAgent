import React from 'react';
import { ProFormTextArea } from '@ant-design/pro-components';
import { useStyles } from '../styles';

export interface PromptPersonaSectionProps {
  id: string;
}

const PromptPersonaSection: React.FC<PromptPersonaSectionProps> = ({ id }) => {
  const { styles } = useStyles();

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>Prompt 与个性</div>

      <ProFormTextArea
        name="systemPrompt"
        label="系统 Prompt"
        rows={8}
        placeholder="输入 Agent 的角色定义和行为准则…"
      />

      <ProFormTextArea
        name="personaPrompt"
        label="人设 / 语气 / 边界（SOUL）"
        rows={4}
        placeholder="定义该 Agent 的表达风格、价值观边界与行为准则"
      />

      <ProFormTextArea
        name="toolsDescription"
        label="工具使用约定（TOOLS）"
        rows={4}
        placeholder="约定何时调用工具、如何解释结果、失败时如何降级"
      />

      <ProFormTextArea
        name="bootstrapTemplate"
        label="首次引导模板（BOOTSTRAP）"
        rows={5}
        placeholder="定义首次对话的开场与引导模板"
      />

      <ProFormTextArea
        name="userPromptTemplate"
        label="用户 Prompt 模板"
        rows={3}
        placeholder="可选，支持 {{variable}} 占位符"
      />
    </section>
  );
};

export default PromptPersonaSection;
