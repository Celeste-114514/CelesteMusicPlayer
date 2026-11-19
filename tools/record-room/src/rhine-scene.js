import * as THREE from 'three';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { cabinet, editions, transport, printCover } from './rhine-models.js';

const HALF_TURN = Math.PI / 2;
const smooth = t => t * t * (3 - 2 * t);
const range = (t, a, b) => smooth(THREE.MathUtils.clamp((t - a) / (b - a), 0, 1));

function writeLabel(mesh, name, detail) {
  const map = mesh.material.map;
  const canvas = map.image;
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = '#4b4b44';
  ctx.font = '24px monospace';
  ctx.fillText(detail, 11, 34, 574);
  ctx.fillStyle = '#252722';
  ctx.font = '600 58px "Segoe UI","Microsoft YaHei",sans-serif';
  ctx.fillText(name, 10, 108, 574);
  map.needsUpdate = true;
}

export function createScene(stage, artists, onFrame) {
  const renderer = new THREE.WebGLRenderer({ alpha: true, antialias: true });
  renderer.setClearColor(0, 0);
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
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
    mesh.castShadow = !mesh.material.transparent && !mesh.material.alphaTest;
    mesh.receiveShadow = mesh.castShadow;
  });
  const maps = new Map();
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
  let groupOfArtists = -1;
  let faceNumber = 0;
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

  function fillCabinet(groupNumber) {
    archive.trays.forEach(({ body, spines, legend, tab }, i) => {
      const index = groupNumber * 24 + i;
      const entry = artists[index];
      body.userData.artist = entry ? index : undefined;
      tab.visible = false;
      body.position.z = 1.556;
      legend.visible = !!entry;
      const count = Math.min(spines.length, entry?.albums.length ?? 0);
      if (entry) writeLabel(legend, entry.name, `${index + 1}   /   ${entry.albums.length} 张专辑`);
      const width = 1.47 / Math.max(8, count);
      spines.forEach((spine, n) => {
        spine.visible = n < count;
        spine.position.x = .74 + (n + .5 - count / 2) * width;
        spine.scale.x = (width - .019) / .100;
      });
    });
  }

  function selectArtist(index, animate) {
    const nextGroup = Math.floor(index / 24);
    const nextFace = Math.floor(index / 6) % 4;
    const changeGroup = nextGroup !== groupOfArtists;
    const difference = (nextFace - faceNumber + 6) % 4 - 2;
    const step = changeGroup && difference === 0 ? (nextGroup > groupOfArtists ? 4 : -4) : difference;
    const target = animate ? archive.cabinet.rotation.y - step * HALF_TURN : -nextFace * HALF_TURN;
    if (changeGroup) fillCabinet(nextGroup);
    turn = animate ? { angle: archive.cabinet.rotation.y, target, started: performance.now() } : null;
    if (!turn) archive.cabinet.rotation.y = target;
    groupOfArtists = nextGroup;
    faceNumber = nextFace;
  }

  function showEditions(reopen) {
    const list = artists[artist].albums;
    const start = Math.max(0, Math.min(album - 2, list.length - 6));
    if (artist !== fanArtist) writeLabel(fan.legend, artists[artist].name, `${list.length} 张专辑`);
    if (reopen) intro = performance.now();
    fan.cases.forEach((sleeve, i) => {
      const idx = start + i;
      sleeve.visible = idx < list.length;
      if (!sleeve.visible) return;
      sleeve.userData.album = idx;
      sleeve.userData.tab.visible = idx === album;
      assign(sleeve.userData.cover, list[idx], artists[artist].name);
      if (reopen || start !== fanStart || artist !== fanArtist) {
        sleeve.position.set((i - 2.5) * (reopen ? .206 : .79), .801, .032);
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
      { at: [0, .86, .10], eye: [1.10, 2.12, 8.75], span: [5.75, 2.95] },
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
        archive.cabinet.rotation.y = THREE.MathUtils.lerp(turn.angle, turn.target, amount);
        if (amount === 1) turn = null;
        else moving = true;
      }
      for (const tray of archive.trays) {
        const selected = tray.body.userData.artist === artist && !turn;
        tray.tab.visible = selected;
        const end = selected ? 2.05 : 1.556;
        tray.body.position.z = THREE.MathUtils.damp(tray.body.position.z, end, 13, dt);
        moving ||= Math.abs(tray.body.position.z - end) > .001;
      }
    }
    if (layer === 2) {
      const age = intro ? (now - intro) / 1000 : 2;
      let lastX = -Infinity;
      for (const [i, sleeve] of fan.cases.entries()) {
        if (!sleeve.visible) continue;
        const focus = sleeve.userData.album === album;
        const spread = range(age, .05 + Math.abs(i - 2.5) * .025, .8);
        const lift = range(age, .58, 1.12);
        const afterFocus = sleeve.userData.album > album;
        const x = (i - (Math.min(6, artists[artist].albums.length) - 1) / 2) * .77 + (afterFocus ? .24 : 0);
        const desired = THREE.MathUtils.lerp((i - 2.5) * .206, x, spread);
        sleeve.position.x = Math.max(lastX + .204, desired);
        lastX = sleeve.position.x;
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
  function pick(event) {
    const rect = stage.getBoundingClientRect();
    ray.setFromCamera(new THREE.Vector2((event.clientX - rect.left) / rect.width * 2 - 1, 1 - (event.clientY - rect.top) / rect.height * 2), camera);
    const targets = layer === 1 ? archive.trays.slice(faceNumber * 6, faceNumber * 6 + 6).map(tray => tray.body) : fan.cases;
    const field = layer === 1 ? 'artist' : 'album';
    if (layer === 3 || (layer === 1 && turn)) return undefined;
    let object = ray.intersectObjects(targets.filter(target => target.visible), true)[0]?.object;
    while (object && object.userData[field] === undefined) object = object.parent;
    return object?.userData[field];
  }

  const observer = new ResizeObserver(view);
  observer.observe(stage);
  renderer.domElement.addEventListener('webglcontextlost', event => { event.preventDefault(); cancelAnimationFrame(frameId); frameId = 0; });
  renderer.domElement.addEventListener('webglcontextrestored', requestFrame);
  return {
    show, pick,
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
