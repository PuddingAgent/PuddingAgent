import React from 'react';
import { getTurnErrorDiagnostic } from '@/services/platform/api';
import {
  buildChatDiagnosticCopyPayload,
  formatChatErrorDiagnostic,
  normalizeChatErrorDiagnostic,
  type ChatErrorDiagnosticEvent,
} from '../utils/chatDiagnostics';

const actionStyle: React.CSSProperties = {
  border: 0, background: 'transparent', color: 'var(--pudding-chat-text-secondary, #52617a)',
  padding: '2px 0', font: 'inherit', cursor: 'pointer',
};

/** Failure details are fetched only when expanded, including after a page reload. */
export default function ErrorDiagnosticPanel({
  event, sessionId, turnId, errorMessage,
}: {
  event?: ChatErrorDiagnosticEvent;
  sessionId?: string | null;
  turnId?: string;
  errorMessage: string;
}) {
  const [open, setOpen] = React.useState(false);
  const [loaded, setLoaded] = React.useState<ChatErrorDiagnosticEvent>();
  const [loading, setLoading] = React.useState(false);
  const [loadError, setLoadError] = React.useState(false);
  const [attempt, setAttempt] = React.useState(0);
  const [copyStatus, setCopyStatus] = React.useState('');
  const diagnostic = normalizeChatErrorDiagnostic(loaded ?? event ?? { errorMessage });
  const hasReport = typeof diagnostic.reportText === 'string' || typeof diagnostic.reportJson === 'string';

  React.useEffect(() => {
    if (!open || hasReport || !sessionId || !turnId) return;
    let active = true;
    setLoading(true);
    setLoadError(false);
    getTurnErrorDiagnostic(sessionId, turnId)
      .then((value) => { if (active) setLoaded(value); })
      .catch(() => { if (active) setLoadError(true); })
      .finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [open, hasReport, sessionId, turnId, attempt]);

  const copy = async () => {
    const payload = buildChatDiagnosticCopyPayload({ event: diagnostic, sessionId, turnId, errorMessage });
    try {
      await navigator.clipboard.writeText(`${payload.text}\n\n-- JSON --\n${payload.json}`);
      setCopyStatus('诊断信息已复制');
    } catch {
      setCopyStatus('复制失败，请选择下方诊断文本手动复制');
    }
  };
  const text = typeof diagnostic.reportText === 'string'
    ? diagnostic.reportText
    : formatChatErrorDiagnostic(diagnostic, { sessionId, turnId });
  return (
    <div data-testid="error-diagnostic-panel" style={{ marginTop: 8, fontSize: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
        <button type="button" style={actionStyle} aria-expanded={open} onClick={() => setOpen(!open)}>
          {open ? '收起诊断信息' : '查看诊断信息'}
        </button>
        {open && (
          <button type="button" style={{ ...actionStyle, opacity: loading ? 0.5 : 1 }}
            disabled={loading} onClick={() => void copy()}>复制诊断信息</button>
        )}
        {open && copyStatus && <span role="status">{copyStatus}</span>}
      </div>
      {open && (
        <div>
          {loading && <p role="status">正在读取诊断信息…</p>}
          {loadError && (
            <p role="status">
              未能读取完整报告，以下是当前可用信息。
              <button type="button" style={{ ...actionStyle, marginLeft: 8 }}
                onClick={() => setAttempt(attempt + 1)}>重新读取</button>
            </p>
          )}
          <pre style={{
            whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: 360, overflow: 'auto',
            margin: '8px 0 0', padding: '12px 14px', lineHeight: 1.65, borderRadius: 8,
            border: '1px solid var(--pudding-chat-border, #dce1eb)',
            background: 'var(--pudding-chat-surface, #fff)', color: 'inherit',
          }}>
            {text}
          </pre>
        </div>
      )}
    </div>
  );
}
