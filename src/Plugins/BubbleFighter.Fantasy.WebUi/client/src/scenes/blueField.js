const MAX_NORMAL_PIXELS = 2_000_000
const MAX_COARSE_PIXELS = 1_200_000
const PARTICLE_COUNT = 980

const vertexShader = `#version 300 es
in vec2 a_position;
void main() {
  gl_Position = vec4(a_position, 0.0, 1.0);
}`

const fieldShader = `#version 300 es
precision highp float;

uniform vec2 u_resolution;
uniform float u_time;
uniform vec2 u_pointer;
uniform vec2 u_hover;
uniform float u_hoverAmount;
uniform vec4 u_ripple;
out vec4 outColor;

float hash21(vec2 value) {
  value = fract(value * vec2(123.34, 456.21));
  value += dot(value, value + 45.32);
  return fract(value.x * value.y);
}

vec2 hash22(vec2 value) {
  vec3 seed = fract(vec3(value.xyx) * vec3(0.1031, 0.1030, 0.0973));
  seed += dot(seed, seed.yzx + 33.33);
  return fract((seed.xx + seed.yz) * seed.zy) * 2.0 - 1.0;
}

float gradientNoise(vec2 point) {
  vec2 cell = floor(point);
  vec2 local = fract(point);
  vec2 eased = local * local * (3.0 - 2.0 * local);
  float a = dot(hash22(cell), local);
  float b = dot(hash22(cell + vec2(1.0, 0.0)), local - vec2(1.0, 0.0));
  float c = dot(hash22(cell + vec2(0.0, 1.0)), local - vec2(0.0, 1.0));
  float d = dot(hash22(cell + vec2(1.0, 1.0)), local - vec2(1.0, 1.0));
  return mix(mix(a, b, eased.x), mix(c, d, eased.x), eased.y) * 0.5 + 0.5;
}

float fieldNoise(vec2 point) {
  mat2 rotation = mat2(0.80, -0.60, 0.60, 0.80);
  float value = 0.0;
  float amplitude = 0.58;
  for (int octave = 0; octave < 3; octave += 1) {
    value += gradientNoise(point) * amplitude;
    point = rotation * point * 2.04 + vec2(13.7, 29.3);
    amplitude *= 0.48;
  }
  return value;
}

float softEllipse(vec2 point, vec2 center, vec2 radius, float angle) {
  float sine = sin(angle);
  float cosine = cos(angle);
  mat2 rotation = mat2(cosine, -sine, sine, cosine);
  vec2 local = rotation * (point - center);
  return exp(-dot(local / radius, local / radius) * 1.66);
}

float mote(vec2 point, vec2 density, float seed, float radius) {
  vec2 cell = floor(point * density);
  vec2 local = fract(point * density);
  float exists = hash21(cell + vec2(seed, seed * 1.93));
  vec2 center = vec2(
    hash21(cell + vec2(seed * 2.61, 11.7)),
    hash21(cell + vec2(19.3, seed * 3.17))
  );
  float distanceToCenter = length((local - center) * vec2(1.0, 1.15));
  float particle = 1.0 - smoothstep(radius * 0.42, radius, distanceToCenter);
  return particle * smoothstep(0.976, 0.999, exists);
}

void main() {
  vec2 uv = gl_FragCoord.xy / u_resolution.xy;
  vec2 screen = vec2(uv.x, 1.0 - uv.y);
  float seconds = u_time * 0.001;

  // The palette begins at the four edges, then broad rotated volumes replace the flat interpolation.
  vec3 topEdge = mix(vec3(1.0), vec3(0.415, 0.690, 0.910), screen.x);
  vec3 bottomEdge = mix(vec3(0.645, 0.800, 0.990), vec3(0.548, 0.805, 1.0), screen.x);
  vec3 color = mix(topEdge, bottomEdge, screen.y);

  vec2 movement = vec2(
    fieldNoise(screen * 1.56 + vec2(seconds * 0.030, -seconds * 0.024)),
    fieldNoise(screen * 1.56 + vec2(8.1 - seconds * 0.021, 2.7))
  ) - 0.5;
  vec2 microMovement = vec2(
    fieldNoise(screen * 2.48 + movement * 0.82 + vec2(4.2, 1.9)),
    fieldNoise(screen * 2.48 + movement * 0.82 + vec2(9.4, 7.1))
  ) - 0.5;
  vec2 scene = screen + movement * 0.012 + microMovement * 0.004;
  scene += u_pointer * vec2(0.018, 0.012);
  vec2 hover = vec2(u_hover.x, 1.0 - u_hover.y);
  scene -= (hover - 0.5) * u_hoverAmount * 0.018;

  float lobeA = softEllipse(scene, vec2(0.309 + sin(seconds * 0.12) * 0.016, 0.159), vec2(0.495, 0.379), -0.265);
  float lobeB = softEllipse(scene, vec2(0.816, 0.291 + cos(seconds * 0.10) * 0.014), vec2(0.395, 0.282), 0.191);
  float lobeC = softEllipse(scene, vec2(1.032 + sin(seconds * 0.08) * 0.012, 0.492), vec2(0.691, 0.342), -0.514);
  float lobeD = softEllipse(scene, vec2(0.182, 0.654 + sin(seconds * 0.075) * 0.012), vec2(0.666, 0.371), 0.531);
  float lobeE = softEllipse(scene, vec2(0.356 + cos(seconds * 0.09) * 0.011, 0.452), vec2(0.446, 0.243), -0.444);
  float lobeF = softEllipse(scene, vec2(0.783, 0.816 + sin(seconds * 0.065) * 0.010), vec2(0.534, 0.231), -0.374);
  float lobeG = softEllipse(scene, vec2(0.004, 1.114), vec2(0.627, 0.197), -0.873);

  color = mix(color, vec3(0.612, 0.829, 0.994), lobeA * 0.62);
  color = mix(color, vec3(0.352, 0.726, 1.0), lobeB * 0.56);
  color = mix(color, vec3(0.647, 0.893, 1.0), lobeC * 0.48);
  color = mix(color, vec3(0.934, 0.980, 1.0), lobeD * 0.50);
  color = mix(color, vec3(0.982, 0.992, 1.0), lobeE * 0.58);
  color = mix(color, vec3(0.890, 0.954, 0.996), lobeF * 0.36);
  color = mix(color, vec3(0.382, 0.741, 0.976), lobeG * 0.30);

  float billow = fieldNoise(scene * 2.18 + vec2(seconds * 0.024, -seconds * 0.019));
  float volume = (lobeA * 0.40 + lobeB * 0.66 + lobeC * 0.45 + lobeG * 0.32) * (billow - 0.49);
  color += vec3(0.020, 0.068, 0.108) * volume;

  vec2 pointerDistance = (screen - hover) * vec2(u_resolution.x / u_resolution.y, 1.0);
  float hoverHalo = exp(-dot(pointerDistance, pointerDistance) / 0.19) * u_hoverAmount;
  float hoverShade = exp(-dot(pointerDistance * vec2(0.72, 1.18), pointerDistance * vec2(0.72, 1.18)) / 0.060) * u_hoverAmount;
  color = mix(color, vec3(0.94, 0.986, 1.0), hoverHalo * 0.13);
  color += vec3(0.018, 0.068, 0.112) * hoverShade * (0.42 + billow * 0.48);

  // Sparse light motes remain subordinate to the volumes and gain only a small pointer response.
  vec2 moteScene = scene + vec2(seconds * 0.00085, -seconds * 0.00052);
  float edgeDensity = clamp(lobeB * 0.48 + lobeC * 0.28 + lobeD * 0.18 + 0.10, 0.0, 0.78);
  float fineMotes = mote(moteScene, vec2(252.0, 166.0), 2.7, 0.145);
  float brightMotes = mote(moteScene + vec2(3.4, 0.9), vec2(132.0, 88.0), 7.3, 0.105);
  float pointerMotes = mote(moteScene + u_pointer * 0.022, vec2(188.0, 124.0), 13.9, 0.118) * hoverHalo;
  color += vec3(1.0) * (fineMotes * 0.16 + brightMotes * 0.22 + pointerMotes * 0.16) * edgeDensity;

  vec2 rippleVector = screen - vec2(u_ripple.x, 1.0 - u_ripple.y);
  rippleVector.x *= u_resolution.x / u_resolution.y;
  float rippleAge = max(0.0, u_time - u_ripple.z) * 0.001;
  float rippleDistance = length(rippleVector);
  float ripple = sin(rippleDistance * 31.0 - rippleAge * 7.2) * exp(-rippleDistance * 5.6) * exp(-rippleAge * 2.2);
  color += ripple * vec3(0.055, 0.115, 0.170) * u_ripple.w;

  // Two uncorrelated samples create a balanced, fine film grain without patterned bands.
  float grainA = hash21(gl_FragCoord.xy + floor(seconds * 31.0) * 17.0);
  float grainB = hash21(gl_FragCoord.yx + floor(seconds * 37.0) * 23.0 + 41.0);
  color += (grainA + grainB - 1.0) * (1.25 / 255.0);
  outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}`

const particleVertexShader = `#version 300 es
precision highp float;

in vec4 a_particle;
uniform float u_time;
uniform vec2 u_pointer;
uniform vec2 u_hover;
uniform float u_hoverAmount;
uniform float u_aspect;
out float v_alpha;

void main() {
  float phase = a_particle.w;
  float depth = a_particle.z;
  vec2 position = a_particle.xy;

  // The scene uses shallow, undulating depth layers. Keeping the layer in
  // the vertex data gives each mote its own parallax and hover response without
  // adding a second buffer or copying any external artwork.
  float surface = sin(position.x * 8.0 + phase + u_time * 0.00010) * 0.55;
  surface += cos(position.y * 6.5 - phase * 1.7 + u_time * 0.00008) * 0.45;
  position.y += surface * (0.004 + depth * 0.010);
  position.x += sin(u_time * 0.00011 + phase) * (0.006 + depth * 0.010);

  // Cursor movement shifts layers by different amounts, making the parallax
  // response visible even when no button is held.
  position += u_pointer * vec2(0.040 + depth * 0.036, 0.028 + depth * 0.026);

  vec2 pointerDelta = (position - u_hover) * vec2(u_aspect, 1.0);
  float distanceToPointer = length(pointerDelta);
  float reach = 0.42;
  float falloff = 1.0 - smoothstep(0.0, reach, distanceToPointer);
  float influence = falloff * falloff * u_hoverAmount;
  vec2 radial = normalize(pointerDelta + vec2(0.00001));
  vec2 outward = radial * vec2(1.0 / max(u_aspect, 0.001), 1.0);
  vec2 tangent = vec2(-outward.y, outward.x);
  float circulation = sin(phase * 1.37 + u_time * 0.00042);

  // Hover is a radial push with a small depth-dependent swirl. It also changes
  // focus and point size, so the cursor response remains visible during slow
  // movement instead of depending on event frequency or a click ripple.
  position += outward * influence * (0.014 + depth * 0.034);
  position += tangent * influence * circulation * (0.005 + depth * 0.012);
  gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
  gl_PointSize = 0.72 + depth * 1.85 + influence * (0.90 + depth * 2.55);
  v_alpha = 0.026 + depth * 0.102 + influence * (0.075 + depth * 0.205);
}`

const particleFragmentShader = `#version 300 es
precision mediump float;

in float v_alpha;
out vec4 outColor;

void main() {
  vec2 point = gl_PointCoord - 0.5;
  float distanceToCenter = length(point);
  float core = 1.0 - smoothstep(0.10, 0.43, distanceToCenter);
  float glow = 1.0 - smoothstep(0.02, 0.50, distanceToCenter);
  outColor = vec4(0.95, 0.988, 1.0, (core * 0.74 + glow * 0.26) * v_alpha);
}`

function compileShader(gl, type, source) {
  const shader = gl.createShader(type)
  gl.shaderSource(shader, source)
  gl.compileShader(shader)
  if (gl.getShaderParameter(shader, gl.COMPILE_STATUS)) return shader
  const details = gl.getShaderInfoLog(shader)
  gl.deleteShader(shader)
  throw new Error(details || 'WebGL shader compilation failed.')
}

function createProgram(gl, vertexSource, fragmentSource) {
  const program = gl.createProgram()
  const vertex = compileShader(gl, gl.VERTEX_SHADER, vertexSource)
  const fragment = compileShader(gl, gl.FRAGMENT_SHADER, fragmentSource)
  gl.attachShader(program, vertex)
  gl.attachShader(program, fragment)
  gl.linkProgram(program)
  gl.deleteShader(vertex)
  gl.deleteShader(fragment)
  if (gl.getProgramParameter(program, gl.LINK_STATUS)) return program
  const details = gl.getProgramInfoLog(program)
  gl.deleteProgram(program)
  throw new Error(details || 'WebGL program linking failed.')
}

function createParticles(count) {
  let state = 0x8f40c8
  const random = () => {
    state = (state * 1664525 + 1013904223) >>> 0
    return state / 4294967296
  }
  const particles = new Float32Array(count * 4)
  for (let index = 0; index < count; index += 1) {
    const offset = index * 4
    const layer = index % 4
    const layerOffset = layer * 0.012
    const rightBiased = Math.pow(random(), 0.78)
    const lowerBiased = Math.pow(random(), 0.93)
    particles[offset] = Math.min(1.04, rightBiased + (random() - 0.5) * 0.08 + layerOffset)
    particles[offset + 1] = Math.min(1.04, lowerBiased + (random() - 0.5) * 0.08 - layerOffset * 0.6)
    particles[offset + 2] = 0.14 + (layer / 3) * 0.62 + random() * 0.24
    particles[offset + 3] = random() * Math.PI * 2
  }
  return particles
}

function prefersReducedMotion() {
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

export function createBlueField(canvas, host) {
  const gl = canvas.getContext('webgl2', { alpha: false, antialias: false, powerPreference: 'high-performance' })
  if (!gl) return { move() {}, reset() {}, ripple() {}, dispose() {} }

  let fieldProgram
  let particleProgram
  let quadBuffer
  let particleBuffer
  let observer
  let frame = 0
  let disposed = false
  let width = 1
  let height = 1
  let resizePending = true
  const pointer = {
    x: 0,
    y: 0,
    targetX: 0,
    targetY: 0,
    hoverX: 0.54,
    hoverY: 0.5,
    targetHoverX: 0.54,
    targetHoverY: 0.5,
    amount: 0,
    inside: false
  }
  const ripple = { x: 0.5, y: 0.45, started: -100000, intensity: 0 }

  try {
    fieldProgram = createProgram(gl, vertexShader, fieldShader)
    particleProgram = createProgram(gl, particleVertexShader, particleFragmentShader)
    quadBuffer = gl.createBuffer()
    gl.bindBuffer(gl.ARRAY_BUFFER, quadBuffer)
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW)
    particleBuffer = gl.createBuffer()
    gl.bindBuffer(gl.ARRAY_BUFFER, particleBuffer)
    gl.bufferData(gl.ARRAY_BUFFER, createParticles(PARTICLE_COUNT), gl.STATIC_DRAW)
  } catch {
    return { move() {}, reset() {}, ripple() {}, dispose() {} }
  }

  const resize = () => {
    const bounds = canvas.getBoundingClientRect()
    width = Math.max(1, Math.round(bounds.width))
    height = Math.max(1, Math.round(bounds.height))
    const pixelBudget = window.matchMedia('(pointer: coarse)').matches ? MAX_COARSE_PIXELS : MAX_NORMAL_PIXELS
    const requestedDpr = Math.min(window.devicePixelRatio || 1, 1.5)
    const dpr = Math.min(requestedDpr, Math.sqrt(pixelBudget / (width * height)))
    const pixelWidth = Math.max(1, Math.round(width * dpr))
    const pixelHeight = Math.max(1, Math.round(height * dpr))
    // Assigning either dimension clears the drawing buffer. Resize only as part of a frame
    // that immediately paints the replacement, never between two animation callbacks.
    if (canvas.width !== pixelWidth) canvas.width = pixelWidth
    if (canvas.height !== pixelHeight) canvas.height = pixelHeight
    gl.viewport(0, 0, canvas.width, canvas.height)
  }

  const uniform = (program, name) => gl.getUniformLocation(program, name)
  const fieldUniforms = {
    resolution: uniform(fieldProgram, 'u_resolution'), time: uniform(fieldProgram, 'u_time'),
    pointer: uniform(fieldProgram, 'u_pointer'), hover: uniform(fieldProgram, 'u_hover'),
    hoverAmount: uniform(fieldProgram, 'u_hoverAmount'), ripple: uniform(fieldProgram, 'u_ripple')
  }
  const particleUniforms = {
    time: uniform(particleProgram, 'u_time'), pointer: uniform(particleProgram, 'u_pointer'),
    hover: uniform(particleProgram, 'u_hover'), hoverAmount: uniform(particleProgram, 'u_hoverAmount'),
    aspect: uniform(particleProgram, 'u_aspect')
  }

  let previousTime = performance.now()
  const render = (time) => {
    if (disposed) return
    if (resizePending) { resize(); resizePending = false }
    const delta = Math.min(0.08, Math.max(0, (time - previousTime) * 0.001))
    previousTime = time
    const pointerEase = 1 - Math.exp(-delta * 6.2)
    const amountEase = 1 - Math.exp(-delta * (pointer.inside ? 5.5 : 2.0))
    pointer.hoverX += (pointer.targetHoverX - pointer.hoverX) * pointerEase
    pointer.hoverY += (pointer.targetHoverY - pointer.hoverY) * pointerEase
    pointer.amount += ((pointer.inside ? 1 : 0) - pointer.amount) * amountEase
    const targetX = pointer.inside ? pointer.targetHoverX - 0.5 : 0
    const targetY = pointer.inside ? pointer.targetHoverY - 0.5 : 0
    pointer.x += (targetX - pointer.x) * pointerEase
    pointer.y += (targetY - pointer.y) * pointerEase
    gl.disable(gl.DEPTH_TEST)
    gl.disable(gl.BLEND)
    gl.useProgram(fieldProgram)
    gl.bindBuffer(gl.ARRAY_BUFFER, quadBuffer)
    const position = gl.getAttribLocation(fieldProgram, 'a_position')
    gl.enableVertexAttribArray(position)
    gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0)
    gl.uniform2f(fieldUniforms.resolution, canvas.width, canvas.height)
    gl.uniform1f(fieldUniforms.time, time)
    gl.uniform2f(fieldUniforms.pointer, pointer.x, pointer.y)
    gl.uniform2f(fieldUniforms.hover, pointer.hoverX, pointer.hoverY)
    gl.uniform1f(fieldUniforms.hoverAmount, pointer.amount)
    gl.uniform4f(fieldUniforms.ripple, ripple.x, ripple.y, ripple.started, ripple.intensity)
    gl.drawArrays(gl.TRIANGLES, 0, 3)

    gl.useProgram(particleProgram)
    gl.bindBuffer(gl.ARRAY_BUFFER, particleBuffer)
    const particle = gl.getAttribLocation(particleProgram, 'a_particle')
    gl.enableVertexAttribArray(particle)
    gl.vertexAttribPointer(particle, 4, gl.FLOAT, false, 0, 0)
    gl.enable(gl.BLEND)
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA)
    gl.uniform1f(particleUniforms.time, time)
    gl.uniform2f(particleUniforms.pointer, pointer.x, pointer.y)
    gl.uniform2f(particleUniforms.hover, pointer.hoverX, pointer.hoverY)
    gl.uniform1f(particleUniforms.hoverAmount, pointer.amount)
    gl.uniform1f(particleUniforms.aspect, width / height)
    gl.drawArrays(gl.POINTS, 0, PARTICLE_COUNT)
    gl.disable(gl.BLEND)

    if (!prefersReducedMotion()) frame = requestAnimationFrame(render)
  }

  observer = new ResizeObserver(() => {
    resizePending = true
    if (prefersReducedMotion()) render(performance.now())
  })
  observer.observe(canvas)
  render(performance.now())

  return {
    move(x, y) {
      if (prefersReducedMotion()) return
      pointer.targetHoverX = x
      pointer.targetHoverY = y
      if (!pointer.inside && pointer.amount < 0.01) {
        pointer.hoverX = x
        pointer.hoverY = y
      }
      pointer.inside = true
    },
    reset() {
      pointer.targetHoverX = 0.54
      pointer.targetHoverY = 0.5
      pointer.inside = false
    },
    ripple(x, y) {
      if (prefersReducedMotion()) return
      ripple.x = x
      ripple.y = y
      ripple.started = performance.now()
      ripple.intensity = 1
    },
    dispose() {
      disposed = true
      cancelAnimationFrame(frame)
      observer?.disconnect()
      gl.deleteBuffer(quadBuffer)
      gl.deleteBuffer(particleBuffer)
      gl.deleteProgram(fieldProgram)
      gl.deleteProgram(particleProgram)
    }
  }
}
