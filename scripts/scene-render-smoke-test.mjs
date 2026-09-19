import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, extname, join, relative, resolve, sep } from 'node:path';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { pathToFileURL } from 'node:url';

const projectRoot = resolve(dirname(new URL(import.meta.url).pathname.replace(/^\/(?:([A-Za-z]):)/, '$1:')), '..');
const companionRoot = join(projectRoot, 'companion');
const libraryFiles = [
  'ThirdParty/we-scene/src/pkg/container.js',
  'ThirdParty/we-scene/src/pkg/texture.js',
  'ThirdParty/we-scene/src/scene/parse.js',
  'ThirdParty/we-scene/src/scene/effects-parse.js',
  'ThirdParty/we-scene/src/render/math.js',
  'ThirdParty/we-scene/src/render/noise.js',
  'ThirdParty/we-scene/src/render/hlsl2glsl.js',
  'ThirdParty/we-scene/src/render/effects.js',
  'ThirdParty/we-scene/src/render/renderer.js'
];
const revision = 'we-scene@6b503a36b952f91dbab5e6f378f632f87baf05cc';

const fail = message => {
  process.stderr.write(`FAIL ${message}\n`);
  process.exitCode = 1;
};

const stripModuleSyntax = source => source.split(/\r?\n/).flatMap(line => {
  if (/^\s*import\s+.*\bfrom\s+['"][^'"]+['"]\s*;?\s*$/.test(line)) return [];
  if (/^\s*export\s*\{.*\}\s*;?\s*$/.test(line)) return [];
  return [line.replace(/^(\s*)export\s+(?=(?:async\s+)?(?:function|const|let|class)\b)/, '$1')];
}).join('\n');

const makeRuntime = () => {
  const sources = libraryFiles.map(file => stripModuleSyntax(readFileSync(join(companionRoot, file), 'utf8'))).join('\n');
  const host = readFileSync(join(companionRoot, 'Runtime/scene-host.js'), 'utf8');
  return `(() => {\n${sources}\nwindow.__cwsWeSceneLibrary = Object.freeze({ version: ${JSON.stringify(revision)}, parsePkg, getEntry, parseTex, decodeMip0, decodeMips, FIF, parseScene, resolveMaterial, resolveEffectChain, BUILTIN_MODELS, BUILTIN_MATERIALS, createRenderer, makeTexture, makeTextureMip, generateNoiseTexture });\n})();\n${host}`;
};

const edgeCandidates = [
  join(process.env['ProgramFiles(x86)'] || '', 'Microsoft/Edge/Application/msedge.exe'),
  join(process.env.ProgramFiles || '', 'Microsoft/Edge/Application/msedge.exe'),
  join(process.env.LOCALAPPDATA || '', 'Microsoft/Edge/Application/msedge.exe')
];
const edge = edgeCandidates.find(candidate => candidate && existsSync(candidate));
if (!edge) {
  fail('Microsoft Edge was not found.');
  process.exit();
}

const explicitProjects = process.argv.slice(2).filter(argument => !argument.startsWith('--')).map(argument => resolve(argument));
const workshopRoots = [
  'C:/Program Files (x86)/Steam/steamapps/workshop/content/431960',
  'C:/Program Files/Steam/steamapps/workshop/content/431960',
  'D:/steam/steamapps/workshop/content/431960',
  'E:/steam/steamapps/workshop/content/431960'
].map(candidate => resolve(candidate));
const projectDirectories = explicitProjects.length ? explicitProjects : workshopRoots
  .filter(existsSync)
  .flatMap(root => readdirSync(root, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => join(root, entry.name)))
  .filter(directory => existsSync(join(directory, 'project.json')) && existsSync(join(directory, 'scene.pkg')))
  .filter(directory => {
    try { return JSON.parse(readFileSync(join(directory, 'project.json'), 'utf8')).type === 'scene'; }
    catch { return false; }
  });

if (projectDirectories.length === 0) {
  fail('No installed Wallpaper Engine Scene projects were found. Pass project directories as arguments.');
  process.exit();
}

const findShaderRoot = projectDirectory => {
  let current = resolve(projectDirectory);
  for (let depth = 0; depth < 10; depth++) {
    if (current.toLowerCase().endsWith(`${sep}steamapps`)) {
      const candidate = join(current, 'common/wallpaper_engine/assets/shaders');
      return existsSync(candidate) ? candidate : null;
    }
    const parent = dirname(current);
    if (parent === current) break;
    current = parent;
  }
  return null;
};

const loadShaders = root => {
  if (!root) return {};
  const shaders = {};
  const pending = [[root, 0]];
  let bytes = 0;
  while (pending.length) {
    const [directory, depth] = pending.pop();
    if (depth > 8) continue;
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        pending.push([path, depth + 1]);
        continue;
      }
      if (!entry.isFile() || !['.frag', '.vert', '.h'].includes(extname(entry.name).toLowerCase())) continue;
      const size = statSync(path).size;
      if (size <= 0 || size > 128 * 1024) continue;
      bytes += size;
      if (Object.keys(shaders).length >= 512 || bytes > 2 * 1024 * 1024) throw new Error('Shader safety budget exceeded.');
      shaders[relative(root, path).replaceAll('\\', '/').toLowerCase()] = readFileSync(path, 'utf8');
    }
  }
  return shaders;
};

const htmlSafeJson = value => JSON.stringify(value).replaceAll('<', '\\u003c');
const decodeHtml = value => value
  .replaceAll('&quot;', '"')
  .replaceAll('&#39;', "'")
  .replaceAll('&lt;', '<')
  .replaceAll('&gt;', '>')
  .replaceAll('&amp;', '&');
const delay = milliseconds => new Promise(resolveDelay => setTimeout(resolveDelay, milliseconds));
const browserWindowSize = /^\d{3,5},\d{3,5}$/.test(process.env.CWS_SCENE_WINDOW_SIZE || '')
  ? process.env.CWS_SCENE_WINDOW_SIZE
  : '1280,720';
const reserveLoopbackPort = () => new Promise((resolvePort, rejectPort) => {
  const server = createServer();
  server.once('error', rejectPort);
  server.listen(0, '127.0.0.1', () => {
    const address = server.address();
    const port = typeof address === 'object' && address ? address.port : 0;
    server.close(error => error ? rejectPort(error) : resolvePort(port));
  });
});

const runBrowserPage = async (page, profile, screenshotPath = null) => {
  const port = await reserveLoopbackPort();
  const child = spawn(edge, [
    '--headless=new', `--remote-debugging-port=${port}`, `--window-size=${browserWindowSize}`,
    '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--disable-background-networking',
    '--disable-component-update', '--disable-sync', '--no-first-run', '--no-default-browser-check',
    `--user-data-dir=${profile}`, pathToFileURL(page).href
  ], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', chunk => {
    if (stderr.length < 8192) stderr += chunk.toString();
  });
  const exited = new Promise(resolveExit => child.once('exit', resolveExit));
  let socket;
  try {
    let target;
    const discoveryDeadline = Date.now() + 15000;
    while (!target && Date.now() < discoveryDeadline) {
      try {
        const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        target = targets.find(item => item.type === 'page' && item.webSocketDebuggerUrl);
      } catch {}
      if (!target) await delay(100);
    }
    if (!target) throw new Error(`Edge DevTools did not become ready. ${stderr.trim()}`);
    socket = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolveSocket, rejectSocket) => {
      socket.addEventListener('open', resolveSocket, { once: true });
      socket.addEventListener('error', () => rejectSocket(new Error('Edge DevTools WebSocket failed.')), { once: true });
    });
    let nextId = 1;
    const pending = new Map();
    socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (!message.id || !pending.has(message.id)) return;
      const callback = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) callback.reject(new Error(message.error.message));
      else callback.resolve(message.result);
    });
    const send = (method, params = {}) => new Promise((resolveMessage, rejectMessage) => {
      const id = nextId++;
      pending.set(id, { resolve: resolveMessage, reject: rejectMessage });
      socket.send(JSON.stringify({ id, method, params }));
    });
    await send('Runtime.enable');
    const completionDeadline = Date.now() + 45000;
    while (Date.now() < completionDeadline) {
      const response = await send('Runtime.evaluate', {
        expression: "document.getElementById('result')?.textContent || ''",
        returnByValue: true
      });
      const payload = response.result?.value || '';
      if (payload !== 'PENDING') {
        if (screenshotPath) {
          const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
          writeFileSync(screenshotPath, Buffer.from(shot.data, 'base64'));
        }
        return payload;
      }
      await delay(200);
    }
    throw new Error('Scene rendering exceeded the 45-second test timeout.');
  } finally {
    try { socket?.close(); } catch {}
    if (child.exitCode === null) child.kill();
    await Promise.race([exited, delay(3000)]);
    if (child.exitCode === null) {
      child.kill('SIGKILL');
      await Promise.race([exited, delay(5000)]);
    }
  }
};
const runtime = makeRuntime().replaceAll('</script', '<\\/script');
const temporaryRoot = mkdtempSync(join(tmpdir(), 'cws-scene-smoke-'));
const screenshotRoot = process.env.CWS_SCENE_SCREENSHOT_DIR ? resolve(process.env.CWS_SCENE_SCREENSHOT_DIR) : null;
const disabledEffects = (process.env.CWS_SCENE_DISABLED_EFFECTS || '').split(';').map(value => value.trim()).filter(Boolean);
if (screenshotRoot) mkdirSync(screenshotRoot, { recursive: true });
let passed = 0;
let failed = 0;

try {
  for (const directory of projectDirectories) {
    const project = JSON.parse(readFileSync(join(directory, 'project.json'), 'utf8'));
    const packageBytes = readFileSync(join(directory, 'scene.pkg'));
    if (packageBytes.length <= 0 || packageBytes.length > 128 * 1024 * 1024) {
      process.stderr.write(`FAIL ${project.title || directory}: scene.pkg exceeds the test limit.\n`);
      failed++;
      continue;
    }
    const shaders = loadShaders(findShaderRoot(directory));
    const packageBase64 = packageBytes.toString('base64');
    const html = `<!doctype html><html style="--app-color-background-surface:#101317"><head><meta charset="utf-8"><style>html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#101317}canvas,img{width:100%;height:100%;object-fit:cover}pre{position:fixed;top:0;left:0;margin:0;color:white;z-index:2;font-size:10px;max-width:100%;white-space:nowrap;overflow:hidden}</style></head><body><main>Codex scene smoke surface</main><pre id="result">PENDING</pre><script type="module">${runtime}\ntry{const raw=atob(${JSON.stringify(packageBase64)});const bytes=new Uint8Array(raw.length);for(let i=0;i<raw.length;i++)bytes[i]=raw.charCodeAt(i);const controller=await window.__cwsCreateSceneWallpaper(bytes,{project:${htmlSafeJson(project)},shaders:${htmlSafeJson(shaders)},disabledEffects:${htmlSafeJson(disabledEffects)},settings:{sceneFrameRate:30,sceneResolutionScale:1,rate:1,pauseWhenHidden:true}});document.body.appendChild(controller.element);const first=controller.element.tagName==='CANVAS'?controller.element.toDataURL('image/png'):'';const startX=innerWidth*.24,startY=innerHeight*.62,endX=innerWidth*.70,endY=innerHeight*.78;window.dispatchEvent(new PointerEvent('pointerdown',{clientX:startX,clientY:startY}));for(let step=0;step<18;step++){const t=step/17;window.dispatchEvent(new PointerEvent('pointermove',{clientX:startX+(endX-startX)*t,clientY:startY+(endY-startY)*t}));await new Promise(resolve=>setTimeout(resolve,35));}window.dispatchEvent(new PointerEvent('pointerup',{clientX:endX,clientY:endY}));await new Promise(resolve=>setTimeout(resolve,180));const second=controller.element.tagName==='CANVAS'?controller.element.toDataURL('image/png'):'';const answer={ok:true,mode:controller.mode,tag:controller.element.tagName,width:controller.element.naturalWidth||controller.element.videoWidth||controller.element.width||0,height:controller.element.naturalHeight||controller.element.videoHeight||controller.element.height||0,animated:!!first&&first!==second,warning:controller.warning||'',diagnostics:controller.diagnostics?controller.diagnostics():{}};window.__sceneSmokeController=controller;document.getElementById('result').textContent='CWS_RESULT '+JSON.stringify(answer)}catch(error){document.getElementById('result').textContent='CWS_ERROR '+String(error&&error.stack||error)}</script></body></html>`;
    const page = join(temporaryRoot, `${projectDirectories.indexOf(directory)}.html`);
    const profile = join(temporaryRoot, `profile-${projectDirectories.indexOf(directory)}`);
    writeFileSync(page, html, 'utf8');
    let payload = '';
    try {
      const screenshotPath = screenshotRoot ? join(screenshotRoot, `${projectDirectories.indexOf(directory)}-${project.title || 'scene'}.png`.replace(/[<>:"/\\|?*]/g, '_')) : null;
      payload = decodeHtml(await runBrowserPage(page, profile, screenshotPath));
    } catch (error) {
      payload = `CWS_ERROR ${error && error.message || error}`;
    }
    if (!payload.startsWith('CWS_RESULT ')) {
      const reason = payload.startsWith('CWS_ERROR ') ? payload.slice('CWS_ERROR '.length) : payload;
      process.stderr.write(`FAIL ${project.title || directory}: ${reason || 'no browser result'}\n`);
      failed++;
      continue;
    }
    const result = JSON.parse(payload.slice('CWS_RESULT '.length));
    if (!result.ok || !['CANVAS', 'IMG'].includes(result.tag) || result.width <= 0 || result.height <= 0) {
      process.stderr.write(`FAIL ${project.title || directory}: invalid renderer result ${JSON.stringify(result)}\n`);
      failed++;
      continue;
    }
    if (result.warning?.includes('safe interactive renderer')
        && (!result.animated || !(result.diagnostics?.pointerRippleLayers > 0)
          || !(result.diagnostics?.pointerEventCount > 0) || !(result.diagnostics?.pointerRipplePassCount > 0))) {
      process.stderr.write(`FAIL ${project.title || directory}: interactive water fallback was not exercised.\n`);
      failed++;
      continue;
    }
    process.stdout.write(`PASS ${project.title || directory}: ${result.mode}, ${result.tag} ${result.width}x${result.height}, animated=${result.animated}, diagnostics=${JSON.stringify(result.diagnostics || {})}${result.warning ? `, ${result.warning}` : ''}\n`);
    passed++;
  }
} finally {
  rmSync(temporaryRoot, { recursive: true, force: true, maxRetries: 8, retryDelay: 250 });
}

process.stdout.write(`Scene render smoke: ${passed} passed, ${failed} failed.\n`);
if (failed) process.exitCode = 1;
