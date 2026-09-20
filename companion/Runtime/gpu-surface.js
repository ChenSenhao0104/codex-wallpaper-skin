(() => {
  'use strict'
  const VERSION = 'cws-gpu-surface-1'
  if (window.__cwsCreateGpuSurface?.version === VERSION) return

  const LIMITS = Object.freeze({
    dimension: 4096,
    pixels: 8294400,
    fragmentBase64: 2 * 1024 * 1024,
    pendingBytes: 32 * 1024 * 1024,
    codecLength: 128,
    codecs: 32,
    sourceOpenTimeout: 12000,
    watchdogMs: 250,
    stallMs: 3000
  })
  const DEFAULT_CODEC = 'video/mp4; codecs="avc1.640028"'
  const FRAME_RATES = Object.freeze([30, 60])
  const FITS = Object.freeze(['cover', 'contain', 'fill', 'none', 'scale-down'])
  const STREAM_ID_PATTERN = /^[A-Za-z0-9_-]{16,64}$/
  const BASE64_PATTERN = /^[A-Za-z0-9+/]*={0,2}$/
  const BASE64_TABLE = (() => {
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'
    const table = new Int16Array(128).fill(-1)
    for (let index = 0; index < alphabet.length; index++) table[alphabet.charCodeAt(index)] = index
    return table
  })()

  const clampNumber = (value, minimum, maximum, fallback) => {
    const number = Number(value)
    if (!Number.isFinite(number)) return fallback
    return Math.min(maximum, Math.max(minimum, number))
  }
  const boundedMessage = value => String(value || '').replace(/\s+/g, ' ').trim().slice(0, 420)
  const parseCodecList = value => {
    if (!Array.isArray(value)) return []
    const codecs = []
    for (const entry of value) {
      if (typeof entry !== 'string' || entry.length < 1 || entry.length > LIMITS.codecLength) continue
      if (!codecs.includes(entry)) codecs.push(entry)
      if (codecs.length >= LIMITS.codecs) break
    }
    return codecs
  }
  const normalizeOptions = opts => {
    if (!opts || typeof opts !== 'object') throw new Error('GPU surface options must be an object.')
    const streamId = opts.streamId
    if (typeof streamId !== 'string' || !STREAM_ID_PATTERN.test(streamId)) {
      throw new Error('GPU surface streamId must match [A-Za-z0-9_-]{16,64}.')
    }
    const width = Number(opts.width)
    const height = Number(opts.height)
    if (!Number.isInteger(width) || !Number.isInteger(height) || width < 64 || height < 64
      || width > LIMITS.dimension || height > LIMITS.dimension) {
      throw new Error('GPU surface dimensions must be integers between 64 and 4096.')
    }
    if (width * height > LIMITS.pixels) {
      throw new Error('GPU surface area exceeds the ' + LIMITS.pixels + ' pixel budget.')
    }
    const frameRate = Number(opts.frameRate)
    if (!FRAME_RATES.includes(frameRate)) {
      throw new Error('GPU surface frameRate must be 30 or 60.')
    }
    let codecCandidates = parseCodecList(opts.codecCandidates ?? opts.codecs)
    if (!codecCandidates.length) codecCandidates = [DEFAULT_CODEC]
    let codec = codecCandidates[0]
    if (opts.codec !== undefined) {
      if (typeof opts.codec !== 'string' || opts.codec.length < 1 || opts.codec.length > LIMITS.codecLength) {
        throw new Error('GPU surface codec must be one of the supplied candidate codec strings.')
      }
      if (!codecCandidates.includes(opts.codec)) {
        throw new Error('GPU surface codec is not one of the supplied candidate codec strings.')
      }
      codec = opts.codec
    }
    return {
      streamId,
      codec,
      codecCandidates,
      width,
      height,
      frameRate,
      liveLatencySeconds: clampNumber(opts.liveLatencySeconds, .05, 2, .35),
      maxBufferSeconds: clampNumber(opts.maxBufferSeconds, .5, 10, 2),
      maxPendingFragments: Math.round(clampNumber(opts.maxPendingFragments, 4, 128, 24))
    }
  }
  // Strict base64: the CDP transport always pads, so anything else is malformed and is
  // rejected instead of being decoded into a partial fragment that would poison the stream.
  const decodeBase64 = text => {
    if (typeof text !== 'string' || text.length < 4 || text.length > LIMITS.fragmentBase64
      || text.length % 4 !== 0 || !BASE64_PATTERN.test(text)) return null
    const padding = text.endsWith('==') ? 2 : (text.endsWith('=') ? 1 : 0)
    if ((text.length / 4) * 3 - padding <= 0) return null
    const bytes = new Uint8Array((text.length / 4) * 3 - padding)
    let offset = 0
    for (let index = 0; index < text.length; index += 4) {
      const a = BASE64_TABLE[text.charCodeAt(index)]
      const b = BASE64_TABLE[text.charCodeAt(index + 1)]
      const c = text.charCodeAt(index + 2) === 61 ? 0 : BASE64_TABLE[text.charCodeAt(index + 2)]
      const d = text.charCodeAt(index + 3) === 61 ? 0 : BASE64_TABLE[text.charCodeAt(index + 3)]
      if (a < 0 || b < 0 || c < 0 || d < 0) return null
      if (offset < bytes.length) bytes[offset++] = (a << 2) | (b >> 4)
      if (offset < bytes.length) bytes[offset++] = ((b & 15) << 4) | (c >> 2)
      if (offset < bytes.length) bytes[offset++] = ((c & 3) << 6) | d
    }
    return bytes
  }
  const bufferedEnd = element => {
    try {
      if (!element.buffered || element.buffered.length < 1) return 0
      return element.buffered.end(element.buffered.length - 1)
    } catch (_) {
      return 0
    }
  }
  const readDecoderMode = async (mediaCapabilities, options, bitrate) => {
    if (!mediaCapabilities || typeof mediaCapabilities.decodingInfo !== 'function') return 'unknown'
    try {
      const info = await mediaCapabilities.decodingInfo({
        type: 'media-source',
        video: {
          contentType: options.codec,
          width: options.width,
          height: options.height,
          bitrate,
          framerate: options.frameRate
        }
      })
      if (!info || !info.supported) return 'unknown'
      return info.powerEfficient ? 'hardware' : 'software'
    } catch (_) {
      return 'unknown'
    }
  }
  const applyFitStyle = (element, settings) => {
    const fit = typeof settings.fit === 'string' ? settings.fit.toLowerCase() : 'cover'
    const resolved = FITS.includes(fit) ? fit : 'cover'
    let focusX = .5
    let focusY = .5
    if (settings.focusX !== undefined) focusX = clampNumber(settings.focusX, 0, 1, .5)
    if (settings.focusY !== undefined) focusY = clampNumber(settings.focusY, 0, 1, .5)
    element.style.objectFit = resolved
    element.style.objectPosition = '50% 50%'
    // Focusing the surface is a transform origin rather than a re-encode of the frame, so
    // the transform can never introduce a new decode or a second video element.
    element.style.transformOrigin = (focusX * 100).toFixed(2) + '% ' + (focusY * 100).toFixed(2) + '%'
  }

  const createGpuSurface = async opts => {
    const options = normalizeOptions(opts)
    if (typeof MediaSource !== 'function') {
      throw new Error('Media Source Extensions are unavailable on this page.')
    }
    if (typeof MediaSource.isTypeSupported !== 'function'
      || !options.codecCandidates.some(candidate => MediaSource.isTypeSupported(candidate))) {
      throw new Error('No candidate MSE codec is supported by this Chromium build.')
    }
    if (!MediaSource.isTypeSupported(options.codec)) {
      throw new Error('The requested MSE codec is not supported by this Chromium build: ' + options.codec)
    }

    const element = document.createElement('video')
    element.muted = true
    element.autoplay = true
    element.playsInline = true
    element.setAttribute('playsinline', '')
    element.setAttribute('aria-hidden', 'true')
    element.setAttribute('data-cws-surface', 'gpu')
    element.style.position = 'absolute'
    element.style.inset = '0'
    element.style.width = '100%'
    element.style.height = '100%'
    element.style.pointerEvents = 'none'
    element.style.background = 'transparent'
    try {
      applyFitStyle(element, {})
    } catch (_) {}

    const mediaSource = new MediaSource()
    const objectUrl = URL.createObjectURL(mediaSource)
    // The one and only src this surface ever receives.  load() is deliberately not called
    // here: setting src already queues the load and a synchronous load() would abort it.
    element.src = objectUrl

    const listeners = []
    const on = (target, type, handler) => {
      if (!target || typeof target.addEventListener !== 'function') return
      target.addEventListener(type, handler)
      listeners.push({ target, type, handler })
    }

    const fragmentQueue = []
    const inflight = new Set()
    let sequence = -1
    let sourceBuffer = null
    let ready = false
    let state = 'pending'
    let reason = ''
    let disposed = false
    let fatalFailure = null
    let disposedResolve = null
    let watchdog = null
    let sourceOpenTimer = 0
    let quotaRetryTimer = 0
    let quotaRetry = null
    let rvfcId = 0
    let rvfcActive = false
    let streamEnded = false
    let controller = null
    let appendedFragments = 0
    let appendedBytes = 0
    let pendingBytes = 0
    let presentedFrames = 0
    let lastPresentedAt = 0
    let lastFrameClock = 0
    let lastFrameAt = 0
    let lastArrivalAt = 0
    let pending = 0
    let lastBufferedSeconds = 0
    let decoderMode = 'unknown'
    const settings = { pauseWhenHidden: true }

    const snapshot = () => {
      const now = Date.now()
      lastBufferedSeconds = bufferedEnd(element)
      const liveEdge = lastBufferedSeconds
      let position = 0
      try {
        position = Number(element.currentTime) || 0
      } catch (_) {}
      const playing = !element.paused && !element.ended
      let lag = 0
      if (liveEdge > 0 && playing) lag = Math.max(0, liveEdge - options.liveLatencySeconds - position)
      // "Frames stopped advancing while fragments keep arriving" is the stall signal; the
      // second condition keeps a deliberately idle surface from looking degraded.
      const stalledFrames = lastFrameAt > 0 && now - lastFrameAt > LIMITS.stallMs
        && now - lastArrivalAt < LIMITS.stallMs
      const failed = !!element.error
      return {
        streamId: options.streamId,
        mode: presentedFrames > 0 ? 'gpu-video-live' : 'gpu-video-pending',
        state: failed ? 'failed' : state,
        decoderMode,
        appendedFragments,
        presentedFrames,
        pendingFragments: pending,
        bufferedSeconds: lastBufferedSeconds,
        appendedBytes,
        lastPresentedAt,
        lastSequence: sequence,
        degraded: lag > 2 * options.liveLatencySeconds || stalledFrames,
        reason: failed && !reason ? 'The decoder reported a fatal media error.' : reason
      }
    }
    const report = (message, nextState) => {
      reason = boundedMessage(message)
      if (nextState) state = nextState
    }

    let resolveDisposed = () => {}
    disposedResolve = new Promise(resolve => { resolveDisposed = resolve })
    const failPending = result => {
      inflight.forEach(waiter => waiter.finish(result))
      inflight.clear()
      resolveDisposed()
    }
    const abortFragments = result => {
      failPending(result)
      while (fragmentQueue.length) fragmentQueue.shift().finish(result)
      pending = 0
      pendingBytes = 0
    }
    // Presentation is only ever confirmed by a composited frame callback or by currentTime
    // genuinely advancing; readyState alone is not proof that a frame reached the surface.
    const confirmed = baseline => presentedFrames > baseline
      || (lastPresentedAt > 0 && Date.now() - lastPresentedAt < 2000)
    const markPresented = () => {
      presentedFrames += 1
      lastPresentedAt = Date.now()
      lastFrameAt = lastPresentedAt
      if (state === 'pending' || state === 'stalled') state = 'live'
    }
    // Every frame request re-arms itself so the counter keeps tracking real presentations;
    // it stops entirely once the surface is disposed.
    const armFrameCallback = () => {
      if (disposed || rvfcActive || typeof element.requestVideoFrameCallback !== 'function') return
      rvfcActive = true
      try {
        rvfcId = element.requestVideoFrameCallback(() => {
          rvfcActive = false
          if (disposed) return
          markPresented()
          armFrameCallback()
        })
      } catch (_) {
        rvfcActive = false
      }
    }
    const ensurePlayback = () => {
      if (disposed || streamEnded || element.error) return
      if (settings.pauseWhenHidden && document.hidden) return
      armFrameCallback()
      if (!element.paused) return
      try {
        const play = element.play()
        if (play && typeof play.catch === 'function') play.catch(() => {})
      } catch (_) {}
    }
    const trimOldestRange = () => {
      const buffer = sourceBuffer
      // Must run after an append error, when no updateend will be delivered, so it cannot
      // wait behind a scheduled drain.
      if (!buffer || disposed || streamEnded || buffer.updating) return false
      try {
        if (!element.buffered || element.buffered.length < 1) return false
        const liveEdge = element.buffered.end(element.buffered.length - 1)
        const start = element.buffered.start(0)
        let end = element.buffered.end(0)
        const position = (Number(element.currentTime) || 0) + .02
        // Anything at or after currentTime is the frame on screen and is never removed.
        if (end > position) {
          if (start >= position) return false
          end = position
        }
        if (!(end - start > 0)) return false
        buffer.remove(start, end)
        return true
      } catch (_) {
        return false
      }
    }
    const boundBuffer = () => {
      const buffer = sourceBuffer
      if (!buffer || disposed || buffer.updating || streamEnded) return
      try {
        if (!element.buffered || element.buffered.length < 1) return
        const liveEdge = element.buffered.end(element.buffered.length - 1)
        lastBufferedSeconds = liveEdge
        // Playback is re-seeked onto the live edge only while playing and only once it has
        // drifted a full latency window behind, otherwise a stable decoder sees no seeks.
        const position = Number(element.currentTime) || 0
        const playing = !element.paused && !element.ended
        if (position - (liveEdge - options.liveLatencySeconds) < -options.liveLatencySeconds) {
          if (playing) element.currentTime = Math.max(0, liveEdge - options.liveLatencySeconds)
          return
        }
        if (liveEdge - element.buffered.start(0) <= options.maxBufferSeconds) return
        // The retained tail is the window the viewer is watching, so the budget is enforced
        // by removing the oldest contiguous range and never anything at or after currentTime.
        trimOldestRange()
      } catch (_) {}
    }
    const appendFragment = (fragment, retried) => {
      if (disposed || !sourceBuffer) {
        fragment.finish('stale')
        return
      }
      if (streamEnded) {
        fragment.finish('decode-failed')
        return
      }
      const buffer = sourceBuffer
      const baseline = presentedFrames
      try {
        buffer.appendBuffer(fragment.bytes.buffer)
      } catch (error) {
        onAppendFailure(error, fragment, retried, baseline)
        return
      }
      fragment.retried = retried
      fragment.baseline = baseline
      inflight.add(fragment)
    }
    const onAppendFailure = (error, fragment, retried, baseline) => {
      const quota = !!error && (error.name === 'QuotaExceededError'
        || /quota/i.test(String(error.message || '')))
      // A rejected append leaves no queued updateend event behind, so the retry has to be
      // armed explicitly on the updateend of the removal it triggered.
      if (quota && !retried && trimOldestRange()) {
        quotaRetry = { fragment, retried: true, baseline }
        // A removal that never reports back must not strand the caller's promise.
        quotaRetryTimer = setTimeout(() => {
          quotaRetryTimer = 0
          const pendingRetry = quotaRetry
          quotaRetry = null
          if (!pendingRetry) return
          report('The media buffer did not release space in time.', 'failed')
          abortFragments('decode-failed')
        }, 4000)
        return
      }
      if (quota) report('The media buffer could not make room for a new fragment.', 'failed')
      else report(error && error.message ? error.message : 'The source buffer rejected a fragment.', 'failed')
      if (!quota) {
        try {
          if (sourceBuffer) sourceBuffer.abort()
        } catch (_) {}
      }
      // A transient append error is reported, never repaired: the surface keeps the last
      // decoded frame instead of being rebuilt or blanked.
      abortFragments('decode-failed')
    }
    const drainQueue = () => {
      if (disposed || !sourceBuffer) return
      while (fragmentQueue.length && !sourceBuffer.updating) {
        const fragment = fragmentQueue.shift()
        pending = fragmentQueue.length
        pendingBytes -= fragment.bytes.byteLength
        if (pendingBytes < 0) pendingBytes = 0
        if (streamEnded) {
          fragment.finish('stale')
          continue
        }
        if (fragment.settled) continue
        appendFragment(fragment, false)
      }
    }
    const handleAppendSuccess = fragment => {
      appendedFragments += 1
      appendedBytes += fragment.bytes.byteLength
      if (appendedBytes > Number.MAX_SAFE_INTEGER) appendedBytes = Number.MAX_SAFE_INTEGER
      sequence = fragment.sequence
      lastArrivalAt = Date.now()
      fragment.bytes = null
      if (state === 'pending' || state === 'stalled') state = 'live'
      try {
        const buffered = element.buffered
        if (buffered && buffered.length > 0) lastBufferedSeconds = buffered.end(buffered.length - 1)
      } catch (_) {}
      // The most recent frame simply stays on screen between fragments, so a recently
      // confirmed presentation already counts for this fragment.
      const visible = confirmed(fragment.baseline)
      if (visible) {
        lastPresentedAt = Date.now()
        lastFrameAt = lastPresentedAt
      }
      fragment.finish(visible ? 'presented' : 'appended')
      ensurePlayback()
      boundBuffer()
    }
    const handleUpdateEnd = () => {
      const retry = quotaRetry
      if (retry) {
        quotaRetry = null
        if (quotaRetryTimer) clearTimeout(quotaRetryTimer)
        quotaRetryTimer = 0
        if (!disposed && sourceBuffer && !streamEnded) appendFragment(retry.fragment, true)
        else retry.fragment.finish('stale')
        return
      }
      const fragment = inflight.values().next().value
      if (fragment) {
        inflight.delete(fragment)
        const bufferError = sourceBuffer && sourceBuffer.error
        if (bufferError) {
          onAppendFailure(bufferError, fragment, fragment.retried === true, fragment.baseline || 0)
          return
        }
        handleAppendSuccess(fragment)
        return
      }
      boundBuffer()
    }
    const attachSourceBuffer = () => {
      if (disposed || ready) return
      try {
        sourceBuffer = mediaSource.addSourceBuffer(options.codec)
        sourceBuffer.mode = 'segments'
      } catch (error) {
        report('Could not create a source buffer for ' + options.codec + '.', 'failed')
        fatalFailure = error
        failPending('decode-failed')
        return
      }
      ready = true
      reason = ''
      state = 'live'
      on(sourceBuffer, 'updateend', handleUpdateEnd)
      drainQueue()
      ensurePlayback()
      boundBuffer()
    }

    on(mediaSource, 'sourceopen', attachSourceBuffer)
    on(mediaSource, 'sourceended', () => {
      streamEnded = true
      state = 'stalled'
      report('The media source ended; the surface keeps the last decoded frame.')
      failPending('decode-failed')
    })
    on(mediaSource, 'sourceclose', () => {
      streamEnded = true
      state = 'disposed'
      report('The media source closed before the surface finished.')
      failPending('decode-failed')
    })
    on(mediaSource, 'error', () => {
      fatalFailure = new Error('The media source failed.')
      report(fatalFailure.message, 'failed')
      failPending('decode-failed')
    })
    on(element, 'error', () => {
      const mediaError = element.error
      fatalFailure = new Error(mediaError && mediaError.message
        ? mediaError.message
        : 'The video element reported a fatal media error.')
      report(fatalFailure.message, 'failed')
      failPending('decode-failed')
    })
    on(element, 'stalled', () => {
      if (state === 'live') state = 'stalled'
    })

    sourceOpenTimer = setTimeout(() => {
      if (ready || disposed) return
      report('The media source did not open in time.', 'failed')
      failPending('decode-failed')
    }, LIMITS.sourceOpenTimeout)

    const watchdogTick = () => {
      if (disposed) return
      const now = Date.now()
      if (ready && !streamEnded && !element.error) {
        const advance = Math.abs((Number(element.currentTime) || 0) - lastFrameClock) > 0
        lastFrameClock = Number(element.currentTime) || 0
        if (typeof element.requestVideoFrameCallback !== 'function' && advance) markPresented()
        ensurePlayback()
        drainQueue()
        if (state === 'pending' || state === 'stalled') state = 'live'
        if (lastFrameAt === 0) lastFrameAt = now
      }
      if (fatalFailure) return
      const stalled = lastFrameAt > 0 && now - lastFrameAt > LIMITS.stallMs
        && now - lastArrivalAt < LIMITS.stallMs
      if (stalled && state === 'live') state = 'stalled'
      if (ready && !streamEnded && !element.paused && !element.ended) {
        const liveEdge = bufferedEnd(element)
        const lag = liveEdge > 0
          ? (Number(element.currentTime) || 0) - (liveEdge - options.liveLatencySeconds)
          : 0
        if (lag < -options.liveLatencySeconds) {
          report('Playback fell a full latency window behind the live edge; re-seeking.')
          boundBuffer()
        } else if (lag > 2 * options.liveLatencySeconds) {
          report('Playback is more than ' + (2 * options.liveLatencySeconds).toFixed(2)
            + 's behind the live edge.')
        } else if (state === 'live') {
          reason = ''
        }
      }
    }
    watchdog = setInterval(watchdogTick, LIMITS.watchdogMs)

    controller = {
      version: VERSION,
      streamId: options.streamId,
      element,
      get mode() {
        return presentedFrames > 0 ? 'gpu-video-live' : 'gpu-video-pending'
      },
      get decoderMode() {
        return decoderMode
      },
      pushFragment(seq, base64) {
        if (disposed) return Promise.resolve('stale')
        if (!Number.isInteger(seq) || seq < 0 || seq !== sequence + 1) return Promise.resolve('rejected')
        if (element.error || fatalFailure) return Promise.resolve('decode-failed')
        const bytes = decodeBase64(base64)
        if (!bytes) return Promise.resolve('rejected')
        // The pending window is bounded by count and by bytes so a fast producer cannot
        // grow the page heap; the caller drops the fragment and restarts the stream.
        if (pending + inflight.size >= options.maxPendingFragments
          || pendingBytes + bytes.byteLength > LIMITS.pendingBytes) {
          return Promise.resolve('busy')
        }
        return new Promise(resolve => {
          const waiter = {
            sequence: seq,
            bytes,
            baseline: presentedFrames,
            retried: false,
            settled: false,
            finish(result) {
              if (this.settled) return
              this.settled = true
              resolve(result)
            }
          }
          if (!ready) {
            fragmentQueue.push(waiter)
            pending = fragmentQueue.length
            pendingBytes += bytes.byteLength
            return
          }
          if (sourceBuffer && sourceBuffer.updating) {
            fragmentQueue.push(waiter)
            pending = fragmentQueue.length
            pendingBytes += bytes.byteLength
            return
          }
          appendFragment(waiter, false)
        })
      },
      status() {
        return snapshot()
      },
      update(next) {
        if (disposed || !next || typeof next !== 'object') return
        if (typeof next.muted === 'boolean') element.muted = next.muted
        if (next.rate !== undefined) element.playbackRate = clampNumber(next.rate, .25, 4, 1)
        if (next.pauseWhenHidden !== undefined) settings.pauseWhenHidden = !!next.pauseWhenHidden
        if (next.opacity !== undefined) element.style.opacity = String(clampNumber(next.opacity, 0, 1, 1))
        if (next.fit !== undefined || next.focusX !== undefined || next.focusY !== undefined) {
          try {
            applyFitStyle(element, next)
          } catch (_) {}
        }
        if (settings.pauseWhenHidden && document.hidden) {
          try {
            element.pause()
          } catch (_) {}
        } else {
          ensurePlayback()
        }
      },
      async dispose() {
        if (disposed) return
        disposed = true
        state = 'disposed'
        if (rvfcId && typeof element.cancelVideoFrameCallback === 'function') {
          try {
            element.cancelVideoFrameCallback(rvfcId)
          } catch (_) {}
        }
        rvfcId = 0
        rvfcActive = false
        if (watchdog) clearInterval(watchdog)
        watchdog = null
        if (sourceOpenTimer) clearTimeout(sourceOpenTimer)
        sourceOpenTimer = 0
        if (quotaRetryTimer) clearTimeout(quotaRetryTimer)
        quotaRetryTimer = 0
        quotaRetry = null
        listeners.forEach(listener => {
          try {
            listener.target.removeEventListener(listener.type, listener.handler)
          } catch (_) {}
        })
        listeners.length = 0
        failPending('stale')
        while (fragmentQueue.length) fragmentQueue.shift().finish('stale')
        try {
          if (sourceBuffer && sourceBuffer.updating) sourceBuffer.abort()
        } catch (_) {}
        try {
          if (mediaSource.readyState === 'open') mediaSource.endOfStream()
        } catch (_) {}
        sourceBuffer = null
        pending = 0
        pendingBytes = 0
        try {
          element.pause()
        } catch (_) {}
        try {
          element.removeAttribute('src')
        } catch (_) {}
        try {
          element.remove()
        } catch (_) {}
        try {
          URL.revokeObjectURL(objectUrl)
        } catch (_) {}
        await disposedResolve
      }
    }
    decoderMode = await readDecoderMode(navigator.mediaCapabilities, options,
      Math.min(60 * 1000 * 1000, options.width * options.height * options.frameRate))
    return controller
  }
  createGpuSurface.version = VERSION
  window.__cwsCreateGpuSurface = createGpuSurface
})();
