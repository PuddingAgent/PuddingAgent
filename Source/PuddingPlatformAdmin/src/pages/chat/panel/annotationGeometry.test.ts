import { MIN_DRAG_PX, normalizePoint, normalizeRect } from './annotationGeometry';

/**
 * 标注几何换算：错得最隐蔽的地方。
 * 重点覆盖反向拖拽、越界裁剪、误触零尺寸、未布局容器。
 */
const box = { width: 400, height: 200 };

describe('normalizeRect', () => {
  it('正向拖拽换算成归一化矩形', () => {
    const rect = normalizeRect(box, { x: 40, y: 20 }, { x: 120, y: 60 });
    expect(rect).not.toBeNull();
    // 用 toBeCloseTo：0.1/0.2 在浮点下是 0.19999999999999998，精确相等无意义。
    expect(rect!.x).toBeCloseTo(0.1, 6);
    expect(rect!.y).toBeCloseTo(0.1, 6);
    expect(rect!.w).toBeCloseTo(0.2, 6);
    expect(rect!.h).toBeCloseTo(0.2, 6);
  });

  it('反向拖拽得到同样的矩形', () => {
    const forward = normalizeRect(box, { x: 40, y: 20 }, { x: 120, y: 60 });
    const backward = normalizeRect(box, { x: 120, y: 60 }, { x: 40, y: 20 });
    expect(backward).toEqual(forward);
  });

  it('超出容器的部分被裁剪到边界', () => {
    const rect = normalizeRect(box, { x: -100, y: -40 }, { x: 200, y: 100 });
    expect(rect).not.toBeNull();
    expect(rect!.x).toBe(0);
    expect(rect!.y).toBe(0);
    expect(rect!.w).toBeCloseTo(0.5, 5);
    expect(rect!.h).toBeCloseTo(0.5, 5);
  });

  it('完全落在容器外返回 null', () => {
    expect(normalizeRect(box, { x: 500, y: 300 }, { x: 600, y: 400 })).toBeNull();
  });

  it('小于阈值的拖拽视为误触', () => {
    expect(
      normalizeRect(box, { x: 10, y: 10 }, { x: 10 + MIN_DRAG_PX - 1, y: 40 }),
    ).toBeNull();
  });

  it('容器未布局（尺寸为 0）不产生标注，也不除零', () => {
    expect(normalizeRect({ width: 0, height: 0 }, { x: 0, y: 0 }, { x: 50, y: 50 })).toBeNull();
  });
});

describe('normalizePoint', () => {
  it('容器内点归一化', () => {
    expect(normalizePoint(box, { x: 200, y: 100 })).toEqual({ x: 0.5, y: 0.5 });
  });

  it('越界点返回 null', () => {
    expect(normalizePoint(box, { x: -1, y: 10 })).toBeNull();
    expect(normalizePoint(box, { x: 10, y: 201 })).toBeNull();
  });
});
