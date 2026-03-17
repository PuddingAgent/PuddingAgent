/**
 * 校验外部 URL
 */
export function isExternal(path: string): boolean {
  return /^(https?:|mailto:|tel:)/.test(path)
}

/**
 * 校验用户名
 */
export function validUsername(str: string): boolean {
  return str.trim().length > 0
}
