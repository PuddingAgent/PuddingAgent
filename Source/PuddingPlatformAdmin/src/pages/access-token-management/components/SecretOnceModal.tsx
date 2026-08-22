import { Alert, Button, Checkbox, Modal, Space, Typography, message } from 'antd';
import { CopyOutlined, WarningOutlined } from '@ant-design/icons';
import React, { useState } from 'react';
import type { CreatedAccessTokenDto, ExternalApiStatusDto } from '@/services/platform/api';

/**
 * ADR-075 §10.4 一次性 Secret Modal。
 * 明文只存在于本组件的内存 props；确认关闭后由父组件清空，刷新/关闭后不可恢复。
 * 不提供 reveal；Secret 不进入 URL、analytics 或错误报告。
 */
export function SecretOnceModal({
  token,
  apiStatus,
  onClose,
}: {
  token: CreatedAccessTokenDto | null;
  apiStatus: ExternalApiStatusDto | null;
  onClose: () => void;
}) {
  const [ack, setAck] = useState(false);

  // token 变化（新创建）时重置确认态。
  const tokenId = token?.tokenId ?? null;
  React.useEffect(() => {
    setAck(false);
  }, [tokenId]);

  const baseUrl = apiStatus?.publicBaseUrl ?? apiStatus?.boundBaseUrl ?? '<base-url>';

  const copy = async (text: string, tip: string) => {
    try {
      await navigator.clipboard.writeText(text);
      message.success(tip);
    } catch {
      message.error('复制失败，请手动复制');
    }
  };

  return (
    <Modal
      open={token !== null}
      title={
        <span>
          <WarningOutlined style={{ color: '#faad14', marginRight: 8 }} />
          Access Token 已创建 — 仅此一次显示
        </span>
      }
      width={640}
      maskClosable={false}
      closable={ack}
      destroyOnClose
      okText="我已安全保存并关闭"
      okButtonProps={{ disabled: !ack }}
      cancelButtonProps={{ disabled: !ack, style: { display: 'none' } }}
      onOk={onClose}
      onCancel={() => {
        if (ack) onClose();
      }}
    >
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 12 }}
        message="关闭此窗口后 Token 明文不可恢复；丢失只能创建新 Token 并撤销旧 Token。请勿写入日志、URL 或代码仓库。"
      />
      {token && (
        <>
          <Typography.Paragraph copyable={{ text: token.accessToken }}>
            <Typography.Text code style={{ wordBreak: 'break-all' }}>
              {token.accessToken}
            </Typography.Text>
          </Typography.Paragraph>
          <Space wrap style={{ marginBottom: 12 }}>
            <Button
              size="small"
              icon={<CopyOutlined />}
              onClick={() => copy(token.accessToken, 'Token 已复制')}
            >
              复制 Token
            </Button>
            <Button
              size="small"
              icon={<CopyOutlined />}
              onClick={() =>
                copy(
                  `curl -H "Authorization: Bearer ${token.accessToken}" ${baseUrl}/api/external/v1/token`,
                  'curl 命令已复制',
                )
              }
            >
              复制 curl 自检
            </Button>
            <Button
              size="small"
              icon={<CopyOutlined />}
              onClick={() =>
                copy(
                  `$headers = @{ Authorization = "Bearer ${token.accessToken}" }\nInvoke-RestMethod -Headers $headers "${baseUrl}/api/external/v1/token"`,
                  'PowerShell 命令已复制',
                )
              }
            >
              复制 PowerShell 自检
            </Button>
          </Space>
        </>
      )}
      <Checkbox checked={ack} onChange={(e) => setAck(e.target.checked)}>
        我已把 Token 安全保存，理解关闭后无法再次查看
      </Checkbox>
    </Modal>
  );
}
