import React from 'react';
import { Col, Row } from 'antd';
import { ProFormDigit, ProFormText } from '@ant-design/pro-components';
import { useStyles } from '../styles';

export interface GuardrailSectionProps {
  id: string;
}

const GuardrailSection: React.FC<GuardrailSectionProps> = ({ id }) => {
  const { styles } = useStyles();

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>执行护栏</div>

      {/* 数字字段三列布局 */}
      <Row gutter={16}>
        <Col xs={24} sm={12} md={8}>
          <ProFormDigit
            name="maxRounds"
            label="最大轮次"
            min={1}
            max={1000}
            initialValue={200}
          />
        </Col>
        <Col xs={24} sm={12} md={8}>
          <ProFormDigit
            name="maxElapsedSeconds"
            label="最大耗时(秒)"
            min={10}
            max={7200}
            initialValue={1200}
          />
        </Col>
        <Col xs={24} sm={12} md={8}>
          <ProFormDigit
            name="maxToolCallsTotal"
            label="最大工具调用"
            min={1}
            max={500}
            initialValue={100}
          />
        </Col>
      </Row>

      <ProFormText
        name="containerImage"
        label="容器镜像"
        placeholder="如 docker.xuanyuan.run/library/ubuntu:latest，留空则使用平台默认"
      />

      {/* Token 限制三列布局 */}
      <Row gutter={16}>
        <Col xs={24} sm={12} md={8}>
          <ProFormDigit
            name="maxContextTokens"
            label="上下文 tokens"
            min={1024}
            rules={[{ required: true, message: '请填写上下文 tokens' }]}
          />
        </Col>
        <Col xs={24} sm={12} md={8}>
          <ProFormDigit
            name="maxReplyTokens"
            label="最大回复 tokens"
            min={128}
            rules={[{ required: true, message: '请填写最大回复 tokens' }]}
          />
        </Col>
      </Row>
    </section>
  );
};

export default GuardrailSection;
