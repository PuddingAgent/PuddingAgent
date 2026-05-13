import { LockOutlined, UserOutlined } from '@ant-design/icons';
import { ProForm, ProFormCheckbox, ProFormText } from '@ant-design/pro-components';
import { FormattedMessage, Helmet, SelectLang, useIntl, useModel } from '@umijs/max';
import { Alert, App } from 'antd';
import { createStyles } from 'antd-style';
import React, { useState } from 'react';
import { flushSync } from 'react-dom';
import { Footer } from '@/components';
import { login } from '@/services/ant-design-pro/api';
import Settings from '../../../../config/defaultSettings';

const useStyles = createStyles(({ token }) => ({
  lang: {
    width: 42,
    height: 42,
    lineHeight: '42px',
    position: 'fixed',
    right: 16,
    top: 16,
    borderRadius: token.borderRadius,
    zIndex: 10,
    color: 'rgba(255,255,255,0.6)',
    ':hover': {
      backgroundColor: 'rgba(255,255,255,0.08)',
      color: 'rgba(255,255,255,0.9)',
    },
  },
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: '100vh',
    overflow: 'auto',
    background: '#0B1020',
    backgroundImage: `
      radial-gradient(ellipse 80% 60% at 50% -10%, rgba(124,58,237,0.12) 0%, transparent 100%),
      radial-gradient(ellipse 50% 40% at 80% 80%, rgba(59,130,246,0.06) 0%, transparent 100%),
      linear-gradient(180deg, #0B1020 0%, #070A12 100%)
    `,
    position: 'relative',
  },
  formArea: {
    flex: '1',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '32px 16px',
    position: 'relative' as const,
    zIndex: 1,
  },
  glassCard: {
    width: 400,
    maxWidth: '90vw',
    padding: '40px 36px',
    background: 'rgba(17,24,39,0.55)',
    backdropFilter: 'blur(32px) saturate(120%)',
    WebkitBackdropFilter: 'blur(32px) saturate(120%)',
    borderRadius: 16,
    border: '1px solid rgba(167,139,250,0.25)',
    boxShadow: '0 8px 64px rgba(0,0,0,0.6), 0 0 0 1px rgba(167,139,250,0.06) inset, 0 0 80px rgba(124,58,237,0.08)',
    animation: 'fadeIn 600ms ease-out',
    '& .ant-form-item-has-error .ant-input-affix-wrapper': { animation: 'none', borderColor: '#F87171' },
    '& .ant-form-item-has-error .ant-input': { animation: 'none' },
    '& .ant-input-affix-wrapper': {
      background: 'rgba(255,255,255,0.06)',
      borderColor: 'rgba(255,255,255,0.12)',
      borderRadius: 10,
      height: 44,
      transition: 'all 300ms ease',
    },
    '& .ant-input-affix-wrapper .ant-input': {
      background: 'transparent',
      color: '#E6EAF2',
      fontSize: 14,
      '&::placeholder': { color: 'rgba(255,255,255,0.25)' },
    },
    '& .ant-input-affix-wrapper-focused': {
      borderColor: '#7c3aed',
      boxShadow: '0 0 0 3px rgba(124,58,237,0.25), 0 0 32px rgba(167,139,250,0.12)',
    },
    '& .ant-input-prefix, & .ant-input-suffix': { color: 'rgba(255,255,255,0.35)' },
    '& .ant-input-password-icon': {
      color: 'rgba(255,255,255,0.35)',
      ':hover': { color: 'rgba(255,255,255,0.7)' },
    },
    '& .ant-checkbox-wrapper': { color: '#94A3B8', fontSize: 13 },
    '& .ant-form-item-label > label': { color: '#94A3B8', fontSize: 13 },
  },
  brand: {
    textAlign: 'center' as const,
    marginBottom: 36,
  },
  brandTitle: {
    fontSize: 32,
    fontWeight: 700,
    color: '#E6EAF2',
    letterSpacing: '0.03em',
    marginBottom: 8,
    textShadow: '0 0 60px rgba(167,139,250,0.2)',
  },
  brandSub: {
    fontSize: 14,
    color: '#94A3B8',
    lineHeight: 1.6,
    marginBottom: 4,
  },
  brandDivider: {
    width: 48,
    height: 1,
    background: 'linear-gradient(90deg, transparent, rgba(167,139,250,0.3), transparent)',
    margin: '12px auto 0',
  },
  submitBtn: {
    background: 'linear-gradient(135deg, #7c3aed 0%, #8B5CF6 100%) !important',
    border: 'none !important',
    height: '44px !important',
    fontSize: '16px !important',
    fontWeight: 600,
    borderRadius: '10px !important',
    position: 'relative' as const,
    overflow: 'hidden',
    transition: 'all 300ms ease',
    boxShadow: '0 4px 24px rgba(124,58,237,0.4)',
    color: '#fff !important',
    '&::after': {
      content: '""',
      position: 'absolute',
      top: 0,
      left: '-100%',
      width: '100%',
      height: '100%',
      background: 'linear-gradient(90deg, transparent, rgba(255,255,255,0.12), transparent)',
      transition: 'left 600ms ease',
    },
    '&:hover': {
      background: 'linear-gradient(135deg, #8B5CF6 0%, #A78BFA 100%) !important',
      boxShadow: '0 6px 32px rgba(124,58,237,0.6)',
      transform: 'translateY(-1px)',
    },
    '&:hover::after': { left: '100%' },
    '&:active': { transform: 'translateY(0)' },
  },
  errorAlert: {
    background: 'rgba(239,68,68,0.1) !important',
    borderColor: 'rgba(239,68,68,0.25) !important',
    borderRadius: '8px !important',
    marginBottom: '20px !important',
    '& .ant-alert-message': { color: '#FCA5A5' },
    '& .ant-alert-icon': { color: '#F87171' },
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
  const { initialState, setInitialState } = useModel('@@initialState');
  const { styles } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();

  const fetchUserInfo = async () => {
    const userInfo = await initialState?.fetchUserInfo?.();
    if (userInfo) {
      flushSync(() => {
        setInitialState((s) => ({ ...s, currentUser: userInfo }));
      });
    }
  };

  const handleSubmit = async (values: API.LoginParams) => {
    try {
      const msg = await login({ ...values, type: 'account' });
      if (msg.status === 'ok') {
        if (msg.token) {
          localStorage.setItem('pudding_token', msg.token);
        }
        message.success(
          intl.formatMessage({
            id: 'pages.login.success',
            defaultMessage: '登录成功！',
          }),
        );
        await fetchUserInfo();
        const urlParams = new URL(window.location.href).searchParams;
        window.location.href = urlParams.get('redirect') || '/admin/chat';
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
      <div className={styles.formArea}>
        <div className={styles.glassCard}>
          <div className={styles.brand}>
            <div className={styles.brandTitle}>Pudding Runtime</div>
            <div className={styles.brandSub}>
              {intl.formatMessage({
                id: 'pages.layouts.userLayout.title',
                defaultMessage: 'Quiet Runtime Space',
              })}
            </div>
            <div className={styles.brandDivider} />
          </div>

          <ProForm
            initialValues={{ autoLogin: true }}
            submitter={{
              searchConfig: {
                submitText: intl.formatMessage({
                  id: 'pages.login.submit',
                  defaultMessage: '进入',
                }),
              },
              submitButtonProps: {
                className: styles.submitBtn,
                block: true,
              },
            }}
            onFinish={async (values) => {
              await handleSubmit(values as API.LoginParams);
            }}
          >
            {status === 'error' && (
              <LoginMessage
                content={intl.formatMessage({
                  id: 'pages.login.accountLogin.errorMessage',
                  defaultMessage: '账户或密码错误，请重试',
                })}
              />
            )}

            <ProFormText
              name="username"
              fieldProps={{
                size: 'large',
                prefix: <UserOutlined />,
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
              fieldProps={{
                size: 'large',
                prefix: <LockOutlined />,
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

            <div style={{ marginBottom: 24 }}>
              <ProFormCheckbox noStyle name="autoLogin">
                <FormattedMessage
                  id="pages.login.rememberMe"
                  defaultMessage="自动登录"
                />
              </ProFormCheckbox>
            </div>
          </ProForm>
        </div>
      </div>
      <Footer />
    </div>
  );
};

export default Login;
