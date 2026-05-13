// ── ParticleSphere：3D 粒子球体（Canvas 2D + 物理模拟）─────────
// 稀疏发光粒子组成大球体，自行旋转 + 呼吸。
// 鼠标在球体附近时粒子受排斥力推开，离开后弹簧力拉回球面。
// 自动适配 prefers-reduced-motion。

import React, { useEffect, useRef } from 'react';

export interface ParticleSphereProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

interface SphereParticle {
  theta: number;
  phi: number;
  radius: number;
  alpha: number;
  px: number; vx: number;
  py: number; vy: number;
}

const PARTICLE_COUNT = 100;
const SPHERE_RADIUS = 60;
const REPEL_FORCE = 1800;
const REPEL_RADIUS = 110;
const SPRING_K = 0.06;
const DAMPING = 0.88;

const ParticleSphere: React.FC<ParticleSphereProps> = ({
  size = 280,
  className,
  style,
}) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const particlesRef = useRef<SphereParticle[]>([]);
  const mouseRef = useRef<{ x: number; y: number; active: boolean }>({ x: -999, y: -999, active: false });

  useEffect(() => {
    const motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    if (motionQuery.matches) return;

    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = size;
    const h = size;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    canvas.style.width = `${w}px`;
    canvas.style.height = `${h}px`;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const cx = w / 2;
    const cy = h / 2;

    const getGlowColor = (): [number, number, number] => {
      const s = getComputedStyle(document.documentElement).getPropertyValue('--memory-glow').trim() || '#A78BFA';
      const m = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(s);
      return m ? [parseInt(m[1],16), parseInt(m[2],16), parseInt(m[3],16)] : [167,139,250];
    };

    if (particlesRef.current.length === 0) {
      const parts: SphereParticle[] = [];
      for (let i = 0; i < PARTICLE_COUNT; i++) {
        const theta = Math.random() * Math.PI * 2;
        const phi = Math.acos(2 * Math.random() - 1);
        parts.push({
          theta, phi,
          radius: SPHERE_RADIUS + (Math.random() - 0.5) * 8,
          alpha: 0.2 + Math.random() * 0.5,
          px: 0, py: 0, vx: 0, vy: 0,
        });
      }
      particlesRef.current = parts;
    }

    const project = (theta: number, phi: number, r: number, rotY: number) => {
      const x3d = r * Math.sin(phi) * Math.cos(theta);
      const y3d = r * Math.sin(phi) * Math.sin(theta);
      const z3d = r * Math.cos(phi);
      // Y 轴旋转（纯正交投影，保持正圆）
      const rx = x3d * Math.cos(rotY) - z3d * Math.sin(rotY);
      const rz = x3d * Math.sin(rotY) + z3d * Math.cos(rotY);
      // 正交投影：无透视压缩，YZ平面微倾 5° 给深度暗示
      const ry = y3d * 0.996 - rz * 0.087;
      return { x: cx + rx, y: cy + ry, z: rz, scale: 1 };
    };

    let startTime = performance.now();

    const animate = (now: number) => {
      const elapsed = now - startTime;
      const rotY = elapsed * 0.0004;
      const breathe = 1 + Math.sin(elapsed * 0.0007) * 0.06;

      ctx.clearRect(0, 0, w, h);
      const [gr, gg, gb] = getGlowColor();
      const parts = particlesRef.current;
      const mouse = mouseRef.current;

      const projected = parts.map((p) => {
        const target = project(p.theta, p.phi, p.radius * breathe, rotY);
        const tx = target.x;
        const ty = target.y;
        if (p.px === 0 && p.py === 0) { p.px = tx; p.py = ty; }

        p.vx += (tx - p.px) * SPRING_K;
        p.vy += (ty - p.py) * SPRING_K;

        if (mouse.active) {
          const dx = p.px - mouse.x;
          const dy = p.py - mouse.y;
          const dist = Math.hypot(dx, dy);
          if (dist < REPEL_RADIUS && dist > 0.1) {
            const force = REPEL_FORCE / (dist * dist);
            p.vx += (dx / dist) * force;
            p.vy += (dy / dist) * force;
          }
        }

        p.vx *= DAMPING;
        p.vy *= DAMPING;
        p.px += p.vx;
        p.py += p.vy;

        return { x: p.px, y: p.py, z: target.z, scale: target.scale, alpha: p.alpha };
      });

      projected.sort((a, b) => a.z - b.z);

      for (const pp of projected) {
        const glowAlpha = pp.alpha * (0.35 + breathe * 0.5);
        const dotR = 2.4;

        const innerG = ctx.createRadialGradient(pp.x, pp.y, 0, pp.x, pp.y, dotR);
        innerG.addColorStop(0, `rgba(${gr},${gg},${gb},${glowAlpha})`);
        innerG.addColorStop(0.6, `rgba(${gr},${gg},${gb},${glowAlpha * 0.5})`);
        innerG.addColorStop(1, `rgba(${gr},${gg},${gb},0)`);
        ctx.beginPath();
        ctx.arc(pp.x, pp.y, dotR, 0, Math.PI * 2);
        ctx.fillStyle = innerG;
        ctx.fill();

        const outerR = dotR * 3;
        const outerG = ctx.createRadialGradient(pp.x, pp.y, dotR * 0.5, pp.x, pp.y, outerR);
        outerG.addColorStop(0, `rgba(${gr},${gg},${gb},${glowAlpha * 0.25})`);
        outerG.addColorStop(1, `rgba(${gr},${gg},${gb},0)`);
        ctx.beginPath();
        ctx.arc(pp.x, pp.y, outerR, 0, Math.PI * 2);
        ctx.fillStyle = outerG;
        ctx.fill();
      }

      requestAnimationFrame(animate);
    };

    const onMouseEnter = () => { mouseRef.current.active = true; };
    const onMouseLeave = () => {
      mouseRef.current.active = false;
      mouseRef.current.x = -999;
      mouseRef.current.y = -999;
    };
    const onMouseMove = (e: MouseEvent) => {
      const rect = canvas.getBoundingClientRect();
      mouseRef.current.x = e.clientX - rect.left;
      mouseRef.current.y = e.clientY - rect.top;
    };

    canvas.addEventListener('mouseenter', onMouseEnter);
    canvas.addEventListener('mouseleave', onMouseLeave);
    canvas.addEventListener('mousemove', onMouseMove);

    requestAnimationFrame(animate);

    return () => {
      canvas.removeEventListener('mouseenter', onMouseEnter);
      canvas.removeEventListener('mouseleave', onMouseLeave);
      canvas.removeEventListener('mousemove', onMouseMove);
    };
  }, [size]);

  return (
    <canvas
      ref={canvasRef}
      className={className}
      style={{
        cursor: 'pointer',
        display: 'block',
        ...style,
      }}
    />
  );
};

export default ParticleSphere;
