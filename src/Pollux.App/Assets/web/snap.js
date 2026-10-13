// Snap Board: marks up a Snap on a canvas. The host (SnapBoard.cs) serves the image at https://pollux.snap/image
// and receives the result as base64 PNG. Every mark lives in image pixels. The content canvas holds the cropped Snap with
// its marks; the view canvas on screen is that content inside the optional backdrop, exactly what is exported.
'use strict';

const host = window.chrome && window.chrome.webview;
const post = msg => host && host.postMessage(JSON.stringify(msg));

const TOOLS = [
  { id: 'arrow', key: 'a', title: 'Arrow', icon: '<path d="M5 19 19 5M10 5h9v9"/>' },
  { id: 'rect', key: 'r', title: 'Rectangle', icon: '<rect x="4" y="6" width="16" height="12" rx="1"/>' },
  { id: 'ellipse', key: 'e', title: 'Ellipse', icon: '<ellipse cx="12" cy="12" rx="8" ry="6"/>' },
  { id: 'line', key: 'l', title: 'Line', icon: '<path d="M5 19 19 5"/>' },
  { id: 'text', key: 't', title: 'Text', icon: '<path d="M6 6h12M12 6v13"/>' },
  { id: 'marker', key: 'm', title: 'Marker', icon: '<path d="M4 20h7M14 4l6 6-9 9H6v-5z"/>' },
  { id: 'step', key: 's', title: 'Step number', icon: '<circle cx="12" cy="12" r="8"/><path d="M11 9l2-1v8"/>' },
  { id: 'blur', key: 'b', title: 'Blur', icon: '<path d="M12 3c3 4 6 7 6 11a6 6 0 0 1-12 0c0-4 3-7 6-11z"/>' },
  { id: 'pixelate', key: 'p', title: 'Pixelate', icon: '<path d="M4 4h5v5H4zM15 4h5v5h-5zM9.5 9.5h5v5h-5zM4 15h5v5H4zM15 15h5v5h-5z"/>' },
  { id: 'spotlight', key: 'o', title: 'Spotlight', icon: '<rect x="3" y="3" width="18" height="18" rx="2" stroke-dasharray="2 2"/><rect x="8" y="8" width="8" height="8" rx="1"/>' },
  { id: 'ruler', key: 'u', title: 'Ruler (snaps to edges; Alt: free)', icon: '<path d="M3 15 15 3l6 6L9 21z"/><path d="M7 11l2 2M10 8l2 2M13 5l2 2"/>' },
  { id: 'picker', key: 'i', title: 'Colour picker (copies #RRGGBB)', icon: '<path d="M14 4l6 6-2 2-6-6zM12 8l-7 7v4h4l7-7"/>' },
  { id: 'crop', key: 'c', title: 'Crop', icon: '<path d="M7 3v14h14M3 7h14v14"/>' },
];
/** Marks drawn by dragging a box; they are kept even when tiny, unlike a click. */
const AREA_TOOLS = ['rect', 'ellipse', 'blur', 'pixelate', 'spotlight', 'crop'];
const COLORS = ['#e5484d', '#f5a524', '#30a46c', '#3e8ef7', '#ffffff', '#111111'];
const WIDTHS = [2, 4, 8];

const view = document.getElementById('board');
const vctx = view.getContext('2d');
const canvas = document.createElement('canvas'); // content: the cropped Snap and its marks
const ctx = canvas.getContext('2d');

const BACKDROPS = [
  { name: 'Dusk', stops: ['#5b5bd6', '#c084fc'] },
  { name: 'Lagoon', stops: ['#0ea5a4', '#3b82f6'] },
  { name: 'Ember', stops: ['#f97316', '#db2777'] },
  { name: 'Moss', stops: ['#3f6212', '#84cc16'] },
  { name: 'Graphite', stops: ['#334155'] },
  { name: 'Paper', stops: ['#f5f5f4'] },
];
const stage = document.getElementById('stage');
const rail = document.getElementById('rail');

const state = {
  image: null,      // HTMLImageElement of the Snap
  scale: 1,         // display scale of the monitor the Snap came from (1.5 = 150 %)
  marks: [],        // drawn marks and crops, oldest first; undo pops from here
  undone: [],       // marks undo removed; redo pushes them back
  draft: null,      // the mark being dragged out
  tool: 'arrow',
  color: COLORS[0],
  width: 1,         // index into WIDTHS
  zoom: 0,          // 0 = fit to the window
  label: null,      // the open text box
  frame: { on: false, preset: 0, color: '#334155', padding: 48, radius: 12, shadow: true }, // the backdrop
};

// ---------------- preferences ----------------

function loadPrefs() {
  try {
    const p = JSON.parse(localStorage.getItem('snap.prefs') || '{}');
    if (TOOLS.some(t => t.id === p.tool) && p.tool !== 'crop') state.tool = p.tool;
    if (COLORS.includes(p.color)) state.color = p.color;
    if (p.width >= 0 && p.width < WIDTHS.length) state.width = p.width;
    const f = p.frame || {};
    if (typeof f.on === 'boolean') state.frame.on = f.on;
    if (Number.isInteger(f.preset) && f.preset >= -1 && f.preset < BACKDROPS.length) state.frame.preset = f.preset;
    if (/^#[0-9a-f]{6}$/i.test(f.color || '')) state.frame.color = f.color;
    if (f.padding >= 0 && f.padding <= 160) state.frame.padding = f.padding;
    if (f.radius >= 0 && f.radius <= 40) state.frame.radius = f.radius;
    if (typeof f.shadow === 'boolean') state.frame.shadow = f.shadow;
  } catch { /* storage unavailable: defaults */ }
}

function savePrefs() {
  try { localStorage.setItem('snap.prefs', JSON.stringify({ tool: state.tool, color: state.color, width: state.width, frame: state.frame })); } catch { }
}

// ---------------- rail ----------------

function button(title, html, onClick) {
  const b = document.createElement('button');
  b.title = title;
  b.innerHTML = html;
  b.addEventListener('click', onClick);
  rail.appendChild(b);
  return b;
}

const toolButtons = new Map();
const colorButtons = new Map();
const widthButtons = [];

function buildRail() {
  for (const t of TOOLS)
    toolButtons.set(t.id, button(`${t.title} (${t.key.toUpperCase()})`, `<svg viewBox="0 0 24 24">${t.icon}</svg><kbd>${t.key.toUpperCase()}</kbd>`, () => setTool(t.id)));
  rail.appendChild(Object.assign(document.createElement('div'), { className: 'sep' }));
  COLORS.forEach((c, i) => colorButtons.set(c, button(`Colour ${i + 1}`, `<span class="swatch" style="background:${c}"></span>`, () => setColor(c))));
  rail.appendChild(Object.assign(document.createElement('div'), { className: 'sep' }));
  WIDTHS.forEach((w, i) => widthButtons.push(button(`Stroke ${['thin', 'medium', 'thick'][i]}`, `<span class="dot" style="width:${w + 3}px;height:${w + 3}px"></span>`, () => setWidth(i))));
  refreshRail();
}

function refreshRail() {
  for (const [id, b] of toolButtons) b.classList.toggle('on', id === state.tool);
  for (const [c, b] of colorButtons) b.classList.toggle('on', c === state.color);
  widthButtons.forEach((b, i) => b.classList.toggle('on', i === state.width));
  view.style.cursor = state.tool === 'text' ? 'text' : 'crosshair';
}

function setTool(id) { commitLabel(); state.tool = id; savePrefs(); refreshRail(); }
function setColor(c) { state.color = c; savePrefs(); refreshRail(); restyleLabel(); }
function setWidth(i) { state.width = i; savePrefs(); refreshRail(); restyleLabel(); }

// ---------------- geometry ----------------

const stroke = () => WIDTHS[state.width] * state.scale;
const fontSize = w => [16, 22, 32][w] * state.scale;

/** The area of the image that is exported: the last crop, or the whole image. */
function cropArea() {
  for (let i = state.marks.length - 1; i >= 0; i--) if (state.marks[i].kind === 'crop') return state.marks[i].rect;
  return { x: 0, y: 0, w: state.image ? state.image.naturalWidth : 1, h: state.image ? state.image.naturalHeight : 1 };
}

function normRect(a, b) {
  return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(b.x - a.x), h: Math.abs(b.y - a.y) };
}

/** Shift: lines snap to 45°, boxes become squares and circles. */
function constrain(kind, a, b, shift) {
  if (!shift) return b;
  const dx = b.x - a.x, dy = b.y - a.y;
  if (kind === 'line' || kind === 'arrow') {
    const angle = Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
    const len = Math.hypot(dx, dy);
    return { x: a.x + Math.cos(angle) * len, y: a.y + Math.sin(angle) * len };
  }
  const side = Math.max(Math.abs(dx), Math.abs(dy));
  return { x: a.x + Math.sign(dx || 1) * side, y: a.y + Math.sign(dy || 1) * side };
}

/** Pointer position in image pixels. */
function toImage(e) {
  const r = view.getBoundingClientRect();
  const crop = cropArea(), pad = framePad();
  return { x: crop.x - pad + (e.clientX - r.left) * view.width / r.width, y: crop.y - pad + (e.clientY - r.top) * view.height / r.height };
}

// ---------------- drawing ----------------

function drawMark(m) {
  ctx.save();
  ctx.strokeStyle = ctx.fillStyle = m.color;
  ctx.lineWidth = m.width;
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  switch (m.kind) {
    case 'line':
      ctx.beginPath(); ctx.moveTo(m.a.x, m.a.y); ctx.lineTo(m.b.x, m.b.y); ctx.stroke();
      break;
    case 'arrow': {
      const angle = Math.atan2(m.b.y - m.a.y, m.b.x - m.a.x);
      const head = Math.max(m.width * 4, 10 * state.scale);
      const neck = { x: m.b.x - Math.cos(angle) * head * 0.8, y: m.b.y - Math.sin(angle) * head * 0.8 };
      ctx.beginPath(); ctx.moveTo(m.a.x, m.a.y); ctx.lineTo(neck.x, neck.y); ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(m.b.x, m.b.y);
      ctx.lineTo(m.b.x - Math.cos(angle - 0.45) * head, m.b.y - Math.sin(angle - 0.45) * head);
      ctx.lineTo(m.b.x - Math.cos(angle + 0.45) * head, m.b.y - Math.sin(angle + 0.45) * head);
      ctx.closePath(); ctx.fill();
      break;
    }
    case 'rect': {
      const r = normRect(m.a, m.b);
      ctx.strokeRect(r.x, r.y, r.w, r.h);
      break;
    }
    case 'ellipse': {
      const r = normRect(m.a, m.b);
      ctx.beginPath(); ctx.ellipse(r.x + r.w / 2, r.y + r.h / 2, r.w / 2, r.h / 2, 0, 0, Math.PI * 2); ctx.stroke();
      break;
    }
    case 'marker':
      ctx.globalAlpha = 0.4;
      ctx.lineWidth = m.width * 4;
      ctx.lineCap = 'square';
      ctx.beginPath();
      m.points.forEach((p, i) => (i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)));
      ctx.stroke();
      break;
    case 'step': {
      const r = m.size;
      ctx.beginPath(); ctx.arc(m.a.x, m.a.y, r, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = isLight(m.color) ? '#111111' : '#ffffff';
      ctx.font = `700 ${r * 1.15}px 'Segoe UI Variable Display','Segoe UI',sans-serif`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.fillText(String(m.n), m.a.x, m.a.y + r * 0.06);
      break;
    }
    case 'blur':
    case 'pixelate':
      obscure(m);
      break;
    case 'ruler':
      drawRuler(m);
      break;
    case 'text':
      ctx.font = `600 ${m.size}px 'Segoe UI Variable Display','Segoe UI',sans-serif`;
      ctx.textBaseline = 'top';
      m.text.split('\n').forEach((line, i) => ctx.fillText(line, m.a.x, m.a.y + i * m.size * 1.2));
      break;
  }
  ctx.restore();
}

function isLight(hex) {
  const v = parseInt(hex.slice(1), 16);
  return ((v >> 16) & 255) * 0.299 + ((v >> 8) & 255) * 0.587 + (v & 255) * 0.114 > 160;
}

const scratch = document.createElement('canvas');
const scratchCtx = scratch.getContext('2d');

/**
 * Blur or pixelate what is already drawn under the mark's box. Works on the canvas pixels, so the exported PNG holds
 * only the obscured result; nothing of the original survives under it.
 */
function obscure(m) {
  const crop = cropArea();
  const r = normRect(m.a, m.b);
  // Canvas pixels of the box (the canvas is translated by the crop's origin).
  const x = Math.max(0, Math.floor(r.x - crop.x)), y = Math.max(0, Math.floor(r.y - crop.y));
  const w = Math.min(canvas.width, Math.ceil(r.x + r.w - crop.x)) - x, h = Math.min(canvas.height, Math.ceil(r.y + r.h - crop.y)) - y;
  if (w < 1 || h < 1) return;
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.beginPath(); ctx.rect(x, y, w, h); ctx.clip();
  if (m.kind === 'pixelate') {
    // Each block becomes the true average of its pixels (canvas downscaling samples only a few of them).
    const block = Math.max(4, Math.round(10 * state.scale));
    const img = ctx.getImageData(x, y, w, h);
    const d = img.data;
    for (let by = 0; by < h; by += block) {
      for (let bx = 0; bx < w; bx += block) {
        const bw = Math.min(block, w - bx), bh = Math.min(block, h - by);
        let r = 0, g = 0, b = 0;
        for (let yy = by; yy < by + bh; yy++) for (let xx = bx; xx < bx + bw; xx++) {
          const i = (yy * w + xx) * 4; r += d[i]; g += d[i + 1]; b += d[i + 2];
        }
        const n = bw * bh; r = Math.round(r / n); g = Math.round(g / n); b = Math.round(b / n);
        for (let yy = by; yy < by + bh; yy++) for (let xx = bx; xx < bx + bw; xx++) {
          const i = (yy * w + xx) * 4; d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = 255;
        }
      }
    }
    ctx.putImageData(img, x, y);
  } else {
    const radius = Math.max(4, Math.round(8 * state.scale));
    const pad = radius * 3;
    const px = Math.max(0, x - pad), py = Math.max(0, y - pad);
    const pw = Math.min(canvas.width, x + w + pad) - px, ph = Math.min(canvas.height, y + h + pad) - py;
    scratch.width = pw; scratch.height = ph;
    scratchCtx.filter = 'none';
    scratchCtx.drawImage(canvas, px, py, pw, ph, 0, 0, pw, ph);
    ctx.filter = `blur(${radius}px)`;
    // Twice: one pass leaves large text readable.
    ctx.drawImage(scratch, px, py);
    ctx.drawImage(scratch, px, py);
    ctx.filter = 'none';
  }
  ctx.restore();
}

// ---------------- ruler and picker ----------------

/** The Snap's own pixels (not the marks), read once per Snap: the ruler snaps to edges in them. */
function basePixels() {
  if (!state.pixels && state.image) {
    const c = document.createElement('canvas');
    c.width = state.image.naturalWidth; c.height = state.image.naturalHeight;
    const g = c.getContext('2d', { willReadFrequently: true });
    g.drawImage(state.image, 0, 0);
    state.pixels = g.getImageData(0, 0, c.width, c.height);
  }
  return state.pixels;
}

function pixelDiff(d, w, x1, y1, x2, y2) {
  const i = (y1 * w + x1) * 4, j = (y2 * w + x2) * 4;
  return Math.abs(d[i] - d[j]) + Math.abs(d[i + 1] - d[j + 1]) + Math.abs(d[i + 2] - d[j + 2]);
}

/**
 * The nearest colour edge to <c>v</c> along one axis (a pixel boundary where neighbours differ clearly), searched
 * <c>reach</c> pixels both ways on row/column <c>at</c>; <c>v</c> itself when there is none.
 */
function snapToEdge(v, at, horizontal, reach) {
  const px = basePixels();
  if (!px) return v;
  const w = px.width, h = px.height, d = px.data;
  const max = horizontal ? w : h, line = Math.round(at);
  if (line < 0 || line >= (horizontal ? h : w)) return v;
  const start = Math.round(v);
  for (let k = 0; k <= reach; k++) {
    for (const b of [start - k, start + k]) { // boundary b lies between pixel b-1 and pixel b
      if (b <= 0 || b >= max) continue;
      const diff = horizontal ? pixelDiff(d, w, b - 1, line, b, line) : pixelDiff(d, w, line, b - 1, line, b);
      if (diff > 60) return b;
    }
  }
  return v;
}

/** A ruler from a to b: straight along the dragged axis, ends on colour edges (Alt: free and unsnapped). */
function rulerEnds(a, b, free) {
  if (free) return { a, b };
  const horizontal = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y);
  const reach = Math.round(12 * state.scale);
  if (horizontal) {
    const y = a.y;
    return { a: { x: snapToEdge(a.x, y, true, reach), y }, b: { x: snapToEdge(b.x, y, true, reach), y } };
  }
  const x = a.x;
  return { a: { x, y: snapToEdge(a.y, x, false, reach) }, b: { x, y: snapToEdge(b.y, x, false, reach) } };
}

/** "240 px", plus the size at 100 % when the Snap came from a scaled monitor ("240 px · 160 at 100 %"). */
function rulerLabel(m) {
  const dx = Math.abs(Math.round(m.b.x - m.a.x)), dy = Math.abs(Math.round(m.b.y - m.a.y));
  const len = dx && dy ? Math.round(Math.hypot(dx, dy)) : dx + dy;
  let text = `${len} px`;
  if (dx && dy) text += ` (${dx} × ${dy})`;
  if (state.scale !== 1) text += ` · ${Math.round(len / state.scale)} at 100 %`;
  return text;
}

function drawRuler(m) {
  const angle = Math.atan2(m.b.y - m.a.y, m.b.x - m.a.x);
  const tick = 6 * state.scale;
  const nx = -Math.sin(angle) * tick, ny = Math.cos(angle) * tick;
  ctx.lineWidth = Math.max(1, state.scale);
  ctx.lineCap = 'butt';
  ctx.beginPath();
  ctx.moveTo(m.a.x, m.a.y); ctx.lineTo(m.b.x, m.b.y);
  ctx.moveTo(m.a.x - nx, m.a.y - ny); ctx.lineTo(m.a.x + nx, m.a.y + ny);
  ctx.moveTo(m.b.x - nx, m.b.y - ny); ctx.lineTo(m.b.x + nx, m.b.y + ny);
  ctx.stroke();
  const text = rulerLabel(m);
  const size = 11 * state.scale;
  ctx.font = `600 ${size}px 'Cascadia Mono',Consolas,monospace`;
  const tw = ctx.measureText(text).width, pad = 4 * state.scale;
  const mx = (m.a.x + m.b.x) / 2, my = (m.a.y + m.b.y) / 2;
  const lx = mx - tw / 2 - pad, ly = my + tick + 2 * state.scale;
  ctx.fillStyle = 'rgba(0,0,0,0.75)';
  ctx.beginPath(); ctx.roundRect(lx, ly, tw + pad * 2, size + pad * 2, 3 * state.scale); ctx.fill();
  ctx.fillStyle = '#ffffff';
  ctx.textBaseline = 'top';
  ctx.fillText(text, lx + pad, ly + pad);
}

/** Spotlights: everything outside their boxes is darkened, once, on top of all other marks. */
function drawSpotlights(marks) {
  if (!marks.length) return;
  const crop = cropArea();
  scratch.width = canvas.width; scratch.height = canvas.height;
  scratchCtx.filter = 'none';
  scratchCtx.globalCompositeOperation = 'source-over';
  scratchCtx.fillStyle = 'rgba(0,0,0,0.55)';
  scratchCtx.fillRect(0, 0, scratch.width, scratch.height);
  scratchCtx.globalCompositeOperation = 'destination-out';
  scratchCtx.fillStyle = '#000';
  for (const m of marks) {
    const r = normRect(m.a, m.b);
    scratchCtx.beginPath();
    scratchCtx.roundRect(r.x - crop.x, r.y - crop.y, r.w, r.h, 4 * state.scale);
    scratchCtx.fill();
  }
  scratchCtx.globalCompositeOperation = 'source-over';
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.drawImage(scratch, 0, 0);
  ctx.restore();
}

/** The crop being dragged: dims what will be cut away. Drawn on top, never exported. */
function drawCropDraft(d) {
  const r = normRect(d.a, d.b);
  const all = cropArea();
  ctx.save();
  ctx.fillStyle = 'rgba(0,0,0,.45)';
  ctx.beginPath();
  ctx.rect(all.x, all.y, all.w, all.h);
  ctx.rect(r.x, r.y, r.w, r.h);
  ctx.fill('evenodd');
  ctx.strokeStyle = getComputedStyle(document.documentElement).getPropertyValue('--accent');
  ctx.lineWidth = state.scale;
  ctx.setLineDash([6 * state.scale, 4 * state.scale]);
  ctx.strokeRect(r.x, r.y, r.w, r.h);
  ctx.restore();
}

function render() {
  if (!state.image) return;
  const crop = cropArea();
  if (canvas.width !== crop.w || canvas.height !== crop.h) {
    canvas.width = crop.w;
    canvas.height = crop.h;
  }

  ctx.setTransform(1, 0, 0, 1, -crop.x, -crop.y);
  ctx.clearRect(crop.x, crop.y, crop.w, crop.h);
  ctx.drawImage(state.image, 0, 0);
  const spots = [];
  const all = state.draft && state.draft.kind !== 'crop' ? [...state.marks, state.draft] : state.marks;
  for (const m of all) {
    if (m.kind === 'spotlight') spots.push(m);
    else if (m.kind !== 'crop') drawMark(m);
  }
  drawSpotlights(spots);
  if (state.draft && state.draft.kind === 'crop') drawCropDraft(state.draft);
  compose();
  status();
}

// ---------------- backdrop ----------------

/** Space around the Snap in image pixels; 0 while the backdrop is off. */
const framePad = () => (state.frame.on ? Math.round(state.frame.padding * state.scale) : 0);

/** Draws the content onto the view: as is, or framed by the backdrop (fill, shadow, rounded corners). */
function compose() {
  const pad = framePad();
  const w = canvas.width + pad * 2, h = canvas.height + pad * 2;
  if (view.width !== w || view.height !== h) {
    view.width = w;
    view.height = h;
    layout();
  }

  vctx.setTransform(1, 0, 0, 1, 0, 0);
  vctx.clearRect(0, 0, w, h);
  const f = state.frame;
  const radius = f.on ? Math.min(f.radius * state.scale, canvas.width / 2, canvas.height / 2) : 0;
  if (f.on && pad > 0) {
    vctx.fillStyle = backdropFill(w, h);
    vctx.fillRect(0, 0, w, h);
    if (f.shadow) {
      vctx.save();
      vctx.shadowColor = 'rgba(0,0,0,0.45)';
      vctx.shadowBlur = Math.min(pad, 28 * state.scale);
      vctx.shadowOffsetY = Math.min(pad / 3, 8 * state.scale);
      vctx.fillStyle = '#000';
      vctx.beginPath(); vctx.roundRect(pad, pad, canvas.width, canvas.height, radius); vctx.fill();
      vctx.restore();
    }
  }

  vctx.save();
  if (radius > 0) { vctx.beginPath(); vctx.roundRect(pad, pad, canvas.width, canvas.height, radius); vctx.clip(); }
  vctx.drawImage(canvas, pad, pad);
  vctx.restore();
}

function backdropFill(w, h) {
  const f = state.frame;
  const stops = f.preset < 0 ? [f.color] : BACKDROPS[f.preset].stops;
  if (stops.length === 1) return stops[0];
  const g = vctx.createLinearGradient(0, 0, w, h);
  stops.forEach((c, i) => g.addColorStop(i / (stops.length - 1), c));
  return g;
}

/** CSS size of the canvas: 100 % means one image pixel per screen pixel; fit shrinks to the window, never enlarges. */
function layout() {
  const dpr = window.devicePixelRatio || 1;
  const natural = { w: view.width / dpr, h: view.height / dpr };
  const fit = Math.min(1, (stage.clientWidth - 32) / natural.w, (stage.clientHeight - 32) / natural.h);
  const zoom = state.zoom || Math.max(0.05, fit);
  view.style.width = `${natural.w * zoom}px`;
  view.style.height = `${natural.h * zoom}px`;
  state.shownZoom = zoom;
  positionLabel();
}

function status(colorAt) {
  post({ type: 'status', size: `${view.width} × ${view.height}`, zoom: `${Math.round((state.shownZoom || 1) * 100)} %`, color: colorAt || '' });
}

// ---------------- history ----------------

function add(mark) {
  state.marks.push(mark);
  state.undone = [];
  render();
}

function undo() {
  commitLabel();
  if (state.marks.length) state.undone.push(state.marks.pop());
  render();
}

function redo() {
  if (state.undone.length) state.marks.push(state.undone.pop());
  render();
}

// ---------------- pointer ----------------

view.addEventListener('pointerdown', e => {
  if (e.button !== 0 || !state.image) return;
  if (state.label) { commitLabel(); return; }
  const p = toImage(e);
  if (state.tool === 'text') { openLabel(p); e.preventDefault(); return; }
  if (state.tool === 'picker') {
    const hex = colorAt(p);
    if (hex) post({ type: 'copyText', text: hex });
    return;
  }
  if (state.tool === 'step') {
    const n = state.marks.filter(m => m.kind === 'step').length + 1;
    add({ kind: 'step', color: state.color, size: [11, 14, 18][state.width] * state.scale, a: p, n });
    return;
  }
  view.setPointerCapture(e.pointerId);
  state.draft = { kind: state.tool, color: state.color, width: stroke(), a: p, b: p };
  if (state.tool === 'marker') state.draft.points = [p];
  if (state.tool === 'ruler') state.draft.start = p;
  render();
});

view.addEventListener('pointermove', e => {
  if (!state.image) return;
  const p = toImage(e);
  if (state.draft) {
    const d = state.draft;
    if (d.kind === 'marker') {
      // Shift: a straight stroke from where it started.
      d.points = e.shiftKey ? [d.a, constrain('line', d.a, p, true)] : [...d.points, p];
      d.b = d.points[d.points.length - 1];
    } else if (d.kind === 'ruler') {
      const ends = rulerEnds(d.start, p, e.altKey);
      d.a = ends.a; d.b = ends.b;
    } else d.b = constrain(d.kind, d.a, p, e.shiftKey);
    render();
  } else {
    status(colorAt(p));
  }
});

view.addEventListener('pointerup', e => {
  const d = state.draft;
  if (!d) return;
  state.draft = null;
  view.releasePointerCapture(e.pointerId);
  const r = normRect(d.a, d.b);
  const tooSmall = AREA_TOOLS.includes(d.kind) ? (r.w < 2 || r.h < 2) : (r.w < 2 && r.h < 2);
  if (tooSmall && !(d.kind === 'marker' && d.points.length > 2)) { render(); return; }
  if (d.kind === 'crop') {
    const all = cropArea();
    const x = Math.max(all.x, Math.round(r.x)), y = Math.max(all.y, Math.round(r.y));
    const w = Math.min(all.x + all.w, Math.round(r.x + r.w)) - x, h = Math.min(all.y + all.h, Math.round(r.y + r.h)) - y;
    if (w >= 2 && h >= 2) add({ kind: 'crop', rect: { x, y, w, h } });
    else render();
    return;
  }
  add(d);
});

stage.addEventListener('wheel', e => {
  if (!e.ctrlKey) return;
  e.preventDefault();
  const current = state.shownZoom || 1;
  state.zoom = Math.min(8, Math.max(0.1, current * (e.deltaY < 0 ? 1.25 : 0.8)));
  layout();
  status();
}, { passive: false });

window.addEventListener('resize', () => { layout(); status(); });

/** The Snap's own colour at an image point (the marks are left out), as "#RRGGBB"; "" outside the image. */
function colorAt(p) {
  const px = basePixels();
  const x = Math.floor(p.x), y = Math.floor(p.y);
  if (!px || x < 0 || y < 0 || x >= px.width || y >= px.height) return '';
  const i = (y * px.width + x) * 4, d = px.data;
  return '#' + [d[i], d[i + 1], d[i + 2]].map(v => v.toString(16).padStart(2, '0')).join('').toUpperCase();
}

// ---------------- text ----------------

function openLabel(p) {
  const t = document.createElement('textarea');
  t.className = 'label';
  t.rows = 1;
  t.spellcheck = false;
  state.label = { el: t, at: p };
  stage.appendChild(t);
  restyleLabel();
  t.addEventListener('input', sizeLabel);
  t.addEventListener('keydown', e => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); commitLabel(); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); dropLabel(); }
    e.stopPropagation();
  });
  setTimeout(() => t.focus(), 0);
}

/** Screen pixels per image pixel at the current zoom. */
const cssPerImagePixel = () => view.getBoundingClientRect().width / view.width;

function restyleLabel() {
  if (!state.label) return;
  const t = state.label.el;
  t.style.color = state.color;
  t.style.fontSize = `${fontSize(state.width) * cssPerImagePixel()}px`;
  positionLabel();
  sizeLabel();
}

function positionLabel() {
  if (!state.label) return;
  const r = view.getBoundingClientRect(), s = stage.getBoundingClientRect(), crop = cropArea(), k = cssPerImagePixel(), pad = framePad();
  state.label.el.style.left = `${r.left - s.left + stage.scrollLeft + (state.label.at.x - crop.x + pad) * k}px`;
  state.label.el.style.top = `${r.top - s.top + stage.scrollTop + (state.label.at.y - crop.y + pad) * k}px`;
}

function sizeLabel() {
  const t = state.label.el;
  const lines = t.value.split('\n');
  t.style.width = `${Math.max(2, ...lines.map(l => l.length)) * 0.62 + 1}em`;
  t.style.height = `${lines.length * 1.2 + 0.2}em`;
}

function commitLabel() {
  if (!state.label) return;
  const { el, at } = state.label;
  const text = el.value.replace(/\s+$/, '');
  dropLabel();
  if (text) add({ kind: 'text', color: state.color, size: fontSize(state.width), a: at, text });
}

function dropLabel() {
  if (!state.label) return;
  state.label.el.remove();
  state.label = null;
}

// ---------------- export ----------------

function exportPng(action) {
  commitLabel();
  state.draft = null;
  render();
  const png = view.toDataURL('image/png').replace(/^data:image\/png;base64,/, '');
  post({ type: 'export', action, png });
}

// ---------------- text panel ----------------

const panel = document.getElementById('textPanel');
const panelBody = document.getElementById('textBody');
const panelProblem = document.getElementById('textProblem');
const panelLanguage = document.getElementById('textLanguage');

function readText() {
  panel.hidden = false;
  panelBody.value = '';
  panelProblem.hidden = false;
  panelProblem.textContent = 'Reading text…';
  panelLanguage.textContent = '';
  layout();
  exportPng('ocr');
}

function showText(msg) {
  panelBody.value = msg.text || '';
  panelLanguage.textContent = msg.language || '';
  panelProblem.hidden = !msg.problem;
  panelProblem.textContent = msg.problem || '';
}

function closeText() {
  panel.hidden = true;
  layout();
}

document.getElementById('textCopy').addEventListener('click', () => {
  // The selection when there is one, else everything.
  const t = panelBody.selectionEnd > panelBody.selectionStart ? panelBody.value.slice(panelBody.selectionStart, panelBody.selectionEnd) : panelBody.value;
  if (t) post({ type: 'copyText', text: t });
});
document.getElementById('textClose').addEventListener('click', closeText);
panelBody.addEventListener('keydown', e => {
  if (e.key === 'Escape') { e.preventDefault(); closeText(); }
  e.stopPropagation(); // Ctrl+C copies the selected text, not the Snap
});

// ---------------- QR code ----------------

const qrChip = document.getElementById('qr');
const qrText = document.getElementById('qrText');
let qrWorker = null;
let qrScan = 0;

/** Looks for a QR code in the Snap itself (not the marks) in a worker; a found code shows a chip. Links are never opened. */
function scanQr(img) {
  qrChip.hidden = true;
  const id = ++qrScan;
  try {
    qrWorker ??= new Worker('snap-qr.js');
    qrWorker.onmessage = e => {
      if (e.data.id !== qrScan || !e.data.data) return;
      qrText.textContent = e.data.data;
      qrText.title = e.data.data;
      qrChip.hidden = false;
    };
    const c = document.createElement('canvas');
    c.width = img.naturalWidth; c.height = img.naturalHeight;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0);
    const pixels = g.getImageData(0, 0, c.width, c.height).data.buffer;
    qrWorker.postMessage({ id, width: c.width, height: c.height, pixels }, [pixels]);
  } catch (err) {
    post({ type: 'error', message: 'QR scan: ' + err });
  }
}

document.getElementById('qrCopy').addEventListener('click', () => {
  if (qrText.textContent) post({ type: 'copyText', text: qrText.textContent });
});

// ---------------- backdrop panel ----------------

const framePanel = document.getElementById('framePanel');
const frameOn = document.getElementById('frameOn');
const frameSwatches = document.getElementById('frameSwatches');
const frameColor = document.getElementById('frameColor');
const framePadding = document.getElementById('framePadding');
const frameRadius = document.getElementById('frameRadius');
const frameShadow = document.getElementById('frameShadow');

BACKDROPS.forEach((b, i) => {
  const s = document.createElement('button');
  s.className = 'swatch';
  s.title = b.name;
  s.style.background = b.stops.length > 1 ? `linear-gradient(135deg, ${b.stops.join(', ')})` : b.stops[0];
  s.addEventListener('click', () => setFrame({ on: true, preset: i }));
  frameSwatches.insertBefore(s, frameColor);
});

function syncFramePanel() {
  const f = state.frame;
  frameOn.checked = f.on;
  frameColor.value = f.color;
  framePadding.value = f.padding;
  frameRadius.value = f.radius;
  frameShadow.checked = f.shadow;
  [...frameSwatches.children].forEach((s, i) => s.classList.toggle('on', f.preset === i));
  frameColor.classList.toggle('on', f.preset < 0);
}

function setFrame(change) {
  Object.assign(state.frame, change);
  savePrefs();
  syncFramePanel();
  render();
}

frameOn.addEventListener('change', () => setFrame({ on: frameOn.checked }));
frameColor.addEventListener('input', () => setFrame({ on: true, preset: -1, color: frameColor.value }));
framePadding.addEventListener('input', () => setFrame({ padding: Number(framePadding.value) }));
frameRadius.addEventListener('input', () => setFrame({ radius: Number(frameRadius.value) }));
frameShadow.addEventListener('change', () => setFrame({ shadow: frameShadow.checked }));
document.getElementById('frameClose').addEventListener('click', () => toggleFramePanel(false));
framePanel.addEventListener('keydown', e => {
  if (e.key === 'Escape') { e.preventDefault(); toggleFramePanel(false); }
  e.stopPropagation();
});

/** G: shows the backdrop panel (turning the frame on the first time), or hides it. */
function toggleFramePanel(show = framePanel.hidden) {
  framePanel.hidden = !show;
  if (show && !state.frame.on) setFrame({ on: true });
  syncFramePanel();
  layout();
}

// ---------------- keys ----------------

document.addEventListener('keydown', e => {
  if (state.label) return;
  const k = e.key.toLowerCase();
  if (e.ctrlKey) {
    if (k === 'z' && !e.shiftKey) undo();
    else if (k === 'y' || (k === 'z' && e.shiftKey)) redo();
    else if (k === 'c') exportPng('copy');
    else if (k === 's') exportPng(e.shiftKey ? 'saveAs' : 'save');
    else if (k === '0') { state.zoom = 0; layout(); status(); }
    else return;
    e.preventDefault();
    return;
  }
  if (e.altKey || e.metaKey) return;
  if (k === 'escape') {
    if (state.draft) { state.draft = null; render(); }
    else if (!panel.hidden) closeText();
    else if (!framePanel.hidden) toggleFramePanel(false);
    else post({ type: 'close' });
  } else if (k === 'x') {
    readText();
  } else if (k === 'g') {
    toggleFramePanel();
  } else if (k === 'f') {
    exportPng('float');
  } else if (TOOLS.some(t => t.key === k)) {
    setTool(TOOLS.find(t => t.key === k).id);
  } else if (k >= '1' && k <= String(COLORS.length)) {
    setColor(COLORS[Number(k) - 1]);
  } else return;
  e.preventDefault();
});

// ---------------- host ----------------

function load(msg) {
  dropLabel();
  panel.hidden = true;
  state.draft = null;
  state.marks = [];
  state.undone = [];
  state.zoom = 0;
  state.scale = msg.scale > 0 ? msg.scale : 1;
  const img = new Image();
  img.crossOrigin = 'anonymous'; // the host allows pollux.assets, so the canvas stays exportable
  img.onload = () => {
    state.image = img;
    state.pixels = null;
    view.width = 0; // forces layout in compose()
    render();
    post({ type: 'loaded' });
    scanQr(img);
  };
  img.onerror = () => post({ type: 'error', message: 'Snap image did not load' });
  img.src = msg.url;
}

function applyTheme(msg) {
  document.documentElement.classList.toggle('dark', !!msg.dark);
  if (/^#[0-9a-f]{6}$/i.test(msg.accent || '')) document.documentElement.style.setProperty('--accent', msg.accent);
}

if (host) {
  host.addEventListener('message', e => {
    let msg;
    try { msg = JSON.parse(e.data); } catch { return; }
    if (msg.type === 'load') { applyTheme(msg); load(msg); }
    else if (msg.type === 'theme') applyTheme(msg);
    else if (msg.type === 'export') exportPng(msg.action);
    else if (msg.type === 'ocrResult') showText(msg);
  });
}

window.addEventListener('error', e => post({ type: 'error', message: String(e.message) }));

loadPrefs();
buildRail();
post({ type: 'ready' });
