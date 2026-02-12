import type { RequestOptions } from '@@/plugin-request/request';
import type { RequestConfig } from '@umijs/max';
import { message, notification } from 'antd';

// 错误处理方案： 错误类型
enum ErrorShowType {
  SILENT = 0,
  WARN_MESSAGE = 1,
  ERROR_MESSAGE = 2,
  NOTIFICATION = 3,
  REDIRECT = 9,
}
// 与后端约定的响应数据格式
interface ResponseStructure {
  success: boolean;
  data: any;
  errorCode?: number;
  errorMessage?: string;
  showType?: ErrorShowType;
}

const readText = (value: unknown): string =>
  typeof value === 'string' ? value : '';

const isAbortLikeError = (error: any): boolean => {
  const name = readText(error?.name) || readText(error?.cause?.name);
  const code = readText(error?.code) || readText(error?.cause?.code);
  const messageText = [
    error?.message,
    error?.cause?.message,
    error?.response?.statusText,
  ]
    .map(readText)
    .join(' ')
    .toLowerCase();

  return (
    name === 'AbortError' ||
    code === 'ERR_CANCELED' ||
    code === 'ECONNABORTED' ||
    messageText.includes('abort') ||
    messageText.includes('cancel')
  );
};

const getHttpErrorMeta = (error: any) => {
  const response = error?.response ?? {};
  const config = response.config ?? error?.config ?? {};
  const method = readText(config.method ?? error?.method).toUpperCase();
  const url = readText(config.url ?? error?.url);
  const rawStatus = response.status ?? error?.status;
  const status = typeof rawStatus === 'number' ? rawStatus : Number(rawStatus);

  return {
    method,
    url,
    status: Number.isFinite(status) ? status : 0,
  };
};

const getResponseBodyMessage = (data: unknown): string => {
  try {
    if (!data) return '';
    if (typeof data === 'string') return data;
    if (typeof data === 'object') {
      const body = data as Record<string, unknown>;
      const messageText = readText(body.message) || readText(body.title);
      if (messageText) return messageText;
      return JSON.stringify(data).slice(0, 200);
    }
  } catch {
    /* ignore malformed response body */
  }
  return '';
};

const showHttpRequestError = (error: any) => {
  const { method, url, status } = getHttpErrorMeta(error);
  const bodyMsg = getResponseBodyMessage(error?.response?.data);

  if (status <= 0) {
    const requestLabel = [method, url].filter(Boolean).join(' ');
    const detail = [
      requestLabel,
      bodyMsg || '网络请求失败，请检查连接或刷新页面',
    ]
      .filter(Boolean)
      .join(' — ');
    message.error(detail);
    return;
  }

  const requestLabel = [method, url].filter(Boolean).join(' ');
  const detail = [requestLabel, `HTTP ${status}`, bodyMsg]
    .filter(Boolean)
    .join(' — ');
  message.error(detail || `HTTP ${status}`);
};

/**
 * @name 错误处理
 * pro 自带的错误处理， 可以在这里做自己的改动
 * @doc https://umijs.org/docs/max/request#配置
 */
export const errorConfig: RequestConfig = {
  // 错误处理： umi@3 的错误处理方案。
  errorConfig: {
    // 错误抛出
    errorThrower: (res) => {
      const { success, data, errorCode, errorMessage, showType } =
        res as unknown as ResponseStructure;
      if (!success) {
        const error: any = new Error(errorMessage);
        error.name = 'BizError';
        error.info = { errorCode, errorMessage, showType, data };
        throw error; // 抛出自制的错误
      }
    },
    // 错误接收及处理
    errorHandler: (error: any, opts: any) => {
      if (opts?.skipErrorHandler) throw error;
      if (isAbortLikeError(error)) return;
      // 我们的 errorThrower 抛出的错误。
      if (error.name === 'BizError') {
        const errorInfo: ResponseStructure | undefined = error.info;
        if (errorInfo) {
          const { errorMessage, errorCode } = errorInfo;
          switch (errorInfo.showType) {
            case ErrorShowType.SILENT:
              // do nothing
              break;
            case ErrorShowType.WARN_MESSAGE:
              message.warning(errorMessage);
              break;
            case ErrorShowType.ERROR_MESSAGE:
              message.error(errorMessage);
              break;
            case ErrorShowType.NOTIFICATION:
              notification.open({
                description: errorMessage,
                message: errorCode,
              });
              break;
            case ErrorShowType.REDIRECT:
              // TODO: redirect
              break;
            default:
              message.error(errorMessage);
          }
        }
      } else if (error.response) {
        // Axios 的错误：请求成功发出且服务器也响应了状态码，但超出了 2xx 范围
        showHttpRequestError(error);
      } else if (error.request) {
        // 请求已经成功发起，但没有收到响应
        message.error('无响应，请检查网络连接');
      } else {
        // 发送请求时出了点问题
        message.error('请求异常，请重试');
      }
    },
  },

  // 请求拦截器
  requestInterceptors: [
    (config: RequestOptions) => {
      const token = localStorage.getItem('pudding_token');
      if (token) {
        return {
          ...config,
          headers: { ...config.headers, Authorization: `Bearer ${token}` },
        };
      }
      return config;
    },
  ],

  // 响应拦截器
  responseInterceptors: [
    (response) => {
      // 401 → 清除 Token 并跳回登录页
      if ((response as any).status === 401) {
        localStorage.removeItem('pudding_token');
        if (window.location.pathname !== '/user/login') {
          window.location.href = '/user/login';
        }
        return response;
      }
      const { data } = response as unknown as ResponseStructure;
      if (data?.success === false) {
        message.error('请求失败！');
      }
      return response;
    },
  ],
};
