import { LockOutlined, MailOutlined, UserOutlined, IdcardOutlined } from '@ant-design/icons';
import { ProForm, ProFormText } from '@ant-design/pro-components';
import { Helmet, history, useIntl } from '@umijs/max';
import { App } from 'antd';
import { createStyles } from 'antd-style';
import React from 'react';
import { Footer } from '@/components';
import Settings from '../../../config/defaultSettings';

const useStyles = createStyles(({ token }) => {
  return {
    container: {
      display: 'flex',
      flexDirection: 'column',
      height: '100vh',
      overflow: 'auto',
      background: 'linear-gradient(135deg, #f0f4ff 0%, #e8eeff 50%, #f5f0ff 100%)',
    },
    card: {
      flex: '1',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      padding: '32px 0',
    },
    content: {
      width: 420,
      maxWidth: '90vw',
      padding: '32px 24px',
      background: '#fff',
      borderRadius: token.borderRadiusLG,
      boxShadow: '0 2px 12px rgba(0,0,0,0.08)',
    },
    header: {
      textAlign: 'center' as const,
      marginBottom: 32,
    },
    logo: {
      width: 64,
      height: 64,
      marginBottom: 16,
    },
    title: {
      fontSize: 24,
      fontWeight: 600,
      color: token.colorTextHeading,
      marginBottom: 4,
    },
    desc: {
      fontSize: 14,
      color: token.colorTextSecondary,
    },
  };
});

const Bootstrap: React.FC = () => {
  const { styles } = useStyles();
  const { message } = App.useApp();
  const intl = useIntl();

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
        message.success('管理员账号创建成功，正在跳转...');
        window.location.href = '/';
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
        <div className={styles.content}>
          <div className={styles.header}>
            <img alt="Pudding" src="/admin/assets/images/logo.png" className={styles.logo} />
            <div className={styles.title}>Pudding Platform</div>
            <div className={styles.desc}>
              {intl.formatMessage({
                id: 'pages.bootstrap.desc',
                defaultMessage: '首次启动 - 创建管理员账号',
              })}
            </div>
          </div>

          <ProForm
            submitter={{
              searchConfig: {
                submitText: intl.formatMessage({
                  id: 'pages.bootstrap.submit',
                  defaultMessage: '创建管理员账号',
                }),
              },
              submitButtonProps: { block: true, size: 'large' },
            }}
            onFinish={handleSubmit}
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
