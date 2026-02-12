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
      <div className={styles.sectionTitle}>角色定义</div>

      <ProFormTextArea
        name="systemPrompt"
        label="模板角色定义"
        rows={8}
        placeholder="定义这一类 Agent 的核心职责、能力边界和行为准则…"
      />

      <ProFormTextArea
        name="personaPrompt"
        label="默认语气与边界（SOUL）"
        rows={4}
        placeholder="定义模板默认表达风格和边界；具体 Agent 可在实例中覆盖"
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
        name="agentsPrompt"
        label="子 Agent 协作规范（AGENTS.md）"
        rows={5}
        placeholder="定义多 Agent 协作时的角色职责、边界与交付约束"
      />

      <ProFormTextArea
        name="memoryPrompt"
        label="记忆策略（MEMORY.md）"
        rows={5}
        placeholder="定义记忆写入、检索、压缩与优先级策略"
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
