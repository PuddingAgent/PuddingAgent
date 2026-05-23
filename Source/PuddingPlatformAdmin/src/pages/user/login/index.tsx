import {
  LockOutlined,
  LoginOutlined,
  UserOutlined,
} from '@ant-design/icons';
import { ProForm, ProFormCheckbox, ProFormText } from '@ant-design/pro-components';
import { FormattedMessage, Helmet, SelectLang, history, useIntl, useModel } from '@umijs/max';
import { Alert, App, Button } from 'antd';
import { createStyles } from 'antd-style';
import React, { useMemo, useState } from 'react';
import { flushSync } from 'react-dom';
import { Footer } from '@/components';
import { login } from '@/services/ant-design-pro/api';
import Settings from '../../../../config/defaultSettings';

const LOGIN_TRANSITION_MS = 240;

const runtimeNodes = [
  { key: 'workspace', label: 'Workspace', x: 16, y: 58 },
  { key: 'agent', label: 'Agent', x: 52, y: 24 },
  { key: 'skills', label: 'Skills', x: 78, y: 60 },
  { key: 'chat', label: 'Chat', x: 50, y: 82 },
];

const normalizeRouteTarget = (target: string | null): string => {
  if (!target) return '/chat';

  try {
    const url = new URL(target, window.location.origin);
    const path = url.pathname.startsWith('/admin/')
      ? url.pathname.slice('/admin'.length)
      : url.pathname;
    return `${path || '/chat'}${url.search}${url.hash}`;
  } catch {
    return target.startsWith('/admin/') ? target.slice('/admin'.length) : target;
  }
};

const useStyles = createStyles(({ token }) => ({
  lang: {
    width: 42,
    height: 42,
    lineHeight: '42px',
    position: 'fixed',
    right: 16,
    top: 16,
    borderRadius: 8,
    zIndex: 10,
    color: 'color-mix(in srgb, var(--earth-brown) 62%, transparent)',
    transition: 'background 160ms ease, color 160ms ease',
    ':hover': {
      backgroundColor: 'color-mix(in srgb, var(--earth-brown) 7%, transparent)',
      color: 'var(--earth-brown)',
    },
  },
  container: {
    minHeight: '100vh',
    display: 'flex',
    flexDirection: 'column' as const,
    overflow: 'auto',
    background: 'var(--warm-beige)',
    color: 'var(--text-primary)',
    position: 'relative' as const,
    '& .ant-pro-global-footer': {
      margin: '0 0 16px',
      color: 'color-mix(in srgb, var(--earth-brown) 48%, transparent)',
    },
  },
  shell: {
    flex: 1,
    width: '100%',
    maxWidth: 1100,
    margin: '0 auto',
    padding: '72px 28px 32px',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 408px',
    gap: 56,
    alignItems: 'center',
    transition: 'opacity 220ms ease, transform 220ms ease',
    '@media (max-width: 860px)': {
      gridTemplateColumns: '1fr',
      gap: 24,
      padding: '64px 18px 24px',
    },
  },
  shellEntering: {
    opacity: 0,
    transform: 'translateY(-6px) scale(0.985)',
  },
  visual: {
    minHeight: 480,
    display: 'flex',
    flexDirection: 'column' as const,
    justifyContent: 'center',
    gap: 24,
    '@media (max-width: 860px)': {
      minHeight: 'auto',
      gap: 14,
    },
  },
  brandMark: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 10,
    fontSize: 13,
    fontWeight: 600,
    color: 'var(--earth-brown)',
    opacity: 0.72,
  },
  brandDot: {
    width: 28,
    height: 28,
    borderRadius: 8,
    display: 'grid',
    placeItems: 'center',
    background: 'color-mix(in srgb, var(--accent-purple) 9%, var(--soft-white))',
    border: '1px solid color-mix(in srgb, var(--accent-purple) 14%, transparent)',
    color: 'var(--accent-purple)',
    fontWeight: 800,
  },
  heroTitle: {
    margin: 0,
    fontSize: 30,
    lineHeight: 1.18,
    fontWeight: 700,
    letterSpacing: 0,
    color: 'var(--text-primary)',
    '@media (max-width: 860px)': {
      fontSize: 26,
    },
  },
  heroSubtitle: {
    margin: 0,
    maxWidth: 480,
    color: 'color-mix(in srgb, var(--earth-brown) 76%, transparent)',
    fontSize: 15,
    lineHeight: 1.75,
  },
  capabilityStrip: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: 8,
  },
  capability: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    minHeight: 28,
    padding: '0 10px',
    borderRadius: 8,
    background: 'color-mix(in srgb, var(--soft-white) 74%, transparent)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 9%, transparent)',
    color: 'color-mix(in srgb, var(--earth-brown) 78%, transparent)',
    fontSize: 12,
    fontWeight: 500,
  },
  capabilityDot: {
    width: 5,
    height: 5,
    borderRadius: '50%',
    background: 'var(--desaturated-green)',
  },
  runtimeMap: {
    width: 'min(520px, 100%)',
    aspectRatio: '1.55 / 1',
    position: 'relative' as const,
    borderRadius: 8,
    border: '1px solid color-mix(in srgb, var(--earth-brown) 8%, transparent)',
    background: `
      linear-gradient(135deg, color-mix(in srgb, var(--soft-white) 86%, transparent), color-mix(in srgb, var(--pale-yellow-sunlight) 50%, transparent)),
      repeating-linear-gradient(90deg, color-mix(in srgb, var(--earth-brown) 5%, transparent) 0 1px, transparent 1px 56px),
      repeating-linear-gradient(0deg, color-mix(in srgb, var(--earth-brown) 4%, transparent) 0 1px, transparent 1px 48px)
    `,
    overflow: 'hidden',
    '@media (max-width: 860px)': {
      display: 'none',
    },
  },
  runtimeLine: {
    position: 'absolute' as const,
    left: '18%',
    right: '18%',
    top: '49%',
    height: 2,
    background: 'linear-gradient(90deg, transparent, color-mix(in srgb, var(--earth-brown) 18%, transparent), transparent)',
  },
  runtimeLineVertical: {
    position: 'absolute' as const,
    left: '50%',
    top: '24%',
    bottom: '18%',
    width: 2,
    background: 'linear-gradient(180deg, transparent, color-mix(in srgb, var(--earth-brown) 16%, transparent), transparent)',
  },
  flowSignal: {
    position: 'absolute' as const,
    left: '18%',
    top: 'calc(49% - 3px)',
    width: 6,
    height: 6,
    borderRadius: '50%',
    background: 'var(--accent-purple)',
    opacity: 0.58,
    animation: 'runtimeFlow 4.2s ease-in-out infinite',
  },
  runtimeNode: {
    position: 'absolute' as const,
    transform: 'translate(-50%, -50%)',
    minWidth: 86,
    minHeight: 38,
    display: 'grid',
    placeItems: 'center',
    borderRadius: 8,
    background: 'color-mix(in srgb, var(--soft-white) 88%, transparent)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    color: 'var(--earth-brown)',
    fontSize: 12,
    fontWeight: 600,
    boxShadow: '0 1px 6px rgba(0,0,0,0.04)',
  },
  panelWrap: {
    width: '100%',
    justifySelf: 'end',
    perspective: 1200,
    '@media (max-width: 860px)': {
      justifySelf: 'stretch',
    },
  },
  panel: {
    width: '100%',
    padding: '30px 32px',
    borderRadius: 8,
    background: 'color-mix(in srgb, var(--soft-white) 94%, transparent)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
    boxShadow: '0 1px 6px rgba(0,0,0,0.04)',
    transition: 'opacity 220ms ease, transform 220ms ease',
    '& .ant-form-item-label > label': {
      color: 'color-mix(in srgb, var(--earth-brown) 82%, transparent)',
      fontSize: 13,
      fontWeight: 500,
    },
    '& .ant-input-affix-wrapper': {
      minHeight: 44,
      borderRadius: 8,
      background: 'color-mix(in srgb, var(--soft-white) 76%, transparent)',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 13%, transparent)',
      boxShadow: 'none',
    },
    '& .ant-input-affix-wrapper-focused': {
      borderColor: 'color-mix(in srgb, var(--accent-purple) 42%, transparent)',
      boxShadow: '0 0 0 2px color-mix(in srgb, var(--accent-purple) 14%, transparent)',
    },
    '& .ant-input-prefix, & .ant-input-password-icon': {
      color: 'color-mix(in srgb, var(--earth-brown) 48%, transparent)',
    },
    '& .ant-input': {
      background: 'transparent',
      color: 'var(--text-primary)',
      fontSize: 14,
    },
    '& .ant-checkbox-wrapper': {
      color: 'color-mix(in srgb, var(--earth-brown) 78%, transparent)',
      fontSize: 13,
    },
    '@media (max-width: 480px)': {
      padding: '24px 20px',
    },
  },
  panelEntering: {
    opacity: 0,
    transform: 'rotateY(-7deg) translateY(-4px)',
  },
  panelHeader: {
    marginBottom: 22,
  },
  panelKicker: {
    marginBottom: 8,
    color: 'color-mix(in srgb, var(--earth-brown) 58%, transparent)',
    fontSize: 12,
    fontWeight: 600,
  },
  panelTitle: {
    margin: 0,
    fontSize: 22,
    lineHeight: 1.3,
    fontWeight: 700,
    color: 'var(--text-primary)',
  },
  panelDesc: {
    margin: '8px 0 0',
    color: 'color-mix(in srgb, var(--earth-brown) 70%, transparent)',
    fontSize: 13,
    lineHeight: 1.65,
  },
  submitBtn: {
    height: '44px !important',
    borderRadius: '8px !important',
    border: 'none !important',
    background: 'var(--accent-purple) !important',
    fontSize: '15px !important',
    fontWeight: 600,
    boxShadow: 'none !important',
    transition: 'background 160ms ease, opacity 160ms ease',
    '&:hover': {
      background: 'color-mix(in srgb, var(--accent-purple) 88%, #000) !important',
    },
  },
  errorAlert: {
    background: 'color-mix(in srgb, #ef4444 5%, var(--soft-white)) !important',
    borderColor: 'color-mix(in srgb, #ef4444 24%, transparent) !important',
    borderRadius: '8px !important',
    marginBottom: '18px !important',
    '& .ant-alert-message': { color: '#8f2f2f' },
    '& .ant-alert-icon': { color: '#c94a4a' },
  },
  authMeta: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
    marginTop: -2,
    marginBottom: 22,
    flexWrap: 'wrap' as const,
  },
  ghostAction: {
    height: 30,
    padding: '0 2px',
    color: 'color-mix(in srgb, var(--earth-brown) 56%, transparent)',
    fontSize: 12,
  },
  '@keyframes runtimeFlow': {
    '0%': { transform: 'translateX(0)', opacity: 0 },
    '16%': { opacity: 0.58 },
    '52%': { transform: 'translateX(265px)', opacity: 0.58 },
    '100%': { transform: 'translateX(265px) translateY(105px)', opacity: 0 },
  },
  '@media (prefers-reduced-motion: reduce)': {
    shell: {
      transition: 'none',
    },
    panel: {
      transition: 'none',
    },
    flowSignal: {
      animation: 'none',
      display: 'none',
    },
  },
}));

const Lang = () => {
  const { styles } = useStyles();
  return (
    <div className={styles.lang} data-lang>
      {SelectLang && <SelectLang />}
    </div>
  );
};

const LoginMessage: React.FC<{ content: string }> = ({ content }) => {
  const { styles } = useStyles();
  return (
    <Alert
      className={styles.errorAlert}
      message={content}
      type="error"
      showIcon
    />
  );
};

const Login: React.FC = () => {
  const [userLoginState, setUserLoginState] = useState<API.LoginResult>({});
  const [entryTransition, setEntryTransition] = useState<'idle' | 'entering-chat'>('idle');
  const { initialState, setInitialState } = useModel('@@initialState');
  const { styles, cx } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();

  const statusText = useMemo(
    () => (entryTransition === 'entering-chat' ? '正在进入 Chat…' : '进入工作台'),
    [entryTransition],
  );

  const fetchUserInfo = async () => {
    const userInfo = await initialState?.fetchUserInfo?.();
    if (userInfo) {
      flushSync(() => {
        setInitialState((s) => ({ ...s, currentUser: userInfo }));
      });
    }
  };

  const navigateToChat = () => {
    const urlParams = new URL(window.location.href).searchParams;
    history.replace(normalizeRouteTarget(urlParams.get('redirect')));
  };

  const handleSubmit = async (values: API.LoginParams) => {
    try {
      const msg = await login({ ...values, type: 'account' });
      if (msg.status === 'ok') {
        if (msg.token) {
          localStorage.setItem('pudding_token', msg.token);
        }
        await fetchUserInfo();
        setEntryTransition('entering-chat');
        message.success(
          intl.formatMessage({
            id: 'pages.login.success',
            defaultMessage: '登录成功！',
          }),
        );
        window.setTimeout(navigateToChat, LOGIN_TRANSITION_MS);
        return;
      }
      setUserLoginState(msg);
    } catch (_error) {
      message.error(
        intl.formatMessage({
          id: 'pages.login.failure',
          defaultMessage: '登录失败，请重试！',
        }),
      );
    }
  };

  const { status } = userLoginState;

  return (
    <div className={styles.container}>
      <Helmet>
        <title>
          {intl.formatMessage({ id: 'menu.login', defaultMessage: '登录' })}
          {Settings.title && ` - ${Settings.title}`}
        </title>
      </Helmet>
      <Lang />
      <main
        className={cx(styles.shell, entryTransition === 'entering-chat' && styles.shellEntering)}
        data-testid="runtime-entry-shell"
        data-transition={entryTransition}
      >
        <section className={styles.visual} data-testid="runtime-entry-visual" aria-label="Pudding Runtime">
          <div className={styles.brandMark}>
            <span className={styles.brandDot}>P</span>
            <span>Pudding Runtime</span>
          </div>
          <div>
            <h1 className={styles.heroTitle}>本地 AI Agent 工作台</h1>
            <p className={styles.heroSubtitle}>
              连接工作空间、Agent 与 Skills，安静地理解，可靠地执行。
            </p>
          </div>
          <div className={styles.capabilityStrip} aria-label="Runtime capabilities">
            {['Workspace', 'Agent', 'Skills'].map((item) => (
              <span className={styles.capability} key={item}>
                <span className={styles.capabilityDot} />
                {item}
              </span>
            ))}
          </div>
          <div className={styles.runtimeMap} aria-hidden="true">
            <div className={styles.runtimeLine} />
            <div className={styles.runtimeLineVertical} />
            <div className={styles.flowSignal} />
            {runtimeNodes.map((node) => (
              <div
                key={node.key}
                className={styles.runtimeNode}
                style={{ left: `${node.x}%`, top: `${node.y}%` }}
              >
                {node.label}
              </div>
            ))}
          </div>
        </section>

        <section className={styles.panelWrap} aria-label="登录">
          <div
            className={cx(styles.panel, entryTransition === 'entering-chat' && styles.panelEntering)}
            data-testid="auth-card-login"
          >
            <div className={styles.panelHeader}>
              <div className={styles.panelKicker}>AUTHENTICATION</div>
              <h2 className={styles.panelTitle}>登录 Pudding Runtime</h2>
              <p className={styles.panelDesc}>
                继续进入 Chat，接管当前工作空间中的 Agent 与 Skills。
              </p>
            </div>

            {status === 'error' && (
              <LoginMessage
                content={intl.formatMessage({
                  id: 'pages.login.accountLogin.errorMessage',
                  defaultMessage: '账户或密码错误，请重试',
                })}
              />
            )}

            <ProForm
              initialValues={{ autoLogin: true }}
              submitter={{
                searchConfig: {
                  submitText: statusText,
                },
                submitButtonProps: {
                  className: styles.submitBtn,
                  block: true,
                  icon: <LoginOutlined />,
                  loading: entryTransition === 'entering-chat',
                },
              }}
              onFinish={async (values) => {
                await handleSubmit(values as API.LoginParams);
              }}
            >
              <ProFormText
                name="username"
                label="用户名"
                fieldProps={{
                  size: 'large',
                  prefix: <UserOutlined />,
                  autoComplete: 'username',
                }}
                placeholder={intl.formatMessage({
                  id: 'pages.login.username.placeholder',
                  defaultMessage: '用户名: admin',
                })}
                rules={[
                  {
                    required: true,
                    message: (
                      <FormattedMessage
                        id="pages.login.username.required"
                        defaultMessage="请输入用户名！"
                      />
                    ),
                  },
                ]}
              />

              <ProFormText.Password
                name="password"
                label="密码"
                fieldProps={{
                  size: 'large',
                  prefix: <LockOutlined />,
                  autoComplete: 'current-password',
                }}
                placeholder={intl.formatMessage({
                  id: 'pages.login.password.placeholder',
                  defaultMessage: '密码: pudding.dev',
                })}
                rules={[
                  {
                    required: true,
                    message: (
                      <FormattedMessage
                        id="pages.login.password.required"
                        defaultMessage="请输入密码！"
                      />
                    ),
                  },
                ]}
              />

              <div className={styles.authMeta}>
                <ProFormCheckbox noStyle name="autoLogin">
                  <FormattedMessage
                    id="pages.login.rememberMe"
                    defaultMessage="自动登录"
                  />
                </ProFormCheckbox>
                <Button type="text" className={styles.ghostAction} disabled>
                  注册功能后续开放
                </Button>
              </div>
            </ProForm>
          </div>
        </section>
      </main>
      <Footer />
    </div>
  );
};

export default Login;
