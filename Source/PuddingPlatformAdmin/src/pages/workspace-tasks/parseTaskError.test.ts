import { parseTaskError, TASK_ERROR_HTTP_STATUS } from './types';

describe('parseTaskError（错误协议映射，TB-04 §5.4）', () => {
  it('从 axios 错误提取 code 与 httpStatus', () => {
    const error = {
      response: {
        status: 409,
        data: {
          code: 'task.version_conflict',
          message: '任务已被更新',
          traceId: 'trace-1',
          actualVersion: 3,
        },
      },
    };
    expect(parseTaskError(error)).toEqual({
      httpStatus: 409,
      body: {
        code: 'task.version_conflict',
        message: '任务已被更新',
        traceId: 'trace-1',
        actualVersion: 3,
      },
    });
  });

  it('无 body 时仅返回 httpStatus', () => {
    expect(parseTaskError({ response: { status: 500 } })).toEqual({
      httpStatus: 500,
      body: undefined,
    });
  });

  it('无 response 时 httpStatus 为 0', () => {
    expect(parseTaskError(new Error('network down'))).toEqual({
      httpStatus: 0,
      body: undefined,
    });
  });

  it('body 缺 code/message 时不误判为 TaskErrorResponse', () => {
    expect(
      parseTaskError({ response: { status: 422, data: { foo: 'bar' } } }),
    ).toEqual({ httpStatus: 422, body: undefined });
  });

  it('关键 code → HTTP 与契约一致', () => {
    expect(TASK_ERROR_HTTP_STATUS['task.version_conflict']).toBe(409);
    expect(TASK_ERROR_HTTP_STATUS['task.invalid_transition']).toBe(422);
    expect(TASK_ERROR_HTTP_STATUS['capability.missing']).toBe(403);
    expect(TASK_ERROR_HTTP_STATUS['task.not_found']).toBe(404);
  });
});
