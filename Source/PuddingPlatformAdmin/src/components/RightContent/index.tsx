import { QuestionCircleOutlined } from '@ant-design/icons';
import { SelectLang as UmiSelectLang } from '@umijs/max';
import React from 'react';

export type SiderTheme = 'light' | 'dark';

/**
 * 帮助入口的唯一真源（IMG03：低频项收进「更多」）。
 * 内联问号图标 `Question` 与 Chat 顶栏「更多」里的帮助菜单项都引用它 —— 两处各写一份
 * URL 迟早会漂移，而"帮助指向哪里"恰恰是用户最容易察觉的那类不一致。
 */
export const PUDDING_HELP_URL = 'https://github.com/PuddingAgent/PuddingAgent';

export const SelectLang: React.FC = () => {
  return (
    <UmiSelectLang
      style={{
        padding: 4,
      }}
    />
  );
};

export const Question: React.FC = () => {
  return (
    <a
      href={PUDDING_HELP_URL}
      target="_blank"
      rel="noreferrer"
      style={{
        display: 'inline-flex',
        padding: '4px',
        fontSize: '18px',
        color: 'inherit',
      }}
    >
      <QuestionCircleOutlined />
    </a>
  );
};
