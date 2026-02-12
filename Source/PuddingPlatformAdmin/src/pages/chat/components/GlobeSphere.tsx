// ── GlobeSphere：矢量地球粒子球 ──────────────────────────────
// 紫色(memory-glow)=陆地/海岸线，蓝色(tool-signal)=海洋。

import React, { useEffect, useRef } from 'react';

import {
  EARTH_LAND_POLYGONS,
  type EarthLandPolygon,
  type LonLat,
} from './earthVectorData';

export interface GlobeSphereProps {
  size?: number;
  className?: string;
  style?: React.CSSProperties;
}

type GlobeKind = 'land' | 'ocean' | 'coast';

interface PreparedLandPolygon extends EarthLandPolygon {
  readonly bbox: readonly [
    minLon: number,
    minLat: number,
    maxLon: number,
    maxLat: number,
  ];
}

interface CoastSegment {
  readonly a: LonLat;
  readonly b: LonLat;
  readonly weight: number;
}

interface GlobeParticle {
  readonly kind: GlobeKind;
  readonly theta: number;
  readonly phi: number;
  readonly alpha: number;
  px: number;
  py: number;
  vx: number;
  vy: number;
}

interface ProjectedParticle {
  readonly x: number;
  readonly y: number;
  readonly z: number;
  readonly alpha: number;
  readonly kind: GlobeKind;
}

const LAND_PARTICLE_COUNT = 320;
const OCEAN_PARTICLE_COUNT = 420;
const COAST_PARTICLE_COUNT = 140;
const MAX_SAMPLE_ATTEMPTS = 90000;
const TILT = (23.5 * Math.PI) / 180;
const SPRING = 0.11;
const DAMPING = 0.82;
const REPEL_FORCE = 3000;
const REPEL_RADIUS = 120;
const TARGET_FPS = 30;
const FRAME_INTERVAL = 1000 / TARGET_FPS;

const PREPARED_LAND_POLYGONS: readonly PreparedLandPolygon[] =
  EARTH_LAND_POLYGONS.map((polygon) => {
    const lons = polygon.ring.map(([lon]) => lon);
    const lats = polygon.ring.map(([, lat]) => lat);
    return {
      ...polygon,
      bbox: [
        Math.min(...lons),
        Math.min(...lats),
        Math.max(...lons),
        Math.max(...lats),
      ],
    };
  });

const COAST_SEGMENTS: readonly CoastSegment[] = PREPARED_LAND_POLYGONS.flatMap(
  (polygon) => {
    return polygon.ring.map((a, index) => {
      const b = polygon.ring[(index + 1) % polygon.ring.length];
      const midLat = (((a[1] + b[1]) / 2) * Math.PI) / 180;
      const dx = (b[0] - a[0]) * Math.cos(midLat);
      const dy = b[1] - a[1];
      return { a, b, weight: Math.max(0.01, Math.hypot(dx, dy)) };
    });
  },
);

const TOTAL_COAST_WEIGHT = COAST_SEGMENTS.reduce(
  (sum, segment) => sum + segment.weight,
  0,
);

/** 判断经纬度点是否落在一个闭合多边形外环内。 */
function pointInRing(
  lon: number,
  lat: number,
  ring: readonly LonLat[],
): boolean {
  let inside = false;

  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const [xi, yi] = ring[i];
    const [xj, yj] = ring[j];
    const crossesLatitude = yi > lat !== yj > lat;
    const intersectLon =
      ((xj - xi) * (lat - yi)) / (yj - yi + Number.EPSILON) + xi;

    if (crossesLatitude && lon < intersectLon) {
      inside = !inside;
    }
  }

  return inside;
}

/** 使用陆地矢量多边形命中检测；海洋是该结果在球面上的补集。 */
function isLandCoordinate(lat: number, lon: number): boolean {
  return PREPARED_LAND_POLYGONS.some((polygon) => {
    const [minLon, minLat, maxLon, maxLat] = polygon.bbox;

    if (lon < minLon || lon > maxLon || lat < minLat || lat > maxLat) {
      return false;
    }

    return pointInRing(lon, lat, polygon.ring);
  });
}

/** 固定种子随机数让地球粒子布局稳定，避免每次刷新大陆形状漂移。 */
function createSeededRandom(seed: number): () => number {
  let state = seed >>> 0;

  return () => {
    state += 0x6d2b79f5;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function toAngles(
  lat: number,
  lon: number,
): Pick<GlobeParticle, 'theta' | 'phi'> {
  return {
    theta: ((lon + 180) * Math.PI) / 180,
    phi: ((90 - lat) * Math.PI) / 180,
  };
}

function createParticle(
  kind: GlobeKind,
  lat: number,
  lon: number,
  alpha: number,
): GlobeParticle {
  const { theta, phi } = toAngles(lat, lon);
  return {
    kind,
    theta,
    phi,
    alpha,
    px: Number.NaN,
    py: Number.NaN,
    vx: 0,
    vy: 0,
  };
}

function createSurfaceParticle(
  kind: 'land' | 'ocean',
  random: () => number,
): GlobeParticle | undefined {
  for (let attempt = 0; attempt < MAX_SAMPLE_ATTEMPTS; attempt++) {
    const theta = random() * Math.PI * 2;
    const phi = Math.acos(2 * random() - 1);
    const lat = 90 - (phi * 180) / Math.PI;
    const lon = (theta * 180) / Math.PI - 180;
    const isLand = isLandCoordinate(lat, lon);

    if ((kind === 'land' && isLand) || (kind === 'ocean' && !isLand)) {
      const alpha =
        kind === 'land' ? 0.42 + random() * 0.34 : 0.15 + random() * 0.18;
      return {
        kind,
        theta,
        phi,
        alpha,
        px: Number.NaN,
        py: Number.NaN,
        vx: 0,
        vy: 0,
      };
    }
  }

  return undefined;
}

/** 沿陆海边界矢量采样，增强海岸线轮廓可读性。 */
function createCoastParticle(random: () => number): GlobeParticle {
  let cursor = random() * TOTAL_COAST_WEIGHT;
  let selected = COAST_SEGMENTS[0];

  for (const segment of COAST_SEGMENTS) {
    cursor -= segment.weight;
    if (cursor <= 0) {
      selected = segment;
      break;
    }
  }

  const t = random();
  const jitter = 0.35;
  const lon =
    selected.a[0] +
    (selected.b[0] - selected.a[0]) * t +
    (random() - 0.5) * jitter;
  const lat =
    selected.a[1] +
    (selected.b[1] - selected.a[1]) * t +
    (random() - 0.5) * jitter;

  return createParticle(
    'coast',
    lat,
    Math.max(-180, Math.min(180, lon)),
    0.66 + random() * 0.22,
  );
}

function createGlobeParticles(): GlobeParticle[] {
  const random = createSeededRandom(0x50756464);
  const particles: GlobeParticle[] = [];

  while (
    particles.filter((particle) => particle.kind === 'land').length <
    LAND_PARTICLE_COUNT
  ) {
    const particle = createSurfaceParticle('land', random);
    if (!particle) break;
    particles.push(particle);
  }

  while (
    particles.filter((particle) => particle.kind === 'ocean').length <
    OCEAN_PARTICLE_COUNT
  ) {
    const particle = createSurfaceParticle('ocean', random);
    if (!particle) break;
    particles.push(particle);
  }

  for (let index = 0; index < COAST_PARTICLE_COUNT; index++) {
    particles.push(createCoastParticle(random));
  }

  return particles;
}

function parseCssColor(
  variableName: string,
  fallback: [number, number, number],
): [number, number, number] {
  const value = getComputedStyle(document.documentElement)
    .getPropertyValue(variableName)
    .trim();
  const match = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(value);

  if (!match) {
    return fallback;
  }

  return [
    parseInt(match[1], 16),
    parseInt(match[2], 16),
    parseInt(match[3], 16),
  ];
}

const GlobeSphere: React.FC<GlobeSphereProps> = ({
  size = 300,
  className,
  style,
}) => {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const particlesRef = useRef<GlobeParticle[]>([]);
  const mouseRef = useRef({ x: -999, y: -999, active: false });

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return undefined;

    const context = canvas.getContext('2d');
    if (!context) return undefined;

    const dpr = Math.min(devicePixelRatio || 1, 1.5);
    const width = size;
    const height = size;
    const centerX = width / 2;
    const centerY = height / 2;
    const radius = width * 0.4;
    const reduceMotion = window.matchMedia?.(
      '(prefers-reduced-motion: reduce)',
    ).matches;

    canvas.width = width * dpr;
    canvas.height = height * dpr;
    canvas.style.width = `${width}px`;
    canvas.style.height = `${height}px`;
    context.setTransform(dpr, 0, 0, dpr, 0, 0);

    if (particlesRef.current.length === 0) {
      particlesRef.current = createGlobeParticles();
    }

    const project = (phi: number, theta: number, rotationY: number) => {
      const x = radius * Math.sin(phi) * Math.cos(theta);
      const y = radius * Math.cos(phi);
      const z = radius * Math.sin(phi) * Math.sin(theta);
      const rotatedX = x * Math.cos(rotationY) - z * Math.sin(rotationY);
      const rotatedZ = x * Math.sin(rotationY) + z * Math.cos(rotationY);
      const tiltedY = y * Math.cos(TILT) - rotatedZ * Math.sin(TILT);
      const tiltedZ = y * Math.sin(TILT) + rotatedZ * Math.cos(TILT);

      return { x: centerX + rotatedX, y: centerY + tiltedY, z: tiltedZ };
    };

    const drawParticle = (
      particle: ProjectedParticle,
      landColor: [number, number, number],
      oceanColor: [number, number, number],
    ) => {
      const isOcean = particle.kind === 'ocean';
      const [red, green, blue] = isOcean ? oceanColor : landColor;
      const radiusScale =
        particle.kind === 'coast' ? 1.7 : isOcean ? 1.25 : 1.4;
      const alpha = particle.kind === 'coast' ? particle.alpha : particle.alpha;
      context.beginPath();
      context.arc(particle.x, particle.y, radiusScale, 0, Math.PI * 2);
      context.fillStyle = `rgba(${red},${green},${blue},${alpha})`;
      context.fill();
    };

    let animationFrame = 0;
    const startTime = performance.now();
    let lastFrameTime = startTime;

    const render = (now: number) => {
      if (!reduceMotion && now - lastFrameTime < FRAME_INTERVAL) {
        animationFrame = requestAnimationFrame(render);
        return;
      }
      lastFrameTime = now;

      const rotationY = reduceMotion ? 0.55 : (now - startTime) * 0.00022;
      const landColor = parseCssColor('--memory-glow', [167, 139, 250]);
      const oceanColor = parseCssColor('--tool-signal', [34, 211, 238]);
      const mouse = mouseRef.current;

      context.clearRect(0, 0, width, height);

      const projected = particlesRef.current.map((particle) => {
        const target = project(particle.phi, particle.theta, rotationY);

        if (!Number.isFinite(particle.px) || reduceMotion) {
          particle.px = target.x;
          particle.py = target.y;
        }

        particle.vx += (target.x - particle.px) * SPRING;
        particle.vy += (target.y - particle.py) * SPRING;

        if (mouse.active && !reduceMotion) {
          const dx = particle.px - mouse.x;
          const dy = particle.py - mouse.y;
          const distance = Math.hypot(dx, dy);

          if (distance < REPEL_RADIUS && distance > 0.1) {
            const force = REPEL_FORCE / (distance * distance);
            particle.vx += (dx / distance) * force;
            particle.vy += (dy / distance) * force;
          }
        }

        particle.vx *= DAMPING;
        particle.vy *= DAMPING;
        particle.px += particle.vx;
        particle.py += particle.vy;

        return {
          x: particle.px,
          y: particle.py,
          z: target.z,
          alpha: particle.alpha,
          kind: particle.kind,
        };
      });

      projected.sort((a, b) => a.z - b.z);

      for (const particle of projected) {
        drawParticle(particle, landColor, oceanColor);
      }

      if (!reduceMotion) {
        animationFrame = requestAnimationFrame(render);
      }
    };

    const handleMouseEnter = () => {
      mouseRef.current.active = true;
    };
    const handleMouseLeave = () => {
      mouseRef.current.active = false;
      mouseRef.current.x = -999;
      mouseRef.current.y = -999;
    };
    const handleMouseMove = (event: MouseEvent) => {
      const rect = canvas.getBoundingClientRect();
      mouseRef.current.x = event.clientX - rect.left;
      mouseRef.current.y = event.clientY - rect.top;
    };

    canvas.addEventListener('mouseenter', handleMouseEnter);
    canvas.addEventListener('mouseleave', handleMouseLeave);
    canvas.addEventListener('mousemove', handleMouseMove);
    render(startTime);

    return () => {
      if (animationFrame) {
        cancelAnimationFrame(animationFrame);
      }
      canvas.removeEventListener('mouseenter', handleMouseEnter);
      canvas.removeEventListener('mouseleave', handleMouseLeave);
      canvas.removeEventListener('mousemove', handleMouseMove);
    };
  }, [size]);

  return (
    <canvas
      ref={canvasRef}
      className={className}
      style={{ cursor: 'pointer', display: 'block', ...style }}
    />
  );
};

export default GlobeSphere;
