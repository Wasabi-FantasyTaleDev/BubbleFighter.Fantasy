<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, useId } from 'vue'

defineProps<{ href: string }>()

const surface = ref<HTMLAnchorElement>()
const displacementMap = ref('')
const filterId = `liquid-glass-${useId().replace(/[^a-zA-Z0-9_-]/g, '-')}`
const glassStyle = computed(() => displacementMap.value ? { '--liquid-glass-refraction': `url("#${filterId}")` } : undefined)
let observer: ResizeObserver | undefined
let lastSize = ''

function updateLens() {
  if (!surface.value) return
  // The map describes the capsule's edge, not the scene behind it. Backdrop filtering
  // leaves compositing of video opacity, masks and contrast layers to the browser.
  const width = Math.min(768, Math.max(1, surface.value.clientWidth))
  const height = Math.min(128, Math.max(1, surface.value.clientHeight))
  const size = `${width}:${height}`
  if (lastSize === size) return
  lastSize = size
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const context = canvas.getContext('2d')
  if (!context) return
  const pixels = context.createImageData(width, height)
  const radius = Math.min(width, height) / 2
  const rim = Math.min(10, radius)
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const px = x + 0.5
      const dx = px - Math.max(radius, Math.min(width - radius, px))
      const dy = y + 0.5 - height / 2
      const distance = Math.hypot(dx, dy)
      const depth = radius - distance
      const bend = depth > 0 && depth < rim ? Math.sin(Math.PI * depth / rim) : 0
      const offset = (y * width + x) * 4
      pixels.data[offset] = Math.round(128 + (distance ? dx / distance : 0) * bend * 100)
      pixels.data[offset + 1] = Math.round(128 + (distance ? dy / distance : 0) * bend * 100)
      pixels.data[offset + 2] = 128
      pixels.data[offset + 3] = 255
    }
  }
  context.putImageData(pixels, 0, 0)
  displacementMap.value = canvas.toDataURL()
}

onMounted(() => {
  updateLens()
  // Rebuild only on size changes, including translated labels and mobile layout.
  // There is no animation loop, video copy, DOM capture or WebGL context.
  observer = new ResizeObserver(updateLens)
  if (surface.value) observer.observe(surface.value)
})
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <a ref="surface" :href="href" class="secondary-action liquid-glass-plugin-action"
    :style="glassStyle">
    <svg class="liquid-glass-filter" width="0" height="0" aria-hidden="true" focusable="false">
      <defs>
        <filter :id="filterId" x="0" y="0" width="1" height="1" color-interpolation-filters="sRGB">
          <feImage :href="displacementMap || undefined" x="0" y="0" width="100%" height="100%" preserveAspectRatio="none" result="lens" />
          <feDisplacementMap in="SourceGraphic" in2="lens" scale="12" xChannelSelector="R" yChannelSelector="G" />
        </filter>
      </defs>
    </svg>
    <span class="liquid-glass-label"><slot /></span>
  </a>
</template>

<style scoped>
.liquid-glass-plugin-action {
  position: relative;
  isolation: isolate;
  overflow: hidden;
  color: var(--ff-ink);
  background: transparent;
  border-color: rgba(255, 255, 255, .72);
  box-shadow: inset 0 1px 1px rgba(255, 255, 255, .75), inset 0 -1px 1px rgba(0, 0, 0, .06), 0 2px 8px rgba(20, 30, 40, .09);
  text-shadow: 0 1px 1px rgba(255, 255, 255, .72);
}
.liquid-glass-plugin-action::before,
.liquid-glass-plugin-action::after {
  content: '';
  position: absolute;
  inset: 0;
  z-index: -1;
  border-radius: inherit;
  pointer-events: none;
}
.liquid-glass-plugin-action::before {
  background: rgba(255, 255, 255, .16);
  -webkit-backdrop-filter: blur(3px) saturate(1.15);
  backdrop-filter: blur(3px) saturate(1.15);
}
/* Keep the base glass independent: engines without SVG backdrop filters still
   render blur and highlights. The small rim lens only distorts composed pixels. */
.liquid-glass-plugin-action::after {
  -webkit-backdrop-filter: var(--liquid-glass-refraction, none);
  backdrop-filter: var(--liquid-glass-refraction, none);
  background: linear-gradient(155deg, rgba(255, 255, 255, .32), transparent 34%, transparent 68%, rgba(255, 255, 255, .12));
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, .16);
}
.liquid-glass-plugin-action:hover { background: transparent; }
.liquid-glass-plugin-action:hover::before { background: rgba(255, 255, 255, .24); }
.liquid-glass-plugin-action:active { transform: translateY(0) scale(.98); }
.liquid-glass-label { display: inline-flex; align-items: center; gap: 9px; }
.liquid-glass-filter { position: absolute; width: 0; height: 0; pointer-events: none; }
:global(.dark) .liquid-glass-plugin-action {
  border-color: rgba(255, 255, 255, .28);
  box-shadow: inset 0 1px 1px rgba(255, 255, 255, .3), inset 0 -1px 1px rgba(0, 0, 0, .3), 0 3px 10px rgba(0, 0, 0, .2);
  text-shadow: 0 1px 2px rgba(0, 0, 0, .75);
}
:global(.dark) .liquid-glass-plugin-action::before { background: rgba(255, 255, 255, .06); }
:global(.dark) .liquid-glass-plugin-action:hover::before { background: rgba(255, 255, 255, .1); }
:global(.dark) .liquid-glass-plugin-action::after {
  background: linear-gradient(155deg, rgba(255, 255, 255, .14), transparent 34%, transparent 68%, rgba(255, 255, 255, .05));
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, .05);
}
</style>
