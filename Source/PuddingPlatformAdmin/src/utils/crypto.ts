import { sm2, sm3 } from 'sm-crypto';

/**
 * SM3 哈希工具
 */
export const SM3 = {
  /** 对字符串进行 SM3 哈希，返回 hex */
  hashString(str: string): string {
    return sm3(str);
  },

  /** 对数据进行 SM3 哈希 */
  hash(data: string | ArrayBuffer): string {
    if (typeof data === 'string') return sm3(data);
    // ArrayBuffer 转 hex string
    const bytes = new Uint8Array(data);
    const hex = Array.from(bytes).map(b => b.toString(16).padStart(2, '0')).join('');
    return sm3(hex);
  },

  /** HMAC-SM3 */
  hmac(message: string, key: string): string {
    // sm-crypto 不直接提供 HMAC-SM3，需要组合使用
    // 简单实现：对 key+message 进行 SM3
    return sm3(key + message);
  },
};

/**
 * SM2 非对称加密工具
 */
export const SM2 = {
  /** 生成 SM2 密钥对 */
  generateKeyPair(): { publicKey: string; privateKey: string } {
    const keypair = sm2.generateKeyPairHex();
    return { publicKey: keypair.publicKey, privateKey: keypair.privateKey };
  },

  /** SM2 加密 */
  encrypt(plainText: string, publicKey: string): string {
    return sm2.doEncrypt(plainText, publicKey, 0); // 0 = C1C3C2
  },

  /** SM2 解密 */
  decrypt(cipherText: string, privateKey: string): string {
    return sm2.doDecrypt(cipherText, privateKey, 0);
  },

  /** SM2 签名 */
  sign(message: string, privateKey: string): string {
    return sm2.doSignature(message, privateKey);
  },

  /** SM2 验签 */
  verify(message: string, signature: string, publicKey: string): boolean {
    return sm2.doVerifySignature(message, signature, publicKey);
  },
};
