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
            initialValue={2400}
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
        label="运行环境"
        placeholder="宿主模式暂不使用，留空即可"
      />
    </section>
  );
};

export default GuardrailSection;
