<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch, type PropType } from 'vue'
import { useI18n } from 'vue-i18n'
import TpIcon from './kit/TpIcon.vue'
import PromptWizard from './PromptWizard.vue'
import { scenes } from './registry'
import { clamp, easeInOut } from './cue'
import { STAGE, type CameraPoint, type Code, type CursorPoint, type Tutorial } from './types'

const props = defineProps({ tutorial: { type: Object as PropType<Tutorial>, required: true }, lang: { type: String as PropType<Code>, required: true }, credit: { type: String, default: '' } })
const { t: label } = useI18n()

const index = ref(0)
const time = ref(0)
const playing = ref(false)
const run = ref(0)
const reduced = ref(false)
const narrow = ref(false)
const still = ref(false)
const asking = ref(false)
const root = ref<HTMLElement>()
const viewport = ref<HTMLElement>()
const world = ref<HTMLElement>()
const cursor = ref<HTMLElement>()

const steps = computed(() => props.tutorial.steps)
const step = computed(() => steps.value[index.value])
const duration = computed(() => step.value.duration)
const ended = computed(() => time.value >= duration.value)
const scene = computed(() => scenes[step.value.scene])
const caption = computed(() => {
  const list = step.value.captions
  let current = -1
  for (let i = 0; i < list.length; i++) if (time.value >= list[i].at) current = i
  return current < 0 ? null : { key: `${index.value}-${current}`, text: list[current].text[props.lang] }
})
const prompt = computed(() => props.tutorial.prompt)

// --- clock ---------------------------------------------------------------------------------
let frame = 0, last = 0
function tick(now: number) {
  if (!playing.value) return
  // A hidden tab pauses requestAnimationFrame; cap the step so the scene never jumps ahead.
  time.value = Math.min(duration.value, time.value + Math.min(64, now - last))
  last = now
  if (time.value >= duration.value) playing.value = false
  else frame = requestAnimationFrame(tick)
}
function play() {
  if (reduced.value) { time.value = duration.value; return }
  // A finished step holds its last frame like a video; only Replay or another step starts it again.
  if (ended.value) return
  playing.value = true
  last = performance.now()
  cancelAnimationFrame(frame)
  frame = requestAnimationFrame(tick)
}
function pause() { playing.value = false; cancelAnimationFrame(frame) }
function toggle() { if (playing.value) pause(); else if (!ended.value) play() }
function restart() { run.value++; time.value = 0 }
function go(target: number) {
  if (target < 0 || target >= steps.value.length) return
  pause()
  carryOver()
  index.value = target
  run.value++
  time.value = reduced.value ? duration.value : 0
  nextTick(play)
}
function replay() { pause(); carryOver(); restart(); nextTick(play) }

// --- camera and pointer --------------------------------------------------------------------
let scale = 1
// Where the camera was on the last frame. A new step or a replay starts from there and glides
// into its own first shot instead of cutting to it.
type Shot = { x: number; y: number; z: number }
let lastCamera: Shot | null = null
let carry: Shot | null = null
const CARRY_MS = 1100
// Time constant of the camera's easing toward its aim, in milliseconds.
const CAMERA_EASE_MS = 260
let eased: (Shot & { at: number }) | null = null
// The pointer carries over the same way, so a step change never teleports it.
type Hand = { x: number; y: number; visible: boolean }
let lastCursor: Hand | null = null
let cursorCarry: Hand | null = null
function carryOver() { carry = lastCamera; cursorCarry = lastCursor }
function locate(name: string) {
  const el = world.value?.querySelector<HTMLElement>(`[data-tp="${name}"]`)
  if (!el || !world.value) return null
  const r = el.getBoundingClientRect(), w = world.value.getBoundingClientRect()
  if (!r.width && !r.height) return null
  return { x: (r.left + r.width / 2 - w.left) / scale, y: (r.top + r.height / 2 - w.top) / scale }
}
const remembered = new Map<object, { x: number; y: number }>()
function place(point: CursorPoint | CameraPoint) {
  const found = point.target ? locate(point.target) : null
  const base = found ?? remembered.get(point) ?? { x: point.x ?? STAGE.width / 2, y: point.y ?? STAGE.height / 2 }
  remembered.set(point, base)
  const shift = point as CursorPoint
  return { x: base.x + (shift.dx ?? 0), y: base.y + (shift.dy ?? 0) }
}
function zoomOf(point: CameraPoint) { return narrow.value ? point.mz ?? Math.max(1.8, point.z ?? 1) : point.z ?? 1 }

function frameCamera() {
  if (!viewport.value || !world.value) return
  const points = step.value.camera, t = time.value
  let x = STAGE.width / 2, y = STAGE.height / 2, z = narrow.value ? 1.8 : 1
  if (points.length) {
    let i = 0
    for (let n = 0; n < points.length; n++) if (t >= points[n].at) i = n
    const target = place(points[i]), from = i > 0 ? place(points[i - 1]) : target
    const fromZ = i > 0 ? zoomOf(points[i - 1]) : zoomOf(points[i])
    const p = t < points[i].at ? 0 : easeInOut(clamp((t - points[i].at) / (points[i].dur ?? 900)))
    x = from.x + (target.x - from.x) * p
    y = from.y + (target.y - from.y) * p
    z = fromZ + (zoomOf(points[i]) - fromZ) * p
  }
  if (carry && !reduced.value) {
    const p = easeInOut(clamp(t / CARRY_MS))
    x = carry.x + (x - carry.x) * p
    y = carry.y + (y - carry.y) * p
    z = carry.z + (z - carry.z) * p
    if (t >= CARRY_MS) carry = null
  }
  // The aim itself can jump: a target found only once a new scene has mounted, or a target that
  // follows the newest console line. While playing, the camera eases toward its aim instead of
  // cutting to it; paused frames and reduced motion show the aim directly.
  const now = performance.now()
  if (eased && playing.value && !reduced.value) {
    const k = 1 - Math.exp(-Math.min(100, now - eased.at) / CAMERA_EASE_MS)
    x = eased.x + (x - eased.x) * k
    y = eased.y + (y - eased.y) * k
    z = eased.z + (z - eased.z) * k
  }
  eased = { x, y, z, at: now }
  lastCamera = { x, y, z }
  const W = viewport.value.clientWidth, H = viewport.value.clientHeight
  scale = (W / STAGE.width) * z
  const vw = W / scale, vh = H / scale
  const ox = vw >= STAGE.width ? (STAGE.width - vw) / 2 : clamp(x - vw / 2, 0, STAGE.width - vw)
  const oy = vh >= STAGE.height ? (STAGE.height - vh) / 2 : clamp(y - vh / 2, 0, STAGE.height - vh)
  world.value.style.transform = `scale(${scale}) translate(${-ox}px, ${-oy}px)`
}

const clicks = computed(() => step.value.cursor.flatMap(p => (p.click ? (p.double ? [p.at, p.at + 170] : [p.at]) : [])))
const ripples = computed(() => clicks.value.filter(at => time.value >= at && time.value < at + 650).map(at => (time.value - at) / 650))

function frameCursor() {
  const el = cursor.value, points = step.value.cursor, t = time.value
  if (!el) return
  if (!points.length || reduced.value) { el.style.opacity = '0'; return }
  let i = -1
  for (let n = 0; n < points.length; n++) if (t >= points[n].at) i = n
  const first = points[0]
  let pos: { x: number; y: number }, visible = true
  if (i < 0) {
    pos = place(first)
    visible = t >= first.at - (first.move ?? 650)
  } else {
    pos = place(points[i])
    visible = !points[i].hide
    const next = points[i + 1]
    if (next) {
      const move = next.move ?? 650, start = next.at - move
      if (t > start) {
        const to = place(next), p = easeInOut(clamp((t - start) / move))
        // A slight arc reads as a hand moving a mouse rather than a straight tween.
        const dx = to.x - pos.x, dy = to.y - pos.y, bend = Math.sin(Math.PI * p) * 0.08
        pos = { x: pos.x + dx * p - dy * bend, y: pos.y + dy * p + dx * bend }
        if (next.hide === false || (!next.hide && points[i].hide)) visible = p > 0.05
      }
    }
  }
  if (cursorCarry) {
    // Glide from where the previous step left the pointer to where this step wants it.
    const p = easeInOut(clamp(t / CARRY_MS))
    pos = { x: cursorCarry.x + (pos.x - cursorCarry.x) * p, y: cursorCarry.y + (pos.y - cursorCarry.y) * p }
    if (cursorCarry.visible) visible = true
    if (t >= CARRY_MS) cursorCarry = null
  }
  lastCursor = { x: pos.x, y: pos.y, visible }
  const pressed = clicks.value.some(at => t >= at - 60 && t < at + 110)
  el.style.opacity = visible ? '1' : '0'
  el.style.transform = `translate(${pos.x}px, ${pos.y}px) scale(${pressed ? 0.84 : 1})`
}

function render() { frameCamera(); frameCursor() }
watch([time, index, run, narrow], () => nextTick(render), { flush: 'post' })

// --- environment -----------------------------------------------------------------------------
let resize: ResizeObserver | undefined, reveal: IntersectionObserver | undefined, motion: MediaQueryList | undefined
function updateMotion() {
  reduced.value = !!motion?.matches
  if (reduced.value) { pause(); time.value = duration.value }
}
function keydown(event: KeyboardEvent) {
  const target = event.target as HTMLElement | null
  if (asking.value || event.altKey || event.ctrlKey || event.metaKey || target?.closest('input, textarea, select, [contenteditable="true"]')) return
  if (event.key === 'ArrowRight') { event.preventDefault(); go(index.value + 1) }
  else if (event.key === 'ArrowLeft') { event.preventDefault(); go(index.value - 1) }
  else if ((event.key === ' ' || event.key === 'k') && root.value?.contains(target)) { event.preventDefault(); toggle() }
}
// The prompt depends on the reader's setup, so the button opens a few questions first.
function askPrompt() { if (prompt.value) { pause(); asking.value = true } }
onMounted(() => {
  motion = window.matchMedia('(prefers-reduced-motion: reduce)')
  updateMotion()
  motion.addEventListener('change', updateMotion)
  resize = new ResizeObserver(() => { narrow.value = (viewport.value?.clientWidth ?? 1000) < 640; render() })
  if (viewport.value) resize.observe(viewport.value)
  // Start when the reader reaches the player, like a video set to autoplay.
  reveal = new IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting)) { reveal?.disconnect(); if (!reduced.value) play() }
  }, { threshold: 0.4 })
  if (viewport.value) reveal.observe(viewport.value)
  window.addEventListener('keydown', keydown)
  // A link such as #step-3 opens that step directly.
  const linked = /^#step-(\d+)$/.exec(location.hash)
  if (linked) { index.value = Math.min(steps.value.length, Math.max(1, Number(linked[1]))) - 1; if (reduced.value) time.value = duration.value }
  // Development builds expose a seek hook for checking individual frames.
  // `seek` shows settled frames (entrance animations skipped); `play` resumes normal playback.
  if (import.meta.env.DEV) (window as unknown as Record<string, unknown>).__tutorial = { go, seek: (ms: number) => { pause(); still.value = true; time.value = ms }, play: () => { still.value = false; play() } }
  render()
})
watch(index, value => { if (history.replaceState) history.replaceState(history.state, '', `#step-${value + 1}`) })
onUnmounted(() => {
  pause()
  resize?.disconnect(); reveal?.disconnect()
  motion?.removeEventListener('change', updateMotion)
  window.removeEventListener('keydown', keydown)
})
</script>

<template>
  <section ref="root" class="tp" :class="{ 'is-paused': !playing, 'is-reduced': reduced || still, 'is-narrow': narrow, 'is-ended': ended }" :aria-label="label('tutorial.player')">
    <header class="tp-head">
      <span class="tp-count">{{ label('tutorial.step') }} {{ index + 1 }} / {{ steps.length }}</span>
      <h2 class="tp-title">
        <Transition name="tp-swap" mode="out-in"><span :key="index">{{ step.title[lang] }}</span></Transition>
      </h2>
      <small v-if="credit" class="tp-head-credit">{{ credit }}</small>
    </header>

    <div ref="viewport" class="tp-viewport" :title="label('tutorial.keys')" @click="toggle">
      <div ref="world" class="tp-world" :data-lang="lang">
        <Transition name="tp-scene"><component :is="scene" :key="`${index}-${run}`" :t="time" :step="step" :lang="lang" /></Transition>
        <div ref="cursor" class="tp-cursor" aria-hidden="true">
          <span v-for="(p, n) in ripples" :key="n" class="tp-ripple" :style="{ transform: `translate(-50%, -50%) scale(${0.3 + p * 1.2})`, opacity: 1 - p }" />
          <svg width="26" height="26" viewBox="0 0 24 24"><path d="M4 2.5v17.2l4.6-4.3 2.9 6.6 2.9-1.3-2.9-6.5h6.3z" fill="#fff" stroke="#111" stroke-width="1.4" stroke-linejoin="round" /></svg>
        </div>
      </div>
      <Transition name="tp-fade">
        <button v-if="!playing && !ended && !reduced && !still" class="tp-big-play" :aria-label="label('tutorial.play')" @click.stop="play"><TpIcon name="play" :size="30" fill /></button>
      </Transition>
      <Transition name="tp-fade">
        <div v-if="ended && !reduced" class="tp-end" @click.stop>
          <button class="tp-end-button" @click="replay"><TpIcon name="replay" :size="18" />{{ label('tutorial.replay') }}</button>
          <button v-if="index < steps.length - 1" class="tp-end-button is-primary" @click="go(index + 1)">{{ label('tutorial.next') }}<TpIcon name="next" :size="18" /></button>
        </div>
      </Transition>
    </div>

    <!-- One control row under the picture: progress on top, then playback, the subtitle and navigation. -->
    <div class="tp-bar">
      <div class="tp-strip" role="tablist" :aria-label="label('tutorial.steps')">
        <button v-for="(item, n) in steps" :key="item.id" class="tp-segment" role="tab" :aria-selected="n === index" :aria-label="`${n + 1}. ${item.title[lang]}`" :title="`${n + 1}. ${item.title[lang]}`" @click="go(n)">
          <i><span :style="{ transform: `scaleX(${n < index ? 1 : n === index ? time / duration : 0})` }" /></i>
        </button>
      </div>
      <div class="tp-bar-row">
        <div class="tp-bar-start">
          <button class="tp-icon-button is-main" :aria-label="playing ? label('tutorial.pause') : label('tutorial.play')" :title="playing ? label('tutorial.pause') : label('tutorial.play')" @click="toggle"><TpIcon :name="playing ? 'pause' : 'play'" :size="16" :fill="!playing" :stroke="playing ? 2.4 : 1.7" /></button>
          <button class="tp-icon-button" :aria-label="label('tutorial.replay')" :title="label('tutorial.replay')" @click="replay"><TpIcon name="replay" :size="17" /></button>
        </div>
        <p class="tp-caption" aria-live="polite">
          <Transition name="tp-swap" mode="out-in"><span :key="caption?.key ?? 'none'">{{ caption?.text ?? '' }}</span></Transition>
        </p>
        <div class="tp-bar-end">
          <button v-if="prompt" class="tp-chip" :class="{ 'is-wanted': index === prompt.step }" :title="prompt.note[lang]" aria-haspopup="dialog" @click="askPrompt"><TpIcon name="copy" :size="15" />{{ label('tutorial.copy') }}</button>
          <button class="tp-icon-button" :disabled="index === 0" :aria-label="label('tutorial.previous')" :title="label('tutorial.previous')" @click="go(index - 1)"><TpIcon name="prev" :size="18" /></button>
          <button class="tp-chip is-next" :class="{ 'is-pulsing': ended && index < steps.length - 1 }" :disabled="index === steps.length - 1" @click="go(index + 1)">{{ label('tutorial.next') }}<TpIcon name="next" :size="16" /></button>
        </div>
      </div>
    </div>

    <Teleport to="body">
      <Transition name="tp-notice">
        <PromptWizard v-if="asking && prompt" :lang="lang" :agent="prompt.agent" @close="asking = false" />
      </Transition>
    </Teleport>
  </section>
</template>
