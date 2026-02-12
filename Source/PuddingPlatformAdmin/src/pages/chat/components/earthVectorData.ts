/**
 * Natural Earth land vectors for the chat empty-state globe.
 *
 * Data source:
 * - `world-atlas/land-110m.json`
 * - Derived from Natural Earth physical land boundaries, 1:110m scale
 * - Stored as TopoJSON in spherical lon/lat coordinates, then converted to
 *   GeoJSON-like polygon rings at module initialization.
 *
 * The ocean outline is the complement of these land polygons on the sphere, so
 * both purple land particles and blue ocean particles share the same coastline.
 */
import { feature } from 'topojson-client';
import landTopology from 'world-atlas/land-110m.json';

export type LonLat = readonly [lon: number, lat: number];

export interface EarthLandPolygon {
  /** Stable semantic name for debugging and future replacement by higher-resolution data. */
  readonly name: string;
  /** Exterior land boundary ring in lon/lat degrees; the ring is implicitly closed. */
  readonly ring: readonly LonLat[];
}

type GeoPosition = readonly [lon: number, lat: number, ...rest: number[]];
type GeoLinearRing = readonly GeoPosition[];
type GeoPolygonCoordinates = readonly GeoLinearRing[];
type GeoMultiPolygonCoordinates = readonly GeoPolygonCoordinates[];

interface GeoGeometry {
  readonly type: 'Polygon' | 'MultiPolygon';
  readonly coordinates: GeoPolygonCoordinates | GeoMultiPolygonCoordinates;
}

interface GeoFeature {
  readonly type: 'Feature';
  readonly geometry: GeoGeometry | null;
}

interface GeoFeatureCollection {
  readonly type: 'FeatureCollection';
  readonly features: readonly GeoFeature[];
}

function toLonLatRing(ring: GeoLinearRing): readonly LonLat[] {
  return ring.map(([lon, lat]) => [lon, lat] as const);
}

function extractExteriorRings(
  geometry: GeoGeometry,
): readonly (readonly LonLat[])[] {
  if (geometry.type === 'Polygon') {
    const [outerRing] = geometry.coordinates as GeoPolygonCoordinates;
    return outerRing ? [toLonLatRing(outerRing)] : [];
  }

  return (geometry.coordinates as GeoMultiPolygonCoordinates)
    .map(([outerRing]) => (outerRing ? toLonLatRing(outerRing) : []))
    .filter((ring) => ring.length >= 3);
}

function buildLandPolygons(): readonly EarthLandPolygon[] {
  const topology = landTopology as unknown as {
    readonly objects: { readonly land: unknown };
  };
  const geo = feature(
    landTopology as never,
    topology.objects.land as never,
  ) as unknown as GeoFeature | GeoFeatureCollection;
  const geometries =
    geo.type === 'FeatureCollection'
      ? geo.features.map((item) => item.geometry).filter(Boolean)
      : [geo.geometry];
  const polygons: EarthLandPolygon[] = [];

  for (const geometry of geometries) {
    if (!geometry) continue;

    for (const ring of extractExteriorRings(geometry)) {
      if (ring.length < 3) continue;
      polygons.push({
        name: `natural-earth-land-${polygons.length + 1}`,
        ring,
      });
    }
  }

  return polygons;
}

export const EARTH_LAND_POLYGONS: readonly EarthLandPolygon[] =
  buildLandPolygons();
