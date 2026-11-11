// 唱片室 · 可点原型 v4（暗室）
// 设计语言取自 Rhine：同一种模块的大量重复 + 一个被抽出的例外 +
// 一条引线把三维物体和排版文字连起来。三层是同一个房间的三个纵深，不是三个页面。
import * as THREE from 'three';
import { RoundedBoxGeometry } from 'three/examples/jsm/geometries/RoundedBoxGeometry.js';


// ---------------------------------------------------------------- DOM 挂载
// 场景自己建 DOM：这样同一份代码既能做独立原型，也能作为覆盖层塞进网页壳。
// 注意：整份样式挂在 .rr-root 下，避免污染播放器界面。
function mountDom(mode) {
  const root = document.createElement('div');
  root.className = 'rr-root';
  root.dataset.mode = mode;
  root.innerHTML = `
    <div class="rr-stage"></div>
    <div class="rr-scrim" aria-hidden="true"></div>
    <svg class="rr-leader" aria-hidden="true"></svg>
    <div class="rr-brand">
      <span class="rr-mark">CELESTE</span><span class="rr-slash">/</span>
      <span class="rr-cn">唱片室</span><span class="rr-en">RECORD&nbsp;ROOM</span>
    </div>
    <button class="rr-back" type="button" hidden>返回</button>
    <nav class="rr-crumb" aria-label="唱片室层级"></nav>
    <div class="rr-controls" aria-label="CD 机控制" hidden></div>
    <div class="rr-left">
    <aside class="rr-card">
      <p class="rr-eyebrow"></p>
      <p class="rr-title">—</p>
      <p class="rr-meta"></p>
      <div class="rr-body"></div>
      <p class="rr-foot"></p>
    </aside>
    <div class="rr-idx"><span class="rr-idx-big">01</span><span class="rr-idx-sub"></span></div>
    </div>
    <p class="rr-hint"></p>
    <button class="rr-action" type="button">放入 CD 机</button>`;
  document.body.appendChild(root);
  const q = sel => root.querySelector(sel);
  return {
    root,
    stage: q('.rr-stage'),
    svg: q('.rr-leader'),
    crumb: q('.rr-crumb'), controls: q('.rr-controls'),
    title: q('.rr-title'),
    eyebrow: q('.rr-eyebrow'),
    meta: q('.rr-meta'),
    body: q('.rr-body'),
    foot: q('.rr-foot'),
    card: q('.rr-card'),
    idxBig: q('.rr-idx-big'),
    idxSub: q('.rr-idx-sub'),
    hint: q('.rr-hint'),
    action: q('.rr-action'),
    back: q('.rr-back'),
  };
}

// ---------------------------------------------------------------- 常量

const W = 1.14, H = 1.00, D = 0.10;
const COLS = 7, GAPX = 0.26, GAPY = 0.14;
const PLINTH = 0.12, CARCASS_D = 0.72, YAW = -0.13;
const RESERVE_PX = 404;
const FAN_GAP = 1.16, FAN_SCALE = 1.45;
const FAN_VISIBLE = 5;              // 一次能看到几张
const FAN_MID = (FAN_VISIBLE - 1) / 2;
const POOL = FAN_VISIBLE + 4;       // 两侧各留两张候场，滚动时从暗处滑进来
const BENCH_Y = 0.50, BENCH_Z = 2.95, BENCH_W = 10.6, BENCH_D = 1.55;
const FOV = 30;

const C = {
  bg: 0x0d0b09,
  floor: 0x151109,
  ivory: 0xdfd4c0,
  carcass: 0x221c15,
  bench: 0x33291e,
  plate: 0x6f6659,
  dark: 0x0a0908,
};

export function startRoom(DATA, OPTS = {}) {
const el = mountDom(OPTS.mode || 'standalone');
const stage = el.stage;
let disposed = false, rafId = 0;
const unlisteners = [];
function addL(target, type, fn, opt) {
  target.addEventListener(type, fn, opt);
  unlisteners.push(() => target.removeEventListener(type, fn, opt));
}
const artists = DATA.artists;
const S = { layer: 1, artist: 0, album: 0, hover: -1, spin: false, trackId: null };
let playback = { playing: false, trackId: null };
const past = [], future = [];
let restoring = false, lidTimer = 0;
const snapshot = () => ({ layer: S.layer, artist: S.artist, album: S.album });
function remember(layer) {
  if (!restoring && S.layer !== layer) { past.push(snapshot()); future.length = 0; }
}
function restore(state) {
  restoring = true;
  S.artist = state.artist; S.album = state.album;
  if (state.layer === 1) toLayer1();
  else { toLayer2(); if (state.layer === 3) toLayer3(); }
  restoring = false;
}
function forwardOne() {
  if (!future.length) return;
  past.push(snapshot()); restore(future.pop());
}
function renderCrumbs() {
  el.root.dataset.layer = String(S.layer);
  el.controls.hidden = S.layer !== 3;
  el.back.hidden = false; el.back.textContent = S.layer === 1 ? '退出唱片室' : '返回';
  const pathKey = [S.layer, S.artist, S.album].join(':');
  if (el.crumb.dataset.path === pathKey) return;
  el.crumb.dataset.path = pathKey;
  el.crumb.replaceChildren();
  const labels = ['唱片室', artists[S.artist].name, artists[S.artist].albums[S.album]?.title || '专辑'];
  [toLayer1, toLayer2, toLayer3].slice(0, S.layer).forEach((action, i) => {
    if (i) { const separator = document.createElement('span'); separator.textContent = '/'; separator.setAttribute('aria-hidden', 'true'); el.crumb.append(separator); }
    const button = document.createElement('button'); button.type = 'button'; button.textContent = labels[i];
    button.dataset.layer = String(i + 1); button.title = labels[i];
    if (i + 1 === S.layer) button.setAttribute('aria-current', 'page');
    button.onclick = () => { if (i + 1 !== S.layer) action(); };
    el.crumb.append(button);
  });
}
function control(command) {
  if (OPTS.onControl) OPTS.onControl(command, artists[S.artist].albums[S.album]);
  else if (command === 'togglePlay') { S.spin = !S.spin; updateControls(); }
}


// ---------------------------------------------------------------- 渲染器
const canvasEl = document.createElement('canvas');
const renderer = new THREE.WebGLRenderer({ canvas: canvasEl, antialias: true });
renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
renderer.shadowMap.enabled = true;
renderer.shadowMap.type = THREE.PCFSoftShadowMap;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.14;
renderer.outputColorSpace = THREE.SRGBColorSpace;
stage.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(C.bg);
scene.fog = new THREE.Fog(C.bg, 9, 26);

const camera = new THREE.PerspectiveCamera(FOV, 1, 0.1, 120);
const world = new THREE.Group();
scene.add(world);

// ---------------------------------------------------------------- 光
const ambient = new THREE.HemisphereLight(0x9ba9bd, 0x30251b, 0.82);
scene.add(ambient);

const key = new THREE.DirectionalLight(0xfff1de, 2.02);
key.position.set(-6.5, 7.2, 9.5);
key.castShadow = true;
key.shadow.mapSize.set(3072, 3072);
key.shadow.radius = 2.6;
key.shadow.bias = -0.0004;
key.shadow.normalBias = 0.02;
Object.assign(key.shadow.camera, { left: -7, right: 7, top: 7, bottom: -2.5, near: 4, far: 30 });
key.shadow.camera.updateProjectionMatrix();
world.add(key);

const rim = new THREE.DirectionalLight(0x7f9dc4, 0.85);
rim.position.set(6, 4.5, -7);
world.add(rim);

// 正前方一盏很弱的冷光：只负责把暗面抬起来一点，不加高光
const frontFill = new THREE.DirectionalLight(0xc9d8e6, 0.30);
frontFill.position.set(-1.5, 3.0, 14);
world.add(frontFill);

const kick = new THREE.PointLight(0xffbe86, 3, 18, 2);
kick.position.set(-3.2, 3.0, 5.0);
world.add(kick);

const deskLamp = new THREE.PointLight(0xffd2a0, 26, 14, 2);
deskLamp.position.set(0.55, 2.35, 3.55);
world.add(deskLamp);

// ---------------------------------------------------------------- 地面
const floor = new THREE.Mesh(new THREE.PlaneGeometry(120, 120),
  new THREE.MeshStandardMaterial({ color: C.floor, roughness: 0.94, metalness: 0.0 }));
floor.rotation.x = -Math.PI / 2;
floor.receiveShadow = true;
world.add(floor);

// ---------------------------------------------------------------- 材质
// 环境反射：一张手绘的等距贴图（暖灯在左上、冷光在右、底下全暗），
// 过 PMREM 后当 IBL 用 —— 塑料盒和玻璃盖的“亮边”全靠它，不加就没质感。
const envTex = (() => {
  const cv = document.createElement('canvas');
  cv.width = 512; cv.height = 256;
  const g = cv.getContext('2d');
  const lg = g.createLinearGradient(0, 0, 0, 256);
  lg.addColorStop(0, '#3a2f22');
  lg.addColorStop(0.42, '#1d1712');
  lg.addColorStop(0.60, '#0c0a08');
  lg.addColorStop(1, '#050403');
  g.fillStyle = lg; g.fillRect(0, 0, 512, 256);
  const blob = (x, y, r, col) => {
    const rg = g.createRadialGradient(x, y, 0, x, y, r);
    rg.addColorStop(0, col); rg.addColorStop(1, 'rgba(0,0,0,0)');
    g.fillStyle = rg; g.fillRect(x - r, y - r, r * 2, r * 2);
  };
  blob(96, 62, 96, 'rgba(255,226,183,0.95)');
  blob(360, 96, 120, 'rgba(120,152,196,0.42)');
  blob(256, 30, 120, 'rgba(255,240,214,0.30)');
  const t = new THREE.CanvasTexture(cv);
  t.mapping = THREE.EquirectangularReflectionMapping;
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
})();
const pmrem = new THREE.PMREMGenerator(renderer);
pmrem.compileEquirectangularShader();
const envRT = pmrem.fromEquirectangular(envTex);
scene.environment = envRT.texture;
envTex.dispose();
pmrem.dispose();

// 柜体木纹：竖向纹理 + 细噪点
const woodTex = (() => {
  const cv = document.createElement('canvas');
  cv.width = 512; cv.height = 512;
  const g = cv.getContext('2d');
  g.fillStyle = '#2a2119'; g.fillRect(0, 0, 512, 512);
  for (let i = 0; i < 260; i++) {
    const x = Math.random() * 512;
    const w = 0.6 + Math.random() * 3.4;
    const a = 0.03 + Math.random() * 0.10;
    g.strokeStyle = Math.random() > 0.5 ? `rgba(120,96,68,${a})` : `rgba(10,7,5,${a})`;
    g.lineWidth = w;
    g.beginPath();
    let y = -10;
    g.moveTo(x, y);
    while (y < 522) { y += 24; g.lineTo(x + Math.sin(y * 0.012 + i) * 2.2, y); }
    g.stroke();
  }
  const t = new THREE.CanvasTexture(cv);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(2, 1.5);
  return t;
})();

const matBody = new THREE.MeshPhysicalMaterial({
  color: C.ivory, roughness: 0.62, clearcoat: 0.22, clearcoatRoughness: 0.42, envMapIntensity: 0.55,
});
const matCase = matBody;                       // 柜里的侧脊用另一套（见下面分户卡）
const matFurn = new THREE.MeshStandardMaterial({
  color: 0xa89a86, map: woodTex, roughness: 0.72, metalness: 0.04, envMapIntensity: 0.22,
});
const matBench = new THREE.MeshStandardMaterial({ color: C.bench, roughness: 0.6, envMapIntensity: 0.3 });
const matFurnEdge = new THREE.MeshStandardMaterial({ color: 0xbaa88d, roughness: 0.46, metalness: 0.08, envMapIntensity: 0.5 });
const matPlate = new THREE.MeshStandardMaterial({ color: C.plate, roughness: 0.38, metalness: 0.45, envMapIntensity: 0.9 });
const matDark = new THREE.MeshStandardMaterial({ color: C.dark, roughness: 0.36, metalness: 0.5, envMapIntensity: 0.8 });
const matMetal = new THREE.MeshStandardMaterial({ color: 0x8b8172, roughness: 0.22, metalness: 0.95, envMapIntensity: 1.2 });

// 细颗粒粗糙度贴图（共享，一次性生成）
const grainTex = (() => {
  const cv = document.createElement('canvas');
  cv.width = cv.height = 256;
  const g = cv.getContext('2d');
  const img = g.createImageData(256, 256);
  for (let i = 0; i < img.data.length; i += 4) {
    const v = 190 + Math.random() * 65;
    img.data[i] = img.data[i + 1] = img.data[i + 2] = v;
    img.data[i + 3] = 255;
  }
  g.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(cv);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  return t;
})();
matBody.roughnessMap = grainTex;
matBody.roughness = 0.74;

// 格内顶部暗角（一张竖直渐变）：让每格像凹进去的
const bayShadeMat = (() => {
  const cv = document.createElement('canvas');
  cv.width = 8; cv.height = 64;
  const g = cv.getContext('2d');
  const lg = g.createLinearGradient(0, 0, 0, 64);
  lg.addColorStop(0, 'rgba(0,0,0,.88)');
  lg.addColorStop(0.32, 'rgba(0,0,0,.38)');
  lg.addColorStop(0.72, 'rgba(0,0,0,0)');
  g.fillStyle = lg; g.fillRect(0, 0, 8, 64);
  const t = new THREE.CanvasTexture(cv);
  return new THREE.MeshBasicMaterial({ map: t, transparent: true, depthWrite: false });
})();

const baseCol = { case: new THREE.Color(C.ivory), furn: new THREE.Color(0xa89a86), edge: new THREE.Color(0xbaa88d) };
const bgCol = new THREE.Color(C.bg);

const boxGeo = new RoundedBoxGeometry(W, H, D, 4, 0.014);
const faceGeo = new THREE.PlaneGeometry(W * 0.935, H * 0.935);

const texLoader = new THREE.TextureLoader();
const texCache = new Map();
function loadCover(p, onFail) {
  if (!p) return null;
  if (texCache.has(p)) return texCache.get(p);
  const t = texLoader.load(p, undefined, undefined, () => { if (onFail) onFail(); });
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  texCache.set(p, t);
  return t;
}

// ---------------------------------------------------------------- 柜体（按曲库规模自适应分格）
// 曲库大小差别很大：3 位艺术家和 3000 位不能用同一套格子。
// 这里按数量算行列，再按页翻；1 位艺术家就是一个单格小柜，不会摆 27 个空格子。
const pitchX = W + GAPX, pitchY = H + GAPY;
let cols = COLS, rows = 1, wallW = 0, wallH = 0, topY = 0, startX = 0;
let pageStart = 0;

const pivot = new THREE.Group();
pivot.rotation.y = YAW;
world.add(pivot);

const cabinet = new THREE.Group();
pivot.add(cabinet);

const slabs = [];
const spineGeo = new THREE.BoxGeometry(1, 1, 1);
const cabMats = [];
const matCardBody = matBody.clone();
cabMats.push({ m: matCardBody, c0: new THREE.Color(C.ivory) });

// 尽量凑成"宽 7 格、高不超过 4 排"的家具比例；装不下就分页。
function computeGrid(n) {
  if (n <= 3) return { cols: Math.max(1, n), rows: 1 };
  if (n <= 9) return { cols: Math.ceil(n / 2), rows: 2 };
  let rows = Math.min(4, Math.max(2, Math.ceil(n / 7)));
  let cols = Math.min(9, Math.ceil(n / rows));
  rows = Math.min(4, Math.ceil(n / cols));
  return { cols, rows };
}

function pageSize() { return cols * rows; }
function pageOf(i) { return Math.floor(i / pageSize()); }
function fitPageTo(i) {
  const p = pageOf(i);
  if (p * pageSize() !== pageStart) { pageStart = p * pageSize(); buildCabinet(); return true; }
  return false;
}

function buildCabinet() {
  for (let i = cabinet.children.length - 1; i >= 0; i--) cabinet.remove(cabinet.children[i]);
  slabs.length = 0;
  cabMats.length = 1;                    // 只保留卡片本体材质

  const g0 = computeGrid(artists.length);
  cols = g0.cols; rows = g0.rows;
  wallW = cols * pitchX - GAPX;
  wallH = rows * pitchY;
  topY = PLINTH + (rows - 1) * pitchY + H + 0.16;
  startX = -wallW / 2 + W / 2;

  // ---- 柜体零件
  const parts = [];
  const shelfGeo = new RoundedBoxGeometry(wallW + 0.14, 0.04, CARCASS_D, 2, 0.007);
  for (let r = 0; r < rows; r++) {
    const y = r === 0 ? PLINTH - 0.025 : PLINTH + r * pitchY - GAPY / 2;
    const m = new THREE.Mesh(shelfGeo, matFurn);
    m.position.set(0, y, -0.02);
    parts.push(m);
    // 层板前沿：一条更深的压条，家具的做法
    const edge = new THREE.Mesh(new RoundedBoxGeometry(wallW + 0.14, 0.052, 0.024, 2, 0.007), matFurnEdge);
    edge.position.set(0, y, CARCASS_D / 2 + 0.012);
    parts.push(edge);
    // 层板与背板之间的阴影缝
    const seam = new THREE.Mesh(new THREE.BoxGeometry(wallW + 0.10, 0.012, 0.02),
      new THREE.MeshStandardMaterial({ color: 0x0b0907, roughness: 1 }));
    seam.position.set(0, y, -CARCASS_D / 2 + 0.04);
    parts.push(seam);
  }

  // 踢脚：往里收，柜子才像"站"在地上而不是压在地上
  const plinth = new THREE.Mesh(new RoundedBoxGeometry(wallW + 0.24, PLINTH - 0.02, CARCASS_D - 0.06, 2, 0.008), matFurn);
  plinth.position.set(0, (PLINTH - 0.02) / 2, -0.02);
  parts.push(plinth);
  const kickShadow = new THREE.Mesh(new THREE.BoxGeometry(wallW + 0.24, 0.012, 0.02),
    new THREE.MeshStandardMaterial({ color: 0x090706, roughness: 1 }));
  kickShadow.position.set(0, PLINTH - 0.03, CARCASS_D / 2 - 0.10);
  parts.push(kickShadow);

  const top = new THREE.Mesh(new RoundedBoxGeometry(wallW + 0.32, 0.09, CARCASS_D + 0.10, 3, 0.012), matFurn);
  top.position.set(0, topY, 0.03);
  parts.push(top);

  [-1, 1].forEach(sd => {
    const side = new THREE.Mesh(new RoundedBoxGeometry(0.12, topY + 0.09, CARCASS_D + 0.08, 2, 0.01), matFurn);
    side.position.set(sd * (wallW / 2 + 0.16), (topY + 0.09) / 2, 0.02);
    parts.push(side);
  });

  const back = new THREE.Mesh(new THREE.BoxGeometry(wallW + 0.30, topY, 0.05),
    new THREE.MeshStandardMaterial({ color: 0x171310, roughness: 0.92 }));
  back.position.set(0, topY / 2, -CARCASS_D / 2 + 0.02);
  back.receiveShadow = true;
  parts.push(back);

  parts.forEach(o => { o.castShadow = true; o.receiveShadow = true; cabinet.add(o); });

  // ---- 侧脊 + 分户卡（只建当前页）
  const to = Math.min(artists.length, pageStart + pageSize());
  for (let i = pageStart; i < to; i++) {
    const a = artists[i];
    const g = new THREE.Group();
    const n = Math.max(2, Math.min(13, a.albums.length));
    const bayInner = pitchX - 0.24;
    let t = 0.052, gap = 0.012;
    let packW = n * (t + gap) - gap;
    if (packW > bayInner) { const k = bayInner / packW; t *= k; gap *= k; packW = bayInner; }

    const tint = new THREE.Color(a.tint || '#8b8072');
    for (let k = 0; k < n; k++) {
      const c = tint.clone();
      const hsl = { h: 0, s: 0, l: 0 };
      c.getHSL(hsl);
      c.setHSL(hsl.h + (k % 3 - 1) * 0.012, Math.min(0.62, hsl.s * 1.15), Math.min(0.72, hsl.l + (k % 4) * 0.035));
      const smat = new THREE.MeshStandardMaterial({ color: c, roughness: 0.72 });
      cabMats.push({ m: smat, c0: c.clone() });
      const m = new THREE.Mesh(spineGeo, smat);
      const hh = H * 0.955 * (0.975 + ((k * 37) % 6) * 0.008);        // 高矮差 1.5%
      const tt = t * (0.95 + ((k * 53) % 5) * 0.025);                  // 厚薄差
      m.scale.set(tt, hh, D * (0.97 + ((k * 29) % 4) * 0.012));
      m.position.set(-packW / 2 + t / 2 + k * (t + gap), -H / 2 + hh / 2, ((k * 17) % 3 - 1) * 0.003);
      m.castShadow = true; m.receiveShadow = true;
      g.add(m);
    }

    const tabMat = new THREE.MeshStandardMaterial({ color: 0xf1eadc, roughness: 0.6 });
    cabMats.push({ m: tabMat, c0: new THREE.Color(0xf1eadc) });
    const tab = new THREE.Mesh(spineGeo, tabMat);
    tab.scale.set(0.028, H * 1.035, D * 1.04);
    tab.position.set(-packW / 2 - 0.035, H * 0.018, 0);
    tab.castShadow = true;
    g.add(tab);

    // 分户卡：斜靠在侧脊前的方卡，正面是艺术家照片
    const cardG = new THREE.Group();
    const cardBody = new THREE.Mesh(new RoundedBoxGeometry(0.52, 0.52, 0.012, 2, 0.006), matCardBody);
    cardBody.castShadow = true; cardBody.receiveShadow = true;
    cardG.add(cardBody);
    const firstCover = a.albums[0] && a.albums[0].cover ? a.albums[0].cover : null;
    let avTex = null;
    const picMat = new THREE.MeshBasicMaterial({ color: 0xffffff });
    if (a.avatar) {
      avTex = loadCover(a.avatar, () => {
        // 没有头像就退回他第一张专辑的封面，别留黑块
        const fb = firstCover ? loadCover(firstCover) : null;
        if (fb) { picMat.map = fb; picMat.needsUpdate = true; }
      });
    }
    if (!a.avatar && firstCover) avTex = loadCover(firstCover);
    if (avTex || firstCover) {
      if (avTex) picMat.map = avTex;
      cabMats.push({ m: picMat, c0: new THREE.Color(0xffffff) });
      const matBorder = new THREE.Mesh(new THREE.PlaneGeometry(0.475, 0.475),
        new THREE.MeshStandardMaterial({ color: 0x2a231b, roughness: 0.85 }));
      matBorder.position.z = 0.007;
      cardG.add(matBorder);
      const pic = new THREE.Mesh(new THREE.PlaneGeometry(0.45, 0.45), picMat);
      pic.position.z = 0.0088;
      cardG.add(pic);
    } else {
      const stripMat = new THREE.MeshBasicMaterial({ color: new THREE.Color(a.tint || '#8b8072') });
      cabMats.push({ m: stripMat, c0: new THREE.Color(a.tint || '#8b8072') });
      const strip = new THREE.Mesh(new THREE.PlaneGeometry(0.44, 0.06), stripMat);
      strip.position.set(0, -0.10, 0.0085);
      cardG.add(strip);
    }
    cardG.position.set(0, -H / 2 + 0.275, 0.086);
    cardG.rotation.x = -0.06;
    g.add(cardG);

    const cover = new THREE.Mesh(faceGeo, new THREE.MeshBasicMaterial({ visible: false }));
    cover.position.set(0, 0, D / 2 + 0.006);
    g.add(cover);

    const slot = i - pageStart;
    const r = Math.floor(slot / cols), c = slot % cols;
    const base = new THREE.Vector3(startX + c * pitchX, PLINTH + r * pitchY + H / 2, -D / 2 - 0.075);
    g.position.copy(base);
    g.userData = { i, base, cover, v: new THREE.Vector3(), f: new THREE.Vector3() };
    cabinet.add(g);
    slabs.push(g);
  }

  // 新一页从暗处推上来
  slabs.forEach(s2 => { s2.position.z -= 0.5; });
}

// ---------------------------------------------------------------- 矮台
const bench = new THREE.Group();
bench.position.set(0, 0, BENCH_Z);
world.add(bench);
{
  const b = new THREE.Mesh(new RoundedBoxGeometry(BENCH_W, BENCH_Y, BENCH_D, 3, 0.02), matBench);
  b.position.y = BENCH_Y / 2;
  b.castShadow = true; b.receiveShadow = true;
  bench.add(b);
}

// ---------------------------------------------------------------- 专辑池
const pool = [];
for (let i = 0; i < POOL; i++) {
  const g = new THREE.Group();
  const body = new THREE.Mesh(boxGeo, matBody);
  body.castShadow = true; body.receiveShadow = true;
  g.add(body);
  // 盒脊：真实 CD 盒左侧那条印着颜色/文字的窄边
  const spine = new THREE.Mesh(new THREE.BoxGeometry(0.05, H * 0.965, D * 1.02),
    new THREE.MeshStandardMaterial({ color: 0x8b8072, roughness: 0.55, envMapIntensity: 0.5 }));
  spine.position.set(-W / 2 + 0.026, 0, 0);
  spine.castShadow = true;
  g.add(spine);
  const cover = new THREE.Mesh(faceGeo, new THREE.MeshBasicMaterial({ visible: false }));
  cover.position.set(0, 0, D / 2 + 0.005);
  g.add(cover);

  // 背板：盒子有厚度，侧着看才是"一盒"而不是"一张"
  const backPlate = new THREE.Mesh(new RoundedBoxGeometry(W * 0.99, H * 0.99, 0.012, 2, 0.005),
    new THREE.MeshStandardMaterial({ color: 0xd8cebb, roughness: 0.62 }));
  backPlate.position.z = -D / 2 - 0.004;
  backPlate.castShadow = true;
  g.add(backPlate);

  // 塑料前檐：上、左、下三边，逆光时各亮一条
  const lipMat = new THREE.MeshPhysicalMaterial({
    color: 0xe8e2d4, roughness: 0.14, metalness: 0, transparent: true, opacity: 0.3,
    clearcoat: 1, clearcoatRoughness: 0.05,
  });
  const lipTop = new THREE.Mesh(new THREE.BoxGeometry(W * 0.99, 0.022, 0.012), lipMat);
  lipTop.position.set(0, H * 0.478, D / 2 + 0.002);
  lipTop.castShadow = true;
  g.add(lipTop);
  const lipLeft = new THREE.Mesh(new THREE.BoxGeometry(0.02, H * 0.96, 0.012), lipMat);
  lipLeft.position.set(-W * 0.487, 0, D / 2 + 0.002);
  g.add(lipLeft);
  const lipBottom = new THREE.Mesh(new THREE.BoxGeometry(W * 0.99, 0.016, 0.012), lipMat);
  lipBottom.position.set(0, -H * 0.483, D / 2 + 0.002);
  g.add(lipBottom);

  // 铰链：左侧两个小缺口
  const hingeMat = new THREE.MeshStandardMaterial({ color: 0x4a4238, roughness: 0.5, metalness: 0.3 });
  [0.3, -0.3].forEach(t => {
    const hinge = new THREE.Mesh(new THREE.BoxGeometry(0.018, 0.06, 0.016), hingeMat);
    hinge.position.set(-W * 0.496, H * t, D / 2 - 0.001);
    g.add(hinge);
  });

  g.visible = false;
  g.userData = { k: i, body, cover, spine, v: new THREE.Vector3(), f: new THREE.Vector3(), target: null, albumIndex: undefined, leaving: false };
  world.add(g);
  pool.push(g);
}

// ---------------------------------------------------------------- CD 机
const player = new THREE.Group();
player.position.set(4.15, BENCH_Y, BENCH_Z);
world.add(player);
const G = { lid: null, disc: null, art: null };
const controlMeshes = [];
let playIcon;
{
  const matPlayer = new THREE.MeshStandardMaterial({ color: 0x4a3e31, roughness: 0.44, metalness: 0.18 });
  const base = new THREE.Mesh(new RoundedBoxGeometry(2.24, 0.18, 2.40, 4, 0.024), matPlayer);
  base.position.y = 0.09;
  base.castShadow = true; base.receiveShadow = true;
  player.add(base);

  const faceplate = new THREE.Mesh(new THREE.BoxGeometry(2.24, 0.055, 0.02), matPlate);
  faceplate.position.set(0, 0.09, 1.205);
  player.add(faceplate);

  const topPlate = new THREE.Mesh(new RoundedBoxGeometry(2.20, 0.012, 2.36, 3, 0.006),
    new THREE.MeshStandardMaterial({ color: 0x6b5f50, roughness: 0.34, metalness: 0.45, envMapIntensity: 0.9 }));
  topPlate.position.set(0, 0.196, 0);
  topPlate.receiveShadow = true;
  player.add(topPlate);

  // 四个脚钉：机器才像放在台面上，而不是"长"在台面上
  [[-1, -1], [-1, 1], [1, -1], [1, 1]].forEach(([sx, sz]) => {
    const foot = new THREE.Mesh(new THREE.CylinderGeometry(0.045, 0.05, 0.028, 16),
      new THREE.MeshStandardMaterial({ color: 0x14100c, roughness: 0.6, metalness: 0.4 }));
    foot.position.set(sx * 0.95, -0.006, sz * 0.78);
    foot.castShadow = true;
    player.add(foot);
  });

  // 后侧铰链轴
  const barrel = new THREE.Mesh(new THREE.CylinderGeometry(0.03, 0.03, 1.9, 16), matMetal);
  barrel.rotation.z = Math.PI / 2;
  barrel.position.set(0, 0.212, -0.96);
  player.add(barrel);

  // 转轴毂：盘面中间那个凸起
  const spindle = new THREE.Mesh(new THREE.CylinderGeometry(0.055, 0.07, 0.02, 24),
    new THREE.MeshStandardMaterial({ color: 0x8a8072, roughness: 0.3, metalness: 0.8 }));
  spindle.position.set(0, 0.208, 0);
  player.add(spindle);

  const slot = new THREE.Mesh(new THREE.BoxGeometry(0.62, 0.026, 0.012),
    new THREE.MeshStandardMaterial({ color: 0x120f0c, roughness: 0.28, metalness: 0.4 }));
  slot.position.set(-0.28, 0.092, 1.218);
  player.add(slot);

  const led = new THREE.Mesh(new THREE.SphereGeometry(0.014, 10, 8),
    new THREE.MeshBasicMaterial({ color: 0xff5b46 }));
  led.position.set(0.98, 0.092, 1.218);
  player.add(led);

  const well = new THREE.Mesh(new THREE.CylinderGeometry(0.63, 0.63, 0.016, 64), matDark);
  well.position.y = 0.19;
  player.add(well);

  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.64, 0.010, 10, 72), matMetal);
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.197;
  player.add(ring);

  const disc = new THREE.Group();
  disc.add(new THREE.Mesh(new THREE.CylinderGeometry(0.515, 0.515, 0.009, 64),
    new THREE.MeshStandardMaterial({ color: 0x2c2823, roughness: 0.26, metalness: 0.3 })));
  const silverRing = new THREE.Mesh(new THREE.RingGeometry(0.415, 0.515, 64),
    new THREE.MeshStandardMaterial({ color: 0xc9c3b6, roughness: 0.22, metalness: 0.85, envMapIntensity: 1.1 }));
  silverRing.rotation.x = -Math.PI / 2;
  silverRing.position.y = 0.0062;
  disc.add(silverRing);

  const art = new THREE.Mesh(new THREE.CircleGeometry(0.415, 64), new THREE.MeshBasicMaterial({ color: 0xffffff }));
  art.rotation.x = -Math.PI / 2;
  art.position.y = 0.006;
  disc.add(art);
  const hub = new THREE.Mesh(new THREE.CircleGeometry(0.042, 20), matMetal);
  hub.rotation.x = -Math.PI / 2;
  hub.position.y = 0.007;
  disc.add(hub);
  disc.position.set(0, 0.20, 0);
  disc.visible = false;
  player.add(disc);

  const lid = new THREE.Group();
  const glass = new THREE.Mesh(new RoundedBoxGeometry(2.18, 0.035, 1.84, 3, 0.014),
    new THREE.MeshPhysicalMaterial({
      color: 0xcdd4d6, roughness: 0.05, metalness: 0, transparent: true, opacity: 0.34,
      clearcoat: 1, clearcoatRoughness: 0.04,
    }));
  glass.position.set(0, 0.016, 0.92);
  glass.castShadow = true;
  lid.add(glass);
  lid.position.set(0, 0.205, -0.92);
  lid.rotation.x = 0;
  player.add(lid);

  Object.assign(G, { lid, disc, art });
}


// 机身前沿的三枚实体键：有金属倒角、凹槽和刻印，命中检测与键盘入口共用命令。
function iconTexture(command, paused = false) {
  const cv = document.createElement('canvas'); cv.width = cv.height = 128;
  const g = cv.getContext('2d'); g.fillStyle = '#241b13';
  const bar = x => g.fillRect(x, 30, 13, 68);
  const triangle = (x, flip = false) => { g.beginPath(); g.moveTo(x, 30); g.lineTo(x + (flip ? -40 : 40), 64); g.lineTo(x, 98); g.closePath(); g.fill(); };
  if (command === 'togglePlay') { if (paused) { bar(40); bar(76); } else triangle(48); }
  else if (command === 'previous') { bar(28); triangle(91, true); }
  else { triangle(35); bar(88); }
  const t = new THREE.CanvasTexture(cv); t.colorSpace = THREE.SRGBColorSpace; return t;
}
const playTexture = iconTexture('togglePlay'), pauseTexture = iconTexture('togglePlay', true);
['previous', 'togglePlay', 'next'].forEach((command, i) => {
  const group = new THREE.Group(); group.position.set((i - 1) * .59, .232, 1.035); group.userData.command = command;
  const socket = new THREE.Mesh(new RoundedBoxGeometry(.50, .018, .25, 3, .012), matDark); socket.position.y = -.025; group.add(socket);
  const keycap = new THREE.Mesh(new RoundedBoxGeometry(.44, .055, .22, 4, .018), new THREE.MeshStandardMaterial({ color: 0xc9b99d, roughness: .32, metalness: .72 }));
  keycap.castShadow = true; keycap.receiveShadow = true; group.add(keycap);
  const icon = new THREE.Mesh(new THREE.PlaneGeometry(.13, .13), new THREE.MeshBasicMaterial({ map: command === 'togglePlay' ? playTexture : iconTexture(command), transparent: true, depthWrite: false }));
  icon.rotation.x = -Math.PI / 2; icon.position.y = .029; group.add(icon); if (command === 'togglePlay') playIcon = icon;
  player.add(group); controlMeshes.push(group);
  const button = document.createElement('button'); button.type = 'button'; button.dataset.command = command;
  button.textContent = ['上一首', '播放 / 暂停', '下一首'][i]; button.onclick = () => control(command); el.controls.append(button);
});
function updateControls() {
  playIcon.material.map = S.spin ? pauseTexture : playTexture;
  const button = el.controls.querySelector('[data-command="togglePlay"]');
  button.textContent = S.spin ? '暂停' : '播放'; button.setAttribute('aria-pressed', String(S.spin));
}

// ---------------------------------------------------------------- 相机
const cam = { p: new THREE.Vector3(0, 4, 11), pv: new THREE.Vector3(), a: new THREE.Vector3(0, 2.2, 0), av: new THREE.Vector3() };
const camT = { p: new THREE.Vector3(0, 4, 11), a: new THREE.Vector3(0, 2.2, 0) };
const tanHalf = Math.tan(THREE.MathUtils.degToRad(FOV / 2));
const STAGE_X = 0.85;

function shotL1() {
  const avail = Math.max(0.45, 1 - RESERVE_PX / innerWidth);
  // 柜子有 yaw，右端离镜头更近、投影更宽，所以留的余量要大于几何宽度
  const fitK = cols >= 8 ? 1.30 : (cols >= 6 ? 1.26 : 1.12);
  const dW = (wallW * Math.cos(YAW) * fitK) / (2 * tanHalf * camera.aspect * avail);
  // 三排以上按实际高度取景；一排两排的小柜子别把镜头拉太远，否则像漂在空房间里
  const nominalH = PLINTH + 2 * pitchY + H + 0.16;
  const stageH = topY >= nominalH ? topY : Math.max(topY * 1.15, 2.5);
  const dH = (stageH * 1.20) / (2 * tanHalf);
  const d = Math.max(dW, dH);
  const aimY = (topY >= nominalH ? topY : stageH) * 0.485;
  camT.p.set(0, aimY + d * Math.tan(THREE.MathUtils.degToRad(11.5)), d);
  camT.a.set(0, aimY, 0);
  const visibleW = 2 * d * tanHalf * camera.aspect;
  world.position.x = (RESERVE_PX / 2) * visibleW / innerWidth;
}
function shotL2() {
  const wx = world.position.x;   // 与第一层同一个房间，镜头跟着整体偏移
  camT.p.set(0.15 + wx, 2.10, 8.7);
  camT.a.set(0.42 + wx, 1.24, 2.85);
}
function shotL3() {
  const wx = world.position.x;
  camT.p.set(2.75 + wx, 2.18, 9.9);
  camT.a.set(3.15 + wx, 1.00, 3.05);
}

// ---------------------------------------------------------------- 布局
function slabTarget(g) {
  const i = g.userData.i;
  const b = g.userData.base;
  const t = { x: b.x, y: b.y, z: b.z, rx: 0, s: 1 };
  if (S.layer === 1) {
    if (i === S.artist) { t.z = 0.34; t.rx = -0.05; t.s = 1.02; }
    else if (i === S.hover) { t.z = b.z + 0.035; }
  } else if (i === S.artist) {
    t.z = 0.52;
  }
  return t;
}

function albumTarget(d) {
  const sel = d === 0;
  const t = {
    x: -0.35 + d * FAN_GAP,
    y: BENCH_Y + H * FAN_SCALE / 2,
    z: BENCH_Z - 0.22 - Math.abs(d) * 0.035,
    ry: sel ? 0 : -0.24,
    s: sel ? FAN_SCALE * 1.04 : FAN_SCALE,
    rx: 0,
  };
  if (sel) { t.z += 0.17; t.y += 0.015; }
  return t;
}

// ---------------------------------------------------------------- 层
function toLayer1() {
  remember(1); S.layer = 1;
  fitPageTo(S.artist);
  G.disc.visible = false;
  G.lid.rotation.x = 0;
  pool.forEach(p => { p.visible = false; });
  cabinet.position.z = 0;
  shotL1();
  renderCrumbs();
  el.action.hidden = true;
  el.back.hidden = false;
  el.hint.innerHTML = '滚轮翻阅 · 单击抽出 · <kbd>ESC</kbd> 回主页';
  setDim(0);
  render(); renderIndex();
}

function toLayer2() {
  remember(2); S.layer = 2;
  G.disc.visible = false;
  G.lid.rotation.x = 0;
  cabinet.position.z = -2.35;
  shotL2();
  renderCrumbs();
  el.action.hidden = false;
  el.back.hidden = false;
  el.hint.innerHTML = '滚轮切换专辑 · 单击选中 · <kbd>ENTER</kbd> 放入 CD 机 · <kbd>ESC</kbd> 返回';
  setDim(2.3);
  pool.forEach(p => { p.visible = false; p.userData.albumIndex = undefined; p.userData.leaving = false; p.userData.target = null; });
  fillPool(true); render(); renderIndex();
}

function toLayer3() {
  remember(3); S.layer = 3;
  const a = artists[S.artist], al = a.albums[S.album];
  const tex = loadCover(al.cover);
  if (tex) { G.art.material.map = tex; G.art.material.color.set(0xffffff); G.art.material.needsUpdate = true; }
  // 第三层只留被选中那一盒，挪到机器左边（其余收回暗处）
  pool.forEach(p => {
    if (p.userData.albumIndex === S.album) {
      p.visible = true;
      p.userData.leaving = false;
      p.userData.target = { x: 1.95, y: BENCH_Y + H * 1.2 / 2, z: 3.05, ry: -0.26, rx: 0, s: 1.2 };
    } else {
      p.visible = false;
      p.userData.albumIndex = undefined;
    }
  });
  G.disc.visible = true;
  G.lid.rotation.x = -1.15;
  clearTimeout(lidTimer); lidTimer = setTimeout(() => { if (!disposed && S.layer === 3) G.lid.rotation.x = 0; }, 620);
  shotL3();
  renderCrumbs();
  el.action.hidden = true;
  el.hint.innerHTML = '空格 播放 / 暂停 · <kbd>ESC</kbd> 返回专辑';
  setDim(2.5);
  render(); renderIndex();
  if (!restoring && OPTS.onPlay && al.trackIds && al.trackIds.length) OPTS.onPlay(al);
  updateControls();
}

function backOne() {
  if (past.length) { future.push(snapshot()); restore(past.pop()); }
  else if (S.layer === 3) toLayer2();
  else if (S.layer === 2) toLayer1();
  else closeRoom();
}

function setDim(k) {
  const a = Math.min(1, k * 0.72);
  cabinet.traverse(o => {
    if (!o.isMesh) return;
    const m = o.material;
    if (!m || !m.color) return;
    if (!m.userData.c0) m.userData.c0 = m.color.clone();
    m.color.copy(m.userData.c0).lerp(bgCol, a);
  });
}

// 把"第 idx 张专辑"喂给某个盒子
function assignAlbum(p, idx) {
  const al = artists[S.artist].albums[idx];
  p.userData.album = al;
  p.userData.albumIndex = idx;
  if (al.tint) p.userData.spine.material.color.set(al.tint);
  const tex = loadCover(al.cover);
  if (tex) {
    p.userData.cover.material.map = tex;
    p.userData.cover.material.color.set(0xffffff);
    p.userData.cover.material.needsUpdate = true;
    p.userData.cover.material.visible = true;
  } else {
    p.userData.cover.material.visible = false;
  }
  return al;
}

// 摆/滑动这一叠。snap=true 表示立刻落位（进第二层那一下），
// 否则只换目标，位置交给弹簧 —— 滚动时旧盒滑出去、新盒从画面外滑进来。
function fillPool(snap) {
  const list = artists[S.artist].albums;
  const want = [];
  for (let d = -FAN_MID; d <= FAN_MID; d++) {
    const idx = S.album + d;
    if (idx >= 0 && idx < list.length) want.push(idx);
  }

  // 1) 先给所有在场盒更新目标；不再需要的标记离场
  pool.forEach(p => {
    const idx = p.userData.albumIndex;
    if (idx === undefined) return;
    if (want.indexOf(idx) < 0) {
      p.userData.leaving = true;
      p.userData.target = albumTarget(idx - S.album);
    } else {
      p.userData.leaving = false;
      p.userData.target = albumTarget(idx - S.album);
    }
  });

  // 2) 补上缺的位置：优先用完全空闲的，其次用已经滑到画面外的
  want.forEach(idx => {
    if (pool.some(p => p.userData.albumIndex === idx)) return;
    const free = pool.find(p => p.userData.albumIndex === undefined)
      || pool.find(p => p.userData.leaving && Math.abs(p.userData.albumIndex - S.album) >= FAN_MID + 1);
    if (!free) return;

    const d = idx - S.album;
    assignAlbum(free, idx);
    free.visible = true;
    free.userData.leaving = false;

    const t = albumTarget(d);
    free.userData.target = t;
    if (snap) {
      free.position.set(t.x, t.y, t.z);
      free.rotation.set(t.rx, t.ry, 0);
      free.scale.setScalar(t.s);
      free.userData.v.set(0, 0, 0);
      free.userData.f.set(0, 0, 0);
    } else {
      // 从相邻的候场位滑进来（不是凭空出现）
      const from = albumTarget(d + (d >= 0 ? 1 : -1));
      free.position.set(from.x, from.y, from.z);
      free.rotation.set(from.rx, from.ry, 0);
      free.scale.setScalar(from.s);
      free.userData.v.set(0, 0, 0);
      free.userData.f.set(0, 0, 0);
    }
  });
}

// ---------------------------------------------------------------- 文案
const pad2 = n => String(n).padStart(2, '0');
const mmss = s => `${Math.floor(s / 60)}:${pad2(Math.round(s % 60))}`;
function yearRange(a) {
  const ys = a.albums.map(x => x.year).filter(Boolean).sort((m, n) => m - n);
  if (!ys.length) return '年份未知';
  return ys[0] === ys[ys.length - 1] ? String(ys[0]) : `${ys[0]}—${ys[ys.length - 1]}`;
}
function albumMeta(al) {
  const b = [];
  if (al.year) b.push(String(al.year));
  b.push(`${al.tracks} 首`);
  if (al.dur) b.push(`${Math.round(al.dur / 60)} 分 ${pad2(Math.round(al.dur % 60))} 秒`);
  return b.join(' · ');
}
const esc = s => String(s).replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

function render() {
  const a = artists[S.artist];
  if (OPTS.mode === 'shell') {
    S.trackId = playback.trackId;
    S.spin = playback.playing && !!a.albums[S.album]?.trackIds.includes(playback.trackId);
    updateControls();
  }
  renderCrumbs();
  if (S.layer === 1) {
    el.eyebrow.textContent = 'ARTIST / 专辑艺术家';
    el.title.textContent = a.name;
    el.meta.textContent = `${a.albums.length} 张专辑 · ${yearRange(a)}`;
    el.body.innerHTML = '';
    el.foot.textContent = `CABINET ${pad2(S.artist + 1)} / ${pad2(artists.length)}`;
  } else {
    const al = a.albums[S.album];
    el.eyebrow.textContent = S.layer === 3 ? (S.spin ? 'NOW PLAYING / 正在播放' : 'CD PLAYER / CD 机') : 'ALBUM / 专辑';
    el.title.textContent = al.title;
    el.meta.textContent = albumMeta(al);
    const rowsHtml = al.list.slice(0, 8).map((t, i) =>
      `<li${S.layer === 3 && al.trackIds?.[i] === S.trackId ? ' class="on"' : ''}><span class="n">${pad2(i + 1)}</span>` +
      `<span class="t">${esc(t.t)}</span><span class="d">${mmss(t.s)}</span></li>`).join('');
    el.body.innerHTML = `<ol class="tracks">${rowsHtml}</ol>`;
    el.foot.textContent = al.tracks > 8 ? `另有 ${al.tracks - 8} 首 · DISC 01` : `共 ${al.tracks} 首 · DISC 01`;
  }
}

function renderIndex() {
  if (S.layer === 1) {
    el.idxBig.textContent = pad2(S.artist + 1);
    el.idxSub.textContent = `ARTIST / ${pad2(artists.length)}`;
  } else {
    el.idxBig.textContent = pad2(S.album + 1);
    el.idxSub.textContent = `ALBUM / ${pad2(artists[S.artist].albums.length)}`;
  }
}

// ---------------------------------------------------------------- 引线
const vA = new THREE.Vector3();
function updateLeader() {
  const w = innerWidth, h = innerHeight;
  const obj = S.layer === 1 ? slabs.find(g => g.userData.i === S.artist) : (S.layer === 2 ? pool.find(p => p.userData.albumIndex === S.album) : player);
  if (!obj || (S.layer === 2 && !obj.visible)) { el.svg.innerHTML = ''; return; }

  if (S.layer === 3) vA.set(-1.05, 0.15, 0); else vA.set(W * 0.5, H * 0.40, D * 0.5);
  vA.applyMatrix4(obj.matrixWorld).project(camera);
  const sx = (vA.x * 0.5 + 0.5) * w;
  const sy = (-vA.y * 0.5 + 0.5) * h;

  const box = el.card.getBoundingClientRect();
  const ex = box.right + 16;
  const ey = Math.min(Math.max(sy, box.top + 6), box.bottom - 6);
  if (sx < ex + 74) { el.svg.innerHTML = ''; return; }

  const px = Math.max(ex + 54, sx - 96);
  el.svg.innerHTML =
    `<path d="M ${sx} ${sy} L ${px} ${sy} L ${px} ${ey} L ${ex + 4} ${ey}" />` +
    `<circle class="dot" cx="${sx}" cy="${sy}" r="2.1" />`;
}

// ---------------------------------------------------------------- 输入
const ray = new THREE.Raycaster();
const ndc = new THREE.Vector2();
function pickAt(cx, cy) {
  const r = renderer.domElement.getBoundingClientRect();
  ndc.x = ((cx - r.left) / r.width) * 2 - 1;
  ndc.y = -((cy - r.top) / r.height) * 2 + 1;
  ray.setFromCamera(ndc, camera);
  const list = S.layer === 1 ? slabs : (S.layer === 2 ? pool.filter(p => p.visible) : controlMeshes);
  const hits = ray.intersectObjects(list, true);
  if (!hits.length) return null;
  let o = hits[0].object;
  while (o && o.userData.i === undefined && !o.userData.album && !o.userData.command) o = o.parent;
  return (o && (o.userData.i !== undefined || o.userData.album || o.userData.command)) ? o : null;
}

const canvas = renderer.domElement;
addL(canvas, 'pointermove', e => {
  const hit = pickAt(e.clientX, e.clientY);
  el.root.classList.toggle('rr-pointer', !!hit);
  if (S.layer === 1) S.hover = hit ? hit.userData.i : -1;
  canvas.title = hit?.userData.command ? ({previous:'上一首',next:'下一首',togglePlay:S.spin?'暂停':'播放'}[hit.userData.command]) : '';
});

addL(canvas, 'pointerdown', e => {
  if (e.button !== 0) return;
  const hit = pickAt(e.clientX, e.clientY);
  if (!hit) return;
  if (hit.userData.command) { control(hit.userData.command); hit.position.y = .218; return; }
  if (S.layer === 1) {
    S.artist = hit.userData.i; S.album = 0;
    renderIndex(); render(); toLayer2();
  } else if (S.layer === 2) {
    const idx = artists[S.artist].albums.indexOf(hit.userData.album);
    if (idx < 0) return;
    if (idx === S.album) toLayer3();
    else { S.album = idx; fillPool(false); renderIndex(); render(); }
  }
});

addL(window, 'wheel', e => {
  const dir = Math.sign(e.deltaY);
  if (!dir) return;
  if (S.layer === 1) {
    S.artist = THREE.MathUtils.clamp(S.artist + dir, 0, artists.length - 1);
    if (fitPageTo(S.artist)) shotL1();
    renderIndex(); render();
  } else if (S.layer === 2) {
    const n = artists[S.artist].albums.length;
    const next = THREE.MathUtils.clamp(S.album + dir, 0, n - 1);
    if (next !== S.album) { S.album = next; fillPool(false); renderIndex(); render(); }
  }
}, { passive: true });

// 独立预览也支持侧键；宿主版本由 shell 优先把侧键路由给唱片室。
if (OPTS.mode !== 'shell') {
  addL(window, 'mousedown', e => { if (e.button === 3 || e.button === 4) e.preventDefault(); }, true);
  addL(window, 'mouseup', e => { if (e.button === 3 || e.button === 4) { e.preventDefault(); e.stopImmediatePropagation(); e.button === 3 ? backOne() : forwardOne(); } }, true);
}
addL(window, 'keydown', e => {
  if (e.defaultPrevented || e.ctrlKey || e.altKey || e.metaKey || e.isComposing) return;
  if (e.target.closest?.('input,textarea,select')) return;
  if (['Escape', 'ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'].includes(e.key)) { e.preventDefault(); e.stopImmediatePropagation(); }
  if ((e.code === 'Space' || e.key === 'Enter') && e.target.closest?.('button')) return;
  if (e.key === 'Escape') { if (S.layer > 1) backOne(); else closeRoom(); }
  else if (e.key === 'Enter' && S.layer === 2) { e.preventDefault(); toLayer3(); }
  else if (e.code === 'Space' && S.layer === 3) { e.preventDefault(); control('togglePlay'); }
  else if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
    if (S.layer === 1) { S.artist = Math.min(artists.length - 1, S.artist + 1); if (fitPageTo(S.artist)) shotL1(); renderIndex(); render(); }
    else if (S.layer === 2) { const n = artists[S.artist].albums.length; if (S.album < n - 1) { S.album++; fillPool(false); renderIndex(); render(); } }
  } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
    if (S.layer === 1) { S.artist = Math.max(0, S.artist - 1); if (fitPageTo(S.artist)) shotL1(); renderIndex(); render(); }
    else if (S.layer === 2) { if (S.album > 0) { S.album--; fillPool(false); renderIndex(); render(); } }
  }
});

el.action.addEventListener('click', () => { if (S.layer === 2) toLayer3(); });

function closeRoom() {
  if (OPTS.onClose) OPTS.onClose();
  else if (history.length > 1) history.back();
}
el.back.addEventListener('click', () => S.layer === 1 ? closeRoom() : backOne());

// ---------------------------------------------------------------- 循环
function resize() {
  const w = innerWidth, h = innerHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  if (S.layer === 1) shotL1();
  else if (S.layer === 2) shotL2();
  else shotL3();
}
addL(window, 'resize', resize);

const clock = new THREE.Clock();
function step(cur, vel, target, k, c, dt) {
  const a = (target - cur) * k - vel * c;
  const nv = vel + a * dt;
  return [cur + nv * dt, nv];
}
function stepV3(v3, vel, t, k, c, dt) {
  let v;
  [v, vel.x] = step(v3.x, vel.x, t.x, k, c, dt); v3.x = v;
  [v, vel.y] = step(v3.y, vel.y, t.y, k, c, dt); v3.y = v;
  [v, vel.z] = step(v3.z, vel.z, t.z, k, c, dt); v3.z = v;
}

const K = 135, Cd = 21;
const pan = { v: 0, vel: 0 };

function frame() {
  if (disposed) return;
  const dt = Math.min(0.05, clock.getDelta());
  // 第一层抬高暗面与曝光；进入近景后平滑回到原有的暖光。
  renderer.toneMappingExposure += ((S.layer === 1 ? 1.48 : 1.14) - renderer.toneMappingExposure) * Math.min(1, dt * 5);
  frontFill.intensity += ((S.layer === 1 ? .80 : .30) - frontFill.intensity) * Math.min(1, dt * 5);
  ambient.intensity += ((S.layer === 1 ? .82 : .5) - ambient.intensity) * Math.min(1, dt * 5);
  controlMeshes.forEach(button => { button.position.y += (.232 - button.position.y) * Math.min(1, dt * 12); });
  rafId = requestAnimationFrame(frame);

  stepV3(cam.p, cam.pv, camT.p, 48, 13.5, dt);
  stepV3(cam.a, cam.av, camT.a, 48, 13.5, dt);
  camera.position.copy(cam.p);
  camera.lookAt(cam.a);

  // 柜体在两层之间做很小的纵深位移（第二层往后退）
  const cz = S.layer === 1 ? 0 : -2.35;
  cabinet.position.z += (cz - cabinet.position.z) * Math.min(1, dt * 6);

  slabs.forEach(g => {
    const t = slabTarget(g);
    stepV3(g.position, g.userData.v, t, K, Cd, dt);
    let rx;
    [rx, g.userData.f.x] = step(g.rotation.x, g.userData.f.x, t.rx, K, Cd, dt);
    g.rotation.x = rx;
    const [s, sv] = step(g.scale.x, g.userData.f.y, t.s, K, Cd, dt);
    g.userData.f.y = sv;
    g.scale.setScalar(s);
  });

  if (S.layer >= 2) {
    pool.forEach(p => {
      if (!p.visible || !p.userData.target) return;
      const t = p.userData.target;
      stepV3(p.position, p.userData.v, t, K, Cd, dt);
      let ry;
      [ry, p.userData.f.x] = step(p.rotation.y, p.userData.f.x, t.ry, K, Cd, dt);
      p.rotation.y = ry;
      const [s, sv] = step(p.scale.x, p.userData.f.y, t.s, K, Cd, dt);
      p.userData.f.y = sv;
      p.scale.setScalar(s);

      // 滑出画面外的旧盒：落位后收起来，空出位置给下一张
      if (p.userData.leaving && Math.abs(p.userData.albumIndex - S.album) > FAN_MID
          && p.position.distanceToSquared(new THREE.Vector3(t.x, t.y, t.z)) < 0.01) {
        p.visible = false;
        p.userData.albumIndex = undefined;
        p.userData.leaving = false;
        p.userData.target = null;
      }
    });
  }

  if (S.layer === 3 && G.disc.visible && S.spin) G.disc.rotation.y += dt * 1.4;

  renderer.render(scene, camera);
  updateLeader();
}

function destroy() {
  disposed = true;
  cancelAnimationFrame(rafId);
  clearTimeout(lidTimer);
  unlisteners.forEach(fn => fn());
  renderer.dispose();
  el.root.remove();
}

buildCabinet();
resize();
toLayer1();
frame();

window.__rr = {
  S,
  proj(i) {
    const v = new THREE.Vector3().applyMatrix4(slabs[i].matrixWorld).project(camera);
    return { x: (v.x * .5 + .5) * innerWidth, y: (-v.y * .5 + .5) * innerHeight };
  },
  meshCheck() {
    let n = 0; const cols = {};
    cabinet.traverse(o => {
      if (!o.isMesh) return;
      n++;
      const c = o.material.color ? o.material.color.getHexString() : 'na';
      cols[c] = (cols[c] || 0) + 1;
    });
    return { meshes: n, colors: cols };
  },
  dimInfo() {
    const pic = cabMats.filter(x => x.m.map);
    return {
      n: cabMats.length,
      pic0: pic[0] ? pic[0].m.color.getHexString() : 'none',
      spine0: cabMats[1] ? cabMats[1].m.color.getHexString() : 'none',
      furn: matFurn.color.getHexString(),
      edge: matFurnEdge.color.getHexString(),
    };
  },
  projAlbum(k) {
    const p = pool.find(q => q.userData.albumIndex === k) || pool[k];
    const v = new THREE.Vector3().applyMatrix4(p.matrixWorld).project(camera);
    return { x: (v.x * .5 + .5) * innerWidth, y: (-v.y * .5 + .5) * innerHeight, vis: p.visible };
  },
  projControl(command) {
    const v = controlMeshes.find(button => button.userData.command === command).getWorldPosition(new THREE.Vector3()).project(camera);
    return { x: (v.x * .5 + .5) * innerWidth, y: (-v.y * .5 + .5) * innerHeight };
  },
  toLayer1, toLayer2, toLayer3,
};

  return { destroy, close: closeRoom, back: backOne, forward: forwardOne, setPlaying(v, trackId) { playback = { playing: !!v, trackId }; S.spin = !!v; S.trackId = trackId; updateControls(); render(); }, state: S };
}
