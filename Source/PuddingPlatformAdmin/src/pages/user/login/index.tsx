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
import { Alert, App, Button } from 'antd';
import { createStyles } from 'antd-style';
import React, { useMemo, useState } from 'react';
import { flushSync } from 'react-dom';
import { Footer } from '@/components';
import { login } from '@/services/ant-design-pro/api';
import Settings from '../../../../config/defaultSettings';

const LOGIN_TRANSITION_MS = 240;

const sceneTags = ['Workspace', 'Agent', 'Skills', 'Chat'];

const normalizeRouteTarget = (target: string | null): string => {
  if (!target) return '/chat';

  try {
    const url = new URL(target, window.location.origin);
    const path = url.pathname.startsWith('/admin/')
      ? url.pathname.slice('/admin'.length)
      : url.pathname;
    return `${path || '/chat'}${url.search}${url.hash}`;
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
    background: `
      radial-gradient(circle at 19% 16%, rgba(255, 235, 174, 0.76), transparent 28%),
      radial-gradient(circle at 80% 78%, rgba(132, 176, 138, 0.22), transparent 30%),
      linear-gradient(145deg, #d9eadf 0%, #eef0d8 38%, #f7ead1 100%)
    `,
    color: 'var(--text-primary)',
    position: 'relative' as const,
    '&::before': {
      content: '""',
      position: 'fixed',
      inset: 0,
      pointerEvents: 'none',
      background: `
        linear-gradient(90deg, rgba(92, 74, 58, 0.04) 1px, transparent 1px),
        linear-gradient(0deg, rgba(92, 74, 58, 0.025) 1px, transparent 1px)
      `,
      backgroundSize: '96px 96px',
      maskImage: 'linear-gradient(180deg, rgba(0,0,0,0.18), transparent 70%)',
    },
    '& .ant-pro-global-footer': {
      margin: '0 0 16px',
      color: 'color-mix(in srgb, var(--earth-brown) 48%, transparent)',
    },
  },
  shell: {
    flex: 1,
    width: '100%',
    maxWidth: 1180,
    margin: '0 auto',
    padding: '72px 28px 32px',
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1.12fr) 400px',
    gap: 48,
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
    minHeight: 620,
    display: 'flex',
    flexDirection: 'column' as const,
    justifyContent: 'center',
    gap: 20,
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
    background: 'linear-gradient(145deg, #fff7d7, #d6e7bd)',
    border: '1px solid rgba(92, 74, 58, 0.14)',
    color: '#31513d',
    fontWeight: 800,
    boxShadow: '0 8px 24px rgba(73, 105, 74, 0.16)',
  },
  heroTitle: {
    margin: 0,
    fontSize: 30,
    lineHeight: 1.18,
    fontWeight: 800,
    letterSpacing: 0,
    color: 'var(--text-primary)',
    textShadow: '0 1px 0 rgba(255, 255, 255, 0.58)',
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
  sceneTitle: {
    margin: '4px 0 0',
    color: '#38593f',
    fontSize: 13,
    fontWeight: 700,
    letterSpacing: 0,
  },
  capabilityStrip: {
    display: 'flex',
    flexWrap: 'wrap' as const,
    gap: 8,
    margin: 0,
    padding: 0,
  },
  capability: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: 6,
    minHeight: 28,
    padding: '0 10px',
    borderRadius: 8,
    background: 'rgba(255, 251, 232, 0.7)',
    border: '1px solid rgba(92, 74, 58, 0.12)',
    color: '#49634b',
    fontSize: 12,
    fontWeight: 700,
    boxShadow: '0 6px 18px rgba(92, 74, 58, 0.07)',
  },
  capabilityDot: {
    width: 5,
    height: 5,
    borderRadius: '50%',
    background: '#d18f3f',
  },
  workshopScene: {
    width: 'min(620px, 100%)',
    aspectRatio: '1.45 / 1',
    position: 'relative' as const,
    borderRadius: 18,
    border: '1px solid rgba(92, 74, 58, 0.16)',
    background: `
      linear-gradient(180deg, #b9d6e8 0%, #dcecbf 52%, #9fbe76 100%)
    `,
    overflow: 'hidden',
    boxShadow:
      '0 28px 80px rgba(83, 92, 58, 0.18), inset 0 1px 0 rgba(255, 255, 255, 0.58)',
    isolation: 'isolate' as const,
    '&::before': {
      content: '""',
      position: 'absolute',
      inset: 0,
      background: `
        radial-gradient(circle at 18% 16%, rgba(255, 245, 183, 0.9), transparent 12%),
        radial-gradient(circle at 74% 20%, rgba(255, 255, 255, 0.54), transparent 15%),
        linear-gradient(120deg, rgba(255, 255, 255, 0.36), transparent 42%)
      `,
      zIndex: 1,
    },
    '&::after': {
      content: '""',
      position: 'absolute',
      inset: 0,
      background:
        'linear-gradient(0deg, rgba(54, 75, 44, 0.14), transparent 38%)',
      zIndex: 5,
      pointerEvents: 'none',
    },
    '@media (max-width: 860px)': {
      aspectRatio: '1.75 / 1',
      borderRadius: 14,
    },
    '@media (max-width: 520px)': {
      display: 'none',
    },
  },
  cloud: {
    position: 'absolute' as const,
    zIndex: 2,
    width: 110,
    height: 34,
    borderRadius: 999,
    background: 'rgba(255, 255, 255, 0.68)',
    boxShadow:
      '34px -10px 0 rgba(255,255,255,0.5), 72px 2px 0 rgba(255,255,255,0.42)',
    filter: 'blur(0.2px)',
  },
  mountainBack: {
    position: 'absolute' as const,
    left: '-4%',
    right: '-4%',
    bottom: '30%',
    height: '48%',
    zIndex: 2,
    background:
      'linear-gradient(135deg, #6b8a6a 0 34%, transparent 34%), linear-gradient(225deg, #88a97b 0 36%, transparent 36%)',
    opacity: 0.62,
  },
  hillFront: {
    position: 'absolute' as const,
    left: '-8%',
    right: '-8%',
    bottom: '-18%',
    height: '48%',
    zIndex: 3,
    borderRadius: '50% 50% 0 0',
    background: 'linear-gradient(180deg, #b7cb77 0%, #71965a 100%)',
    boxShadow: 'inset 0 20px 42px rgba(255, 255, 210, 0.24)',
  },
  workshopHouse: {
    position: 'absolute' as const,
    left: '35%',
    bottom: '22%',
    width: '30%',
    height: '32%',
    zIndex: 6,
    borderRadius: '10px 10px 16px 16px',
    background: 'linear-gradient(160deg, #f8dfaa 0%, #d29e65 100%)',
    border: '1px solid rgba(92, 74, 58, 0.22)',
    boxShadow:
      '0 18px 38px rgba(63, 65, 40, 0.22), inset 0 1px 0 rgba(255, 255, 255, 0.42)',
    '&::before': {
      content: '""',
      position: 'absolute',
      left: '-11%',
      right: '-11%',
      top: '-36%',
      height: '48%',
      borderRadius: '18px 18px 8px 8px',
      background:
        'linear-gradient(135deg, #6f4937 0%, #9b6541 62%, #c4894e 100%)',
      transform: 'skewX(-6deg)',
      boxShadow: '0 10px 18px rgba(65, 42, 28, 0.2)',
    },
    '&::after': {
      content: '""',
      position: 'absolute',
      left: '42%',
      bottom: 0,
      width: '18%',
      height: '48%',
      borderRadius: '8px 8px 0 0',
      background: 'linear-gradient(180deg, #6d4f3d, #3e2c25)',
    },
  },
  chimney: {
    position: 'absolute' as const,
    left: '57%',
    bottom: '48%',
    width: '4.5%',
    height: '15%',
    zIndex: 5,
    borderRadius: '5px 5px 0 0',
    background: '#7e563f',
    boxShadow: '0 -18px 28px rgba(255, 255, 255, 0.32)',
  },
  windowGlow: {
    position: 'absolute' as const,
    bottom: '33%',
    width: '9%',
    height: '10%',
    zIndex: 7,
    borderRadius: 5,
    background: 'linear-gradient(180deg, #fff6b8, #f0a94a)',
    border: '1px solid rgba(93, 62, 39, 0.28)',
    boxShadow: '0 0 22px rgba(255, 201, 93, 0.68)',
  },
  path: {
    position: 'absolute' as const,
    left: '42%',
    bottom: '-2%',
    width: '25%',
    height: '27%',
    zIndex: 4,
    borderRadius: '50% 50% 0 0',
    background:
      'linear-gradient(180deg, rgba(232, 201, 139, 0.88), rgba(151, 112, 72, 0.44))',
    clipPath: 'polygon(40% 0, 62% 0, 100% 100%, 0 100%)',
  },
  pine: {
    position: 'absolute' as const,
    zIndex: 5,
    width: 0,
    height: 0,
    borderLeft: '22px solid transparent',
    borderRight: '22px solid transparent',
    borderBottom: '82px solid #416944',
    filter: 'drop-shadow(0 10px 10px rgba(45, 68, 40, 0.2))',
    '&::before': {
      content: '""',
      position: 'absolute',
      left: -16,
      top: 24,
      width: 32,
      height: 38,
      borderRadius: '50% 50% 0 0',
      background: '#5c8752',
    },
  },
  signalTrail: {
    position: 'absolute' as const,
    left: '22%',
    right: '18%',
    bottom: '39%',
    height: 2,
    zIndex: 8,
    background:
      'linear-gradient(90deg, transparent, rgba(255, 245, 168, 0.92), rgba(80, 123, 83, 0.7), transparent)',
    boxShadow: '0 0 16px rgba(255, 225, 117, 0.56)',
    transform: 'rotate(-4deg)',
  },
  signalDot: {
    position: 'absolute' as const,
    zIndex: 9,
    width: 9,
    height: 9,
    borderRadius: '50%',
    background: '#fff1a6',
    boxShadow: '0 0 18px rgba(255, 230, 126, 0.85)',
    animation: 'workshopPulse 3.8s ease-in-out infinite',
  },
  sceneLabel: {
    position: 'absolute' as const,
    left: 18,
    bottom: 18,
    zIndex: 10,
    display: 'inline-flex',
    alignItems: 'center',
    gap: 8,
    minHeight: 34,
    padding: '0 12px',
    borderRadius: 999,
    background: 'rgba(255, 249, 222, 0.76)',
    border: '1px solid rgba(92, 74, 58, 0.14)',
    color: '#405d42',
    fontSize: 12,
    fontWeight: 800,
    boxShadow: '0 12px 28px rgba(72, 88, 56, 0.16)',
    backdropFilter: 'blur(8px)',
  },
  sceneTags: {
    position: 'absolute' as const,
    right: 16,
    top: 16,
    zIndex: 10,
    display: 'flex',
    flexWrap: 'wrap' as const,
    justifyContent: 'flex-end',
    gap: 8,
    maxWidth: 260,
  },
  sceneTag: {
    minHeight: 28,
    display: 'inline-flex',
    alignItems: 'center',
    borderRadius: 999,
    padding: '0 10px',
    background: 'rgba(255, 255, 240, 0.62)',
    border: '1px solid rgba(92, 74, 58, 0.12)',
    color: '#4c6547',
    fontSize: 12,
    fontWeight: 800,
    backdropFilter: 'blur(6px)',
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
    padding: '34px 34px',
    borderRadius: 18,
    background: `
      linear-gradient(180deg, rgba(255, 253, 244, 0.94), rgba(247, 236, 213, 0.9)),
      radial-gradient(circle at 16% 8%, rgba(255,255,255,0.7), transparent 28%)
    `,
    border: '1px solid rgba(92, 74, 58, 0.16)',
    boxShadow:
      '0 26px 70px rgba(74, 82, 54, 0.2), inset 0 1px 0 rgba(255, 255, 255, 0.72)',
    backdropFilter: 'blur(18px)',
    position: 'relative' as const,
    overflow: 'hidden',
    transition: 'opacity 220ms ease, transform 220ms ease',
    '&::before': {
      content: '""',
      position: 'absolute',
      inset: 0,
      pointerEvents: 'none',
      background:
        'linear-gradient(90deg, rgba(92,74,58,0.035) 1px, transparent 1px), linear-gradient(0deg, rgba(92,74,58,0.025) 1px, transparent 1px)',
      backgroundSize: '28px 28px',
      opacity: 0.42,
    },
    '& > *': {
      position: 'relative' as const,
      zIndex: 1,
    },
    '& .ant-form-item-label > label': {
      color: 'color-mix(in srgb, var(--earth-brown) 82%, transparent)',
      fontSize: 13,
      fontWeight: 500,
    },
    '& .ant-input-affix-wrapper': {
      minHeight: 44,
      borderRadius: 12,
      background: 'rgba(255, 253, 245, 0.72)',
      borderColor: 'rgba(92, 74, 58, 0.16)',
      boxShadow: 'inset 0 1px 0 rgba(255,255,255,0.6)',
    },
    '& .ant-input-affix-wrapper-focused': {
      borderColor: 'rgba(63, 105, 65, 0.55)',
      boxShadow: '0 0 0 3px rgba(107, 143, 90, 0.18)',
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
    fontWeight: 800,
    letterSpacing: 0,
  },
  panelTitle: {
    margin: 0,
    fontSize: 22,
    lineHeight: 1.3,
    fontWeight: 800,
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
    borderRadius: '12px !important',
    border: 'none !important',
    background: 'linear-gradient(135deg, #31583d, #6f8f4d) !important',
    fontSize: '15px !important',
    fontWeight: 600,
    boxShadow: '0 14px 28px rgba(55, 88, 58, 0.28) !important',
    transition: 'filter 160ms ease, opacity 160ms ease, transform 160ms ease',
    '&:hover': {
      filter: 'brightness(1.06)',
      transform: 'translateY(-1px)',
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
  '@keyframes workshopPulse': {
    '0%, 100%': { transform: 'scale(0.82)', opacity: 0.44 },
    '50%': { transform: 'scale(1.18)', opacity: 1 },
  },
  '@media (prefers-reduced-motion: reduce)': {
    shell: {
      transition: 'none',
    },
    panel: {
      transition: 'none',
    },
    signalDot: {
      animation: 'none',
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
    'idle' | 'entering-chat'
  >('idle');
  const { initialState, setInitialState } = useModel('@@initialState');
  const { styles, cx } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();

  const statusText = useMemo(
    () =>
      entryTransition === 'entering-chat' ? '正在进入 Chat…' : '进入工作台',
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
        className={cx(
          styles.shell,
          entryTransition === 'entering-chat' && styles.shellEntering,
        )}
        data-testid="runtime-entry-shell"
        data-transition={entryTransition}
      >
        <section
          className={styles.visual}
          data-testid="runtime-entry-visual"
          aria-label="Pudding Runtime"
        >
          <div className={styles.brandMark}>
            <span className={styles.brandDot}>P</span>
            <span>Pudding Runtime</span>
          </div>
          <div>
            <h1 className={styles.heroTitle}>本地 AI Agent 工作台</h1>
            <p className={styles.heroSubtitle}>
              森林边的本地运行工坊，连接工作空间、Agent 与
              Skills，安静地理解，可靠地执行。
            </p>
            <p className={styles.sceneTitle}>森林边的本地运行工坊</p>
          </div>
          <ul
            className={styles.capabilityStrip}
            aria-label="Runtime capabilities"
          >
            {sceneTags.map((item) => (
              <li className={styles.capability} key={item}>
                <span className={styles.capabilityDot} />
                {item}
              </li>
            ))}
          </ul>
          <div
            className={styles.workshopScene}
            data-testid="workshop-illustration"
            aria-hidden="true"
          >
            <div className={styles.cloud} style={{ left: '8%', top: '16%' }} />
            <div
              className={styles.cloud}
              style={{ right: '16%', top: '10%', transform: 'scale(0.72)' }}
            />
            <div className={styles.mountainBack} />
            <div className={styles.hillFront} />
            <div
              className={styles.pine}
              style={{ left: '12%', bottom: '23%', transform: 'scale(0.86)' }}
            />
            <div
              className={styles.pine}
              style={{ left: '18%', bottom: '17%', transform: 'scale(1.08)' }}
            />
            <div
              className={styles.pine}
              style={{ right: '18%', bottom: '18%', transform: 'scale(0.92)' }}
            />
            <div className={styles.chimney} />
            <div className={styles.workshopHouse}>
              <div className={styles.windowGlow} style={{ left: '18%' }} />
              <div className={styles.windowGlow} style={{ right: '18%' }} />
            </div>
            <div className={styles.path} />
            <div className={styles.signalTrail} />
            <div
              className={styles.signalDot}
              style={{ left: '23%', bottom: '39%' }}
            />
            <div
              className={styles.signalDot}
              style={{ left: '47%', bottom: '43%', animationDelay: '0.8s' }}
            />
            <div
              className={styles.signalDot}
              style={{ right: '22%', bottom: '36%', animationDelay: '1.6s' }}
            />
            <div className={styles.sceneTags}>
              {sceneTags.map((item) => (
                <span className={styles.sceneTag} key={item}>
                  {item}
                </span>
              ))}
            </div>
            <div className={styles.sceneLabel}>Local AI Workshop</div>
          </div>
        </section>

        <section className={styles.panelWrap} aria-label="登录">
          <div
            className={cx(
              styles.panel,
              entryTransition === 'entering-chat' && styles.panelEntering,
            )}
            data-testid="auth-card-login"
            data-surface="warm-paper"
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
