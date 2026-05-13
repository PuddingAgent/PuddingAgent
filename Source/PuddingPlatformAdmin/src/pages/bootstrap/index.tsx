import { LockOutlined, MailOutlined, UserOutlined, IdcardOutlined } from '@ant-design/icons';
import { ProForm, ProFormText } from '@ant-design/pro-components';
import { Helmet, useIntl } from '@umijs/max';
import { App } from 'antd';
import { createStyles } from 'antd-style';
import React, { useState } from 'react';
import { Footer } from '@/components';
import Settings from '../../../config/defaultSettings';

const STEPS = [
  { key: 'identity', label: 'Identity' },
  { key: 'seal', label: 'Admin Seal' },
  { key: 'enter', label: 'Enter Runtime' },
];

const useStyles = createStyles(({ token }) => ({
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
  card: {
    flex: '1',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '32px 16px',
  },
  glassCard: {
    width: 440,
    maxWidth: '90vw',
    padding: '36px 32px',
    background: 'rgba(17,24,39,0.55)',
    backdropFilter: 'blur(32px) saturate(120%)',
    WebkitBackdropFilter: 'blur(32px) saturate(120%)',
    borderRadius: 16,
    border: '1px solid rgba(167,139,250,0.25)',
    boxShadow: '0 8px 64px rgba(0,0,0,0.6), 0 0 0 1px rgba(167,139,250,0.06) inset, 0 0 80px rgba(124,58,237,0.08)',
    animation: 'fadeIn 600ms ease-out',
    '& .ant-form-item-has-error .ant-input-affix-wrapper': { animation: 'none', borderColor: '#F87171' },
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
    '& .ant-form-item-label > label': { color: '#94A3B8' },
  },
  header: {
    textAlign: 'center' as const,
    marginBottom: 28,
  },
  title: {
    fontSize: 32,
    fontWeight: 700,
    color: '#E6EAF2',
    marginBottom: 6,
    letterSpacing: '0.03em',
    textShadow: '0 0 60px rgba(167,139,250,0.2)',
  },
  subTitle: {
    fontSize: 14,
    color: '#94A3B8',
    marginBottom: 4,
  },
  divider: {
    width: 48,
    height: 1,
    background: 'linear-gradient(90deg, transparent, rgba(167,139,250,0.3), transparent)',
    margin: '12px auto 24px',
  },
  stepsRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 0,
  },
  stepNode: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: 6,
    position: 'relative' as const,
  },
  stepDot: {
    width: 12,
    height: 12,
    borderRadius: '50%',
    background: 'rgba(167,139,250,0.25)',
    boxShadow: '0 0 8px rgba(167,139,250,0.1)',
    transition: 'all 400ms ease',
  },
  stepDotActive: {
    background: '#A78BFA',
    boxShadow: '0 0 16px rgba(167,139,250,0.5), 0 0 32px rgba(167,139,250,0.2)',
  },
  stepDotDone: {
    background: '#22C55E',
    boxShadow: '0 0 12px rgba(34,197,94,0.4)',
  },
  stepLabel: {
    fontSize: 11,
    color: '#64748B',
    whiteSpace: 'nowrap' as const,
    transition: 'color 400ms ease',
  },
  stepLabelActive: { color: '#A78BFA' },
  stepLabelDone: { color: '#4ADE80' },
  stepLine: {
    width: 48,
    height: 1,
    background: 'rgba(167,139,250,0.15)',
    margin: '0 4px',
    marginBottom: 18,
    transition: 'background 400ms ease',
  },
  stepLineActive: { background: 'rgba(167,139,250,0.4)' },
  strengthPanel: {
    background: 'rgba(255,255,255,0.03)',
    borderRadius: 8,
    padding: '10px 14px',
    marginBottom: 20,
    border: '1px solid rgba(255,255,255,0.06)',
  },
  strengthItem: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    fontSize: 12,
    color: '#64748B',
    marginBottom: 4,
    transition: 'color 300ms ease',
    '&:last-child': { marginBottom: 0 },
  },
  strengthItemPass: { color: '#4ADE80' },
  strengthDot: {
    width: 6,
    height: 6,
    borderRadius: '50%',
    background: 'rgba(255,255,255,0.1)',
    transition: 'all 300ms ease',
    flexShrink: 0,
  },
  strengthDotPass: {
    background: '#4ADE80',
    boxShadow: '0 0 6px rgba(34,197,94,0.4)',
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
}));

const Bootstrap: React.FC = () => {
  const { styles } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();
  const [password, setPassword] = useState('');

  const hasMinLength = password.length >= 8;
  const hasLower = /[a-z]/.test(password);
  const hasUpper = /[A-Z]/.test(password);
  const hasDigit = /\d/.test(password);
  const allPass = hasMinLength && hasLower && hasUpper && hasDigit;

  const handleSubmit = async (values: Record<string, any>) => {
    if (values.password !== values.confirmPassword) {
      message.error('两次输入的密码不一致');
      return;
    }

    try {
      const res = await fetch('/api/bootstrap/admin', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          userId: values.userId,
          email: values.email,
          displayName: values.displayName,
          password: values.password,
        }),
      });

      const data = await res.json();

      if (res.ok && data.status === 'ok') {
        if (data.token) {
          localStorage.setItem('pudding_token', data.token);
        }
        message.success('Runtime 已完成封存，即将进入...');
        setTimeout(() => {
          window.location.href = '/admin/chat';
        }, 1800);
      } else {
        message.error(data.message || '初始化失败，请重试');
      }
    } catch {
      message.error('网络错误，请检查后端服务是否启动');
    }
  };

  return (
    <div className={styles.container}>
      <Helmet>
        <title>系统初始化 - {Settings.title}</title>
      </Helmet>
      <div className={styles.card}>
        <div className={styles.glassCard}>
          <div className={styles.header}>
            <div className={styles.title}>Initialize Pudding Runtime</div>
            <div className={styles.subTitle}>
              {intl.formatMessage({
                id: 'pages.bootstrap.desc',
                defaultMessage: '首次启动 — 创建管理员账号',
              })}
            </div>
            <div className={styles.divider} />

            <div className={styles.stepsRow}>
              {STEPS.map((step, i) => (
                <React.Fragment key={step.key}>
                  <div className={styles.stepNode}>
                    <div
                      className={`${styles.stepDot} ${
                        i === 0 || (i === 1 && allPass)
                          ? styles.stepDotActive
                          : i === 2 && allPass
                            ? styles.stepDotDone
                            : ''
                      }`}
                    />
                    <div
                      className={`${styles.stepLabel} ${i === 0 ? styles.stepLabelActive : ''} ${
                        i > 0 && allPass ? styles.stepLabelDone : ''
                      }`}
                    >
                      {step.label}
                    </div>
                  </div>
                  {i < STEPS.length - 1 && (
                    <div
                      className={`${styles.stepLine} ${
                        i < (allPass ? 2 : 1) ? styles.stepLineActive : ''
                      }`}
                    />
                  )}
                </React.Fragment>
              ))}
            </div>
          </div>

          {password.length > 0 && (
            <div className={styles.strengthPanel}>
              <div className={`${styles.strengthItem} ${hasMinLength ? styles.strengthItemPass : ''}`}>
                <div className={`${styles.strengthDot} ${hasMinLength ? styles.strengthDotPass : ''}`} />
                至少 8 个字符
              </div>
              <div className={`${styles.strengthItem} ${hasLower ? styles.strengthItemPass : ''}`}>
                <div className={`${styles.strengthDot} ${hasLower ? styles.strengthDotPass : ''}`} />
                包含小写字母
              </div>
              <div className={`${styles.strengthItem} ${hasUpper ? styles.strengthItemPass : ''}`}>
                <div className={`${styles.strengthDot} ${hasUpper ? styles.strengthDotPass : ''}`} />
                包含大写字母
              </div>
              <div className={`${styles.strengthItem} ${hasDigit ? styles.strengthItemPass : ''}`}>
                <div className={`${styles.strengthDot} ${hasDigit ? styles.strengthDotPass : ''}`} />
                包含数字
              </div>
            </div>
          )}

          <ProForm
            submitter={{
              searchConfig: {
                submitText: intl.formatMessage({
                  id: 'pages.bootstrap.submit',
                  defaultMessage: '创建管理员账号',
                }),
              },
              submitButtonProps: { className: styles.submitBtn, block: true, size: 'large' },
            }}
            onFinish={handleSubmit}
            onValuesChange={(changedValues) => {
              if ('password' in changedValues) {
                setPassword(changedValues.password || '');
              }
            }}
          >
            <ProFormText
              name="userId"
              fieldProps={{
                size: 'large',
                prefix: <IdcardOutlined />,
              }}
              placeholder={intl.formatMessage({
                id: 'pages.bootstrap.userId.placeholder',
                defaultMessage: '登录用户名 (如: admin)',
              })}
              rules={[
                { required: true, message: '请输入用户名' },
                { min: 3, message: '用户名至少3个字符' },
              ]}
            />

            <ProFormText
              name="email"
              fieldProps={{
                size: 'large',
                prefix: <MailOutlined />,
              }}
              placeholder={intl.formatMessage({
                id: 'pages.bootstrap.email.placeholder',
                defaultMessage: '邮箱地址',
              })}
              rules={[
                { required: true, message: '请输入邮箱' },
                { type: 'email', message: '请输入有效的邮箱地址' },
              ]}
            />

            <ProFormText
              name="displayName"
              fieldProps={{
                size: 'large',
                prefix: <UserOutlined />,
              }}
              placeholder={intl.formatMessage({
                id: 'pages.bootstrap.displayName.placeholder',
                defaultMessage: '显示名称 (可选)',
              })}
            />

            <ProFormText.Password
              name="password"
              fieldProps={{
                size: 'large',
                prefix: <LockOutlined />,
              }}
              placeholder={intl.formatMessage({
                id: 'pages.bootstrap.password.placeholder',
                defaultMessage: '密码 (≥8位, 含大小写+数字)',
              })}
              rules={[
                { required: true, message: '请输入密码' },
                {
                  pattern: /^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$/,
                  message: '密码至少8位，且包含大写字母、小写字母和数字',
                },
              ]}
            />

            <ProFormText.Password
              name="confirmPassword"
              fieldProps={{
                size: 'large',
                prefix: <LockOutlined />,
              }}
              placeholder={intl.formatMessage({
                id: 'pages.bootstrap.confirmPassword.placeholder',
                defaultMessage: '确认密码',
              })}
              rules={[
                { required: true, message: '请再次输入密码' },
                ({ getFieldValue }) => ({
                  validator(_, value) {
                    if (!value || getFieldValue('password') === value) {
                      return Promise.resolve();
                    }
                    return Promise.reject(new Error('两次输入的密码不一致'));
                  },
                }),
              ]}
            />
          </ProForm>
        </div>
      </div>
      <Footer />
    </div>
  );
};

export default Bootstrap;
