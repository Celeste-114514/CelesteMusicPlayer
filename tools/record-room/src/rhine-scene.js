import * as THREE from 'three';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { cabinet, editions, transport, printCover, ARTISTS_PER_FACE, EDITIONS_PER_ROW } from './rhine-models.js';

const HALF_TURN = Math.PI / 2;
export { ARTISTS_PER_FACE };
const mod = (n, m) => ((n % m) + m) % m;
const smooth = t => t * t * (3 - 2 * t);
const range = (t, a, b) => smooth(THREE.MathUtils.clamp((t - a) / (b - a), 0, 1));

export function albumWindow(album, total) {
  const capacity = EDITIONS_PER_ROW * 2;
  const start = Math.max(0, Math.min(album - Math.floor(capacity / 2), total - capacity));
  return { start, end: Math.min(total, start + capacity) };
}

function writeLabel(mesh, name, detail, portrait, artistCard = false) {
  const map = mesh.material.map;
  const canvas = map.image;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = '#ede9df';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  let textX = 32;
  if (artistCard) {
    const size = 184, x = 36, y = 36;
    ctx.fillStyle = '#d6cfc2';
    ctx.fillRect(x, y, size, size);
    if (portrait) {
      const w = portrait.naturalWidth, h = portrait.naturalHeight;
      const crop = Math.min(w, h);
      ctx.drawImage(portrait, (w - crop) / 2, (h - crop) / 2, crop, crop, x, y, size, size);
    } else {
      ctx.fillStyle = '#61584d';
      ctx.font = '600 82px "Segoe UI","Microsoft YaHei",sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(Array.from(name.trim())[0] || '', x + size / 2, 156, size - 24);
      ctx.textAlign = 'left';
    }
    textX = 255;
  }
  const textWidth = canvas.width - textX - 32;
  ctx.fillStyle = '#525248';
  ctx.font = '500 42px "Segoe UI","Microsoft YaHei",sans-serif';
  ctx.fillText(detail, textX, 89, textWidth);
  ctx.fillStyle = '#232420';
  ctx.font = '600 82px "Segoe UI","Microsoft YaHei",sans-serif';
  ctx.fillText(name, textX, 193, textWidth);
  map.needsUpdate = true;
}

export function createScene(stage, artists, onFrame) {
  const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true });
  renderer.setClearColor(0, 0);
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = .88;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.VSMShadowMap;
  stage.prepend(renderer.domElement);

  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  const room = new RoomEnvironment();
  const environment = pmrem.fromScene(room, .032);
  scene.environment = environment.texture;
  scene.environmentIntensity = .64;
  room.dispose();
  pmrem.dispose();
  scene.add(new THREE.HemisphereLight(0xfffaec, 0xbab8ac, .4));
  const key = new THREE.DirectionalLight(0xfff1df, 1.75);
  key.position.set(-2.7, 8.8, 4.1);
  key.castShadow = true;
  key.shadow.mapSize.set(512, 512);
  Object.assign(key.shadow.camera, { left: -5, right: 5, top: 5.5, bottom: -4.5, far: 22 });
  key.shadow.normalBias = .005;
  key.shadow.bias = -.00018;
  key.shadow.radius = 8;
  scene.add(key);
  const fill = new THREE.DirectionalLight(0xe4eeee, .75);
  fill.position.set(3.5, 4, -3.5);
  scene.add(fill);
  const ground = new THREE.Mesh(new THREE.PlaneGeometry(50, 50), new THREE.ShadowMaterial({ opacity: .19, color: 0x50473c }));
  ground.rotation.x = -Math.PI / 2;
  ground.position.y = -.02;
  ground.receiveShadow = true;
  scene.add(ground);

  const roots = [new THREE.Group(), new THREE.Group(), new THREE.Group()];
  const archive = cabinet(roots[0]);
  const fan = editions(roots[1]);
  const deck = transport(roots[2]);
  scene.add(...roots);
  for (const root of roots) root.traverse(mesh => {
    if (!mesh.isMesh) return;
    mesh.castShadow = !mesh.material.transparent && mesh.material.toneMapped !== false;
    mesh.receiveShadow = mesh.castShadow;
  });
  const maps = new Map();
  const portraits = new Map();
  const loader = new THREE.TextureLoader();
  const printSurfaces = [...fan.cases, deck.sleeve].map(sleeve => sleeve.userData.cover);
  printSurfaces.push(deck.discFace);
  let disposed = false;
  let frameId = 0;
  let lastFrame = 0;
  let layer = 1;
  let artist = 0;
  let album = 0;
  let playing = false;
  let loading = null;
  let turn = null;
  let intro = 0;
  let pressed = 0;
  const lastFace = Math.ceil(artists.length / ARTISTS_PER_FACE) - 1;
  const assignedFaces = Array(4).fill(null);
  let dragging = false;
  let fanStart = -1;
  let fanArtist = -1;
  const camera = new THREE.PerspectiveCamera(24, 1, .08, 100);

  function assign(surface, edition, name) {
    const material = surface.material;
    if (surface.userData.edition === edition) return;
    surface.userData.edition = edition;
    let art = maps.get(edition);
    if (!art) {
      const canvas = document.createElement('canvas');
      canvas.width = canvas.height = 640;
      printCover(canvas.getContext('2d'), edition, name, 640, 640);
      const fallback = new THREE.CanvasTexture(canvas);
      fallback.colorSpace = THREE.SRGBColorSpace;
      art = { fallback, cover: null };
      maps.set(edition, art);
      if (edition.cover) {
        art.cover = loader.load(edition.cover, texture => {
          if (disposed || maps.get(edition) !== art) return;
          art.loaded = true;
          for (const print of printSurfaces) {
            if (print.userData.edition === edition) {
              print.material.map = texture;
              print.material.needsUpdate = true;
            }
          }
          requestFrame();
        }, undefined, () => { art.cover.dispose(); art.cover = null; });
        art.cover.colorSpace = THREE.SRGBColorSpace;
      }
    }
    material.map = art.loaded ? art.cover : art.fallback;
    material.needsUpdate = true;
  }

  function trimMaps() {
    const active = new Set(printSurfaces.map(mesh => mesh.userData.edition));
    for (const [edition, art] of maps) {
      if (maps.size <= 32) break;
      if (active.has(edition)) continue;
      art.fallback.dispose();
      art.cover?.dispose();
      maps.delete(edition);
    }
  }

  function portraitFor(entry) {
    if (portraits.has(entry)) return portraits.get(entry);
    const loadImage = url => new Promise(resolve => {
      if (!url) { resolve(null); return; }
      const image = new Image();
      image.crossOrigin = 'anonymous';
      image.onload = () => resolve(image);
      image.onerror = () => resolve(null);
      image.src = url;
    });
    const pending = loadImage(entry.avatar).then(photo => photo || (disposed ? null : loadImage(entry.albums.find(album => album.cover)?.cover)));
    if (portraits.size >= 48) portraits.delete(portraits.keys().next().value);
    portraits.set(entry, pending);
    return pending;
  }

  function fillFace(face) {
    const section = mod(face, 4);
    if (assignedFaces[section] === face) return;
    assignedFaces[section] = face;
    const offset = section * ARTISTS_PER_FACE;
    archive.trays.slice(offset, offset + ARTISTS_PER_FACE).forEach(({ body, spines, legend, tab, panel }, i) => {
      const index = face * ARTISTS_PER_FACE + i;
      const entry = artists[index];
      panel.userData.artist = entry ? index : undefined;
      tab.visible = false;
      body.position.z = 1.556;
      legend.visible = !!entry;
      const count = Math.min(spines.length, entry?.albums.length ?? 0);
      if (entry) {
        const detail = `${entry.albums.length} 张专辑`;
        writeLabel(legend, entry.name, detail, null, true);
        portraitFor(entry).then(photo => {
          if (!photo || disposed || panel.userData.artist !== index) return;
          writeLabel(legend, entry.name, detail, photo, true);
          requestFrame();
        });
      }
      const width = 1.47 / Math.max(8, count);
      spines.forEach((spine, n) => {
        spine.visible = n < count;
        spine.position.x = .74 + (n + .5 - count / 2) * width;
        spine.scale.x = (width - .019) / .100;
      });
    });
  }

  function setCabinetAngle(angle) {
    archive.cabinet.rotation.y = angle;
    // Reassign only the face around the back as the cabinet turns.
    const front = Math.round(-angle / HALF_TURN);
    for (let face = front - 1; face <= front + 2; face++) fillFace(face);
  }

  function selectArtist(index, animate) {
    dragging = false;
    const angle = archive.cabinet.rotation.y;
    const target = -Math.floor(index / ARTISTS_PER_FACE) * HALF_TURN;
    turn = animate && Math.abs(angle - target) > .002 ? { angle, target, started: performance.now() } : null;
    if (!turn) setCabinetAngle(target);
  }

  function dragCabinet(startAngle, turns) {
    turn = null;
    dragging = true;
    const limit = .45 * HALF_TURN;
    setCabinetAngle(THREE.MathUtils.clamp(startAngle + turns * HALF_TURN, -lastFace * HALF_TURN - limit, limit));
    requestFrame();
  }

  function releaseCabinet() {
    const face = THREE.MathUtils.clamp(Math.round(-archive.cabinet.rotation.y / HALF_TURN), 0, lastFace);
    return Math.floor(artist / ARTISTS_PER_FACE) === face ? artist : face * ARTISTS_PER_FACE;
  }

  function showEditions(reopen) {
    const list = artists[artist].albums;
    const { start, end } = albumWindow(album, list.length);
    fan.upper.visible = end - start > EDITIONS_PER_ROW;
    if (artist !== fanArtist) writeLabel(fan.legend, artists[artist].name, `${list.length} 张专辑`);
    if (reopen) intro = performance.now();
    fan.cases.forEach((sleeve, i) => {
      const idx = start + i;
      sleeve.visible = idx < end;
      if (!sleeve.visible) return;
      sleeve.userData.album = idx;
      sleeve.userData.tab.visible = idx === album;
      assign(sleeve.userData.cover, list[idx], artists[artist].name);
      if (reopen || start !== fanStart || artist !== fanArtist) {
        sleeve.position.set((i % EDITIONS_PER_ROW - 2.5) * (reopen ? .206 : .79), .801, .032);
        sleeve.rotation.y = reopen ? HALF_TURN : .995;
      }
    });
    fanStart = start;
    fanArtist = artist;
  }

  function view() {
    const width = stage.clientWidth;
    const height = stage.clientHeight;
    if (!width || !height) return;
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
    frameCamera();
    requestFrame();
  }

  function frameCamera() {
    const shot = [
      { at: [0, 1.76, .07], eye: [2.1, 1.45, 8.75], span: [5.4, 4.62] },
      { at: [0, fan.upper.visible ? 1.42 : .86, -.15], eye: [1.10, 2.12, 8.75], span: [5.96, fan.upper.visible ? 3.75 : 2.95] },
      { at: [-.19, loading ? 1.09 : .65, .05], eye: [1.50, 4.15, 8.75], span: [4.65, loading ? 3.15 : 2.5] }
    ][layer - 1];
    const target = new THREE.Vector3(...shot.at);
    const distance = Math.max(shot.span[0] / camera.aspect, shot.span[1]) / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)));
    camera.position.set(...shot.eye).normalize().multiplyScalar(distance).add(target);
    camera.lookAt(target);
    camera.updateMatrixWorld();
  }

  function show(next, animate, onSeated) {
    const previousLayer = layer;
    const previousArtist = artist;
    layer = next.layer; artist = next.artist; album = next.album;
    roots.forEach((root, n) => { root.visible = n + 1 === layer; });
    loading = null;
    if (layer === 1) selectArtist(artist, animate && previousLayer === 1);
    if (layer === 2) showEditions(animate && (previousLayer !== 2 || previousArtist !== artist));
    if (layer === 3) {
      const current = artists[artist];
      const edition = current.albums[album];
      assign(deck.sleeve.userData.cover, edition, current.name);
      assign(deck.discFace, edition, current.name);
      loading = animate ? { started: performance.now(), onSeated } : null;
      deck.artworkAt(loading ? 0 : 1);
    }
    frameCamera();
    trimMaps();
    requestFrame();
  }

  function requestFrame() {
    if (!disposed && !frameId) frameId = requestAnimationFrame(draw);
  }
  function draw(now) {
    frameId = 0;
    const dt = Math.min(.045, (now - lastFrame) / 1000);
    lastFrame = now;
    let moving = false;
    if (layer === 1) {
      if (turn) {
        const amount = range(now - turn.started, 0, 620);
        setCabinetAngle(THREE.MathUtils.lerp(turn.angle, turn.target, amount));
        if (amount === 1) turn = null;
        else moving = true;
      }
      for (const tray of archive.trays) {
        const selected = tray.panel.userData.artist === artist && !turn && !dragging;
        tray.tab.visible = selected;
        const end = selected ? 2.05 : 1.556;
        tray.body.position.z = THREE.MathUtils.damp(tray.body.position.z, end, 13, dt);
        moving ||= Math.abs(tray.body.position.z - end) > .001;
      }
    }
    if (layer === 2) {
      const age = intro ? (now - intro) / 1000 : 2;
      for (const [i, sleeve] of fan.cases.entries()) {
        if (!sleeve.visible) continue;
        const column = i % EDITIONS_PER_ROW;
        const rowStart = i - column;
        const rowSize = Math.min(EDITIONS_PER_ROW, artists[artist].albums.length - fanStart - rowStart);
        const centered = column - (rowSize - 1) / 2;
        const focus = sleeve.userData.album === album;
        const spread = range(age, .05 + Math.abs(centered) * .025, .8);
        const lift = range(age, .58, 1.12);
        const focusInRow = album >= fanStart + rowStart && album < fanStart + rowStart + rowSize;
        const gap = focusInRow ? Math.sign(sleeve.userData.album - album) * .26 : 0;
        const x = centered * .77 + gap;
        sleeve.position.x = THREE.MathUtils.lerp(centered * .206, x, spread);
        const y = .801 + (focus ? .27 * lift : 0);
        const z = .032 + (focus ? .91 * lift : 0);
        const angle = THREE.MathUtils.lerp(HALF_TURN, focus ? .14 : .995, range(age, focus ? .98 : .56, 1.42));
        sleeve.position.y = THREE.MathUtils.damp(sleeve.position.y, y, 14, dt);
        sleeve.position.z = THREE.MathUtils.damp(sleeve.position.z, z, 14, dt);
        sleeve.rotation.y = THREE.MathUtils.damp(sleeve.rotation.y, angle, 14, dt);
        moving ||= age < 1.42 || Math.abs(sleeve.rotation.y - angle) > .001 || Math.abs(sleeve.position.z - z) > .001;
      }
    }
    if (layer === 3) {
      if (loading) {
        const progress = THREE.MathUtils.clamp((now - loading.started) / 2650, 0, 1);
        deck.artworkAt(progress);
        if (progress === 1) {
          const seated = loading.onSeated;
          loading = null;
          frameCamera();
          seated?.();
        } else moving = true;
      } else if (playing) {
        deck.disc.rotation.y -= dt * 4.2;
        moving = true;
      }
      const offset = pressed ? .034 * (1 - range(now - pressed, 30, 165)) : 0;
      deck.key.position.z = .988 - offset;
      moving ||= offset > 0;
    }
    renderer.render(scene, camera);
    const projection = deck.key.getWorldPosition(new THREE.Vector3()).project(camera);
    onFrame(projection, !!loading);
    if (moving) requestFrame();
  }

  const ray = new THREE.Raycaster();
  const panelNormal = new THREE.Vector3();
  const panelOrientation = new THREE.Quaternion();
  function pick(event) {
    const rect = stage.getBoundingClientRect();
    ray.setFromCamera(new THREE.Vector2((event.clientX - rect.left) / rect.width * 2 - 1, 1 - (event.clientY - rect.top) / rect.height * 2), camera);
    if (layer === 1) {
      archive.cabinet.updateWorldMatrix(true, true);
      const panels = archive.trays.map(tray => tray.panel).filter(panel => {
        panel.getWorldQuaternion(panelOrientation);
        return panelNormal.set(0, 0, 1).applyQuaternion(panelOrientation).dot(ray.ray.direction) < -.02;
      });
      return ray.intersectObjects(panels, false)[0]?.object.userData.artist;
    }
    if (layer === 3) return undefined;
    const targets = fan.cases;
    const field = 'album';
    let object = ray.intersectObjects(targets.filter(target => target.visible), true)[0]?.object;
    while (object && object.userData[field] === undefined) object = object.parent;
    return object?.userData[field];
  }

  const observer = new ResizeObserver(view);
  observer.observe(stage);
  renderer.domElement.addEventListener('webglcontextlost', event => { event.preventDefault(); cancelAnimationFrame(frameId); frameId = 0; });
  renderer.domElement.addEventListener('webglcontextrestored', requestFrame);
  return {
    show, pick, dragCabinet, releaseCabinet,
    cabinetAngle() { return archive.cabinet.rotation.y; },
    press() { pressed = performance.now(); requestFrame(); },
    setPlaying(value) {
      playing = value;
      deck.keyIcon.material = value ? deck.pause : deck.play;
      requestFrame();
    },
    destroy() {
      disposed = true;
      cancelAnimationFrame(frameId);
      observer.disconnect();
      for (const art of maps.values()) { art.fallback.dispose(); art.cover?.dispose(); }
      portraits.clear();
      const resources = new Set([deck.play, deck.pause, deck.play.map, deck.pause.map]);
      scene.traverse(mesh => {
        if (!mesh.isMesh) return;
        resources.add(mesh.geometry);
        resources.add(mesh.material);
        if (mesh.material.map) resources.add(mesh.material.map);
      });
      for (const resource of resources) resource.dispose();
      environment.dispose();
      renderer.dispose();
      renderer.domElement.remove();
    }
  };
}
