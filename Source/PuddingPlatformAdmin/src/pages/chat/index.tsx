import {
  CopyOutlined,
  DeleteOutlined,
  DownloadOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  MessageOutlined,
  PlusOutlined,
  ReloadOutlined,
  SendOutlined,
  SettingOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { history } from '@umijs/max';
import dayjs from 'dayjs';
import { Alert, App, Avatar, Button, Divider, Form, Input, Modal, Progress, Select, Space, Spin, Tooltip, Typography, theme } from 'antd';
import { createStyles } from 'antd-style';
import 'katex/dist/katex.min.css';
import Prism from 'prismjs';
import 'prismjs/components/prism-markup';
import 'prismjs/components/prism-clike';
import 'prismjs/components/prism-javascript';
import 'prismjs/components/prism-jsx';
import 'prismjs/components/prism-typescript';
import 'prismjs/components/prism-tsx';
import 'prismjs/components/prism-bash';
import 'prismjs/components/prism-csharp';
import 'prismjs/components/prism-json';
import 'prismjs/components/prism-python';
import 'prismjs/themes/prism-tomorrow.css';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import {
  createWorkspace,
  createWorkspaceAgent,
  listSessions,
  listSessionMessages,
  listTeams,
  listWorkspaceAgents,
  listWorkspaces,
  sendAdminChatMessageStream,
  type CreateWorkspaceRequest,
  type ChatMessageDto,
  type MessageListResponse,
  type TokenUsageDto,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

// ── 类型 ─────────────────────────────────────────────
interface ChatMessage {
  id: string;
  role: 'user' | 'agent';
  text: string;
  timestamp: number;
  status: 'sending' | 'success' | 'error';
  usage?: TokenUsageDto;
  isStreaming?: boolean;
}

interface SessionGroup {
  label: string;
  items: { sessionId: string; title: string; timestamp: number }[];
}

const { Text, Title } = Typography;
const SIDEBAR_WIDTH = 260;
const DEFAULT_CONTEXT_WINDOW = 4096;

// ── 工具函数 ─────────────────────────────────────────
const stringToColor = (str: string) => {
  let hash = 0;
  for (let i = 0; i < str.length; i++) hash = str.charCodeAt(i) + ((hash << 5) - hash);
  const colors = ['#f97316','#ef4444','#8b5cf6','#06b6d4','#22c55e','#eab308','#ec4899','#6366f1','#14b8a6','#f43f5e'];
  return colors[Math.abs(hash) % colors.length];
};
const getAgentName = (a: WorkspaceAgentDto) => a.displayName || a.name || 'Agent';
const getAgentAvatar = (a: WorkspaceAgentDto) => a.avatarUrl || undefined;

const groupSessions = (raw: { sessionId: string; title: string; timestamp: number }[]): SessionGroup[] => {
  const now = dayjs();
  const groups: Record<string, { sessionId: string; title: string; timestamp: number }[]> = {};
  for (const s of raw) {
    const d = dayjs(s.timestamp);
    let key: string;
    if (d.isSame(now, 'day')) key = '今天';
    else if (d.isSame(now.subtract(1, 'day'), 'day')) key = '昨天';
    else if (d.isAfter(now.subtract(7, 'day'))) key = '本周';
    else key = '更早';
    (groups[key] ??= []).push(s);
  }
  return ['今天','昨天','本周','更早'].filter(k => groups[k]?.length).map(label => ({
    label,
    items: groups[label]!.sort((a,b) => b.timestamp - a.timestamp),
  }));
};

// ── 样式 ─────────────────────────────────────────────
const useStyles = createStyles(({ token }) => ({
  layout: { display: 'flex', height: '100vh', overflow: 'hidden', background: token.colorBgLayout },
  sidebar: {
    width: SIDEBAR_WIDTH, minWidth: SIDEBAR_WIDTH, height: '100%',
    display: 'flex', flexDirection: 'column',
    borderRight: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer,
    transition: 'width 0.2s ease, min-width 0.2s ease, border 0.2s ease',
    overflow: 'hidden',
  },
  sidebarCollapsed: { width: 0, minWidth: 0, borderRight: 'none' },
  sidebarHeader: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '10px 12px', borderBottom: `1px solid ${token.colorBorderSecondary}` },
  sidebarNewBtn: { flex: 1, justifyContent: 'flex-start', fontSize: 13, fontWeight: 500 },
  sessionList: { flex: 1, overflowY: 'auto' as const, padding: '4px 8px' },
  groupLabel: { padding: '12px 8px 4px', fontSize: 11, fontWeight: 600, color: token.colorTextTertiary, textTransform: 'uppercase' as const },
  sessionItem: {
    display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderRadius: token.borderRadius,
    cursor: 'pointer', fontSize: 13, color: token.colorTextSecondary, transition: 'background 0.15s',
    overflow: 'hidden', '&:hover': { background: token.colorFillQuaternary },
  },
  sessionItemActive: { background: token.colorFillSecondary, color: token.colorText, fontWeight: 500 },
  sessionTitle: { flex: 1, overflow: 'hidden', whiteSpace: 'nowrap' as const, textOverflow: 'ellipsis' },
  mainArea: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, overflow: 'hidden' },
  header: {
    display: 'flex', alignItems: 'center', gap: 10, padding: '6px 14px',
    borderBottom: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer, minHeight: 44, flexShrink: 0,
  },
  headerLogo: { width: 24, height: 24, objectFit: 'contain' as const },
  headerBrand: { fontSize: 14, fontWeight: 700, color: token.colorText, whiteSpace: 'nowrap' as const },
  headerSelect: { minWidth: 110, maxWidth: 180, fontSize: 12 },
  headerSpacer: { flex: 1 },
  chatBody: { display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, overflow: 'hidden', padding: '0 20px', background: token.colorBgLayout },
  messageList: { flex: 1, overflowY: 'auto' as const, padding: '14px 0 8px', display: 'flex', flexDirection: 'column' as const, gap: 10 },
  historyLoading: { display: 'flex', justifyContent: 'center', padding: '8px 0' },
  messageRow: { display: 'flex' },
  messageContent: {
    maxWidth: '74%', display: 'flex', flexDirection: 'column', gap: 4, position: 'relative' as const,
    '&:hover .message-actions': { opacity: 1, transform: 'translateY(0)', pointerEvents: 'auto' as const },
  },
  userContent: { alignItems: 'flex-end' },
  agentContent: { alignItems: 'flex-start' },
  userRow: { justifyContent: 'flex-end' },
  agentRow: { justifyContent: 'flex-start' },
  bubble: {
    maxWidth: '100%', padding: '10px 16px', borderRadius: token.borderRadiusLG,
    lineHeight: 1.6, wordBreak: 'break-word' as const, whiteSpace: 'pre-wrap' as const,
    border: '1px solid transparent',
  },
  userBubble: { background: token.colorPrimary, color: token.colorTextLightSolid, borderBottomRightRadius: 4 },
  agentBubble: { background: token.colorFillQuaternary, color: token.colorText, border: `1px solid ${token.colorBorderSecondary}`, borderBottomLeftRadius: 4 },
  errorBubble: { borderColor: token.colorError },
  messageActions: { opacity: 0, transform: 'translateY(3px)', pointerEvents: 'none' as const, transition: 'opacity 0.16s ease, transform 0.16s ease' },
  messageMeta: { display: 'flex', alignItems: 'center', gap: 8, minHeight: 20 },
  timeText: { color: token.colorTextQuaternary, fontSize: 12 },
  sendingText: { color: token.colorTextTertiary, fontSize: 12 },
  retryButton: { paddingInline: 4, fontSize: 12, height: 22 },
  timeDivider: { display: 'flex', alignItems: 'center', gap: 8, margin: '2px 0', color: token.colorTextQuaternary, fontSize: 12, '&::before, &::after': { content: '""', flex: 1, height: 1, background: token.colorBorderSecondary } },
  inputPanel: { padding: '10px 0 16px', display: 'flex', flexDirection: 'column', gap: 8 },
  tokenIndicator: { display: 'flex', alignItems: 'center', gap: 8, padding: '0 2px' },
  tokenProgress: { flex: 1 },
  inputArea: { display: 'flex', gap: 8, alignItems: 'flex-end' },
  input: { flex: 1 },
  emptyState: { flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: token.colorTextQuaternary, fontSize: 15 },
  onboardingState: { flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 12, padding: '24px 0' },
  onboardingLogo: { width: 56, height: 56, objectFit: 'contain' as const, opacity: 0.7 },
  onboardingTitle: { margin: 0 },
  onboardingSubtitle: { color: token.colorTextSecondary, fontSize: 14, textAlign: 'center' as const },
  errorAlert: { margin: '8px 0' },
  sidebarEmpty: { flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center', color: token.colorTextQuaternary, fontSize: 13, padding: '0 16px', textAlign: 'center' as const },
  markdownBody: {
    whiteSpace: 'normal' as const,
    '& p': { margin: '0 0 8px' }, '& p:last-child': { marginBottom: 0 },
    '& ul, & ol': { paddingLeft: 22, margin: '6px 0' },
    '& blockquote': { margin: '8px 0', paddingLeft: 12, borderLeft: `3px solid ${token.colorBorder}`, color: token.colorTextSecondary },
    '& table': { borderCollapse: 'collapse' as const, minWidth: 480 },
    '& th, & td': { border: `1px solid ${token.colorBorderSecondary}`, padding: '6px 10px', textAlign: 'left' as const },
    '& th': { background: token.colorFillQuaternary },
    '& .katex-display': { overflowX: 'auto' as const, overflowY: 'hidden' as const, padding: '4px 0' },
  },
  markdownTableScroll: { maxWidth: '100%', overflowX: 'auto' as const, margin: '8px 0' },
  inlineCode: { padding: '1px 5px', borderRadius: 4, background: token.colorFillSecondary, fontSize: '0.92em' },
  codeBlockWrap: { position: 'relative' as const, margin: '10px 0', borderRadius: 10, overflow: 'hidden', background: '#1e1e1e', '& pre': { margin: 0, padding: '14px 16px', overflowX: 'auto' as const } },
  codeCopyButton: { position: 'absolute' as const, top: 8, right: 8, zIndex: 1 },
  streamingCursor: { display: 'inline-block', width: 8, marginLeft: 2, color: token.colorPrimary, animation: 'cursorBlink 1s steps(1) infinite' },
  '@keyframes cursorBlink': { '0%, 100%': { opacity: 1 }, '50%': { opacity: 0 } },
}));

// ── CodeBlock ─────────────────────────────────────────
const CodeBlock: React.FC<{ code: string; className?: string; wrapClassName: string; buttonClassName: string }> = ({ code, className, wrapClassName, buttonClassName }) => {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => { if (ref.current) Prism.highlightElement(ref.current); }, [code, className]);
  return <div className={wrapClassName}><Button size="small" className={buttonClassName} icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(code)}>复制</Button><pre><code ref={ref} className={className}>{code}</code></pre></div>;
};

// ── 主组件 ──────────────────────────────────────────
const ChatPage: React.FC = () => {
  const { styles, cx } = useStyles();
  const { message: messageApi } = App.useApp();
  const { token } = theme.useToken();

  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [agentId, setAgentId] = useState<string>();
  const [agentLoading, setAgentLoading] = useState(false);

  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [sessions, setSessions] = useState<{ sessionId: string; title: string; timestamp: number }[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [sessionsLoading, setSessionsLoading] = useState(false);

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [hasMoreHistory, setHasMoreHistory] = useState(false);
  const [loadingHistory, setLoadingHistory] = useState(false);
  const oldestRef = useRef<number | null>(null);

  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [latestUsage, setLatestUsage] = useState<TokenUsageDto | undefined>();
  const sessionIdRef = useRef<string | undefined>(undefined);
  const abortRef = useRef<AbortController | null>(null);
  const listEndRef = useRef<HTMLDivElement>(null);
  const listTopRef = useRef<HTMLDivElement>(null);

  const [createSceneOpen, setCreateSceneOpen] = useState(false);
  const [createSceneLoading, setCreateSceneLoading] = useState(false);
  const [createSceneForm] = Form.useForm<{ name: string }>();

  const selectedAgent = agents.find(a => a.agentId === agentId);
  const createId = () => `msg-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

  const formatTime = (ts: number) => {
    const diff = dayjs().diff(dayjs(ts), 'minute');
    if (diff < 1) return '刚刚';
    if (diff < 60) return `${diff}分钟前`;
    const diffH = dayjs().diff(dayjs(ts), 'hour');
    if (diffH < 24) return `${diffH}小时前`;
    return dayjs(ts).format('MM-DD HH:mm');
  };

  const resetConversation = () => {
    abortRef.current?.abort(); abortRef.current = null;
    setMessages([]); setError(null); setLatestUsage(undefined); setLoading(false);
    sessionIdRef.current = undefined; setSelectedSessionId(null); oldestRef.current = null; setHasMoreHistory(false);
  };

  // ── 初始化工作空间 ─────────────────────────────
  useEffect(() => { let a = true; (async () => { setWorkspaceLoading(true); try { const items = await listWorkspaces(); if (!a) return; setWorkspaces(items); const wid = items.find(x => x.workspaceId==='default'&&x.isEnabled&&!x.isFrozen)?.workspaceId??items.find(x=>x.workspaceId==='default')?.workspaceId??items.find(x=>x.isEnabled&&!x.isFrozen)?.workspaceId??items[0]?.workspaceId; setWorkspaceId(wid); if(!wid) setError('无可用工作空间'); } catch(e:any){ if(a) setError(e?.message||'加载失败'); } finally { if(a) setWorkspaceLoading(false); } })(); return ()=>{a=false}; }, []);

  // ── 工作空间→Agent ────────────────────────────
  useEffect(() => { let a = true; (async () => { if(!workspaceId){ setAgents([]); setAgentId(undefined); return; } setAgentLoading(true); try { const items = await listWorkspaceAgents(workspaceId); if(!a) return; if(items.length===0){ try { const c = await createWorkspaceAgent(workspaceId, {name:'Pudding 助手',displayName:'布丁',sourceTemplateId:'global:general-assistant'}); setAgents([c]); setAgentId(c.agentId); } catch { setAgents([]); setAgentId(undefined); } } else { setAgents(items); setAgentId(items.find(x=>x.isEnabled&&!x.isFrozen)?.agentId??items.find(x=>x.isEnabled)?.agentId??items[0]?.agentId); } } catch(e:any){ if(a) setError(e?.message||'加载Agent失败'); } finally { if(a) setAgentLoading(false); } })(); return ()=>{a=false}; }, [workspaceId]);

  // ── 刷新会话列表 ──────────────────────────────
  const refreshSessions = useCallback(async () => { if(!workspaceId) return; setSessionsLoading(true); try { const list = await listSessions(workspaceId); setSessions(list.map(s=>({sessionId:s.sessionId,title:s.agentTemplateId?.replace('global:','')||'对话',timestamp:new Date(s.createdAt).getTime()})).sort((a,b)=>b.timestamp-a.timestamp)); } catch { setSessions([]); } finally { setSessionsLoading(false); } }, [workspaceId]);
  useEffect(() => { refreshSessions(); }, [refreshSessions, messages.length]);

  // ── 选择会话加载历史 ───────────────────────────
  const handleSelectSession = useCallback(async (sid: string) => {
    if(sid===selectedSessionId) return;
    setSelectedSessionId(sid); sessionIdRef.current = sid; setMessages([]); setHistoryLoading(true);
    try { const res: MessageListResponse = await listSessionMessages(sid);
      setMessages(res.items.map(d=>({id:`hist-${d.id}`,role:d.role as 'user'|'agent',text:d.content,timestamp:d.createdAt,status:'success' as const,usage:d.usage?{promptTokens:d.usage.promptTokens,completionTokens:d.usage.completionTokens,totalTokens:d.usage.totalTokens,contextWindowTokens:d.usage.contextWindowTokens}:undefined})));
      setHasMoreHistory(res.hasMore); oldestRef.current = res.oldestCreatedAt;
    } catch { messageApi.error('加载历史消息失败'); } finally { setHistoryLoading(false); }
  }, [selectedSessionId, messageApi]);

  // ── 滚动加载更多历史 ────────────────────────────
  const loadMoreHistory = useCallback(async () => { if(!selectedSessionId||loadingHistory||!hasMoreHistory) return; setLoadingHistory(true); try { const res: MessageListResponse = await listSessionMessages(selectedSessionId, oldestRef.current??undefined);
      const older: ChatMessage[] = res.items.map(d=>({id:`hist-${d.id}`,role:d.role as 'user'|'agent',text:d.content,timestamp:d.createdAt,status:'success' as const}));
      setMessages(p=>[...older,...p]); setHasMoreHistory(res.hasMore); oldestRef.current = res.oldestCreatedAt;
    } catch {} finally { setLoadingHistory(false); } }, [selectedSessionId, loadingHistory, hasMoreHistory]);

  useEffect(() => { const el = listTopRef.current; if(!el||!hasMoreHistory) return; const io = new IntersectionObserver(([e])=>{ if(e?.isIntersecting) loadMoreHistory(); },{root:el.parentElement,threshold:0.1}); io.observe(el); return ()=>io.disconnect(); }, [hasMoreHistory, loadMoreHistory]);

  // ── 发送消息 ──────────────────────────────────
  const sendMessage = async (text: string, retryId?: string) => {
    if(!text||loading||!workspaceId||!agentId) return; setError(null); const now = Date.now(); let uid = retryId;
    if(retryId){ setMessages(p=>p.map(m=>m.id===retryId?{...m,status:'sending' as const,timestamp:now}:m)); }
    else { uid = createId(); setMessages(p=>[...p,{id:uid,role:'user',text,timestamp:now,status:'sending'}]); }
    const aid = createId(); setMessages(p=>[...p,{id:aid,role:'agent',text:'',timestamp:now,status:'sending',isStreaming:true}]);
    const ctrl = new AbortController(); abortRef.current = ctrl; setLoading(true);
    try { await sendAdminChatMessageStream(workspaceId,{messageText:text,sessionId:sessionIdRef.current,agentId},(ev)=>{
      if(ev.type==='metadata'){ sessionIdRef.current=ev.sessionId; setMessages(p=>p.map(m=>m.id===uid?{...m,status:'success' as const}:m)); }
      else if(ev.type==='delta'&&ev.delta) setMessages(p=>p.map(m=>m.id===aid?{...m,text:m.text+ev.delta}:m));
      else if(ev.type==='usage'&&ev.usage){ setLatestUsage(ev.usage); setMessages(p=>p.map(m=>m.id===aid?{...m,usage:ev.usage}:m)); }
      else if(ev.type==='done'){ if(ev.usage) setLatestUsage(ev.usage); setMessages(p=>p.map(m=>m.id===aid?{...m,text:m.text||ev.reply||'(无回复)',status:'success' as const,isStreaming:false,usage:ev.usage??m.usage}:m)); }
      else if(ev.type==='cancelled') setMessages(p=>p.map(m=>m.id===aid?{...m,status:'success' as const,isStreaming:false}:m));
      else if(ev.type==='error'){ setMessages(p=>p.map(m=>m.id===aid?{...m,status:'error' as const,isStreaming:false}:m)); }
    },ctrl.signal); } catch(e:any){ if(e?.name!=='AbortError'){ setError(e?.message||'请求失败'); setMessages(p=>p.map(m=>m.id===aid?{...m,status:'error' as const,isStreaming:false}:m)); } }
    finally { if(abortRef.current===ctrl) abortRef.current=null; setLoading(false); }
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => { if(e.key==='Enter'&&!e.shiftKey){ e.preventDefault(); loading ? abortRef.current?.abort() : (()=>{ const t=inputValue.trim(); if(!t) return; setInputValue(''); void sendMessage(t); })(); } };

  const handleRegenerate = (msg: ChatMessage) => { const i=messages.findIndex(m=>m.id===msg.id); const p=[...messages.slice(0,i)].reverse().find(m=>m.role==='user'); if(!p) return; setMessages(prev=>prev.filter(m=>m.id!==msg.id)); void sendMessage(p.text,p.id); };

  const exportConv = () => { if(messages.length===0){ messageApi.info('无对话'); return; } const md=messages.map(m=>`## ${m.role==='user'?'User':'Agent'} · ${dayjs(m.timestamp).format('YYYY-MM-DD HH:mm:ss')}\n\n${m.text}`).join('\n\n---\n\n'); const b=new Blob([md],{type:'text/markdown;charset=utf-8'}); const u=URL.createObjectURL(b); const a=document.createElement('a'); a.href=u; a.download=`pudding-chat-${dayjs().format('YYYYMMDD-HHmmss')}.md`; a.click(); URL.revokeObjectURL(u); };

  // ── Workspace 创建 ─────────────────────────────
  const buildWsId = (n: string) => (n.trim().toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/^-+|-+$/g,'').slice(0,48)||'ws')+'-'+Date.now().toString().slice(-6);
  const handleCreateWs = async () => { try { const v=await createSceneForm.validateFields(); setCreateSceneLoading(true); const teams=await listTeams(); const tid=teams[0]?.teamId; if(!tid){ setError('无可用分组'); return; } await createWorkspace({workspaceId:buildWsId(v.name),teamId:tid,name:v.name,teamAccessPolicy:'Write',companyAccessPolicy:'None'}); const items=await listWorkspaces(); setWorkspaces(items); setWorkspaceId(items[items.length-1]?.workspaceId); resetConversation(); setCreateSceneOpen(false); } catch(e:any){ if(e&&typeof e==='object'&&'errorFields' in e) return; setError('创建工作空间失败'); } finally { setCreateSceneLoading(false); } };

  // ── 渲染变量 ──────────────────────────────────
  const wsOpts = workspaces.map(w=>({value:w.workspaceId,label:w.name||w.workspaceId,disabled:!w.isEnabled||w.isFrozen}));
  const agOpts = agents.map(a=>({value:a.agentId,label:getAgentName(a),disabled:!a.isEnabled||a.isFrozen}));
  const groups = groupSessions(sessions);
  const tLimit = latestUsage?.contextWindowTokens??DEFAULT_CONTEXT_WINDOW;
  const tUsed = latestUsage?.totalTokens??0;
  const tPct = Math.min(100,Math.round((tUsed/tLimit)*100));

  const renderMd = (msg: ChatMessage) => (
    <div className={styles.markdownBody}>
      <ReactMarkdown remarkPlugins={[remarkGfm,remarkMath]} rehypePlugins={[rehypeKatex]}
        components={{ table:({children,...p}:any)=><div className={styles.markdownTableScroll}><table {...p}>{children}</table></div>,
          code:({inline,className,children,...p}:any)=>{ const c=String(children??'').replace(/\n$/,''); if(inline) return <code className={styles.inlineCode} {...p}>{children}</code>; return <CodeBlock code={c} className={className} wrapClassName={styles.codeBlockWrap} buttonClassName={styles.codeCopyButton} />; } }}>
        {msg.text||(msg.isStreaming?' ':'')}
      </ReactMarkdown>
      {msg.isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );

  return (
    <div className={styles.layout}>
      {/* 侧边栏 */}
      <div className={cx(styles.sidebar, !sidebarOpen && styles.sidebarCollapsed)}>
        <div className={styles.sidebarHeader}>
          <Button type="primary" icon={<PlusOutlined />} className={styles.sidebarNewBtn} onClick={()=>{resetConversation();}}>新对话</Button>
          <Tooltip title="收起"><Button type="text" size="small" icon={<MenuFoldOutlined />} onClick={()=>setSidebarOpen(false)} /></Tooltip>
        </div>
        <div className={styles.sessionList}>
          {sessionsLoading && <div style={{textAlign:'center',padding:16}}><Spin /></div>}
          {!sessionsLoading && groups.length===0 && <div className={styles.sidebarEmpty}>在这里开始你的第一段对话</div>}
          {groups.map(g => <React.Fragment key={g.label}>
            <div className={styles.groupLabel}>{g.label}</div>
            {g.items.map(s => <div key={s.sessionId} className={cx(styles.sessionItem, s.sessionId===selectedSessionId&&styles.sessionItemActive)} onClick={()=>handleSelectSession(s.sessionId)}><MessageOutlined style={{fontSize:14,flexShrink:0}} /><span className={styles.sessionTitle}>{s.title}</span></div>)}
          </React.Fragment>)}
        </div>
      </div>
      {/* 主区域 */}
      <div className={styles.mainArea}>
        <div className={styles.header}>
          {!sidebarOpen && <Button type="text" size="small" icon={<MenuUnfoldOutlined />} onClick={()=>setSidebarOpen(true)} />}
          <img src="/admin/assets/images/logo.png" alt="P" className={styles.headerLogo} />
          <span className={styles.headerBrand}>Pudding</span>
          <Select className={styles.headerSelect} size="small" variant="borderless" value={workspaceId} loading={workspaceLoading} options={wsOpts} onChange={v=>{setWorkspaceId(v);}} placeholder="工作空间" popupMatchSelectWidth={false}
            dropdownRender={menu=><><>{menu}</><Divider style={{margin:'4px 0'}} /><Button type="link" block size="small" onClick={()=>{createSceneForm.resetFields();setCreateSceneOpen(true);}}>+ 新建工作空间</Button></>} />
          <Select className={styles.headerSelect} size="small" variant="borderless" value={agentId} loading={agentLoading} options={agOpts} onChange={v=>{setAgentId(v);resetConversation();}} placeholder="Agent" popupMatchSelectWidth={false} notFoundContent="无Agent" />
          <div className={styles.headerSpacer} />
          {selectedAgent && <Tooltip title={getAgentName(selectedAgent)}><Avatar size={26} src={getAgentAvatar(selectedAgent)} style={{background:stringToColor(getAgentName(selectedAgent)),flexShrink:0}}>{getAgentName(selectedAgent).charAt(0)}</Avatar></Tooltip>}
          <Tooltip title="控制台"><Button type="text" size="small" icon={<SettingOutlined />} onClick={()=>history.push('/workspace')} /></Tooltip>
        </div>
        <div className={styles.chatBody}>
          <div className={styles.messageList}>
            {hasMoreHistory && <div className={styles.historyLoading} ref={listTopRef}>{loadingHistory ? <Spin /> : <Button type="link" size="small" onClick={loadMoreHistory}>加载更早的消息</Button>}</div>}
            {historyLoading && messages.length===0 && <div style={{textAlign:'center',padding:48}}><Spin /></div>}
            {!agentId && !error && <div className={styles.onboardingState}><img src="/admin/assets/images/logo.png" alt="Pudding" className={styles.onboardingLogo} /><Title level={2} className={styles.onboardingTitle}>你好，我是布丁</Title><Text className={styles.onboardingSubtitle}>选择一个工作空间和 Agent，然后把任务交给我。</Text></div>}
            {agentId && messages.length===0 && !error && !historyLoading && <div className={styles.emptyState}><Text type="secondary">开始和 Agent 对话吧</Text></div>}
            {messages.map((msg,idx) => <React.Fragment key={msg.id}>
              {idx>0 && msg.timestamp-messages[idx-1].timestamp>5*60*1000 && <div className={styles.timeDivider}><span>—— {dayjs(msg.timestamp).format('HH:mm')} ——</span></div>}
              <div className={cx(styles.messageRow, msg.role==='user'?styles.userRow:styles.agentRow)}>
                <div className={cx(styles.messageContent, msg.role==='user'?styles.userContent:styles.agentContent)}>
                  <div className={cx(styles.bubble, msg.role==='user'?styles.userBubble:styles.agentBubble, msg.status==='error'&&styles.errorBubble)}>{msg.role==='agent'?renderMd(msg):msg.text}</div>
                  <Space size={2} className={`${styles.messageActions} message-actions`}>
                    <Tooltip title="复制"><Button size="small" type="text" icon={<CopyOutlined />} onClick={()=>{navigator.clipboard.writeText(msg.text);messageApi.success('已复制');}} /></Tooltip>
                    {msg.role==='agent' && <Tooltip title="重新生成"><Button size="small" type="text" icon={<ReloadOutlined />} onClick={()=>handleRegenerate(msg)} disabled={loading} /></Tooltip>}
                    <Tooltip title="删除"><Button size="small" type="text" danger icon={<DeleteOutlined />} onClick={()=>setMessages(p=>p.filter(m=>m.id!==msg.id))} /></Tooltip>
                  </Space>
                  <div className={styles.messageMeta}>
                    <Tooltip title={dayjs(msg.timestamp).format('YYYY-MM-DD HH:mm:ss')}><Text className={styles.timeText}>{formatTime(msg.timestamp)}</Text></Tooltip>
                    {msg.status==='sending'&&msg.role==='user' && <Text className={styles.sendingText}>发送中...</Text>}
                    {msg.isStreaming && <Text className={styles.sendingText}>生成中...</Text>}
                    {msg.usage?.totalTokens ? <Text className={styles.sendingText}>{msg.usage.totalTokens.toLocaleString()} tokens</Text> : null}
                  </div>
                  {msg.status==='error'&&msg.role==='user' && <Button type="link" size="small" className={styles.retryButton} onClick={()=>{const i=messages.findIndex(m=>m.id===msg.id);const n=i>=0?messages[i+1]:undefined;if(n?.role==='agent'&&n.status==='error') setMessages(p=>p.filter(m=>m.id!==n.id));void sendMessage(msg.text,msg.id);}}>重新发送</Button>}
                </div>
              </div>
            </React.Fragment>)}
            {error && <Alert type="error" message={error} closable onClose={()=>setError(null)} className={styles.errorAlert} />}
            <div ref={listEndRef} />
          </div>
          <div className={styles.inputPanel}>
            <div className={styles.tokenIndicator}>
              <Text type="secondary" style={{fontSize:12}}>Tokens</Text>
              <Progress className={styles.tokenProgress} percent={tPct} size="small" />
              <Text type="secondary" style={{fontSize:12}}>{tUsed}/{tLimit}</Text>
            </div>
            <div className={styles.inputArea}>
              <Input.TextArea value={inputValue} onChange={e=>setInputValue(e.target.value)} onKeyDown={handleKeyDown} placeholder="交给我吧。Enter 发送，Shift+Enter 换行" disabled={!workspaceId||!agentId} autoSize={{minRows:1,maxRows:5}} className={styles.input} />
              <Button type={loading?'default':'primary'} danger={loading} icon={loading?<StopOutlined />:<SendOutlined />} onClick={loading?(()=>abortRef.current?.abort()):(()=>{const t=inputValue.trim();if(!t)return;setInputValue('');void sendMessage(t);})} disabled={loading?false:(!inputValue.trim()||!workspaceId||!agentId)}>{loading?'停止':'发送'}</Button>
              <Tooltip title="导出"><Button icon={<DownloadOutlined />} onClick={exportConv} /></Tooltip>
            </div>
          </div>
        </div>
      </div>
      <Modal title="新建工作空间" open={createSceneOpen} onOk={handleCreateWs} onCancel={()=>setCreateSceneOpen(false)} confirmLoading={createSceneLoading} okText="创建" cancelText="取消" destroyOnClose>
        <Form form={createSceneForm} layout="vertical"><Form.Item name="name" label="名称" rules={[{required:true,message:'请输入名称'},{max:128}]}><Input placeholder="例如：研发协作空间" /></Form.Item></Form>
      </Modal>
    </div>
  );
};

export default ChatPage;
