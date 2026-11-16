import { createSilenceStop, defaultSilenceStopConfig } from './voiceSilenceStop';

describe('createSilenceStop', () => {
  const config = defaultSilenceStopConfig;

  it('预热期内即使安静也不会结束', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    expect(stop.push(50, 0, 40)).toBe(false);
    expect(stop.push(120, 0, 110)).toBe(false);
  });

  it('从未听到人声时不结束', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    let fired = false;
    for (let elapsed = 0; elapsed <= 10000; elapsed += 100) {
      fired = stop.push(elapsed, 0.01, elapsed) || fired;
    }
    expect(fired).toBe(false);
  });

  it('说完并静默达到阈值后结束', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    expect(stop.push(400, 0.5, 400)).toBe(false);
    expect(stop.push(800, 0.6, 800)).toBe(false);
    expect(stop.push(1300, 0, 1300)).toBe(false);
    expect(stop.push(1700, 0, 1700)).toBe(true);
  });

  it('中途停顿但未达静默时长不结束', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    stop.push(400, 0.5, 400);
    expect(stop.push(800, 0, 800)).toBe(false);
    expect(stop.push(1200, 0.5, 1200)).toBe(false);
  });

  it('只触发一次', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    stop.push(400, 0.5, 400);
    expect(stop.push(1400, 0, 1400)).toBe(true);
    expect(stop.push(2400, 0, 2400)).toBe(false);
  });

  it('reset 之后重新可用', () => {
    const stop = createSilenceStop(config);
    stop.reset(0);
    stop.push(400, 0.5, 400);
    expect(stop.push(1400, 0, 1400)).toBe(true);
    stop.reset(2000);
    stop.push(2400, 0.5, 400);
    expect(stop.push(3400, 0, 1400)).toBe(true);
  });
});
