// ── AmbientParticles：背景粒子雾组件 ───────────────────────────────
// 在 Canvas 上渲染缓慢飘浮的粒子点，营造"空气感"。
// 粒子只做 position 偏移，不做烟花/爆炸/碰撞效果。
// 自动适配 prefers-reduced-motion（不渲染任何粒子）。

import React, { useEffect, useRef } from 'react';

export interface AmbientParticlesProps {
  /** 粒子数量（桌面 24-48，移动端 0-12） */
  count?: number;
  /** 运动周期范围 ms（默认 4000-6000） */
  speed?: [number, number];
  /** 粒子透明度范围（默认 0.15-0.3） */
  opacity?: [number, number];
  /** 容器 className */
  className?: string;
  /** 容器 style */
  style?: React.CSSProperties;
}

interface Particle {
  x: number;
  y: number;
  baseX: number;
  baseY: number;
  radius: number;
  phase: number;
  period: number;
  alpha: number;
  hue: number;
}

const TARGET_FPS = 24;
const FRAME_INTERVAL = 1000 / TARGET_FPS;

const AmbientParticles: React.FC<AmbientParticlesProps> = ({
  count = 36,
  speed = [4000, 6000],
  opacity: opacityRange = [0.25, 0.45],
  className,
  style,
}) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const animFrameRef = useRef<number>(0);
  const particlesRef = useRef<Particle[]>([]);

  useEffect(() => {
    // reduced-motion 下不渲染粒子
    const motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    if (motionQuery.matches) return;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    let w = 0;
    let h = 0;
    let dpr = window.devicePixelRatio || 1;

    const resize = () => {
      const rect = canvas.parentElement?.getBoundingClientRect();
      w = rect?.width ?? window.innerWidth;
      h = rect?.height ?? window.innerHeight;
      dpr = Math.min(window.devicePixelRatio || 1, 1.5);
      canvas.width = w * dpr;
      canvas.height = h * dpr;
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    };

    // 读取 CSS 变量获取粒子色调
    const getComputedColors = () => {
      const style = getComputedStyle(document.documentElement);
      const glow = style.getPropertyValue('--memory-glow').trim() || '#A78BFA';
      const accent =
        style.getPropertyValue('--accent-purple').trim() || '#7c3aed';
      return [glow, accent];
    };

    const hexToRgb = (hex: string): [number, number, number] => {
      const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
      return result
        ? [
            parseInt(result[1], 16),
            parseInt(result[2], 16),
            parseInt(result[3], 16),
          ]
        : [167, 139, 250];
    };

    const initParticles = () => {
      const [colorA, colorB] = getComputedColors();
      const rgbA = hexToRgb(colorA);
      const rgbB = hexToRgb(colorB);
      const particles: Particle[] = [];
      for (let i = 0; i < count; i++) {
        const useColorA = Math.random() > 0.5;
        const [r, g, b] = useColorA ? rgbA : rgbB;
        particles.push({
          x: Math.random() * w,
          y: Math.random() * h,
          baseX: Math.random() * w,
          baseY: Math.random() * h,
          radius: 1.5 + Math.random() * 4,
          phase: Math.random() * Math.PI * 2,
          period: speed[0] + Math.random() * (speed[1] - speed[0]),
          alpha:
            opacityRange[0] +
            Math.random() * (opacityRange[1] - opacityRange[0]),
          hue: (r << 16) | (g << 8) | b, // 打包为 int 便于动画循环使用
        });
      }
      particlesRef.current = particles;
    };

    resize();
    initParticles();

    const startTime = performance.now();
    let lastFrameTime = startTime;

    const animate = (now: number) => {
      if (now - lastFrameTime < FRAME_INTERVAL) {
        animFrameRef.current = requestAnimationFrame(animate);
        return;
      }
      lastFrameTime = now;

      const elapsed = now - startTime;
      ctx.clearRect(0, 0, w, h);

      for (const p of particlesRef.current) {
        // 使用 sin/cos 做缓慢椭圆漂移
        const t = (elapsed % p.period) / p.period;
        const angle = p.phase + t * Math.PI * 2;
        const driftR = 30 + Math.sin(p.phase * 3) * 25; // 漂移半径 30-55px
        p.x = p.baseX + Math.cos(angle) * driftR;
        p.y = p.baseY + Math.sin(angle * 0.7) * driftR * 0.6;

        const r = (p.hue >> 16) & 0xff;
        const g = (p.hue >> 8) & 0xff;
        const b = p.hue & 0xff;

        const glowSize = p.radius * 2;
        ctx.beginPath();
        ctx.arc(p.x, p.y, glowSize, 0, Math.PI * 2);
        ctx.fillStyle = `rgba(${r},${g},${b},${p.alpha * 0.45})`;
        ctx.fill();
      }

      animFrameRef.current = requestAnimationFrame(animate);
    };

    animFrameRef.current = requestAnimationFrame(animate);

    const onResize = () => {
      resize();
      initParticles();
    };
    window.addEventListener('resize', onResize);

    return () => {
      cancelAnimationFrame(animFrameRef.current);
      window.removeEventListener('resize', onResize);
    };
  }, [count, speed, opacityRange]);

  // reduced-motion 下渲染空 div
  const motionQuery =
    typeof window !== 'undefined'
      ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
      : false;

  return (
    <canvas
      ref={canvasRef}
      className={className}
      style={{
        position: 'absolute',
        inset: 0,
        width: '100%',
        height: '100%',
        pointerEvents: 'none',
        zIndex: 0,
        display: motionQuery ? 'none' : 'block',
        ...style,
      }}
    />
  );
};

export default AmbientParticles;
