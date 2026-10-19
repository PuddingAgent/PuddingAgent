import {
  SIDE_PANEL_DEFAULT_WIDTH_PX,
  SIDE_PANEL_MAX_WIDTH_PX,
  SIDE_PANEL_MIN_WIDTH_PX,
  SIDE_PANEL_WIDTH_STORAGE_KEY,
  clampWidth,
  loadSavedWidth,
  saveWidth,
  sidePanelHostStyle,
  widthAfterDrag,
} from './sidePanelSizing';

describe('clampWidth', () => {
  it('低于下限夹到最小可读宽度', () => {
    expect(clampWidth(100)).toBe(SIDE_PANEL_MIN_WIDTH_PX);
  });

  it('高于上限夹到最大宽度', () => {
    expect(clampWidth(5000)).toBe(SIDE_PANEL_MAX_WIDTH_PX);
  });

  it('非有限数回落到默认值（NaN 参与运算会污染整行布局）', () => {
    expect(clampWidth(Number.NaN)).toBe(SIDE_PANEL_DEFAULT_WIDTH_PX);
    expect(clampWidth(Number.POSITIVE_INFINITY)).toBe(SIDE_PANEL_DEFAULT_WIDTH_PX);
  });

  it('区间内取整', () => {
    expect(clampWidth(432.7)).toBe(433);
  });
});

describe('widthAfterDrag', () => {
  // 面板在右侧：分隔条向左拖（deltaX 为负）应该让面板**变宽**。
  it('向左拖变宽', () => {
    expect(widthAfterDrag(420, -60)).toBe(480);
  });

  it('向右拖变窄', () => {
    expect(widthAfterDrag(420, 60)).toBe(360);
  });

  it('拖过头被夹住，不会越界', () => {
    expect(widthAfterDrag(420, -99999)).toBe(SIDE_PANEL_MAX_WIDTH_PX);
    expect(widthAfterDrag(420, 99999)).toBe(SIDE_PANEL_MIN_WIDTH_PX);
  });
});

describe('宽度持久化', () => {
  it('没有记录时用默认宽度', () => {
    expect(loadSavedWidth({ getItem: () => null })).toBe(SIDE_PANEL_DEFAULT_WIDTH_PX);
  });

  it('读到坏值不抛错，回落默认', () => {
    expect(loadSavedWidth({ getItem: () => 'not-a-number' })).toBe(SIDE_PANEL_DEFAULT_WIDTH_PX);
  });

  it('读到的值也会被夹进允许区间', () => {
    expect(loadSavedWidth({ getItem: () => '99999' })).toBe(SIDE_PANEL_MAX_WIDTH_PX);
  });

  it('storage 抛错（隐私模式）时回落默认，不影响布局', () => {
    expect(
      loadSavedWidth({
        getItem: () => {
          throw new Error('denied');
        },
      }),
    ).toBe(SIDE_PANEL_DEFAULT_WIDTH_PX);
  });

  it('写入前先夹值，并落到约定键上', () => {
    const written: Record<string, string> = {};
    saveWidth(99999, {
      setItem: (k: string, v: string) => {
        written[k] = v;
      },
    });
    expect(written[SIDE_PANEL_WIDTH_STORAGE_KEY]).toBe(String(SIDE_PANEL_MAX_WIDTH_PX));
  });

  it('写入抛错不向调用方冒泡', () => {
    expect(() =>
      saveWidth(420, {
        setItem: () => {
          throw new Error('quota');
        },
      }),
    ).not.toThrow();
  });
});

describe('sidePanelHostStyle', () => {
  it('宽度取像素值，硬顶取百分比，且不被内容挤扁', () => {
    const style = sidePanelHostStyle(500);
    expect(style.width).toBe('500px');
    expect(style.maxWidth).toBe('55%');
    expect(style.flex).toBe('0 0 auto');
  });
});
