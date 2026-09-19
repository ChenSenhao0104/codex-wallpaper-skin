(() => {
  'use strict'
  const HOST_VERSION = 'cws-scene-host-2'
  if (window.__cwsCreateSceneWallpaper?.version === HOST_VERSION) return
  const lib = window.__cwsWeSceneLibrary
  if (!lib) throw new Error('The embedded scene renderer library is unavailable.')

  const LIMITS = Object.freeze({
    packageBytes: 128 * 1024 * 1024,
    entries: 4096,
    entryBytes: 64 * 1024 * 1024,
    jsonBytes: 2 * 1024 * 1024,
    layers: 512,
    effects: 4096,
    textures: 256,
    texturePixels: 33554432,
    totalTexturePixels: 67108864,
    decodedTexBytes: 512 * 1024 * 1024,
    dimension: 8192,
    canvasPixels: 8294400,
    canvasDimension: 4096
  })
  const utf8 = new TextDecoder('utf-8', { fatal: true })

  const canonicalPath = value => {
    if (typeof value !== 'string' || value.length < 1 || value.length > 1024
      || value.startsWith('/') || value.includes('\\') || value.includes(':')
      || /[\0-\x1f]/.test(value)) throw new Error('Scene package contains an unsafe virtual path.')
    const parts = value.split('/')
    if (parts.some(part => !part || part === '.' || part === '..')) throw new Error('Scene package contains path traversal.')
    return value
  }
  const entryBytes = (pkg, name, maximum = LIMITS.entryBytes) => {
    const safeName = canonicalPath(name)
    const meta = pkg.entries.find(entry => entry.name === safeName)
    if (!meta) return null
    if (!Number.isSafeInteger(meta.size) || meta.size <= 0 || meta.size > maximum) {
      throw new Error('Scene asset exceeds its bounded size: ' + safeName)
    }
    return lib.getEntry(pkg, safeName)
  }
  const readText = (bytes, label = 'JSON') => {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength <= 0 || bytes.byteLength > LIMITS.jsonBytes) {
      throw new Error(label + ' exceeds the scene JSON limit.')
    }
    return utf8.decode(bytes).replace(/^\uFEFF/, '')
  }
  const parseJson = (bytes, label) => {
    const value = JSON.parse(readText(bytes, label))
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(label + ' must be a JSON object.')
    return value
  }
  const validatePkg = pkg => {
    const version = pkg && /^PKGV\d{4}$/.test(pkg.magic) ? Number(pkg.magic.slice(4)) : 0
    if (!pkg || version < 12 || version > 23 || !Number.isInteger(pkg.count)
      || pkg.count < 1 || pkg.count > LIMITS.entries || pkg.entries.length !== pkg.count) {
      throw new Error('scene.pkg metadata failed the renderer boundary check.')
    }
    const seen = new Set()
    const ranges = []
    for (const entry of pkg.entries) {
      const name = canonicalPath(entry.name)
      const folded = name.toLowerCase()
      if (seen.has(folded)) throw new Error('scene.pkg contains duplicate virtual paths.')
      seen.add(folded)
      const start = pkg.dataStart + entry.offset
      const end = start + entry.size
      if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || entry.size <= 0
        || entry.size > LIMITS.entryBytes || start < pkg.dataStart || end > pkg.fileSize) {
        throw new Error('scene.pkg entry points outside the validated package.')
      }
      ranges.push([start, end])
    }
    ranges.sort((left, right) => left[0] - right[0])
    for (let index = 1; index < ranges.length; index++) {
      if (ranges[index][0] < ranges[index - 1][1]) throw new Error('scene.pkg entries overlap.')
    }
  }
  const preflightPkgHeader = bytes => {
    if (bytes.byteLength < 16) throw new Error('scene.pkg header is truncated.')
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
    if (view.getUint32(0, true) !== 8) throw new Error('scene.pkg has an invalid version header length.')
    const magic = utf8.decode(bytes.subarray(4, 12))
    const version = /^PKGV\d{4}$/.test(magic) ? Number(magic.slice(4)) : 0
    const count = view.getUint32(12, true)
    if (version < 12 || version > 23 || count < 1 || count > LIMITS.entries) {
      throw new Error('scene.pkg header exceeds the renderer safety boundary.')
    }
  }
  const u32 = (view, offset) => {
    if (offset < 0 || offset + 4 > view.byteLength) throw new Error('TEX metadata is truncated.')
    return view.getUint32(offset, true)
  }
  const i32 = (view, offset) => {
    if (offset < 0 || offset + 4 > view.byteLength) throw new Error('TEX metadata is truncated.')
    return view.getInt32(offset, true)
  }
  const ascii = (bytes, offset, length) => {
    if (offset < 0 || offset + length > bytes.byteLength) throw new Error('TEX header is truncated.')
    let value = ''
    for (let index = offset; index < offset + length; index++) value += String.fromCharCode(bytes[index])
    return value
  }
  const expectedTexBytes = (format, width, height) => {
    const pixels = width * height
    if (format === 0) return pixels * 4
    if (format === 1) return pixels * 3
    if (format === 2 || format === 8) return pixels * 2
    if (format === 9) return pixels
    if (format === 7) return Math.ceil(width / 4) * Math.ceil(height / 4) * 8
    if (format === 4 || format === 6) return Math.ceil(width / 4) * Math.ceil(height / 4) * 16
    return null
  }
  const preflightTex = bytes => {
    if (!(bytes instanceof Uint8Array) || bytes.byteLength < 64 || bytes.byteLength > LIMITS.entryBytes) {
      throw new Error('TEX asset has an invalid size.')
    }
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
    if (ascii(bytes, 0, 9) !== 'TEXV0005\0' || ascii(bytes, 9, 9) !== 'TEXI0001\0') {
      throw new Error('Unsupported TEX container.')
    }
    const format = u32(view, 18)
    const flags = u32(view, 22)
    const declaredWidth = u32(view, 34)
    const declaredHeight = u32(view, 38)
    if (!declaredWidth || !declaredHeight || declaredWidth > LIMITS.dimension || declaredHeight > LIMITS.dimension
      || declaredWidth * declaredHeight > LIMITS.texturePixels) {
      throw new Error('TEX declares unsafe image dimensions.')
    }
    let offset = 46
    const containerMagic = ascii(bytes, offset, 9)
    offset += 9
    const imageCount = u32(view, offset)
    offset += 4
    if (imageCount < 1 || imageCount > 16) throw new Error('TEX image count exceeds the safety limit.')
    let freeImageFormat = -1
    let version = 0
    if (containerMagic === 'TEXB0004\0') {
      freeImageFormat = i32(view, offset)
      offset += 4
      const isMp4 = u32(view, offset)
      offset += 4
      if (freeImageFormat === -1 && isMp4 === 1) freeImageFormat = lib.FIF.MP4
      version = freeImageFormat === lib.FIF.MP4 ? 4 : 3
    } else if (containerMagic === 'TEXB0003\0') {
      freeImageFormat = i32(view, offset)
      offset += 4
      version = 3
    } else if (containerMagic === 'TEXB0002\0') version = 2
    else if (containerMagic === 'TEXB0001\0') version = 1
    else throw new Error('Unsupported TEX payload container.')
    const isVideo = (flags & 32) !== 0 || freeImageFormat === lib.FIF.MP4
    let decodedBytes = 0
    for (let image = 0; image < imageCount; image++) {
      const mipCount = u32(view, offset)
      offset += 4
      if (mipCount < 1 || mipCount > 16) throw new Error('TEX mip count exceeds the safety limit.')
      for (let mip = 0; mip < mipCount; mip++) {
        if (version === 4) {
          offset += 8
          const conditionStart = offset
          while (offset < bytes.byteLength && bytes[offset] !== 0 && offset - conditionStart <= 65536) offset++
          if (offset >= bytes.byteLength || offset - conditionStart > 65536) throw new Error('TEX condition metadata is unbounded.')
          offset += 5
        }
        const width = u32(view, offset)
        const height = u32(view, offset + 4)
        offset += 8
        if (!width || !height || width > LIMITS.dimension || height > LIMITS.dimension
          || width * height > LIMITS.texturePixels) throw new Error('TEX mip dimensions exceed the safety limit.')
        let compression = 0
        let uncompressedSize = 0
        if (version >= 2) {
          compression = u32(view, offset)
          uncompressedSize = i32(view, offset + 4)
          offset += 8
        }
        const compressedSize = i32(view, offset)
        offset += 4
        if (isVideo) {
          if (imageCount !== 1 || mipCount !== 1 || offset >= bytes.byteLength) throw new Error('TEX video layout is invalid.')
          return { width: declaredWidth, height: declaredHeight, isVideo: true }
        }
        if (compressedSize <= 0 || offset + compressedSize > bytes.byteLength || (compression !== 0 && compression !== 1)) {
          throw new Error('TEX mip payload is invalid.')
        }
        if (compression === 0) uncompressedSize = compressedSize
        if (uncompressedSize <= 0 || uncompressedSize > 128 * 1024 * 1024) throw new Error('TEX decoded mip is too large.')
        decodedBytes += uncompressedSize
        if (decodedBytes > LIMITS.decodedTexBytes) throw new Error('TEX decoded data exceeds the safety budget.')
        if (freeImageFormat === lib.FIF.UNKNOWN) {
          const expected = expectedTexBytes(format, width, height)
          if (expected !== null && uncompressedSize !== expected) throw new Error('TEX decoded size does not match its dimensions.')
        }
        offset += compressedSize
      }
    }
    if (offset > bytes.byteLength) throw new Error('TEX payload escaped its entry.')
    return { width: declaredWidth, height: declaredHeight, isVideo: false }
  }
  const waitForImage = image => new Promise((resolve, reject) => {
    let settled = false
    const finish = callback => value => {
      if (settled) return
      settled = true
      clearTimeout(timeout)
      image.removeEventListener('load', ready)
      image.removeEventListener('error', failed)
      callback(value)
    }
    const ready = finish(resolve)
    const failed = finish(() => reject(new Error('Chromium could not decode the scene fallback image.')))
    const timeout = setTimeout(failed, 15000)
    image.addEventListener('load', ready, { once: true })
    image.addEventListener('error', failed, { once: true })
    if (image.complete && image.naturalWidth > 0) ready()
  })
  const boundedWarning = value => String(value || '').replace(/\s+/g, ' ').trim().slice(0, 420)

  const createSceneWallpaper = async (packageBytes, options = {}) => {
    if (!(packageBytes instanceof Uint8Array) || packageBytes.byteLength <= 0 || packageBytes.byteLength > LIMITS.packageBytes) {
      throw new Error('The uploaded scene.pkg exceeds the renderer package limit.')
    }
    preflightPkgHeader(packageBytes)
    const pkg = lib.parsePkg(packageBytes)
    validatePkg(pkg)
    const sceneJson = parseJson(entryBytes(pkg, 'scene.json', 4 * 1024 * 1024), 'scene.json')
    const scene = lib.parseScene(sceneJson, options.project || null)
    const disabledEffects = new Set(Array.isArray(options.disabledEffects)
      ? options.disabledEffects.filter(value => typeof value === 'string')
      : [])
    const fidelityFallbackEffects = new Set([
      'effects/waterflow/effect.json',
      'effects/waterripple/effect.json'
    ])
    let fidelityFallbackCount = 0
    for (const layer of scene.layers) {
      if (!Array.isArray(layer.effects)) continue
      const removed = layer.effects.filter(effect => fidelityFallbackEffects.has(effect.file))
      layer.effects = layer.effects.filter(effect => !fidelityFallbackEffects.has(effect.file))
      if (removed.length) layer.cwsSafePointerRipple = true
      fidelityFallbackCount += removed.length
    }
    if (disabledEffects.size) {
      for (const layer of scene.layers) {
        if (Array.isArray(layer.effects)) layer.effects = layer.effects.filter(effect => !disabledEffects.has(effect.file))
      }
    }
    if (!Array.isArray(scene.layers) || scene.layers.length > LIMITS.layers) {
      throw new Error('Scene layer count exceeds the renderer safety limit.')
    }
    let effectCount = 0
    for (const layer of scene.layers) {
      effectCount += Array.isArray(layer.effects) ? layer.effects.length : 0
      if (effectCount > LIMITS.effects) throw new Error('Scene effect count exceeds the renderer safety limit.')
    }

    const canvas = document.createElement('canvas')
    canvas.setAttribute('aria-hidden', 'true')
    const shaderSources = options.shaders && typeof options.shaders === 'object' ? options.shaders : {}
    const packageShaderCache = new Map()
    let packageShaderBytes = 0
    const resolveShader = async requested => {
      if (typeof requested !== 'string') return null
      let key = requested.replace(/\\/g, '/').toLowerCase()
      if (key.startsWith('shaders/')) key = key.slice(8)
      if (!key || key.startsWith('/') || key.includes('..') || key.includes(':')) return null
      if (packageShaderCache.has(key)) return packageShaderCache.get(key)
      const embedded = entryBytes(pkg, canonicalPath('shaders/' + key), 256 * 1024)
      if (embedded) {
        packageShaderBytes += embedded.byteLength
        if (packageShaderBytes > 2 * 1024 * 1024) throw new Error('Scene shader sources exceed the aggregate safety budget.')
        const source = utf8.decode(embedded)
        if (source.includes('\0')) throw new Error('Scene shader contains invalid text data.')
        packageShaderCache.set(key, source)
        return source
      }
      const source = shaderSources[key]
      return typeof source === 'string' && source.length <= 256 * 1024 ? source : null
    }
    const initialSceneScale = Math.min(1, Math.max(.5, Number(options.settings?.sceneResolutionScale) || 1))
    const renderer = lib.createRenderer(canvas, { shaderResolver: resolveShader, fboCapFactor: initialSceneScale >= .99 ? 0 : initialSceneScale })
    const textures = new Map()
    const objectUrls = new Set()
    const videoElements = new Set()
    let disposed = false
    let decodedPixels = 0
    let bestFallback = null

    const rememberFallback = (bytes, mime, width, height) => {
      const pixels = width * height
      if (!bestFallback || pixels > bestFallback.width * bestFallback.height) {
        bestFallback = { bytes: bytes.slice(), mime, width, height }
      }
    }
    const installUtilityTextures = () => {
      textures.set('util/white', {
        glTex: lib.makeTexture(renderer.gl, new Uint8Array([255, 255, 255, 255]), 1, 1),
        width: 1, height: 1, rg88: false
      })
      textures.set('util/noflow', {
        glTex: lib.makeTexture(renderer.gl, new Uint8Array([127, 127, 127, 255]), 1, 1),
        width: 1, height: 1, rg88: false
      })
      textures.set('util/noise', {
        glTex: lib.makeTexture(renderer.gl, lib.generateNoiseTexture(), 256, 256),
        width: 256, height: 256, rg88: false
      })
    }
    installUtilityTextures()

    const loadTexture = async (name, primary = false) => {
      if (typeof name !== 'string' || !name) return null
      canonicalPath(name)
      if (textures.has(name)) return textures.get(name)
      if (textures.size >= LIMITS.textures + 3) throw new Error('Scene texture count exceeds the safety limit.')
      const virtualPath = canonicalPath('materials/' + name + '.tex')
      const bytes = entryBytes(pkg, virtualPath)
      if (!bytes) return null
      preflightTex(bytes)
      const tex = lib.parseTex(bytes)
      const mip = lib.decodeMip0(tex)
      const rg88 = tex.format === 8
      let result = null
      if (mip.video !== undefined) {
        const blob = new Blob([mip.video], { type: 'video/mp4' })
        const url = URL.createObjectURL(blob)
        objectUrls.add(url)
        const video = document.createElement('video')
        video.src = url
        video.loop = true
        video.muted = true
        video.playsInline = true
        video.preload = 'metadata'
        videoElements.add(video)
        video.addEventListener('loadedmetadata', () => {
          if (video.videoWidth > LIMITS.dimension || video.videoHeight > LIMITS.dimension
            || video.videoWidth * video.videoHeight > LIMITS.texturePixels) {
            try { video.pause() } catch (_) {}
            video.removeAttribute('src')
          }
        }, { once: true })
        video.play().catch(() => {})
        result = {
          video,
          glTex: lib.makeTexture(renderer.gl, new Uint8Array([0, 0, 0, 0]), 1, 1),
          width: mip.width, height: mip.height, rg88: false, lastUploaded: -1
        }
      } else if (mip.png !== undefined || (mip.image !== undefined && mip.fif === lib.FIF.JPEG)) {
        const payload = mip.png || mip.image
        const mime = mip.png ? 'image/png' : 'image/jpeg'
        const bitmap = await createImageBitmap(new Blob([payload], { type: mime }))
        if (bitmap.width <= 0 || bitmap.height <= 0 || bitmap.width > LIMITS.dimension
          || bitmap.height > LIMITS.dimension || bitmap.width * bitmap.height > LIMITS.texturePixels) {
          bitmap.close()
          throw new Error('Decoded scene image dimensions exceed the safety limit.')
        }
        decodedPixels += bitmap.width * bitmap.height
        if (decodedPixels > LIMITS.totalTexturePixels) {
          bitmap.close()
          throw new Error('Decoded scene textures exceed the aggregate pixel budget.')
        }
        result = {
          glTex: lib.makeTexture(renderer.gl, null, 0, 0, bitmap),
          width: bitmap.width, height: bitmap.height, rg88
        }
        if (primary) rememberFallback(payload, mime, bitmap.width, bitmap.height)
        bitmap.close()
      } else if (mip.image !== undefined) {
        return null
      } else {
        if (!(mip.rgba instanceof Uint8Array) || mip.rgba.byteLength > 128 * 1024 * 1024) {
          throw new Error('Decoded scene texture exceeds the byte limit.')
        }
        decodedPixels += mip.width * mip.height
        if (decodedPixels > LIMITS.totalTexturePixels) throw new Error('Decoded scene textures exceed the aggregate pixel budget.')
        result = {
          glTex: lib.makeTextureMip(renderer.gl, [mip], rg88),
          width: mip.width, height: mip.height, rg88, mips: [mip]
        }
      }
      textures.set(name, result)
      return result
    }

    const resolveEffects = effect => {
      const effectBytes = entryBytes(pkg, effect.file, LIMITS.jsonBytes)
      if (!effectBytes) return
      const definition = parseJson(effectBytes, effect.file)
      const passes = Array.isArray(definition.passes) ? definition.passes : []
      if (passes.length > 128) throw new Error('A scene effect contains too many passes.')
      effect.fbos = Array.isArray(definition.fbos) ? definition.fbos.slice(0, 64) : []
      effect.materialPasses = passes.map(pass => {
        if (!pass.material) {
          return {
            shader: null, copyCommand: true, target: pass.target || null,
            binds: Array.isArray(pass.bind) ? pass.bind.slice(0, 32) : [],
            blending: 'normal', textures: [], combos: {}, constants: {}
          }
        }
        const materialPath = canonicalPath(pass.material)
        const materialBytes = entryBytes(pkg, materialPath, LIMITS.jsonBytes)
        const material = lib.BUILTIN_MATERIALS[materialPath]
          || (materialBytes ? parseJson(materialBytes, materialPath) : null)
        if (!material) {
          return {
            shader: null, copyCommand: false, target: pass.target || null,
            binds: Array.isArray(pass.bind) ? pass.bind.slice(0, 32) : [],
            blending: 'normal', textures: [], combos: {}, constants: {}
          }
        }
        const materialPass = Array.isArray(material.passes) && material.passes[0] ? material.passes[0] : {}
        return {
          shader: typeof materialPass.shader === 'string' ? materialPass.shader : null,
          copyCommand: false,
          target: pass.target || null,
          binds: Array.isArray(pass.bind) ? pass.bind.slice(0, 32) : [],
          blending: materialPass.blending || 'normal',
          textures: Array.isArray(materialPass.textures) ? materialPass.textures.slice(0, 16) : [],
          combos: materialPass.combos || {},
          constants: materialPass.constantshadervalues || {}
        }
      })
    }

    try {
      for (const layer of scene.layers) {
        if (!layer.visible || !layer.image) continue
        let model
        if (lib.BUILTIN_MODELS[layer.image]) model = lib.BUILTIN_MODELS[layer.image]
        else {
          const modelBytes = entryBytes(pkg, canonicalPath(layer.image), LIMITS.jsonBytes)
          if (!modelBytes) continue
          model = parseJson(modelBytes, layer.image)
        }
        const materialReference = lib.resolveMaterial(model)
        if (!materialReference) continue
        let material
        if (lib.BUILTIN_MATERIALS[materialReference.materialPath]) {
          material = lib.BUILTIN_MATERIALS[materialReference.materialPath]
        } else {
          const materialPath = canonicalPath(materialReference.materialPath)
          const materialBytes = entryBytes(pkg, materialPath, LIMITS.jsonBytes)
          if (!materialBytes) continue
          material = parseJson(materialBytes, materialPath)
        }
        const firstPass = Array.isArray(material.passes) ? material.passes[0] : null
        const textureName = firstPass && Array.isArray(firstPass.textures) ? firstPass.textures[0] : null
        if (typeof textureName === 'string' && textureName && await loadTexture(textureName, true)) {
          layer.textureName = textureName
        }
        // Scene-level pass overrides contain author-provided masks which are not
        // repeated in the material definition.  Cursor ripple projects (such as
        // Saki's bathtub) rely on this mask to constrain interaction to water.
        for (const effect of layer.effects || []) {
          if (effect.file === 'effects/cursorripple/effect.json') {
            for (const pass of effect.passes || []) {
              for (const textureName of pass.textures || []) {
                if (typeof textureName === 'string' && textureName && !textureName.startsWith('util/')
                  && !textureName.startsWith('_rt_') && textureName !== 'previous') {
                  await loadTexture(textureName, false)
                }
              }
            }
          }
          resolveEffects(effect)
        }
        for (const effect of layer.effects || []) {
          for (const pass of effect.materialPasses || []) {
            for (const textureName of pass.textures || []) {
              if (typeof textureName === 'string' && textureName && !textureName.startsWith('util/')
                && !textureName.startsWith('_rt_') && textureName !== 'previous') {
                await loadTexture(textureName, false)
              }
            }
          }
        }
      }

      const particleLayers = scene.layers.filter(layer => !!layer.particle).length
      const unsupportedLayers = scene.layers.filter(layer => !layer.image && !layer.particle && !layer.solid).length
      const warnings = []
      if (particleLayers) warnings.push(particleLayers + ' particle layer(s) are omitted by the 2D renderer')
      if (unsupportedLayers) warnings.push(unsupportedLayers + ' component/text/other layer(s) are omitted')
      if (fidelityFallbackCount) warnings.push(fidelityFallbackCount + ' unstable water feedback effect(s) were replaced by the safe interactive renderer')
      const hasEmbeddedShaders = pkg.entries.some(entry => entry.name.toLowerCase().startsWith('shaders/'))
      if (effectCount && Object.keys(shaderSources).length === 0 && !hasEmbeddedShaders) {
        warnings.push('Wallpaper Engine shader assets were not found')
      }

      let currentSettings = options.settings || {}
      let rafId = 0
      let renderBusy = false
      let lastRenderAt = 0
      let lastClock = performance.now()
      let sceneTime = 0
      let resizeObserver = null
      let resizeHandler = null
      let lastRenderError = null

      const resize = () => {
        if (disposed) return
        const scale = Math.min(1, Math.max(.5, Number(currentSettings.sceneResolutionScale) || 1))
        const dpr = Math.min(2, Math.max(1, Number(window.devicePixelRatio) || 1))
        let width = Math.max(1, Math.round((document.documentElement.clientWidth || window.innerWidth || 1280) * dpr * scale))
        let height = Math.max(1, Math.round((document.documentElement.clientHeight || window.innerHeight || 720) * dpr * scale))
        const dimensionScale = Math.min(1, LIMITS.canvasDimension / width, LIMITS.canvasDimension / height)
        const pixelScale = Math.min(1, Math.sqrt(LIMITS.canvasPixels / (width * height)))
        const cap = Math.min(dimensionScale, pixelScale)
        width = Math.max(1, Math.round(width * cap))
        height = Math.max(1, Math.round(height * cap))
        if (canvas.width !== width || canvas.height !== height) {
          canvas.width = width
          canvas.height = height
        }
      }
      resize()
      try {
        await renderer.render(scene, textures, canvas.width, canvas.height, 0)
      } catch (error) {
        if (!bestFallback) throw error
        try { renderer.dispose && renderer.dispose() } catch (_) {}
        for (const video of videoElements) {
          try { video.pause(); video.removeAttribute('src'); video.load() } catch (_) {}
        }
        for (const url of objectUrls) {
          try { URL.revokeObjectURL(url) } catch (_) {}
        }
        objectUrls.clear()
        const url = URL.createObjectURL(new Blob([bestFallback.bytes], { type: bestFallback.mime }))
        const image = document.createElement('img')
        image.src = url
        await waitForImage(image)
        packageBytes = null
        pkg.buf = new Uint8Array(0)
        pkg.entries.length = 0
        return {
          element: image,
          paletteSource: image,
          mode: 'scene-static',
          warning: boundedWarning('Live 2D rendering failed; using the full-resolution scene texture instead. ' + (error && error.message)),
          update() {},
          dispose() {
            if (disposed) return
            disposed = true
            try { image.removeAttribute('src') } catch (_) {}
            try { URL.revokeObjectURL(url) } catch (_) {}
          }
        }
      }

      const tick = now => {
        if (disposed) return
        rafId = requestAnimationFrame(tick)
        const pause = !!currentSettings.pauseWhenHidden && document.hidden
        const fps = [10, 15].includes(Number(currentSettings.sceneFrameRate))
          ? Number(currentSettings.sceneFrameRate) : 15
        const minimumGap = 1000 / fps
        const delta = Math.min(.1, Math.max(0, (now - lastClock) / 1000))
        lastClock = now
        if (pause || renderBusy || now - lastRenderAt < minimumGap) return
        sceneTime += delta * Math.min(2, Math.max(.25, Number(currentSettings.rate) || 1))
        lastRenderAt = now
        renderBusy = true
        renderer.render(scene, textures, canvas.width, canvas.height, sceneTime)
          .catch(error => { lastRenderError = boundedWarning(error && error.message) })
          .finally(() => { renderBusy = false })
      }
      if (typeof ResizeObserver === 'function') {
        resizeObserver = new ResizeObserver(resize)
        resizeObserver.observe(document.documentElement)
      } else {
        resizeHandler = resize
        window.addEventListener('resize', resizeHandler)
      }
      rafId = requestAnimationFrame(tick)
      packageBytes = null
      pkg.buf = new Uint8Array(0)
      pkg.entries.length = 0
      return {
        element: canvas,
        paletteSource: canvas,
        mode: warnings.length ? 'scene-partial' : 'live-scene',
        warning: boundedWarning(warnings.join('; ')),
        diagnostics() {
          return renderer.getDiagnostics ? renderer.getDiagnostics(scene) : {}
        },
        update(settings) {
          currentSettings = settings || currentSettings
          const effectScale = Math.min(1, Math.max(.5, Number(currentSettings.sceneResolutionScale) || 1))
          renderer.setFboCapFactor && renderer.setFboCapFactor(effectScale >= .99 ? 0 : effectScale)
          resize()
          for (const video of videoElements) {
            video.muted = true
            video.playbackRate = Math.min(2, Math.max(.25, Number(currentSettings.rate) || 1))
            if (currentSettings.pauseWhenHidden && document.hidden) video.pause()
            else video.play().catch(() => {})
          }
        },
        dispose() {
          if (disposed) return
          disposed = true
          if (rafId) cancelAnimationFrame(rafId)
          try { resizeObserver && resizeObserver.disconnect() } catch (_) {}
          try { resizeHandler && window.removeEventListener('resize', resizeHandler) } catch (_) {}
          for (const video of videoElements) {
            try { video.pause(); video.removeAttribute('src'); video.load() } catch (_) {}
          }
          for (const url of objectUrls) {
            try { URL.revokeObjectURL(url) } catch (_) {}
          }
          objectUrls.clear()
          try { renderer.dispose && renderer.dispose() } catch (_) {}
          if (lastRenderError) console.warn('Codex Wallpaper Skin scene renderer stopped after an error:', lastRenderError)
        }
      }
    } catch (error) {
      disposed = true
      for (const video of videoElements) {
        try { video.pause(); video.removeAttribute('src'); video.load() } catch (_) {}
      }
      for (const url of objectUrls) {
        try { URL.revokeObjectURL(url) } catch (_) {}
      }
      try { renderer.dispose && renderer.dispose() } catch (_) {}
      throw error
    }
  }
  createSceneWallpaper.version = HOST_VERSION
  window.__cwsCreateSceneWallpaper = createSceneWallpaper
})();
