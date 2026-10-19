import { Button, Segmented } from 'antd';
import React, { useMemo, useState } from 'react';
import { DocumentRefCard } from './components/DocumentRefCard';
import { FoldableMessage } from './components/FoldableMessage';
import { PanelView } from './panel/PanelView';
import type { PanelSpec } from './panel/spec';
import { buildUiSubmission, type UiSubmissionPayload } from './panel/submission';
import { extractDocumentRefs } from './markdown/documentRefs';

/**
 * 面板组件演示页（`/chat/panel-demo`）。
 *
 * 目的：用**真实组件**跑一遍完整数据，而不是手写 HTML 原型。
 * 这里不接后端、不发消息，Submit 只把协议文本打到页面上——
 * 能看清"面板回传长什么样"，又不依赖 Core 是否在跑。
 *
 * 这个页面是开发期工具，不承担产品功能。
 */

const longReport = [
  '# 温度巡检报告 · 2026-10-19',
  '',
  '## 一、结论',
  '',
  '客厅 **26.1°C** 为全屋最高，超过阈值 0.1°C；主卧 **22.4°C** 较昨日同期下降 2.3°C，需确认是否开窗。',
  '',
  '## 二、异常',
  '',
  '| 房间 | 当前 | 较昨日 | 持续 |',
  '| --- | --- | --- | --- |',
  '| 客厅 | 26.1°C | +0.4 | 42 分钟 |',
  '| 主卧 | 22.4°C | -2.3 | 3 小时 |',
  '',
  '## 三、建议',
  '',
  '1. 客厅开窗或降温 30 分钟；',
  '2. 主卧确认窗户状态。',
].join('\n');

const surveySpec: PanelSpec = {
  v: 1,
  id: 'demo-survey',
  kind: 'survey',
  title: '偏好收集',
  description: '提交后作为一条用户消息回到对话；我只在你点提交时收到。',
  fields: [
    {
      key: 'rooms',
      label: '关注房间（可多选）',
      type: 'checkbox',
      required: true,
      options: [
        { value: 'living', label: '客厅' },
        { value: 'bed', label: '主卧' },
        { value: 'study', label: '书房' },
        { value: 'kitchen', label: '厨房' },
      ],
    },
    { key: 'threshold', label: '告警阈值（°C）', type: 'range', min: 18, max: 32, step: 1 },
    { key: 'note', label: '补充说明', type: 'textarea', maxLength: 200 },
  ],
  submitLabel: '提交偏好',
};

const variantsSpec: PanelSpec = {
  v: 1,
  id: 'demo-variants',
  kind: 'variants',
  title: '登录页方案对比',
  description: '点选一个方案；红框是标注示例。',
  layout: 3,
  selectMode: 'single',
  variants: [
    {
      id: 'A',
      title: '极简',
      html:
        '<div style="padding:16px;font-family:system-ui">' +
        '<div style="height:8px;width:40%;background:#e3e7ee;border-radius:4px;margin-bottom:10px"></div>' +
        '<div style="height:8px;width:90%;background:#e3e7ee;border-radius:4px;margin-bottom:6px"></div>' +
        '<div style="height:8px;width:70%;background:#e3e7ee;border-radius:4px;margin-bottom:16px"></div>' +
        '<div style="height:28px;background:#3370ff;border-radius:6px"></div>' +
        '</div>',
    },
    {
      id: 'B',
      title: '品牌重',
      html:
        '<div style="padding:16px;font-family:system-ui;background:linear-gradient(135deg,#1f2329,#3b4a6b);height:100%">' +
        '<div style="height:8px;width:40%;background:#5b6b8f;border-radius:4px;margin-bottom:10px"></div>' +
        '<div style="height:8px;width:80%;background:#5b6b8f;border-radius:4px;margin-bottom:16px"></div>' +
        '<div style="height:28px;background:#e8f0ff;border-radius:6px"></div>' +
        '</div>',
    },
    {
      id: 'C',
      title: '引导式',
      svg:
        '<svg viewBox="0 0 100 80" xmlns="http://www.w3.org/2000/svg">' +
        '<rect x="8" y="10" width="84" height="6" rx="3" fill="#e3e7ee"/>' +
        '<rect x="8" y="26" width="60" height="6" rx="3" fill="#e3e7ee"/>' +
        '<rect x="8" y="44" width="38" height="14" rx="4" fill="#cfdcff"/>' +
        '<rect x="52" y="44" width="38" height="14" rx="4" fill="#cfdcff"/>' +
        '</svg>',
    },
  ],
};

const draftSpec: PanelSpec = {
  v: 1,
  id: 'demo-draft',
  kind: 'draft',
  title: '草稿预览（沙箱）',
  description: 'HTML 草稿跑在 sandbox="" iframe 里：禁脚本、禁同源。',
  format: 'html',
  source:
    '<div style="font-family:system-ui;padding:20px">' +
    '<h3 style="margin:0 0 8px">登录</h3>' +
    '<p style="color:#8a9099;font-size:12px;margin:0 0 14px">这一段即使带 &lt;script&gt; 也不会执行。</p>' +
    '<script>document.body.innerHTML = "SHOULD NOT RUN";</script>' +
    '<input placeholder="手机号" style="width:100%;padding:8px;border:1px solid #e5e7eb;border-radius:8px;margin-bottom:8px"/>' +
    '<button style="width:100%;padding:9px;background:#3370ff;color:#fff;border:0;border-radius:8px">继续</button>' +
    '</div>',
};

const SPECS: Array<{ label: string; spec: PanelSpec }> = [
  { label: '问卷', spec: surveySpec },
  { label: '方案对比', spec: variantsSpec },
  { label: '草稿', spec: draftSpec },
];

export default function PanelDemoPage() {
  const [tab, setTab] = useState<string>('问卷');
  const [lastSubmission, setLastSubmission] = useState<string>('（还没有提交）');

  const active = useMemo(
    () => SPECS.find((item) => item.label === tab)?.spec ?? surveySpec,
    [tab],
  );

  const refs = useMemo(() => extractDocumentRefs(longReport), []);
  const [openedRef, setOpenedRef] = useState<string>('');

  const handleSubmit = (payload: UiSubmissionPayload) => {
    setLastSubmission(buildUiSubmission(payload));
  };

  return (
    <div style={{ display: 'flex', gap: 12, padding: 12, background: '#f2f3f5', minHeight: '100vh' }}>
      {/* 左：模拟对话 */}
      <section
        style={{
          flex: '1 1 55%',
          background: '#fff',
          border: '1px solid #e5e7eb',
          borderRadius: 14,
          padding: 16,
          display: 'flex',
          flexDirection: 'column',
          gap: 14,
          minWidth: 0,
        }}
      >
        <div style={{ fontSize: 12, color: '#8a9099' }}>Chat（真实渲染链）</div>

        <div style={{ alignSelf: 'flex-end', background: '#eaf2ff', padding: '10px 12px', borderRadius: 12 }}>
          看下家里各房间温度，最近 24 小时。
        </div>

        <div style={{ background: '#f7f8fa', padding: '10px 12px', borderRadius: 12 }}>
          客厅 <b>26.1°C</b> 最高，主卧比昨天同期低 <b>2.3°C</b>，其余平稳。
        </div>

        <div>
          <div style={{ fontSize: 12, color: '#8a9099', marginBottom: 6 }}>
            默认助手（长消息自动折叠）
          </div>
          <FoldableMessage
            markdown={longReport}
            onOpenInSide={() => setOpenedRef('已请求在右侧打开全文')}
          >
            <pre style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{longReport}</pre>
          </FoldableMessage>
        </div>

        <div>
          <div style={{ fontSize: 12, color: '#8a9099', marginBottom: 6 }}>
            文档引用（行内 code 识别）
          </div>
          {refs.length === 0 ? (
            <div style={{ fontSize: 12, color: '#8a9099' }}>
              本段没有行内 code 引用；看下面的静态示例：
              <DocumentRefCard
                ref={{
                  kind: 'workspace',
                  raw: 'Docs/12_features/聊天富媒体组件设计方案-2026-10-19.md',
                  target: 'Docs/12_features/聊天富媒体组件设计方案-2026-10-19.md',
                  ext: 'md',
                }}
                meta="31 KB"
                onOpen={(ref) => setOpenedRef(`打开：${ref.target}`)}
              />
            </div>
          ) : (
            refs.map((ref) => (
              <DocumentRefCard
                key={`${ref.kind}:${ref.target}`}
                ref={ref}
                onOpen={(item) => setOpenedRef(`打开：${item.target}`)}
              />
            ))
          )}
        </div>

        {(openedRef || lastSubmission !== '（还没有提交）') && (
          <div
            data-testid="demo-output"
            style={{
              marginTop: 'auto',
              borderTop: '1px solid #e5e7eb',
              paddingTop: 12,
              fontSize: 12,
              color: '#5b6169',
            }}
          >
            {openedRef ? <div>↳ {openedRef}</div> : null}
            {lastSubmission !== '（还没有提交）' ? (
              <pre
                style={{
                  background: '#f7f8fa',
                  padding: 10,
                  borderRadius: 8,
                  maxHeight: 220,
                  overflow: 'auto',
                  marginTop: 8,
                  whiteSpace: 'pre-wrap',
                }}
              >
                {lastSubmission}
              </pre>
            ) : null}
          </div>
        )}
      </section>

      {/* 右：面板 */}
      <section
        style={{
          flex: '1 1 45%',
          background: '#fff',
          border: '1px solid #e5e7eb',
          borderRadius: 14,
          padding: 16,
          minWidth: 0,
          overflow: 'auto',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 14 }}>
          <span style={{ fontSize: 12, color: '#8a9099' }}>交互面板（真实组件）</span>
          <Segmented
            size="small"
            value={tab}
            options={SPECS.map((item) => item.label)}
            onChange={(value) => setTab(String(value))}
          />
        </div>

        <PanelView spec={active} onSubmit={handleSubmit} />

        <div style={{ marginTop: 24, borderTop: '1px solid #e5e7eb', paddingTop: 12 }}>
          <div style={{ fontSize: 12, color: '#8a9099', marginBottom: 6 }}>
            最近一次回传（协议原文）
          </div>
          <pre
            style={{
              background: '#f7f8fa',
              padding: 10,
              borderRadius: 8,
              fontSize: 12,
              maxHeight: 200,
              overflow: 'auto',
              whiteSpace: 'pre-wrap',
            }}
          >
            {lastSubmission}
          </pre>
          <Button size="small" onClick={() => setLastSubmission('（还没有提交）')}>
            清空
          </Button>
        </div>
      </section>
    </div>
  );
}
