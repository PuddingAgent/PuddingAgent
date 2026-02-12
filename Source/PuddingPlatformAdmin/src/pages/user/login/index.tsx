import { LockOutlined, LoginOutlined, UserOutlined } from '@ant-design/icons';
import {
  ProForm,
  ProFormCheckbox,
  ProFormText,
} from '@ant-design/pro-components';
import {
  FormattedMessage,
  Helmet,
  history,
  SelectLang,
  useIntl,
  useModel,
} from '@umijs/max';
import { Alert, App } from 'antd';
import { createStyles } from 'antd-style';
import React, { useMemo, useState } from 'react';
import { flushSync } from 'react-dom';
import { Footer } from '@/components';
import { login } from '@/services/ant-design-pro/api';
import { PUDDING_WORKSPACES_PATH } from '@/utils/workspaceNavigation';
import Settings from '../../../../config/defaultSettings';

const LOGIN_TRANSITION_MS = 240;
const LOGIN_AMBIENT_BG = '/admin/assets/images/login-ambient-workspace-bg.png';

const normalizeRouteTarget = (target: string | null): string => {
  if (!target) return PUDDING_WORKSPACES_PATH;

  try {
    const url = new URL(target, window.location.origin);
    const path = url.pathname.startsWith('/admin/')
      ? url.pathname.slice('/admin'.length)
      : url.pathname;
    return `${path || '/'}${url.search}${url.hash}`;
  } catch {
    return target.startsWith('/admin/')
      ? target.slice('/admin'.length)
      : target;
  }
};

const useStyles = createStyles(() => ({
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
    isolation: 'isolate' as const,
    '&::before': {
      content: '""',
      position: 'fixed',
      inset: 0,
      zIndex: 1,
      pointerEvents: 'none',
      background: `
        linear-gradient(90deg, rgba(92, 74, 58, 0.025) 1px, transparent 1px),
        linear-gradient(0deg, rgba(92, 74, 58, 0.018) 1px, transparent 1px)
      `,
      backgroundSize: '96px 96px',
      opacity: 0.12,
    },
    '& .ant-pro-global-footer': {
      position: 'relative' as const,
      zIndex: 2,
      margin: '0 0 16px',
      color: 'color-mix(in srgb, var(--earth-brown) 48%, transparent)',
    },
  },
  ambientBackdrop: {
    position: 'fixed' as const,
    inset: 0,
    zIndex: 0,
    overflow: 'hidden',
    pointerEvents: 'none',
    '&::before': {
      content: '""',
      position: 'absolute',
      inset: 0,
      backgroundImage: `url(${LOGIN_AMBIENT_BG})`,
      backgroundSize: 'cover',
      backgroundPosition: 'center left',
      filter: 'saturate(0.88) brightness(1.0) blur(0px)',
      transform: 'scale(1.002)',
      opacity: 0.78,
    },
    '&::after': {
      content: '""',
      position: 'absolute',
      inset: 0,
      background: `linear-gradient(90deg, rgba(248, 244, 237, 0.18) 0%, rgba(248, 244, 237, 0.32) 48%, rgba(248, 244, 237, 0.56) 100%)`,
    },
    '@media (max-width: 860px)': {
      '&::before': {
        backgroundPosition: 'center',
        opacity: 0.32,
      },
      '&::after': {
        background: 'rgba(248, 244, 237, 0.56)',
      },
    },
  },
  shell: {
    flex: 1,
    width: '100%',
    maxWidth: 1180,
    margin: '0 auto',
    padding: '72px 28px 32px',
    display: 'grid',
    gridTemplateColumns: '1fr 400px',
    gap: 64,
    alignItems: 'center',
    position: 'relative' as const,
    zIndex: 2,
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
    minHeight: 620,
    display: 'flex',
    flexDirection: 'column' as const,
    justifyContent: 'center',
    gap: 12,
    opacity: 1,
    transform: 'translateY(18px)',
    pointerEvents: 'none',
    '@media (max-width: 860px)': {
      minHeight: 'auto',
      gap: 14,
      opacity: 1,
      transform: 'none',
    },
  },
  heroCopy: {
    width: 'fit-content',
    maxWidth: 470,
    padding: '16px 20px 18px',
    borderRadius: 8,
    background:
      'linear-gradient(90deg, rgba(248, 244, 237, 0.88) 0%, rgba(248, 244, 237, 0.64) 76%, rgba(248, 244, 237, 0) 100%)',
    borderLeft: '2px solid color-mix(in srgb, var(--earth-brown) 18%, transparent)',
    '@media (max-width: 860px)': {
      width: 'auto',
      maxWidth: 'none',
      padding: 0,
      background: 'transparent',
      borderLeft: 'none',
    },
  },
  heroTitle: {
    margin: 0,
    fontSize: 28,
    lineHeight: 1.18,
    fontWeight: 820,
    letterSpacing: 0,
    color: '#171827',
    textShadow: '0 1px 0 rgba(255, 255, 255, 0.9)',
    '@media (max-width: 860px)': {
      fontSize: 24,
    },
  },
  heroSubtitle: {
    margin: 0,
    maxWidth: 430,
    color: '#5f5146',
    fontSize: 15,
    lineHeight: 1.75,
    fontWeight: 500,
  },
  panelWrap: {
    gridColumn: '2',
    width: '400px',
    maxWidth: '100%',
    position: 'relative' as const,
    zIndex: 2,
    '@media (max-width: 860px)': {
      gridColumn: 'auto',
      width: '100%',
    },
  },
  panel: {
    width: '100%',
    padding: '32px',
    borderRadius: 8,
    background: 'rgba(255, 255, 255, 0.88)',
    border: '1px solid color-mix(in srgb, var(--earth-brown) 16%, transparent)',
    boxShadow: '0 18px 44px rgba(65, 50, 35, 0.13)',
    backdropFilter: 'blur(12px)',
    position: 'relative' as const,
    overflow: 'hidden',
    transition: 'opacity 220ms ease, transform 220ms ease',
    '& > *': {
      position: 'relative' as const,
      zIndex: 1,
    },
    '& .ant-form-item-label > label': {
      color: 'color-mix(in srgb, var(--earth-brown) 92%, var(--text-primary))',
      fontSize: 13,
      fontWeight: 600,
    },
    '& .ant-input-affix-wrapper': {
      minHeight: 44,
      borderRadius: 8,
      background: '#fffefa !important',
      borderColor: 'color-mix(in srgb, var(--earth-brown) 24%, transparent) !important',
      color: '#171827',
    },
    '& .ant-input-affix-wrapper-focused': {
      borderColor: 'color-mix(in srgb, var(--earth-brown) 52%, transparent) !important',
      boxShadow: '0 0 0 2px color-mix(in srgb, var(--earth-brown) 14%, transparent) !important',
    },
    '& .ant-input-affix-wrapper-status-error': {
      background: '#fffefa !important',
      borderColor: '#cf3f33 !important',
      boxShadow: '0 0 0 2px rgba(207, 63, 51, 0.1) !important',
    },
    '& .ant-input-prefix, & .ant-input-password-icon': {
      color: '#7a6a5c',
    },
    '& .ant-input-affix-wrapper-status-error .ant-input-prefix, & .ant-input-affix-wrapper-status-error .ant-input-password-icon': {
      color: '#9c3a31',
    },
    '& .ant-input': {
      background: 'transparent',
      color: '#171827 !important',
      fontSize: 14,
      fontWeight: 500,
      WebkitTextFillColor: '#171827',
    },
    '& .ant-input::placeholder': {
      color: '#a99e94',
      opacity: 1,
      fontWeight: 400,
      WebkitTextFillColor: '#a99e94',
    },
    '& .ant-input::-webkit-input-placeholder': {
      color: '#a99e94',
      opacity: 1,
      fontWeight: 400,
      WebkitTextFillColor: '#a99e94',
    },
    '& .ant-input::-moz-placeholder': {
      color: '#a99e94',
      opacity: 1,
      fontWeight: 400,
    },
    '& .ant-form-item-explain-error': {
      color: '#cf3f33',
      fontSize: 13,
      fontWeight: 500,
    },
    '& .ant-checkbox-wrapper': {
      color: 'color-mix(in srgb, var(--earth-brown) 90%, var(--text-primary))',
      fontSize: 13,
    },
    '& .ant-checkbox-checked .ant-checkbox-inner': {
      backgroundColor: '#6d53b6',
      borderColor: '#6d53b6',
    },
    '& .ant-checkbox-wrapper:hover .ant-checkbox-inner, & .ant-checkbox:hover .ant-checkbox-inner': {
      borderColor: '#6d53b6',
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
    color: 'color-mix(in srgb, var(--earth-brown) 74%, var(--text-primary))',
    fontSize: 12,
    fontWeight: 800,
    letterSpacing: 0,
  },
  panelTitle: {
    margin: 0,
    fontSize: 22,
    lineHeight: 1.3,
    fontWeight: 850,
    color: '#101022',
  },
  panelDesc: {
    margin: '8px 0 0',
    color: 'color-mix(in srgb, var(--earth-brown) 90%, var(--text-primary))',
    fontSize: 13,
    lineHeight: 1.65,
  },
  submitBtn: {
    height: '44px !important',
    borderRadius: '8px !important',
    border: 'none !important',
    background: '#6d53b6 !important',
    fontSize: '15px !important',
    fontWeight: 600,
    boxShadow: '0 8px 18px rgba(109, 83, 182, 0.18) !important',
    transition: 'background-color 160ms ease, opacity 160ms ease',
    '&:hover': {
      background: '#5f46a7 !important',
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
    minHeight: 30,
    display: 'inline-flex',
    alignItems: 'center',
    padding: 0,
    color: 'color-mix(in srgb, var(--earth-brown) 70%, var(--text-primary))',
    fontSize: 12,
  },
  '@media (prefers-reduced-motion: reduce)': {
    shell: {
      transition: 'none',
    },
    panel: {
      transition: 'none',
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
  const [entryTransition, setEntryTransition] = useState<
    'idle' | 'entering-workbench'
  >('idle');
  const { initialState, setInitialState } = useModel('@@initialState');
  const { styles, cx } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();

  const statusText = useMemo(
    () =>
      entryTransition === 'entering-workbench' ? '正在进入工作台…' : '进入工作台',
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

  const navigateToWorkbench = () => {
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
        setEntryTransition('entering-workbench');
        message.success(
          intl.formatMessage({
            id: 'pages.login.success',
            defaultMessage: '登录成功！',
          }),
        );
        window.setTimeout(navigateToWorkbench, LOGIN_TRANSITION_MS);
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
      <div
        className={styles.ambientBackdrop}
        data-testid="login-ambient-backdrop"
        aria-hidden="true"
      />
      <Helmet>
        <title>
          {intl.formatMessage({ id: 'menu.login', defaultMessage: '登录' })}
          {Settings.title && ` - ${Settings.title}`}
        </title>
      </Helmet>
      <Lang />
      <main
        className={cx(
          styles.shell,
          entryTransition === 'entering-workbench' && styles.shellEntering,
        )}
        data-testid="runtime-entry-shell"
        data-transition={entryTransition}
      >
        <section className={styles.panelWrap} aria-label="登录">
          <div
            className={cx(
              styles.panel,
              entryTransition === 'entering-workbench' && styles.panelEntering,
            )}
            data-testid="auth-card-login"
            data-surface="warm-paper"
          >
            <div className={styles.panelHeader}>
              <h2 className={styles.panelTitle}>登录</h2>
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
                  loading: entryTransition === 'entering-workbench',
                },
                resetButtonProps: {
                  style: { display: 'none' },
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
                <span className={styles.ghostAction}>注册功能后续开放</span>
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
