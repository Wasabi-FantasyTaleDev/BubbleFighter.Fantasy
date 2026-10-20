<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, useId, watch } from 'vue'

const props = withDefaults(defineProps<{ open: boolean; label: string; align?: 'center' | 'right'; hover?: boolean }>(), { align: 'center', hover: true })
const emit = defineEmits<{ 'update:open': [value: boolean] }>()
const trigger = ref<HTMLButtonElement>()
const popup = ref<HTMLElement>()
const popupId = `header-dropdown-${useId()}`
const position = ref({ left: '0px', top: '0px' })
let closeTimer: ReturnType<typeof setTimeout> | undefined

function cancelClose() { clearTimeout(closeTimer) }
function close() { cancelClose(); emit('update:open', false) }
function scheduleClose(event: PointerEvent) {
  if (event.pointerType !== 'mouse') return
  cancelClose()
  // Allow crossing the gap from the trigger to its floating panel.
  closeTimer = setTimeout(close, 140)
}
function hoverOpen(event: PointerEvent) {
  cancelClose()
  if (props.hover && event.pointerType === 'mouse') emit('update:open', true)
}
function place() {
  if (!trigger.value || !popup.value) return
  const rect = trigger.value.getBoundingClientRect()
  if (!rect.width) { close(); return }
  const width = popup.value.offsetWidth
  const left = props.align === 'right' ? rect.right - width : rect.left + (rect.width - width) / 2
  position.value = { left: `${Math.max(8, Math.min(innerWidth - width - 8, left))}px`, top: `${rect.bottom + 10}px` }
}
function links() { return Array.from(popup.value?.querySelectorAll<HTMLAnchorElement>('a[href]') || []) }
async function openAndFocus(last = false) {
  cancelClose()
  emit('update:open', true)
  await nextTick()
  const items = links()
  ;(last ? items.at(-1) : items[0])?.focus()
}
function triggerKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp' || (event.key === 'Tab' && !event.shiftKey && props.open)) {
    event.preventDefault()
    openAndFocus(event.key === 'ArrowUp')
  } else if (event.key === 'Escape') close()
}
function popupKeydown(event: KeyboardEvent) {
  const items = links(), current = items.indexOf(document.activeElement as HTMLAnchorElement)
  if (event.key === 'Escape') { event.preventDefault(); close(); trigger.value?.focus() }
  else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
    event.preventDefault()
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length
    items[index]?.focus()
  } else if (event.key === 'Tab' && ((event.shiftKey && current === 0) || (!event.shiftKey && current === items.length - 1))) {
    // Teleport changes DOM order; return Tab to the trigger's logical position.
    const controls = Array.from(document.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), summary, input, [tabindex="0"]'))
      .filter(el => !popup.value?.contains(el) && !el.closest('[inert]') && el.getClientRects().length > 0)
    const target = event.shiftKey ? trigger.value : controls[controls.indexOf(trigger.value!) + 1]
    if (target) { event.preventDefault(); close(); target.focus() }
  }
}
function focusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!trigger.value?.contains(next) && !popup.value?.contains(next)) close()
}
function outsidePointer(event: PointerEvent) {
  if (!trigger.value?.contains(event.target as Node) && !popup.value?.contains(event.target as Node)) close()
}
function scroll(event: Event) {
  if (!popup.value?.contains(event.target as Node)) close()
}
watch(() => props.open, async open => { cancelClose(); if (open) { await nextTick(); place() } })
onMounted(() => {
  document.addEventListener('pointerdown', outsidePointer)
  window.addEventListener('resize', place)
  window.addEventListener('scroll', scroll, true)
})
onUnmounted(() => {
  cancelClose()
  document.removeEventListener('pointerdown', outsidePointer)
  window.removeEventListener('resize', place)
  window.removeEventListener('scroll', scroll, true)
})
</script>

<template>
  <div class="header-dropdown" @pointerenter="hoverOpen" @pointerleave="scheduleClose">
    <button ref="trigger" class="header-dropdown-trigger" :aria-label="label" :aria-expanded="open" :aria-controls="popupId"
      @click="emit('update:open', !open)" @keydown="triggerKeydown" @focusout="focusOut"><slot name="trigger" /></button>
    <ClientOnly>
      <!-- The navbar is already a backdrop root. Mount outside it so the panel can
           blur the page beneath it, including content below the navbar bounds. -->
      <Teleport to="body">
        <Transition name="header-popover">
          <div v-if="open" :id="popupId" ref="popup" class="header-popover" :style="position" :aria-label="label"
            @pointerenter="cancelClose" @pointerleave="scheduleClose" @focusout="focusOut" @keydown="popupKeydown" @click="close">
            <slot />
          </div>
        </Transition>
      </Teleport>
    </ClientOnly>
  </div>
</template>

<style>
.header-dropdown { position: relative; }
.header-dropdown-trigger { display:inline-flex; align-items:center; gap:6px; min-height:38px; padding:0 12px; border:0; border-radius:999px; color:var(--ff-ink); background:transparent; font:inherit; font-size:13px; font-weight:500; cursor:pointer; white-space:nowrap; transition:background .15s; }
.header-dropdown-trigger:hover, .header-dropdown-trigger[aria-expanded='true'] { background:var(--ff-hover); }
.header-dropdown-trigger svg { width:14px; height:14px; transition:transform .18s var(--motion-ease); }
.header-dropdown-trigger[aria-expanded='true'] svg:last-child { transform:rotate(180deg); }
.header-popover {
  position:fixed;
  z-index:43;
  min-width:190px;
  max-width:calc(100vw - 16px);
  max-height:calc(100dvh - 100px);
  overflow-y:auto;
  padding:7px;
  border:1px solid rgba(255,255,255,.85);
  border-radius:16px;
  background:linear-gradient(145deg,rgba(255,255,255,.78),rgba(248,250,253,.6));
  -webkit-backdrop-filter:blur(24px) saturate(1.5);
  backdrop-filter:blur(24px) saturate(1.5);
  box-shadow:inset 0 1px 0 rgba(255,255,255,.9),0 0 0 1px rgba(24,35,48,.05),0 12px 32px rgba(24,35,48,.13),0 2px 6px rgba(24,35,48,.04);
  transform-origin:top center;
}
.header-popover a { display:flex; align-items:center; justify-content:space-between; gap:16px; min-height:38px; padding:9px 12px; border-radius:10px; color:var(--ff-ink); font-size:13px; line-height:1.5; text-decoration:none; transition:background .15s; }
.header-popover a:hover, .header-popover a:focus-visible, .header-popover a[aria-current='true'] { background:rgba(80,100,125,.09); }
.header-popover a:focus-visible { outline-offset:-2px; }
.dark .header-popover { border-color:rgba(255,255,255,.16); background:linear-gradient(145deg,rgba(49,52,57,.8),rgba(29,31,35,.7)); box-shadow:inset 0 1px 0 rgba(255,255,255,.12),0 12px 32px rgba(0,0,0,.3),0 2px 6px rgba(0,0,0,.14); }
.dark .header-popover a:hover, .dark .header-popover a:focus-visible, .dark .header-popover a[aria-current='true'] { background:rgba(255,255,255,.09); }
.header-popover-enter-active, .header-popover-leave-active { transition:opacity .16s ease,transform .16s var(--motion-ease); }
.header-popover-enter-from, .header-popover-leave-to { opacity:0; transform:translateY(-4px) scale(.98); }
@supports not ((backdrop-filter:blur(1px)) or (-webkit-backdrop-filter:blur(1px))) {
  .header-popover { background:#f8fafc; }
  .dark .header-popover { background:#26282c; }
}
</style>
