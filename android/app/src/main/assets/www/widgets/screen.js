import { API, PANEL_HEADERS, bridge, h, haptic, t } from '../main.js';

// ---------------------------------------------------------------- full-screen video of the playing window
// Opened from the Now playing widget when its "Show the video when tapped" option is on. The agent captures the window
// of the app that plays and streams H.264 (ScreenFeed.cs); WebCodecs decodes it with the phone's hardware decoder and
// each picture is drawn on a canvas as soon as it is out, so the picture stays with the sound the computer plays. Frames
// that can't be decoded in time are skipped up to the next key frame rather than queued. One message on the stream:
// 4-byte big-endian length, a type byte (1 setup JSON, 2 key frame, 3 frame), and for frames an 8-byte timestamp (µs).
const SETUP = 1, KEY = 2;
const STATES = {
  off: 'w.media.videoOff', nothing: 'w.media.videoNothing', 'no-window': 'w.media.videoNoWindow',
  unsupported: 'w.media.videoUnsupported',
};

let open = null;

export const canShowVideo = () => typeof VideoDecoder === 'function' && typeof EncodedVideoChunk === 'function';

// The viewer's settings, kept on this phone: the largest picture to ask for and how much bitrate. "auto" is the
// phone's own screen; a bigger size only helps on a phone whose screen is bigger than the one asked for, while a
// higher quality (2x, 3x the bitrate) keeps fine detail and fast scenes sharp at any size.
const SIZES = { auto: null, 720: [1280, 720], 1080: [1920, 1080], 1440: [2560, 1440], max: [3840, 2160] };
const SIZE_LABELS = { auto: 'w.media.sizeAuto', 720: '720p', 1080: '1080p', 1440: '1440p', max: 'w.media.sizeMax' };
const SIZE_ORDER = ['auto', '720', '1080', '1440', 'max']; // an object lists number-like keys first
const QUALITY_LABELS = { 1: 'w.media.qualityStandard', 2: 'w.media.qualityHigh', 3: 'w.media.qualityMax' };
const PREFS = 'pb-video';
const readPrefs = () => {
  let p = {};
  try { p = JSON.parse(localStorage.getItem(PREFS) || '{}') || {}; } catch (_) {}
  return { size: p.size in SIZES ? p.size : 'auto', quality: [1, 2, 3].includes(p.quality) ? p.quality : 1 };
};
const savePrefs = (p) => { try { localStorage.setItem(PREFS, JSON.stringify(p)); } catch (_) {} };
const label = (key) => (/^w\./.test(key) ? t(key) : key);

export function openScreenView(widget) {
  if (open) return;
  const prefs = readPrefs();
  const canvas = h('canvas', 'screen-canvas');
  const note = h('div', 'screen-note', t('w.media.videoStarting'));
  const hint = h('div', 'screen-hint', t('w.media.tapToClose'));
  const gear = h('button', 'screen-gear');
  gear.type = 'button';
  gear.setAttribute('aria-label', t('w.media.videoSettings'));
  gear.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>';
  const panel = h('div', 'screen-panel');
  panel.hidden = true;
  const now = h('div', 'screen-now');
  const row = (title, options, current, pick) => {
    const r = h('div', 'screen-row', h('div', 'screen-row-t', title));
    for (const [value, text] of options) {
      const b = h('button', 'screen-opt' + (String(value) === String(current) ? ' on' : ''), text);
      b.type = 'button';
      b.addEventListener('click', (e) => { e.stopPropagation(); haptic(); pick(value); });
      r.append(b);
    }
    return r;
  };
  // a change starts the stream again with the new settings
  const restart = (p) => { savePrefs(p); close(); openScreenView(widget); };
  panel.append(
    row(t('w.media.videoSize'), SIZE_ORDER.map((k) => [k, label(SIZE_LABELS[k])]), prefs.size, (v) => restart({ ...prefs, size: v })),
    row(t('w.media.videoQuality'), [1, 2, 3].map((q) => [q, label(QUALITY_LABELS[q])]), prefs.quality, (v) => restart({ ...prefs, quality: v })),
    now);
  panel.addEventListener('click', (e) => e.stopPropagation()); // a tap in the settings doesn't close the video
  gear.addEventListener('click', (e) => { e.stopPropagation(); haptic(); panel.hidden = !panel.hidden; });
  const view = h('div', 'screen-view', canvas, note, hint, gear, panel);
  view.setAttribute('role', 'dialog');
  document.body.append(view);
  setTimeout(() => hint.classList.add('gone'), 2500);

  const ctl = new AbortController();
  const g = canvas.getContext('2d', { alpha: false });
  let decoder = null, waitKey = true, shown = 0, closed = false, quiet = 0, onClose = null;
  const say = (text) => { note.textContent = text || ''; note.hidden = !text; };

  const prevBack = window.onAndroidBack;
  const close = () => {
    if (closed) return;
    closed = true;
    open = null;
    ctl.abort();
    if (onClose) onClose();
    clearInterval(quiet);
    try { if (decoder && decoder.state !== 'closed') decoder.close(); } catch (_) {}
    document.removeEventListener('visibilitychange', onHidden);
    if (window.onAndroidBack === back) window.onAndroidBack = prevBack;
    view.remove();
  };
  const back = () => close(); // Android's Back closes the video first
  const onHidden = () => { if (document.hidden) close(); };
  window.onAndroidBack = back;
  document.addEventListener('visibilitychange', onHidden);
  view.addEventListener('click', () => { haptic(); close(); });
  open = { close };

  if (!canShowVideo()) { say(t('w.media.videoNoDecoder')); return; }

  const setup = (info) => {
    try { if (decoder && decoder.state !== 'closed') decoder.close(); } catch (_) {}
    canvas.width = info.width; canvas.height = info.height;
    // what the computer sends: smaller than asked when the window on the computer is smaller
    now.textContent = t('w.media.videoNow', { size: `${info.width} × ${info.height}` });
    decoder = new VideoDecoder({
      output: (frame) => {
        try { if (!closed) g.drawImage(frame, 0, 0, canvas.width, canvas.height); } finally { frame.close(); }
        if (!shown++) say('');
      },
      error: () => { waitKey = true; if (!closed) setup(info); }, // a broken frame: start over at the next key frame
    });
    decoder.configure({
      codec: info.codec, codedWidth: info.width, codedHeight: info.height,
      hardwareAcceleration: 'prefer-hardware', optimizeForLatency: true,
    });
    waitKey = true;
  };

  const frame = (type, bytes) => {
    if (!decoder || decoder.state !== 'configured') return;
    const key = type === KEY;
    // well behind: what is queued would only show the past, so skip to the next key frame (one comes every second). A
    // hardware decoder holds a few frames as it works (a vivo 1606 up to 3), so a short queue is normal
    if (!key && (waitKey || decoder.decodeQueueSize > 8)) { waitKey = true; return; }
    waitKey = false;
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const time = Number(view.getBigInt64(0));
    decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: time, data: bytes.subarray(8) }));
  };

  // In the app the frames come from the native side (ScreenStream.kt) as they arrive: the WebView would hand a fetched
  // stream over in 64 KB blocks, a burst of frames every half second. In a browser (the web demo), fetch.
  // the largest picture to ask for: the screen in real pixels ("auto": a tablet gets it sharp and a small phone no more
  // than it can show), or the size picked in the settings. The computer never sends more than the window itself.
  const dpr = window.devicePixelRatio || 1;
  const [screenW, screenH] = SIZES[prefs.size] || [Math.round(window.innerWidth * dpr), Math.round(window.innerHeight * dpr)];
  const quality = prefs.quality;

  if (bridge && bridge.screenStart) {
    let lastData = Date.now();
    quiet = setInterval(() => { if (!shown && Date.now() - lastData > 3000) say(t('w.media.videoWaiting')); }, 1000);
    const bytes = (b64) => { const s = atob(b64), out = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i); return out; };
    const prev = window.onScreenStream;
    window.onScreenStream = (type, data) => {
      if (closed) return;
      if (type === 0) { let body = {}; try { body = JSON.parse(data); } catch (_) {} say(t(STATES[body.state] || 'w.offline')); return; }
      if (type === -1) { say(t('w.media.videoEnded')); return; }
      if (shown) lastData = Date.now();
      const payload = bytes(data);
      if (type === SETUP) setup(JSON.parse(new TextDecoder().decode(payload)));
      else frame(type, payload);
    };
    onClose = () => { try { bridge.screenStop(); } catch (_) {} window.onScreenStream = prev; };
    bridge.screenStart(widget, screenW, screenH, quality);
    return;
  }

  (async () => {
    let r;
    try {
      r = await fetch(`${API}/screen?widget=${encodeURIComponent(widget)}&w=${screenW}&h=${screenH}&q=${quality}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
    } catch (_) { if (!closed) say(t('w.offline')); return; }
    if (!r.ok || !r.body) {
      const body = await r.json().catch(() => ({}));
      if (!closed) say(t(STATES[body.state] || 'w.offline'));
      return;
    }
    // the stream is up but nothing comes: a minimized window draws nothing
    let lastData = Date.now();
    quiet = setInterval(() => { if (!shown && Date.now() - lastData > 3000) say(t('w.media.videoWaiting')); }, 1000);
    const reader = r.body.getReader();
    let buf = new Uint8Array(0);
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done || closed) break;
        if (shown) lastData = Date.now();
        const joined = new Uint8Array(buf.length + value.length);
        joined.set(buf); joined.set(value, buf.length);
        buf = joined;
        let at = 0;
        while (buf.length - at >= 5) {
          const len = ((buf[at] << 24) >>> 0) + (buf[at + 1] << 16) + (buf[at + 2] << 8) + buf[at + 3];
          if (buf.length - at - 4 < len) break;
          const type = buf[at + 4], payload = buf.subarray(at + 5, at + 4 + len);
          at += 4 + len;
          if (type === SETUP) setup(JSON.parse(new TextDecoder().decode(payload)));
          else frame(type, payload);
        }
        buf = buf.slice(at);
      }
    } catch (_) { /* closed, or the connection dropped */ }
    if (!closed) say(t('w.media.videoEnded'));
  })();
}
