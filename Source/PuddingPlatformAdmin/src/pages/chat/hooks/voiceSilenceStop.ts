/**
 * 免按键录音的静音判定。
 *
 * 采集侧（cloudVoiceInput）每帧回调一次能量值，这里只做状态机：听到人声之后
 * 连续安静达到阈值就返回 true，让调用方结束录音并自动发送。
 * 判定逻辑独立成纯函数，便于用假时钟覆盖“预热、从未说话、说完即停、只触发一次”。
 */
export type SilenceStopConfig = {
  /** 能量阈值，采集侧已做 min(1, rms * 4) 归一化 */
  threshold: number;
  /** 说完之后需要连续安静的时长 */
  silenceMs: number;
  /** 开麦初期的预热，避免起音前的底噪被当成结束 */
  warmupMs: number;
};

export const defaultSilenceStopConfig: SilenceStopConfig = {
  threshold: 0.08,
  silenceMs: 900,
  warmupMs: 350,
};

export type SilenceStop = {
  /** 开始一段新录音时重置，elapsedMs 归零点由采集侧提供 */
  reset(now: number): void;
  /** 每帧调用；返回 true 表示应当结束录音，且只会返回一次 */
  push(now: number, level: number, elapsedMs: number): boolean;
};

export function createSilenceStop(config: SilenceStopConfig = defaultSilenceStopConfig): SilenceStop {
  let spoken = false;
  let fired = false;
  let lastVoiceAt = 0;

  return {
    reset(now: number) {
      spoken = false;
      fired = false;
      lastVoiceAt = now;
    },
    push(now: number, level: number, elapsedMs: number) {
      if (fired) return false;
      if (elapsedMs < config.warmupMs) return false;
      if (level >= config.threshold) {
        spoken = true;
        lastVoiceAt = now;
        return false;
      }
      if (!spoken) return false;
      if (now - lastVoiceAt < config.silenceMs) return false;
      fired = true;
      return true;
    },
  };
}
