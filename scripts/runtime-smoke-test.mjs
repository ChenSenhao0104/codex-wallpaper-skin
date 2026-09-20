import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const sourcePath = path.join(scriptDirectory, '..', 'companion', 'CdpInjectionService.cs');
const source = fs.readFileSync(sourcePath, 'utf8');
const match = source.match(/internal const string BootstrapCoreScript = """\r?\n(?<script>[\s\S]*?)\r?\n        """;/);
if (!match?.groups?.script) throw new Error('Could not extract BootstrapCoreScript from CdpInjectionService.cs.');
const runtime = match.groups.script.split(/\r?\n/).map(line => line.replace(/^        /, '')).join('\n');
const expectedSceneRevision = runtime.match(/__cwsWeSceneLibrary\?\.version === '([^']+)'/)?.[1];
const expectedSceneHostVersion = runtime.match(/__cwsCreateSceneWallpaper\.version === '([^']+)'/)?.[1];
if (!expectedSceneRevision || !expectedSceneHostVersion) throw new Error('Could not extract the expected Scene runtime fingerprints.');
const sceneLibraryFiles = [
  'pkg/container.js', 'pkg/texture.js', 'scene/parse.js', 'scene/effects-parse.js',
  'render/math.js', 'render/noise.js', 'render/hlsl2glsl.js', 'render/effects.js', 'render/renderer.js'
];
const stripModuleSyntax = sceneSource => sceneSource.split(/\r?\n/).flatMap(line => {
  if (/^\s*import\s+.*\bfrom\s+['"][^'"]+['"]\s*;?\s*$/.test(line)) return [];
  if (/^\s*export\s*\{.*\}\s*;?\s*$/.test(line)) return [];
  return [line.replace(/^(\s*)export\s+(?=(?:async\s+)?(?:function|const|let|class)\b)/, '$1')];
}).join('\n');
const sceneLibrarySource = sceneLibraryFiles.map(file => stripModuleSyntax(fs.readFileSync(
  path.join(scriptDirectory, '..', 'companion', 'ThirdParty', 'we-scene', 'src', ...file.split('/')), 'utf8'))).join('\n');
const sceneHostSource = fs.readFileSync(path.join(scriptDirectory, '..', 'companion', 'Runtime', 'scene-host.js'), 'utf8');
const completeRuntime = `(() => {\n${sceneLibrarySource}\nwindow.__cwsWeSceneLibrary = Object.freeze({ version: 'runtime-boundary-smoke', parsePkg, getEntry, parseTex, decodeMip0, decodeMips, FIF, parseScene, resolveMaterial, resolveEffectChain, BUILTIN_MODELS, BUILTIN_MATERIALS, createRenderer, makeTexture, makeTextureMip, generateNoiseTexture });\n})();\n${sceneHostSource}\n${runtime}`;
const cleanupMatch = source.match(/internal const string CleanupScript = """\r?\n(?<script>[\s\S]*?)\r?\n        """;/);
if (!cleanupMatch?.groups?.script) throw new Error('Could not extract CleanupScript from CdpInjectionService.cs.');
const cleanupRuntime = cleanupMatch.groups.script.split(/\r?\n/).map(line => line.replace(/^        /, '')).join('\n');
const verificationMatch = source.match(/internal const string CleanupVerificationScript = """\r?\n(?<script>[\s\S]*?)\r?\n        """;/);
if (!verificationMatch?.groups?.script) throw new Error('Could not extract CleanupVerificationScript from CdpInjectionService.cs.');
const cleanupVerification = verificationMatch.groups.script.split(/\r?\n/).map(line => line.replace(/^        /, '')).join('\n');

class StyleDeclaration {
  #values = new Map();
  setProperty(name, value) { this.#values.set(name, String(value)); }
  removeProperty(name) { this.#values.delete(name); }
  getPropertyValue(name) { return this.#values.get(name) ?? ''; }
  [Symbol.iterator]() { return this.#values.keys(); }
}

class ClassList {
  #values = new Set();
  add(...names) { names.forEach(name => this.#values.add(name)); }
  remove(...names) { names.forEach(name => this.#values.delete(name)); }
  contains(name) { return this.#values.has(name); }
  toggle(name, enabled) { enabled ? this.#values.add(name) : this.#values.delete(name); }
}

class MockElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.children = [];
    this.parentNode = null;
    this.style = new StyleDeclaration();
    this.classList = new ClassList();
    this.attributes = new Map();
    this.listeners = new Map();
    this.clientWidth = 1200;
    this.clientHeight = 800;
    this.complete = false;
    this.naturalWidth = 0;
    this.naturalHeight = 0;
    this.readyState = 0;
    this.pointerEvents = 'auto';
  }
  get parentElement() { return this.parentNode instanceof MockElement ? this.parentNode : null; }
  get isConnected() {
    let current = this;
    while (current) {
      if (current === globalThis.document?.documentElement) return true;
      current = current.parentNode;
    }
    return false;
  }
  appendChild(child) { child.parentNode = this; this.children.push(child); return child; }
  insertBefore(child, before) {
    child.parentNode = this;
    const index = this.children.indexOf(before);
    if (index < 0) this.children.push(child); else this.children.splice(index, 0, child);
    return child;
  }
  remove() {
    if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(child => child !== this);
    this.parentNode = null;
  }
  contains(candidate) {
    if (candidate === this) return true;
    return this.children.some(child => child.contains(candidate));
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); }
  hasAttribute(name) { return this.attributes.has(name); }
  closest(selector) {
    let current = this;
    while (current) {
      if (selector === '[data-cws-surface]' && current.hasAttribute('data-cws-surface')) return current;
      current = current.parentElement;
    }
    return null;
  }
  addEventListener(name, callback) { this.listeners.set(name, callback); }
  removeEventListener(name) { this.listeners.delete(name); }
  matches(selector) {
    return selector.split(',').map(value => value.trim()).some(value => {
      if (/^[a-z]+$/i.test(value)) return this.tagName === value.toUpperCase();
      const role = value.match(/^\[role="([^"]+)"\]$/)?.[1];
      if (role) return this.getAttribute('role') === role;
      return false;
    });
  }
  getBoundingClientRect() { return { width: this.rectWidth ?? 1200, height: this.rectHeight ?? 800 }; }
  pause() { this.paused = true; }
  play() { this.paused = false; return Promise.resolve(); }
  set src(value) {
    this._src = value;
    if (globalThis.deferMediaDecode) return;
    if (this.tagName === 'IMG') {
      this.complete = true;
      this.naturalWidth = 1920;
      this.naturalHeight = 1080;
    } else if (this.tagName === 'VIDEO') {
      this.readyState = 2;
      this.videoWidth = 1920;
      this.videoHeight = 1080;
    }
    if (typeof this.onload === 'function') this.onload();
  }
  get src() { return this._src; }
}

class MockImageElement extends MockElement {
  constructor() { super('img'); }
}

class MockCanvasElement extends MockElement {
  constructor() {
    super('canvas');
    this.drawCount = 0;
    this.context = {
      drawImage: () => { this.drawCount++; },
      getImageData: () => {
        if (paletteReadFailures > 0) {
          paletteReadFailures--;
          throw new Error('mock frame is not paintable yet');
        }
        return { data: canvasPixels };
      }
    };
  }
  getContext() { return this.context; }
}

globalThis.HTMLElement = MockElement;
globalThis.HTMLImageElement = MockImageElement;
globalThis.HTMLCanvasElement = MockCanvasElement;
globalThis.innerWidth = 1200;
globalThis.innerHeight = 800;
const windowListeners = new Map();
globalThis.addEventListener = (name, callback) => windowListeners.set(name, callback);
globalThis.removeEventListener = name => windowListeners.delete(name);
const root = new MockElement('html');
const head = new MockElement('head');
const body = new MockElement('body');
const shell = new MockElement('div');
const app = new MockElement('main');
const sidebar = new MockElement('aside');
const navigation = new MockElement('nav');
const inertOverlay = new MockElement('div');
app.rectWidth = 950;
sidebar.rectWidth = 250;
navigation.rectWidth = 250;
inertOverlay.pointerEvents = 'none';
sidebar.appendChild(navigation);
shell.appendChild(app);
shell.appendChild(sidebar);
body.appendChild(shell);
body.appendChild(inertOverlay);
root.appendChild(head);
root.appendChild(body);

const canvasPixels = new Uint8ClampedArray(32 * 32 * 4);
let paletteReadFailures = 0;
for (let index = 0; index < canvasPixels.length; index += 4) {
  const accent = index < canvasPixels.length / 5;
  canvasPixels[index] = accent ? 40 : 92;
  canvasPixels[index + 1] = accent ? 180 : 55;
  canvasPixels[index + 2] = accent ? 220 : 154;
  canvasPixels[index + 3] = 255;
}

const documentListeners = new Map();
const walk = element => [element, ...element.children.flatMap(walk)];
const findById = (element, id) => {
  if (element.id === id) return element;
  for (const child of element.children) {
    const found = findById(child, id);
    if (found) return found;
  }
  return null;
};
globalThis.document = {
  documentElement: root,
  head,
  body,
  hidden: false,
  createElement(tagName) {
    if (tagName === 'canvas') return new MockCanvasElement();
    if (tagName === 'img') return new MockImageElement();
    return new MockElement(tagName);
  },
  querySelectorAll(selector) {
    const elements = walk(root);
    if (selector === '[data-cws-surface]') return elements.filter(element => element.hasAttribute('data-cws-surface'));
    if (selector === '.cws-media, .cws-overlay') {
      return elements.filter(element => ['cws-media', 'cws-overlay'].includes(element.className));
    }
    if (selector === '[data-cws-surface], .cws-media, .cws-overlay') {
      return elements.filter(element => element.hasAttribute('data-cws-surface')
        || ['cws-media', 'cws-overlay'].includes(element.className));
    }
    if (selector === 'main, aside, nav, [role="dialog"], [role="region"]') {
      return elements.filter(element => element.matches(selector));
    }
    return [];
  },
  getElementById(id) { return findById(root, id); },
  addEventListener(name, callback) { documentListeners.set(name, callback); },
  removeEventListener(name) { documentListeners.delete(name); }
};
globalThis.window = globalThis;
globalThis.location = { href: 'app://codex/' };
let nativeSurfaceValue = '#17191f';
globalThis.getComputedStyle = element => ({
  pointerEvents: element?.pointerEvents ?? 'auto',
  getPropertyValue(name) { return name === '--app-color-background-surface' ? nativeSurfaceValue : ''; }
});
globalThis.MutationObserver = class { constructor(callback) { this.callback = callback; } observe() {} disconnect() {} };
let nextFrame = 1;
const frames = new Map();
globalThis.requestAnimationFrame = callback => {
  const id = nextFrame++;
  frames.set(id, setTimeout(() => { frames.delete(id); callback(); }, 0));
  return id;
};
globalThis.cancelAnimationFrame = id => { clearTimeout(frames.get(id)); frames.delete(id); };
globalThis.URL = {
  createObjectURL() { return `blob:mock-${Math.random()}`; },
  revokeObjectURL() {}
};

let sceneDisposeCount = 0;
const installSceneStubs = (rendererVersion = expectedSceneRevision, hostVersion = expectedSceneHostVersion) => {
  globalThis.__cwsWeSceneLibrary = { version: rendererVersion };
  const createScene = async () => {
    const canvas = document.createElement('canvas');
    canvas.width = 1920;
    canvas.height = 1080;
    return {
      element: canvas,
      paletteSource: canvas,
      mode: 'scene-partial',
      warning: 'particle layers omitted in smoke stub',
      update() {},
      dispose() { sceneDisposeCount++; }
    };
  };
  createScene.version = hostVersion;
  globalThis.__cwsCreateSceneWallpaper = createScene;
};
const bootstrap = (rendererVersion, hostVersion) => {
  installSceneStubs(rendererVersion, hostVersion);
  return Function(`return ${runtime}`)();
};

const assert = (condition, message) => { if (!condition) throw new Error(message); };
const assertThrows = (operation, message) => {
  try { operation(); } catch { return; }
  throw new Error(message);
};
const assertRejects = async (promise, message) => {
  try { await promise; } catch { return; }
  throw new Error(message);
};
const completeResult = (0, eval)(completeRuntime);
assert(completeResult === 'ready', 'complete bundled runtime did not initialize');
assert(typeof window.__cwsCreateSceneWallpaper === 'function', 'complete runtime omitted the scene host');
assert(Function(`return ${cleanupRuntime}`)() === 'cleaned', 'complete runtime boundary cleanup failed');
assert(Function(`return ${cleanupVerification}`)() === true, 'complete runtime boundary left artifacts');

const result = bootstrap();
assert(result === 'ready', 'runtime did not initialize');
let originalHost = document.getElementById('codex-wallpaper-skin-host');
assert(bootstrap() === 'ready', 'healthy runtime was not reusable');
assert(document.getElementById('codex-wallpaper-skin-host') === originalHost, 'healthy runtime was unnecessarily rebuilt');
assert(bootstrap('stale-scene-renderer', 'stale-scene-host') === 'ready', 'stale Scene runtime was not repaired');
assert(document.getElementById('codex-wallpaper-skin-host') !== originalHost, 'stale Scene runtime fingerprints were accepted as healthy');
originalHost = document.getElementById('codex-wallpaper-skin-host');
window.__codexWallpaperSkinSetSettings = () => true;
assert(bootstrap() === 'ready', 'runtime with a replaced helper was not repaired');
const helperRepairedHost = document.getElementById('codex-wallpaper-skin-host');
assert(helperRepairedHost !== originalHost, 'runtime accepted a replaced helper as healthy');
helperRepairedHost.remove();
assert(bootstrap() === 'ready', 'detached runtime was not repaired');
assert(document.getElementById('codex-wallpaper-skin-host') !== helperRepairedHost, 'detached runtime host was reused');

const settings = {
  fit: 'cover', focusX: 50, focusY: 50, opacity: .9, overlay: .12,
  autoPalette: true, paletteStrength: .72, panelOpacity: .72, tintInterfaceText: true,
  blur: 0, brightness: 1, contrast: 1, saturation: 1,
  rate: 1, muted: true, pauseWhenHidden: true,
  sceneFrameRate: 30, sceneResolutionScale: 1
};
window.__codexWallpaperSkinBeginUpload('oversized', 'image/png');
assertThrows(
  () => window.__codexWallpaperSkinPushChunk('oversized', btoa('x'.repeat(64 * 1024 + 1))),
  'oversized runtime upload chunk was accepted');
assert(window.__codexWallpaperSkin.uploads.size === 0, 'rejected upload retained partial chunks');
const token = 'smoke';
window.__codexWallpaperSkinBeginUpload(token, 'image/png');
window.__codexWallpaperSkinPushChunk(token, btoa('mock-image'));
const applied = await window.__codexWallpaperSkinFinishUpload(token, 'image', settings, null);
assert(applied.palette, 'palette was not returned');
assert(applied.palette.textContrast >= 4.5, 'text contrast fell below 4.5:1');
assert(root.classList.contains('cws-active'), 'active class missing after apply');
assert(root.classList.contains('cws-palette'), 'palette class missing after apply');
assert(window.__codexWallpaperSkin.media?.tagName === 'IMG', 'image background was not installed');
assert(shell.getAttribute('data-cws-surface') === 'root', 'outer application shell was not marked as the root surface');
assert(!app.hasAttribute('data-cws-surface'), 'nested full-size surface would stack another root veil');
assert(sidebar.getAttribute('data-cws-surface') === 'panel', 'outer semantic panel was not marked');
assert(!navigation.hasAttribute('data-cws-surface'), 'nested semantic panel would stack another panel veil');
assert(!inertOverlay.hasAttribute('data-cws-surface'), 'pointer-inert overlay was incorrectly tinted');

const captureLease = 'capturelease1234567890';
const initialCaptureImage = window.__codexWallpaperSkin.media;
assert(window.__codexWallpaperSkinBeginCapturedStream(captureLease) === true, 'native capture lease was rejected');
const preCaptureMedia = window.__codexWallpaperSkin.media;
assert(preCaptureMedia?.tagName === 'CANVAS' && !initialCaptureImage.isConnected,
  'native capture did not install its persistent canvas');
const drawCountBeforeFrame = preCaptureMedia.drawCount;
assert(window.__codexWallpaperSkinSetCapturedFrame(captureLease, btoa('mock-jpeg-frame')) === true,
  'native capture frame was rejected');
assert(window.__codexWallpaperSkin.media === preCaptureMedia && preCaptureMedia.isConnected
  && preCaptureMedia.drawCount > drawCountBeforeFrame,
  'decoded native frame was not committed to the persistent canvas');
windowListeners.get('pointermove')?.({ clientX: 300, clientY: 600 });
windowListeners.get('pointerdown')?.({ clientX: 300, clientY: 600, buttons: 1 });
windowListeners.get('wheel')?.({ clientX: 300, clientY: 600, deltaY: -120 });
const capturedPointer = window.__codexWallpaperSkinGetCapturedPointer(captureLease);
assert(capturedPointer && Math.abs(capturedPointer.x - .25) < .001 && Math.abs(capturedPointer.y - .75) < .001,
  'native capture pointer coordinates were not normalized');
assert(capturedPointer.buttons === 1 && capturedPointer.wheel === 120 && capturedPointer.inside === true,
  'native capture button/wheel state was not preserved');
windowListeners.get('pointerleave')?.({ clientX: 300, clientY: 600 });
const departedPointer = window.__codexWallpaperSkinGetCapturedPointer(captureLease);
assert(departedPointer.buttons === 0 && departedPointer.inside === false,
  'native capture pointer leave did not release input state');
assert(window.__codexWallpaperSkinGetCapturedPointer('stalelease123456789') === false,
  'a stale native capture stream could read pointer state');
globalThis.deferMediaDecode = true;
const lastGoodCaptureMedia = window.__codexWallpaperSkin.media;
assert(window.__codexWallpaperSkinSetCapturedFrame(captureLease, btoa('deferred-native-frame')) === true,
  'deferred native frame was rejected');
assert(window.__codexWallpaperSkin.media === lastGoodCaptureMedia,
  'an undecoded native frame replaced the last known-good frame');
const deferredCaptureMedia = window.__codexWallpaperSkin.captureStaging;
const drawCountBeforeDeferredFrame = lastGoodCaptureMedia.drawCount;
globalThis.deferMediaDecode = false;
deferredCaptureMedia.onload();
assert(window.__codexWallpaperSkin.media === lastGoodCaptureMedia && lastGoodCaptureMedia.isConnected
  && lastGoodCaptureMedia.drawCount > drawCountBeforeDeferredFrame,
  'decoded native frame did not update the persistent canvas');
assert(window.__codexWallpaperSkinSetCapturedFrame('stalelease123456789', btoa('stale')) === false,
  'a stale native capture stream could overwrite the active lease');

window.__codexWallpaperSkinSetSettings({ ...settings, autoPalette: false });
assert(!root.classList.contains('cws-palette'), 'palette toggle did not turn off');
assert(root.style.getPropertyValue('--cws-surface-rgb') === '', 'turning palette off retained stale palette variables');
assert(window.__codexWallpaperSkin.media?.tagName === 'CANVAS', 'turning palette off removed the background');

document.hidden = true;
const videoToken = 'smoke-video';
paletteReadFailures = 1;
window.__codexWallpaperSkinBeginUpload(videoToken, 'video/mp4');
window.__codexWallpaperSkinPushChunk(videoToken, btoa('mock-video'));
const videoApplied = await window.__codexWallpaperSkinFinishUpload(videoToken, 'video', settings, null);
assert(window.__codexWallpaperSkinSetCapturedFrame(captureLease, btoa('stale-after-replacement')) === false,
  'replacing media did not revoke the native capture lease');
assert(videoApplied.palette, 'temporary first-frame palette failure was not retried');
assert(window.__codexWallpaperSkin.media?.tagName === 'VIDEO', 'video background was not installed');
assert(window.__codexWallpaperSkin.media.paused === true, 'video kept playing while the document was hidden');
document.hidden = false;
window.__codexWallpaperSkinSetSettings(settings);
assert(window.__codexWallpaperSkin.media.paused === false, 'video did not resume after the document became visible');

const sceneToken = 'smoke-scene';
window.__codexWallpaperSkinBeginUpload(sceneToken, 'application/x-wallpaper-engine-scene');
window.__codexWallpaperSkinPushChunk(sceneToken, btoa('mock-scene-package'));
const sceneApplied = await window.__codexWallpaperSkinFinishUpload(
  sceneToken,
  'scene',
  { ...settings, brightness: 1.2 },
  { project: null, shaders: {} });
assert(sceneApplied.mode === 'scene-partial', 'scene renderer mode was not preserved');
assert(sceneApplied.warning?.includes('particle layers'), 'scene renderer warning was not preserved');
assert(window.__codexWallpaperSkin.media?.tagName === 'CANVAS', 'scene canvas was not installed');
assert(window.__codexWallpaperSkin.media.style.filter.includes('brightness(1.2)'), 'scene quality filters were not applied');

window.__codexWallpaperSkinBeginUpload('scene-replacement', 'image/png');
window.__codexWallpaperSkinPushChunk('scene-replacement', btoa('replacement-image'));
await window.__codexWallpaperSkinFinishUpload('scene-replacement', 'image', settings, null);
assert(sceneDisposeCount === 1, 'replacing a scene did not dispose its renderer');

location.href = 'https://example.invalid/';
assertThrows(() => Function(`return ${cleanupRuntime}`)(), 'cleanup modified a target after navigation away from app://');
assert(globalThis.__codexWallpaperSkin, 'refused cleanup removed runtime state');
location.href = 'app://codex/';
const cleanup = Function(`return ${cleanupRuntime}`)();
assert(cleanup === 'cleaned', 'cleanup did not report success');
assert(!root.classList.contains('cws-active'), 'cleanup left the active class');
assert(!globalThis.__codexWallpaperSkin, 'cleanup left runtime state');
assert(Function(`return ${cleanupVerification}`)() === true, 'post-cleanup verification did not confirm the native Codex surface');

assert(bootstrap() === 'ready', 'runtime did not restart for pending-decode cleanup test');
globalThis.deferMediaDecode = true;
window.__codexWallpaperSkinBeginUpload('pending', 'image/png');
window.__codexWallpaperSkinPushChunk('pending', btoa('pending-image'));
const pendingApply = window.__codexWallpaperSkinFinishUpload('pending', 'image', settings, null);
Function(`return ${cleanupRuntime}`)();
await assertRejects(pendingApply, 'cleanup did not cancel a pending media decode');
globalThis.deferMediaDecode = false;
assert(Function(`return ${cleanupVerification}`)() === true, 'pending-decode cleanup left runtime artifacts');

const orphanMedia = document.createElement('video');
orphanMedia.className = 'cws-media';
body.appendChild(orphanMedia);
const orphanOverlay = document.createElement('div');
orphanOverlay.className = 'cws-overlay';
body.appendChild(orphanOverlay);
assert(bootstrap() === 'ready', 'runtime did not recover from orphan nodes');
assert(!orphanMedia.isConnected && !orphanOverlay.isConnected, 'bootstrap left orphan media nodes connected');
Function(`return ${cleanupRuntime}`)();
assert(Function(`return ${cleanupVerification}`)() === true, 'final cleanup verification failed');

nativeSurfaceValue = '';
let mismatchRefused = false;
try { bootstrap(); } catch { mismatchRefused = true; }
assert(mismatchRefused, 'runtime did not fail closed when the Codex surface marker was missing');
assert(Function(`return ${cleanupVerification}`)() === true, 'surface-mismatch refusal left runtime artifacts');

process.stdout.write(`PASS runtime image/video/scene/palette/quality/hidden-pause/integrity/pending-cleanup/orphan-repair/restore/mismatch-refusal (${applied.palette.surface}, ${applied.palette.accent}, ${applied.palette.textContrast}:1)\n`);
