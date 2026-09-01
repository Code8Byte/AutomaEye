/* Scroll-driven 3D story: camera keyframes tied to scroll progress, the
 * inspected part cross-fading into a controller board, and DOM overlays
 * toggled by discrete stage index. Plain ES module - no framework - but the
 * renderer and listeners are torn down together in destroy() so this can be
 * dropped into a component lifecycle later without leaking a WebGL context.
 */
import * as THREE from 'three';

const ACCENT = 0x00f0c0;
const ACCENT_2 = 0x7c5cff;
const DANGER = 0xff5d6c;

const mount = document.getElementById('story3d-canvas');
const storyEl = document.getElementById('story3d');

if (mount && storyEl) {
  try {
    init();
  } catch (err) {
    console.warn('AutomaEye: story 3D unavailable', err);
  }
}

function init() {
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.setSize(mount.clientWidth, mount.clientHeight, false);
  mount.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(45, mount.clientWidth / mount.clientHeight, 0.1, 100);

  scene.add(new THREE.AmbientLight(0x6d7893, 1.35));
  const key = new THREE.DirectionalLight(0xffffff, 2.0);
  key.position.set(4, 5, 4);
  scene.add(key);
  const rimA = new THREE.PointLight(ACCENT, 26, 22);
  rimA.position.set(-4, 2, 4);
  scene.add(rimA);
  const rimB = new THREE.PointLight(ACCENT_2, 20, 22);
  rimB.position.set(4, -2, -4);
  scene.add(rimB);

  /* ---------- Object A: the inspected part (stamped lead frame) ---------- */
  const partGroup = new THREE.Group();
  const partMaterials = [];

  function partMat(options) {
    const mat = new THREE.MeshStandardMaterial(Object.assign({ transparent: true }, options));
    partMaterials.push(mat);
    return mat;
  }

  const metal = partMat({ color: 0xaab4c4, metalness: 0.95, roughness: 0.28 });
  const bar = new THREE.Mesh(new THREE.BoxGeometry(3.0, 0.16, 0.62), metal);
  partGroup.add(bar);

  // Upright tabs along the strip - the features being inspected.
  for (let i = 0; i < 5; i++) {
    const tab = new THREE.Mesh(new THREE.BoxGeometry(0.3, 0.52, 0.12), metal);
    tab.position.set(-1.2 + i * 0.6, 0.34, -0.12);
    partGroup.add(tab);

    const notch = new THREE.Mesh(
      new THREE.CylinderGeometry(0.07, 0.07, 0.2, 20),
      partMat({ color: 0x1a1f2c, metalness: 0.5, roughness: 0.6 })
    );
    notch.rotation.x = Math.PI / 2;
    notch.position.set(-1.2 + i * 0.6, 0, 0.2);
    partGroup.add(notch);
  }

  const barEdges = new THREE.LineSegments(
    new THREE.EdgesGeometry(new THREE.BoxGeometry(3.0, 0.16, 0.62)),
    new THREE.LineBasicMaterial({ color: 0x5a6685, transparent: true })
  );
  partMaterials.push(barEdges.material);
  partGroup.add(barEdges);

  // The defect: a burr on the fourth tab, lit red once it is flagged.
  const defectMat = partMat({ color: 0x6b2630, emissive: 0x000000, metalness: 0.4, roughness: 0.5 });
  const defect = new THREE.Mesh(new THREE.ConeGeometry(0.07, 0.2, 14), defectMat);
  defect.position.set(0.6, 0.66, -0.12);
  partGroup.add(defect);

  scene.add(partGroup);

  /* ---------- Object B: PLC / Arduino-style controller ---------- */
  const boardGroup = new THREE.Group();
  const boardMaterials = [];

  function boardMat(options) {
    const mat = new THREE.MeshStandardMaterial(Object.assign({ transparent: true, opacity: 0 }, options));
    boardMaterials.push(mat);
    return mat;
  }

  const pcb = new THREE.Mesh(
    new THREE.BoxGeometry(2.4, 0.1, 1.5),
    boardMat({ color: 0x0e5c46, metalness: 0.2, roughness: 0.6 })
  );
  boardGroup.add(pcb);

  const chip = new THREE.Mesh(
    new THREE.BoxGeometry(0.55, 0.16, 0.55),
    boardMat({ color: 0x0a0a0a, metalness: 0.3, roughness: 0.4 })
  );
  chip.position.set(-0.45, 0.13, 0);
  boardGroup.add(chip);

  const pinMat = boardMat({ color: 0xc0c0c0, metalness: 0.85, roughness: 0.28 });
  for (let i = 0; i < 8; i++) {
    const pin = new THREE.Mesh(new THREE.BoxGeometry(0.05, 0.16, 0.05), pinMat);
    pin.position.set(-1.05 + i * 0.28, 0.13, 0.8);
    boardGroup.add(pin);
  }

  const capMat = boardMat({ color: 0x1d2740, metalness: 0.5, roughness: 0.5 });
  for (let i = 0; i < 3; i++) {
    const cap = new THREE.Mesh(new THREE.CylinderGeometry(0.09, 0.09, 0.22, 16), capMat);
    cap.position.set(0.35 + i * 0.28, 0.16, -0.42);
    boardGroup.add(cap);
  }

  // The output LED - emissive turns on when the reject signal fires.
  const ledMat = boardMat({ color: 0x113322, emissive: 0x000000 });
  const led = new THREE.Mesh(new THREE.SphereGeometry(0.1, 20, 16), ledMat);
  led.position.set(0.85, 0.16, 0.55);
  boardGroup.add(led);

  const ledGlow = new THREE.PointLight(ACCENT, 0, 3);
  ledGlow.position.copy(led.position);
  boardGroup.add(ledGlow);

  scene.add(boardGroup);

  /* ---------- Camera keyframes, one per narrative stage ---------- */
  const camKeys = [
    new THREE.Vector3(0, 0.2, 5.5),    // 01 camera input - frontal
    new THREE.Vector3(1.7, 0.7, 4.4),  // 02 detection frame - slight turn
    new THREE.Vector3(-1.9, 1.1, 4.0), // 03 live interface - orbit
    new THREE.Vector3(0.5, 0.6, 2.4),  // 04 defect - push in
    new THREE.Vector3(0, 1.7, 6.4),    // 05 output - pull back to the board
  ];
  const lookTarget = new THREE.Vector3(0, 0, 0);

  const railItems = Array.prototype.slice.call(document.querySelectorAll('.story3d-rail .item'));
  const allOverlays = Array.prototype.slice.call(document.querySelectorAll('.story3d .overlay'));
  const overlaysByStage = {
    0: [],
    1: ['ov-frame'],
    2: ['ov-frame', 'ov-panel'],
    3: ['ov-frame', 'ov-panel', 'ov-defect'],
    4: ['ov-output'],
  };
  const fill = document.getElementById('story3d-fill');

  let lastStage = -1;

  function setStageVisuals(stageIndex) {
    railItems.forEach((item) => {
      item.classList.toggle('active', Number(item.dataset.i) === stageIndex);
    });
    const ids = overlaysByStage[stageIndex] || [];
    allOverlays.forEach((el) => el.classList.toggle('active', ids.indexOf(el.id) !== -1));
  }

  /* ---------- Scroll -> camera, crossfade, overlays ---------- */
  function onScroll() {
    const rect = storyEl.getBoundingClientRect();
    const total = storyEl.offsetHeight - window.innerHeight;
    const p = total > 0 ? Math.max(0, Math.min(1, -rect.top / total)) : 0;

    if (fill) fill.style.height = (p * 100) + '%';

    const raw = p * (camKeys.length - 1);
    const segIndex = Math.min(camKeys.length - 2, Math.floor(raw));
    const frac = raw - segIndex;
    const stageIndex = Math.min(camKeys.length - 1, Math.floor(p * camKeys.length));

    camera.position.lerpVectors(camKeys[segIndex], camKeys[segIndex + 1], frac);
    camera.lookAt(lookTarget);

    // Cross-fade the part into the controller board across the final segment.
    let partOpacity = 1;
    let boardOpacity = 0;
    if (segIndex === camKeys.length - 2) {
      partOpacity = 1 - frac;
      boardOpacity = frac;
    }
    partMaterials.forEach((mat) => { mat.opacity = partOpacity; });
    boardMaterials.forEach((mat) => { mat.opacity = boardOpacity; });

    // Defect highlight during stage 04.
    const flagged = stageIndex === 3;
    defectMat.emissive.setHex(flagged ? DANGER : 0x000000);
    defectMat.color.setHex(flagged ? DANGER : 0x6b2630);

    // Output fires near the end of the final segment.
    const ledOn = segIndex === camKeys.length - 2 && frac > 0.6;
    ledMat.emissive.setHex(ledOn ? ACCENT : 0x000000);
    ledMat.color.setHex(ledOn ? ACCENT : 0x113322);
    ledGlow.intensity = ledOn ? 6 : 0;

    if (stageIndex !== lastStage) {
      setStageVisuals(stageIndex);
      lastStage = stageIndex;
    }
  }

  function onResize() {
    const w = mount.clientWidth;
    const h = mount.clientHeight;
    if (!w || !h) return;
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h, false);
    onScroll();
  }

  /* ---------- Render loop, paused while off-screen ---------- */
  let running = false;

  function animate() {
    if (!running) return;
    requestAnimationFrame(animate);
    if (!reduced) {
      partGroup.rotation.y += 0.004;
      boardGroup.rotation.y += 0.004;
    }
    renderer.render(scene, camera);
  }

  const io = new IntersectionObserver((entries) => {
    const visible = entries[0].isIntersecting;
    if (visible && !running) {
      running = true;
      animate();
    } else if (!visible) {
      running = false;
    }
  }, { threshold: 0 });
  io.observe(storyEl);

  document.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onResize);

  onResize();
  onScroll();
  setStageVisuals(0);

  // Exposed so a future component wrapper can tear everything down.
  window.AutomaEyeStory3D = {
    destroy() {
      running = false;
      io.disconnect();
      document.removeEventListener('scroll', onScroll);
      window.removeEventListener('resize', onResize);
      renderer.dispose();
      scene.traverse((obj) => {
        if (obj.geometry) obj.geometry.dispose();
        if (obj.material) {
          (Array.isArray(obj.material) ? obj.material : [obj.material]).forEach((m) => m.dispose());
        }
      });
      if (renderer.domElement.parentNode) renderer.domElement.parentNode.removeChild(renderer.domElement);
    },
  };
}
