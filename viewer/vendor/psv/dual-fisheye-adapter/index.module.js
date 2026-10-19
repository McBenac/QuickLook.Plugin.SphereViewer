/*!
 * Photo Sphere Viewer / Dual Fisheye Adapter 5.15.1
 * @copyright 2015-2026 Damien "Mistic" Sorel
 * @licence MIT (https://opensource.org/licenses/MIT)
 */

// src/DualFisheyeAdapter.ts
import { AbstractAdapter, CONSTANTS, PSVError, utils } from "@photo-sphere-viewer/core";
import { Mesh, ShaderMaterial, SphereGeometry } from "three";

// src/shaders/dual-fisheye.fragment.glsl
var dual_fisheye_fragment_default = "varying vec3 vPos;\nuniform sampler2D map;\nuniform float opacity;\n\nconst float PI = 3.1415926535897932384626433832795;\nconst float C = 0.946; // Calibration factor for dual-fisheye disc mapping\n\nvoid main() {\n    vec3 dir = normalize(vPos);\n    float r = sqrt(dir.x * dir.x + dir.y * dir.y);\n\n    vec2 uv;\n    if (dir.z > 0.0) {\n        // Front hemisphere -> left half of the texture, centered at (0.25, 0.5)\n        float correction = r > 0.0 ? acos(dir.z) / r * (2.0 / PI) : 1.0;\n        uv.x = -dir.x * (C / 4.0) * correction + 0.25;\n        uv.y =  dir.y * (C / 2.0) * correction + 0.5;\n    } else {\n        // Back hemisphere -> right half of the texture, centered at (0.75, 0.5)\n        float correction = r > 0.0 ? acos(-dir.z) / r * (2.0 / PI) : 1.0;\n        uv.x =  dir.x * (C / 4.0) * correction + 0.75;\n        uv.y =  dir.y * (C / 2.0) * correction + 0.5;\n    }\n\n    gl_FragColor = texture2D(map, uv);\n    gl_FragColor.a *= opacity;\n}\n";

// src/shaders/dual-fisheye.vertex.glsl
var dual_fisheye_vertex_default = "varying vec3 vPos;\n\nvoid main() {\n    vPos = position;\n    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);\n}\n";

// src/DualFisheyeAdapter.ts
var _DualFisheyeAdapter = class _DualFisheyeAdapter extends AbstractAdapter {
  static withConfig(config) {
    return [_DualFisheyeAdapter, config];
  }
  constructor(viewer) {
    super(viewer);
  }
  supportsTransition() {
    return true;
  }
  supportsPreload() {
    return true;
  }
  async loadTexture(panorama, loader) {
    if (typeof panorama !== "string" && (typeof panorama !== "object" || !panorama.path)) {
      return Promise.reject(new PSVError("Invalid panorama url, are you using the right adapter?"));
    }
    let cleanPanorama;
    if (typeof panorama === "string") {
      cleanPanorama = {
        path: panorama
      };
    } else {
      cleanPanorama = {
        ...panorama
      };
    }
    const img = await this.viewer.textureLoader.loadImage(
      cleanPanorama.path,
      loader ? (p) => this.viewer.textureLoader.dispatchProgress(p) : null,
      cleanPanorama.path
    );
    const texture = utils.createSizedTexture(img);
    return {
      panorama,
      texture
    };
  }
  createMesh() {
    const geometry = new SphereGeometry(CONSTANTS.SPHERE_RADIUS, 32, 16).scale(-1, 1, 1);
    geometry.rotateX(-Math.PI / 2);
    geometry.rotateY(Math.PI);
    const material = new ShaderMaterial({
      uniforms: {
        map: { value: null },
        opacity: { value: 1 }
      },
      vertexShader: dual_fisheye_vertex_default,
      fragmentShader: dual_fisheye_fragment_default,
      depthTest: false,
      depthWrite: false,
      transparent: true
    });
    return new Mesh(geometry, material);
  }
  setTexture(mesh, textureData) {
    mesh.material.uniforms.map.value = textureData.texture;
  }
  setTextureOpacity(mesh, opacity) {
    mesh.material.uniforms.opacity.value = opacity;
  }
  disposeTexture({ texture }) {
    texture.dispose();
  }
  disposeMesh(mesh) {
    mesh.geometry.dispose();
    mesh.material.dispose();
  }
};
_DualFisheyeAdapter.id = "dual-fisheye";
_DualFisheyeAdapter.VERSION = "5.15.1";
_DualFisheyeAdapter.supportsDownload = true;
var DualFisheyeAdapter = _DualFisheyeAdapter;

// src/DualFisheyeVideoAdapter.ts
import { utils as utils2 } from "@photo-sphere-viewer/core";

// ../shared/AbstractVideoAdapter.ts
import { AbstractAdapter as AbstractAdapter2, PSVError as PSVError2 } from "@photo-sphere-viewer/core";
import { VideoTexture } from "three";

// ../shared/video-utils.ts
function createVideo({
  src,
  withCredentials,
  muted,
  autoplay
}) {
  const video = document.createElement("video");
  video.crossOrigin = withCredentials ? "use-credentials" : "anonymous";
  video.loop = true;
  video.playsInline = true;
  video.autoplay = autoplay;
  video.muted = muted;
  video.preload = "metadata";
  if (src instanceof MediaStream) {
    video.srcObject = src;
  } else {
    video.src = src;
  }
  return video;
}

// ../shared/AbstractVideoAdapter.ts
var AbstractVideoAdapter = class extends AbstractAdapter2 {
  constructor(viewer) {
    super(viewer);
  }
  init() {
    super.init();
    this.viewer.needsContinuousUpdate(true);
  }
  destroy() {
    this.__removeVideo();
    super.destroy();
  }
  supportsPreload() {
    return false;
  }
  supportsTransition() {
    return false;
  }
  async loadTexture(panorama) {
    if (typeof panorama !== "object" || !panorama.source) {
      return Promise.reject(new PSVError2("Invalid panorama configuration, are you using the right adapter?"));
    }
    if (!this.viewer.getPlugin("video")) {
      return Promise.reject(new PSVError2("Video adapters require VideoPlugin to be loaded too."));
    }
    const video = panorama.source instanceof HTMLVideoElement ? panorama.source : createVideo({
      src: panorama.source,
      withCredentials: this.viewer.config.withCredentials(panorama.source),
      muted: true,
      autoplay: false
    });
    await this.__videoLoadPromise(video);
    const texture = new VideoTexture(video);
    return { panorama, texture };
  }
  switchVideo(texture) {
    let currentTime;
    let duration;
    let paused = !this.config.autoplay;
    let muted = this.config.muted;
    let volume = 1;
    if (this.video) {
      ({ currentTime, duration, paused, muted, volume } = this.video);
    }
    this.__removeVideo();
    this.video = texture.image;
    if (this.video.duration === duration) {
      this.video.currentTime = currentTime;
    }
    this.video.muted = muted;
    this.video.volume = volume;
    if (!paused) {
      this.video.play();
    }
  }
  setTextureOpacity() {
  }
  disposeTexture({ texture }) {
    texture.dispose();
  }
  disposeMesh(mesh) {
    mesh.geometry.dispose();
    mesh.material.dispose();
  }
  __removeVideo() {
    if (this.video) {
      this.video.pause();
      this.video.remove();
      delete this.video;
    }
  }
  __videoLoadPromise(video) {
    return new Promise((resolve, reject) => {
      const onLoaded = () => {
        if (this.video && video.duration === this.video.duration) {
          video.currentTime = this.video.currentTime;
        }
        resolve();
        video.removeEventListener("loadedmetadata", onLoaded);
      };
      const onError = (err) => {
        reject(err);
        video.removeEventListener("error", onError);
      };
      video.addEventListener("loadedmetadata", onLoaded);
      video.addEventListener("error", onError);
    });
  }
};
AbstractVideoAdapter.supportsDownload = false;

// src/DualFisheyeVideoAdapter.ts
var getConfig = utils2.getConfigParser({
  autoplay: false,
  muted: false
});
var _DualFisheyeVideoAdapter = class _DualFisheyeVideoAdapter extends AbstractVideoAdapter {
  static withConfig(config) {
    return [_DualFisheyeVideoAdapter, config];
  }
  constructor(viewer, config) {
    super(viewer);
    this.config = getConfig(config);
    this.adapter = new DualFisheyeAdapter(this.viewer);
  }
  destroy() {
    this.adapter.destroy();
    delete this.adapter;
    super.destroy();
  }
  createMesh() {
    return this.adapter.createMesh();
  }
  setTexture(mesh, { texture }) {
    mesh.material.uniforms.map.value = texture;
    this.switchVideo(texture);
  }
};
_DualFisheyeVideoAdapter.id = "dual-fisheye-video";
_DualFisheyeVideoAdapter.VERSION = "5.15.1";
var DualFisheyeVideoAdapter = _DualFisheyeVideoAdapter;
export {
  DualFisheyeAdapter,
  DualFisheyeVideoAdapter
};
//# sourceMappingURL=index.module.js.map