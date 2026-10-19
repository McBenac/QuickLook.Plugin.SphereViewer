import { CONSTANTS } from '@photo-sphere-viewer/core';
import { DualFisheyeAdapter, DualFisheyeVideoAdapter } from '@photo-sphere-viewer/dual-fisheye-adapter';
import { Matrix3, Mesh, ShaderMaterial, SphereGeometry, Vector2, Vector3 } from 'three';

/**
 * Lens model for side-by-side dual-fisheye frames (left half = rear lens, right half = front lens).
 *
 * For a view direction d (x right, y up, z forward):
 *   lens frame  l = M^T d          M = base rotation (front: identity, rear: 180° about y) · R(yaw, pitch, roll)
 *   theta = acos(l.z), phi = atan(l.y, l.x)
 *   r     = k1·θ + k2·θ³ + k3·θ⁵     (radius in half-heights of the frame)
 *   pixel = centre + r·(cos φ, −sin φ) within that lens's half of the frame
 *
 * The Insta360 X5 values were fitted against a reference stitch (rms colour error ≈ 2 %).
 * Photos use ≈ 197° per lens; video modes crop the lenses slightly more (≈ 193°).
 */
export const LENS_PROFILES = {
  'insta360-x5-photo': {
    rear: { yaw: -0.00394, pitch: -0.00047, roll: -0.00868, cx: 0.50054, cy: 0.49881, k: [0.52771, 0.02174, -0.00108] },
    front: { yaw: -0.00686, pitch: 0.00224, roll: 0.01392, cx: 0.49919, cy: 0.50054, k: [0.52575, 0.02321, -0.00172] },
  },
  'insta360-x5-video': {
    rear: { yaw: -0.00487, pitch: 0.00036, roll: -0.0091, cx: 0.5007, cy: 0.49939, k: [0.53694, 0.01951, 0.00027] },
    front: { yaw: -0.0059, pitch: 0.00162, roll: 0.01431, cx: 0.49989, cy: 0.50075, k: [0.53541, 0.01941, 0.00035] },
  },
};

// Lenses are cross-faded over this many radians either side of the line where both are equally far
// off-axis, hiding the exposure step at the lens edges.
const BLEND = (5 * Math.PI) / 180;

const vertexShader = `
varying vec3 vPos;
void main() {
  vPos = position;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}`;

const fragmentShader = `
varying vec3 vPos;
uniform sampler2D map;
uniform float opacity;
uniform float blend;
uniform mat3 rot0;
uniform mat3 rot1;
uniform vec2 centre0;
uniform vec2 centre1;
uniform vec3 poly0;
uniform vec3 poly1;
uniform mat3 world2cam; // levels a tilted camera; identity when there is no motion data

vec2 lensUv(vec3 d, mat3 rot, vec2 centre, vec3 poly, float half_, out float theta) {
  vec3 l = rot * d;
  theta = acos(clamp(l.z, -1.0, 1.0));
  float phi = atan(l.y, l.x);
  float t2 = theta * theta;
  float r = theta * (poly.x + t2 * (poly.y + t2 * poly.z));
  vec2 p = clamp(centre + vec2(cos(phi), -sin(phi)) * r * 0.5, 0.0, 1.0);
  // half_ = 0 for the left half of the frame, 1 for the right; textures are flipped (v = 0 at the bottom).
  return vec2((p.x + half_) * 0.5, 1.0 - p.y);
}

void main() {
  vec3 pos = normalize(vPos);
  vec3 d = world2cam * vec3(-pos.x, pos.y, pos.z); // PSV sphere -> x right, y up, z forward
  float theta0;
  float theta1;
  vec2 uv0 = lensUv(d, rot0, centre0, poly0, 0.0, theta0);
  vec2 uv1 = lensUv(d, rot1, centre1, poly1, 1.0, theta1);
  float w = smoothstep(-blend, blend, theta0 - theta1);
  vec4 colour = mix(texture2D(map, uv0), texture2D(map, uv1), w);
  gl_FragColor = vec4(colour.rgb, colour.a * opacity);
}`;

function rotation({ yaw, pitch, roll }, base) {
  const m = () => new Matrix3();
  const ry = (a) => m().set(Math.cos(a), 0, Math.sin(a), 0, 1, 0, -Math.sin(a), 0, Math.cos(a));
  const rx = (a) => m().set(1, 0, 0, 0, Math.cos(a), -Math.sin(a), 0, Math.sin(a), Math.cos(a));
  const rz = (a) => m().set(Math.cos(a), -Math.sin(a), 0, Math.sin(a), Math.cos(a), 0, 0, 0, 1);
  // world -> lens = (base · Ry · Rx · Rz)^T
  return ry(base).multiply(ry(yaw)).multiply(rx(pitch)).multiply(rz(roll)).transpose();
}

/** Sphere mesh with the calibrated dual-fisheye shader. Only vertex positions are used (no uv), in PSV's world frame. */
export function createDualFisheyeMesh(profile) {
  const geometry = new SphereGeometry(CONSTANTS.SPHERE_RADIUS, 64, 32).scale(-1, 1, 1);
  const material = new ShaderMaterial({
    uniforms: {
      map: { value: null },
      opacity: { value: 1 },
      blend: { value: BLEND },
      rot0: { value: rotation(profile.rear, Math.PI) },
      rot1: { value: rotation(profile.front, 0) },
      centre0: { value: new Vector2(profile.rear.cx, profile.rear.cy) },
      centre1: { value: new Vector2(profile.front.cx, profile.front.cy) },
      poly0: { value: new Vector3(...profile.rear.k) },
      poly1: { value: new Vector3(...profile.front.k) },
      world2cam: { value: new Matrix3() },
    },
    vertexShader,
    fragmentShader,
    depthTest: false,
    depthWrite: false,
    transparent: true,
  });
  return new Mesh(geometry, material);
}

export class X5DualFisheyeAdapter extends DualFisheyeAdapter {
  static id = 'x5-dual-fisheye';

  createMesh() {
    this.mesh = createDualFisheyeMesh(LENS_PROFILES['insta360-x5-photo']);
    return this.mesh;
  }

  /** Rotates the sphere so that `up` (a unit vector in the camera frame) points straight up. */
  setCameraUp(up) {
    this.mesh?.material.uniforms.world2cam.value.copy(levelling(up));
  }
}

export class X5DualFisheyeVideoAdapter extends DualFisheyeVideoAdapter {
  static id = 'x5-dual-fisheye-video';

  createMesh() {
    this.mesh = createDualFisheyeMesh(LENS_PROFILES['insta360-x5-video']);
    return this.mesh;
  }

  /** Rotates the sphere so that `up` (a unit vector in the camera frame) points straight up. */
  setCameraUp(up) {
    this.mesh?.material.uniforms.world2cam.value.copy(levelling(up));
  }
}

/**
 * World -> camera rotation for a camera whose "up" direction is `up`: the shortest rotation that takes
 * up to (0, 1, 0), transposed. Heading follows the camera; only the tilt is removed.
 */
export function levelling([x, y, z]) {
  const m = new Matrix3();
  const c = y; // cos of the tilt
  if (c > 0.9999) return m;
  if (c < -0.9999) return m.set(1, 0, 0, 0, -1, 0, 0, 0, -1);
  // axis = up × (0,1,0) = (-z, 0, x); Rodrigues with k = 1 / (1 + c)
  const vx = -z;
  const vz = x;
  const k = 1 / (1 + c);
  // camera -> world
  m.set(
    1 - vz * vz * k, -vz, vx * vz * k,
    vz, c, -vx,
    vx * vz * k, vx, 1 - vx * vx * k,
  );
  return m.transpose();
}
