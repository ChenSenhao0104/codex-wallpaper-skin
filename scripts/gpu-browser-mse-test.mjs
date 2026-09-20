// v0.4 acceptance evidence: prove that real Chromium Media Source Extensions
// accepts and decodes the fragmented MP4 that the Media Foundation hardware
// encoder produces, using the same fragment delivery shape the production
// transport uses (bounded batches of base64 Media Source units).
//
// Local test tooling only. It reads a synthetic fixture produced by
// `--gpu-encoder-smoke-test --output` and never touches Workshop media.
//
// This check needs a normal desktop shell: Chromium requires named pipes for its
// inter-process channels and for its crash handler, so it cannot start inside a
// restricted sandbox. It is therefore an optional manual verification and is not
// part of the packaging preflight. When it cannot run, the equivalent automated
// evidence is `--gpu-encoder-smoke-test` (container plus AVC elementary-stream
// validation) and scripts/runtime-smoke-test.mjs (renderer contract).
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';

const fixture = process.argv[2];
if (!fixture || !fs.existsSync(fixture)) {
  console.error('usage: node scripts/gpu-browser-mse-test.mjs <fixture.mp4>');
  process.exit(64);
}
const stream = fs.readFileSync(fixture);

// Split the fixture into top-level MP4 boxes exactly like the native transport
// does, so the browser receives the same initialisation segment plus
// moof+mdat fragment sequence.
const boxes = [];
let offset = 0;
while (offset + 8 <= stream.length) {
  let size = stream.readUInt32BE(offset);
  let header = 8;
  if (size === 1) { size = Number(stream.readBigUInt64BE(offset + 8)); header = 16; }
  if (size < header || offset + size > stream.length) break;
  boxes.push({ type: stream.toString('ascii', offset + 4, offset + 8), bytes: stream.subarray(offset, offset + size) });
  offset += size;
}
const initBoxes = [];
const fragments = [];
let pending = [];
for (const box of boxes) {
  if (box.type === 'moov') { initBoxes.push(box.bytes); continue; }
  if (box.type === 'moof') { pending = [box.bytes]; continue; }
  if (box.type === 'mdat') { pending.push(box.bytes); fragments.push(Buffer.concat(pending)); pending = []; continue; }
  (fragments.length ? pending : initBoxes).push(box.bytes);
}
if (!initBoxes.length || fragments.length < 2) {
  console.error(`fixture is not a fragmented stream: ${initBoxes.length} init boxes, ${fragments.length} fragments`);
  process.exit(1);
}
const init = Buffer.concat(initBoxes);
const profile = init[init.length - 1];
const avcIndex = init.indexOf(Buffer.from('avcC'));
if (avcIndex < 0 || avcIndex + 4 >= init.length) {
  console.error('fixture has no avcC decoder configuration');
  process.exit(1);
}
const codec = `avc1.${init[avcIndex + 1].toString(16).padStart(2, '0')}`
  + `${init[avcIndex + 2].toString(16).padStart(2, '0')}`
  + `${init[avcIndex + 3].toString(16).padStart(2, '0')}`;

const browser = process.env.CWS_BROWSER
  ?? (fs.existsSync('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe')
    ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'
    : 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe');
const userDataDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'cws-mse-'));
const port = 9515 + (process.pid % 300);
const child = spawn(browser, [
  '--headless=new',
  '--disable-gpu',
  '--no-first-run',
  '--no-default-browser-check',
  '--autoplay-policy=no-user-gesture-required',
  `--user-data-dir=${userDataDirectory}`,
  `--remote-debugging-port=${port}`,
  'about:blank'
], { stdio: 'ignore' });

const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const targets = async () => {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/json/list`);
      const list = await response.json();
      const page = list.find(target => target.type === 'page');
      if (page) return page;
    } catch (_) { /* browser is still starting */ }
    await sleep(250);
  }
  throw new Error('the browser never exposed a CDP page');
};

let socket;
const pendingCalls = new Map();
let nextId = 0;
try {
  const page = await targets();
  socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    const entry = pendingCalls.get(message.id);
    if (entry) { pendingCalls.delete(message.id); entry(message); }
  });
  const command = (method, params = {}) => new Promise(resolve => {
    const id = ++nextId;
    pendingCalls.set(id, resolve);
    socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression, awaitPromise = true) => {
    const message = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise });
    const details = message.result?.exceptionDetails;
    if (details) throw new Error(details.exception?.description ?? details.text);
    return message.result?.result?.value;
  };

  const setup = await evaluate(`(() => {
    const support = MediaSource.isTypeSupported('video/mp4; codecs="${codec}"');
    const video = document.createElement('video');
    video.muted = true;
    video.playsInline = true;
    document.body.appendChild(video);
    window.__cwsTest = { video, presented: 0, errors: [], support };
    video.addEventListener('error', () => window.__cwsTest.errors.push('media error ' + (video.error && video.error.code)));
    if (typeof video.requestVideoFrameCallback === 'function') {
      const tick = () => { window.__cwsTest.presented++; video.requestVideoFrameCallback(tick); };
      video.requestVideoFrameCallback(tick);
    }
    return support;
  })()`, false);
  if (!setup) throw new Error(`Chromium reports no support for ${codec} in Media Source Extensions`);

  const started = await evaluate(`(async () => {
    const test = window.__cwsTest;
    const mediaSource = new MediaSource();
    test.video.src = URL.createObjectURL(mediaSource);
    await new Promise(resolve => mediaSource.addEventListener('sourceopen', resolve, { once: true }));
    const buffer = mediaSource.addSourceBuffer('video/mp4; codecs="${codec}"');
    buffer.mode = 'segments';
    test.buffer = buffer;
    test.mediaSource = mediaSource;
    test.append = data => new Promise((resolve, reject) => {
      const finish = () => { buffer.removeEventListener('updateend', finish); buffer.removeEventListener('error', finish); resolve(); };
      buffer.addEventListener('updateend', finish, { once: true });
      buffer.addEventListener('error', finish, { once: true });
      try { buffer.appendBuffer(data); } catch (error) { reject(new Error(String(error && error.message || error))); }
    });
    return true;
  })()`);
  if (!started) throw new Error('Media Source could not be initialised');

  // The initialisation segment first, then bounded batches of fragments, which is
  // exactly the ordering and batching the production transport uses.
  await evaluate(`window.__cwsTest.append(Uint8Array.from(atob(${JSON.stringify(init.toString('base64'))}), c => c.charCodeAt(0)))`);
  const batchSize = 4;
  let delivered = 0;
  for (let index = 0; index < fragments.length; index += batchSize) {
    const group = fragments.slice(index, index + batchSize);
    const payload = group.map(fragment => atob(fragment.toString('base64')));
    await evaluate(`(async () => {
      for (const chunk of ${JSON.stringify(payload)}) {
        await window.__cwsTest.append(Uint8Array.from(chunk, c => c.charCodeAt(0)));
      }
      return true;
    })()`);
    delivered += group.length;
  }
  await evaluate(`window.__cwsTest.video.play().catch(() => {})`, false);
  await sleep(2500);
  const result = await evaluate(`(() => {
    const test = window.__cwsTest;
    const video = test.video;
    const buffered = video.buffered.length ? video.buffered.end(video.buffered.length - 1) : 0;
    return {
      delivered: ${delivered},
      presentedFrames: test.presented,
      currentTime: Math.round(video.currentTime * 1000) / 1000,
      bufferedSeconds: Math.round(buffered * 1000) / 1000,
      videoWidth: video.videoWidth,
      videoHeight: video.videoHeight,
      readyState: video.readyState,
      errors: test.errors,
      sourceBufferBytes: test.buffer.buffered.length ? Math.round(test.buffer.buffered.end(0) * 1000) / 1000 : 0
    };
  })()`, false);

  const passed = result.presentedFrames > 0
    && result.videoWidth === 1280
    && result.videoHeight === 720
    && result.errors.length === 0;
  console.log(JSON.stringify({ codec, fragments: fragments.length, ...result }, null, 2));
  console.log(passed
    ? `PASS Chromium Media Source decode (${codec}, ${result.presentedFrames} presented frames at ${result.videoWidth}x${result.videoHeight}, no media errors)`
    : `FAIL Chromium Media Source decode (${JSON.stringify(result)})`);
  process.exitCode = passed ? 0 : 1;
} catch (error) {
  console.error('FAIL Chromium Media Source decode: ' + (error && error.message || error));
  process.exitCode = 1;
} finally {
  try { socket?.close(); } catch (_) { /* already closed */ }
  try { child.kill(); } catch (_) { /* already gone */ }
  await sleep(300);
  try { fs.rmSync(userDataDirectory, { recursive: true, force: true }); } catch (_) { /* best effort */ }
}
