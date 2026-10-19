import { Viewer } from '@photo-sphere-viewer/core';
import { VideoPlugin } from '@photo-sphere-viewer/video-plugin';
import { X5DualFisheyeAdapter, X5DualFisheyeVideoAdapter } from './dual-fisheye.js';

// Query string set by the plugin: src (media URL), projection (equirect|dualfisheye), video (0|1), name.
const params = new URLSearchParams(location.search);
const src = params.get('src');
const projection = params.get('projection') || 'equirect';
const isVideo = params.get('video') === '1';

let reported = false;

/** Tells the host (QuickLook) how loading went. Also exposed on window for automated tests. */
function report(status) {
  if (reported) return;
  reported = true;
  window.sphereViewerStatus = status;
  window.chrome?.webview?.postMessage(status);
}

function fail(text) {
  if (reported) return; // late errors must not cover a working view
  const box = document.getElementById('message');
  box.textContent = text;
  box.hidden = false;
  report(`error:${text}`);
}

window.addEventListener('error', (e) => fail(e.message || 'Script error'));
window.addEventListener('unhandledrejection', (e) => fail(String(e.reason?.message || e.reason)));

// PSV renders the caption as HTML.
function escapeHtml(text) {
  return text.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

const FLAT_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="2" y="6" width="20" height="12" rx="1"/><circle cx="8" cy="12" r="3"/><circle cx="16" cy="12" r="3"/></svg>';

/**
 * Shows the unprojected original (both fisheye circles) on top of the sphere, and back.
 * For video the flat player takes over at the current time and hands it back on return.
 */
function flatViewButton() {
  const flat = document.getElementById('flat');
  const back = document.getElementById('flat-back');
  const error = document.getElementById('flat-error');
  const media = document.createElement(isVideo ? 'video' : 'img');
  if (isVideo) {
    media.controls = true;
    media.loop = true; // like the sphere video
  } else media.alt = 'Original dual-fisheye frame';
  media.addEventListener('error', () => {
    if (media.getAttribute('src')) error.hidden = false;
  });
  flat.prepend(media);

  let videoPlugin;
  const close = () => {
    flat.hidden = true;
    error.hidden = true;
    if (isVideo) {
      const playing = !media.paused;
      media.pause();
      if (media.readyState > 0) videoPlugin.setTime(media.currentTime);
      videoPlugin.setVolume(media.muted ? 0 : media.volume); // PSV volume is 0..1
      if (playing) videoPlugin.play();
      media.removeAttribute('src');
      media.load(); // release the decoder
    } else {
      media.removeAttribute('src'); // a 70 MP original is ~280 MB decoded; free it
    }
  };
  back.addEventListener('click', close);
  flat.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      close();
    }
  });

  return {
    id: 'flat-view',
    title: 'Show original (flat)',
    content: FLAT_ICON,
    onClick: (viewer) => {
      if (!media.getAttribute('src')) media.src = src;
      flat.hidden = false;
      back.focus();
      if (isVideo) {
        videoPlugin = viewer.getPlugin(VideoPlugin);
        media.volume = videoPlugin.getVolume();
        media.muted = videoPlugin.getVolume() === 0;
        const playing = videoPlugin.isPlaying();
        videoPlugin.pause();
        media.currentTime = videoPlugin.getTime();
        if (playing) media.play().catch(() => {}); // interrupted if the view closes at once
      }
    },
  };
}

function buildConfig() {
  if (projection === 'equirect' && !isVideo) {
    return { panorama: src, navbar: ['zoom', 'move', 'caption'] };
  }
  if (projection === 'dualfisheye' && !isVideo) {
    return { adapter: X5DualFisheyeAdapter, panorama: src, navbar: ['zoom', 'move', flatViewButton(), 'caption'] };
  }
  if (projection === 'dualfisheye' && isVideo) {
    return {
      adapter: [X5DualFisheyeVideoAdapter, { autoplay: true, muted: false }],
      panorama: { source: src },
      plugins: [[VideoPlugin, { progressbar: true, bigbutton: true }]],
      navbar: ['videoPlay', 'videoVolume', 'videoTime', 'zoom', 'move', flatViewButton(), 'caption'],
    };
  }
  throw new Error(`Unsupported media: ${projection}${isVideo ? ' video' : ''}`);
}

/**
 * Keeps the horizon level in video recorded with a tilted camera. `tilt` comes from the camera's
 * motion sensors (set by the plugin): tEnd = seconds before the last frame, up = camera-frame vectors.
 */
function levelHorizon(viewer, tilt) {
  const { tEnd, up } = tilt;
  if (!Array.isArray(tEnd) || tEnd.length < 2 || !Array.isArray(up) || up.length < 3 * tEnd.length) return;
  let i = 0;
  let lastTime = NaN;
  viewer.addEventListener('before-render', () => {
    const video = viewer.getPlugin(VideoPlugin);
    const duration = video.getDuration();
    if (!duration) return;
    const time = video.getTime();
    if (time === lastTime) return;
    lastTime = time;
    const t = time - duration; // seconds relative to the end, like tEnd
    while (i > 0 && tEnd[i] > t) i--;
    while (i < tEnd.length - 2 && tEnd[i + 1] <= t) i++;
    const f = Math.min(1, Math.max(0, (t - tEnd[i]) / (tEnd[i + 1] - tEnd[i] || 1)));
    const v = [0, 1, 2].map((k) => up[3 * i + k] * (1 - f) + up[3 * (i + 1) + k] * f);
    const n = Math.hypot(...v);
    if (!(n > 0) || !Number.isFinite(n)) return;
    viewer.adapter.setCameraUp(v.map((c) => c / n));
    viewer.needsUpdate(); // also after a seek while paused
  });
}

/** Same for a photo: one "up" for the whole picture. */
function levelPhoto(viewer, tilt) {
  const up = tilt.up;
  if (!Array.isArray(up) || up.length !== 3 || !up.every(Number.isFinite)) return;
  const level = () => {
    viewer.adapter.setCameraUp(up);
    viewer.needsUpdate();
  };
  viewer.addEventListener('panorama-loaded', level, { once: true });
}

try {
  if (!src) throw new Error('No media given');

  const viewer = new Viewer({
    container: 'viewer',
    caption: escapeHtml(params.get('name') || ''),
    loadingTxt: '',
    defaultZoomLvl: 0,
    mousewheelCtrlKey: false,
    touchmoveTwoFingers: false,
    ...buildConfig(),
  });

  viewer.addEventListener('panorama-loaded', () => report('ready'), { once: true });
  if (isVideo && window.sphereViewerTilt) levelHorizon(viewer, window.sphereViewerTilt);
  if (!isVideo && projection === 'dualfisheye' && window.sphereViewerTilt) levelPhoto(viewer, window.sphereViewerTilt);
  viewer.addEventListener('panorama-error', (e) => fail(e.error?.message || 'Could not load media'), { once: true });
  window.sphereViewer = viewer;
} catch (e) {
  fail(e.message);
}
