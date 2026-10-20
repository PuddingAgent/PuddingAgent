import React, { useEffect, useMemo, useState } from 'react';
import DocumentPreview from './chat/components/DocumentPreview';
import { loadDocumentContent, type LoadedDocument } from './chat/documents/documentSource';
import { parseDocPreviewQuery } from './chat/documents/documentPath';
import { createMarkdownDownload, triggerDownload } from './chat/markdown/download';

/**
 * `/doc-preview?path=…&ws=…` —— 供 Shell 工具区新开 tab（WebView2）加载的文档预览页。
 *
 * 为什么是独立路由而不是嵌进 chat 页：用户明确要求文档不再占用对话区的横向空间，
 * 改由 Shell 在自己的工具区开 tab。Shell 只需要知道这个 URL，不需要理解 React 结构。
 *
 * 页面职责刻意很窄：解析参数 → 加载 → 渲染。失败态给出**可行动的原因**，
 * 而不是空白页——空白页会被误读成"文件是空的"。
 */

type ViewState =
  | { kind: 'loading' }
  | { kind: 'ready'; document: LoadedDocument }
  | { kind: 'error'; reason: string; hint?: string };

const DocPreviewPage: React.FC = () => {
  const query = useMemo(
    () => parseDocPreviewQuery(typeof window === 'undefined' ? '' : window.location.search),
    [],
  );

  const [state, setState] = useState<ViewState>({ kind: 'loading' });

  useEffect(() => {
    const controller = new AbortController();
    let active = true;

    if (!query.rawPath) {
      setState({ kind: 'error', reason: '缺少 path 参数' });
      return () => controller.abort();
    }

    setState({ kind: 'loading' });
    loadDocumentContent({
      workspaceId: query.workspaceId,
      path: query.rawPath,
      signal: controller.signal,
    })
      .then((result) => {
        if (!active) return;
        setState(
          result.ok
            ? { kind: 'ready', document: result.document }
            : { kind: 'error', reason: result.reason, hint: result.hint },
        );
      })
      .catch((error: unknown) => {
        if (!active) return;
        const message = error instanceof Error ? error.message : String(error);
        setState({ kind: 'error', reason: `加载异常：${message}` });
      });

    return () => {
      active = false;
      controller.abort();
    };
  }, [query]);

  const handleDownload = (document: LoadedDocument) => {
    triggerDownload(createMarkdownDownload(document.name, document.markdown));
  };

  return (
    <div
      data-testid="doc-preview-page"
      style={{ height: '100vh', display: 'flex', flexDirection: 'column', background: '#fff' }}
    >
      {state.kind === 'loading' ? (
        <div data-testid="doc-preview-loading" style={{ padding: 24, opacity: 0.7 }}>
          正在读取 {query.rawPath ?? ''}…
        </div>
      ) : null}

      {state.kind === 'error' ? (
        <div data-testid="doc-preview-error" style={{ padding: 24, maxWidth: 720 }}>
          <div style={{ fontWeight: 600, marginBottom: 8 }}>无法预览这份文档</div>
          <div style={{ color: '#b42318' }}>{state.reason}</div>
          {state.hint ? (
            <div style={{ marginTop: 8, fontSize: 12, opacity: 0.7 }}>{state.hint}</div>
          ) : null}
          {query.rawPath ? (
            <div style={{ marginTop: 12, fontSize: 12, opacity: 0.6 }}>
              请求路径：{query.rawPath}（工作区 {query.workspaceId}）
            </div>
          ) : null}
        </div>
      ) : null}

      {state.kind === 'ready' ? (
        <DocumentPreview
          name={state.document.name}
          markdown={state.document.markdown}
          meta={state.document.meta}
          onDownload={() => handleDownload(state.document)}
        />
      ) : null}
    </div>
  );
};

export default DocPreviewPage;
