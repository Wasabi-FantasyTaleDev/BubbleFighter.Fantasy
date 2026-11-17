<script setup lang="ts" generic="T extends string">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import TpIcon from './TpIcon.vue'

// A segmented switch whose highlight slides to the chosen option instead of jumping. The highlight
// is measured from the chosen button, so options of different widths and languages line up.
const props = defineProps<{ options: { id: T; label: string; icon: string; fill?: boolean }[]; label: string }>()
const model = defineModel<T>({ required: true })
const root = ref<HTMLElement>()
const thumb = ref({ x: 0, w: 0, ready: false })

function measure() {
  const button = root.value?.querySelector<HTMLElement>('button[aria-selected="true"]')
  if (!button) return
  thumb.value = { x: button.offsetLeft, w: button.offsetWidth, ready: thumb.value.ready || thumb.value.w > 0 }
}
let observer: ResizeObserver | undefined
onMounted(() => {
  nextTick(measure)
  // Fonts and translations change the widths after mounting; follow them.
  observer = new ResizeObserver(measure)
  if (root.value) observer.observe(root.value)
})
onBeforeUnmount(() => observer?.disconnect())
watch(() => [model.value, props.options.map(o => o.label).join()], () => nextTick(measure))

function keydown(event: KeyboardEvent) {
  const n = props.options.findIndex(o => o.id === model.value)
  const next = event.key === 'ArrowRight' ? n + 1 : event.key === 'ArrowLeft' ? n - 1 : -1
  if (next < 0 || next >= props.options.length) return
  event.preventDefault()
  model.value = props.options[next].id
  nextTick(() => root.value?.querySelector<HTMLElement>('button[aria-selected="true"]')?.focus())
}
</script>

<template>
  <div ref="root" class="tp-switch" role="tablist" :aria-label="label" @keydown="keydown">
    <span class="tp-switch-thumb" :class="{ 'is-ready': thumb.ready }" :style="{ width: `${thumb.w}px`, transform: `translateX(${thumb.x}px)` }" aria-hidden="true" />
    <button v-for="item in options" :key="item.id" type="button" role="tab" :aria-selected="model === item.id" :tabindex="model === item.id ? 0 : -1" :class="{ 'is-on': model === item.id }" @click="model = item.id">
      <TpIcon :name="item.icon" :size="15" :fill="item.fill" />{{ item.label }}
    </button>
  </div>
</template>
