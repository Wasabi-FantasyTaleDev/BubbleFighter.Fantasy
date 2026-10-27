<script setup>
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = defineProps({ modelValue: { type: String, required: true }, items: { type: Array, required: true }, label: String })
const emit = defineEmits(['update:modelValue'])
const root = ref(null)
const indicator = ref({ width: '0px', transform: 'translateX(0px)', opacity: 0 })
let observer
let disposed = false
function measure() {
  if (disposed) return
  const button = [...(root.value?.querySelectorAll('button') || [])].find(element => element.dataset.value === props.modelValue)
  indicator.value = button
    ? { width: `${button.offsetWidth}px`, transform: `translateX(${button.offsetLeft}px)`, opacity: 1 }
    : { ...indicator.value, opacity: 0 }
}
function navigate(event) {
  const buttons = [...root.value.querySelectorAll('button')]
  const index = buttons.indexOf(event.target)
  if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
  event.preventDefault()
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
    : (index + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length
  buttons[next]?.focus({ preventScroll: true })
  if (buttons[next]) emit('update:modelValue', buttons[next].dataset.value)
}
watch(() => [props.modelValue, props.items], () => nextTick(measure), { deep: true })
onMounted(() => {
  observer = new ResizeObserver(measure)
  observer.observe(root.value)
  measure()
  document.fonts.ready.then(measure)
})
onBeforeUnmount(() => { disposed = true; observer?.disconnect() })
</script>

<template>
  <div ref="root" class="store-tabs" role="group" :aria-label="label" @keydown="navigate">
    <button v-for="item in items" :key="item.id" type="button" :data-value="item.id" :aria-pressed="modelValue === item.id"
      @click="emit('update:modelValue', item.id)">{{ item.label }}<span v-if="item.count != null" class="tab-count">{{ item.count }}</span></button>
    <span class="store-tabs-indicator-clip" aria-hidden="true"><span class="store-tabs-indicator" :style="indicator" /></span>
  </div>
</template>

<style scoped>
.store-tabs { position: relative; display: inline-flex; gap: 22px; min-width: 0; max-width: 100%; padding-bottom: 3px; overflow-x: auto; overflow-y: hidden; scrollbar-width: none; }
.store-tabs::-webkit-scrollbar { display: none; }
.store-tabs button { flex: none; display: flex; align-items: center; gap: 8px; border: 0; background: none; padding: 8px 0; min-height: 38px; color: var(--text-muted); font: inherit; cursor: pointer; white-space: nowrap; transition: color 160ms; }
.store-tabs button[aria-pressed='true'] { color: var(--text); }
.store-tabs button:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; border-radius: 4px; }
.tab-count { font-size: var(--text-caption); color: var(--text-muted); font-variant-numeric: tabular-nums; }
.store-tabs-indicator-clip { position: absolute; inset: 0; overflow: hidden; pointer-events: none; }
.store-tabs-indicator { position: absolute; left: 0; bottom: 0; height: 3px; border-radius: 3px; background: var(--accent); pointer-events: none; transition: transform 240ms cubic-bezier(.2,.8,.2,1), width 240ms cubic-bezier(.2,.8,.2,1), opacity 120ms; }
@media (prefers-reduced-motion: reduce) { .store-tabs-indicator, .store-tabs button { transition: none; } }
</style>
