<template>
  <Teleport to="body">
    <Transition name="noah-modal-fade">
      <div v-if="modelValue" class="noah-modal-overlay glass-backdrop" @click="handleOverlayClick">
        <div
          class="noah-modal-dialog glass-panel"
          :style="{ maxWidth: width, height: dialogHeight }"
          @click.stop
        >
          <div ref="content" class="noah-modal-content">
          <!-- Header -->
          <div v-if="title || $slots.title || showClose" class="noah-modal-header">
            <slot name="title">
              <h3 class="noah-modal-title">{{ title }}</h3>
            </slot>

            <button
              v-if="showClose"
              type="button"
              class="noah-modal-close-btn"
              :aria-label="closeLabel"
              @click="handleClose"
            >
              <NoahIcon name="close" :size="14" />
            </button>
          </div>

          <!-- Body Content -->
          <div class="noah-modal-body">
            <slot />
          </div>

          <!-- Footer -->
          <div v-if="$slots.footer" class="noah-modal-footer">
            <slot name="footer" />
          </div>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<script setup>
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  modelValue: {
    type: Boolean,
    default: false
  },
  title: {
    type: String,
    default: ''
  },
  width: {
    type: String,
    default: '540px'
  },
  closeLabel: {
    type: String,
    default: 'Close'
  },
  showClose: {
    type: Boolean,
    default: true
  },
  closeOnClickOverlay: {
    type: Boolean,
    default: true
  }
})

const emit = defineEmits(['update:modelValue', 'close'])
const content = ref(null)
const dialogHeight = ref(undefined)
let sizeObserver
let observation = 0

// Measure the unconstrained inner flow so changing forms can animate the outer height.
watch(() => props.modelValue, async open => {
  const mine = ++observation
  sizeObserver?.disconnect()
  dialogHeight.value = undefined
  if (!open) return
  await nextTick()
  if (mine !== observation || !content.value) return
  const measure = () => { if (mine === observation && content.value) dialogHeight.value = `${content.value.offsetHeight}px` }
  measure()
  sizeObserver = new ResizeObserver(measure)
  sizeObserver.observe(content.value)
}, { immediate: true })
onBeforeUnmount(() => { observation++; sizeObserver?.disconnect() })

const handleClose = () => {
  emit('update:modelValue', false)
  emit('close')
}

const handleOverlayClick = () => {
  if (props.closeOnClickOverlay) {
    handleClose()
  }
}
</script>

<style scoped>
.noah-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  background: rgb(15 23 42 / .26);
  backdrop-filter: blur(8px);
}

.noah-modal-dialog {
  position: relative;
  width: 100%;
  border: 1px solid rgb(255 255 255 / .7);
  border-radius: 22px;
  background: var(--surface);
  box-shadow: 0 1px 2px rgb(15 23 42 / .06), 0 24px 64px rgb(15 23 42 / .18);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  transition: height 240ms cubic-bezier(.2, .8, .2, 1), max-width 240ms cubic-bezier(.2, .8, .2, 1);
}

.noah-modal-content { display: flex; flex-direction: column; flex: none; min-width: 0; }

.noah-modal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 22px 20px 6px 26px;
}

.noah-modal-title {
  margin: 4px 0 0;
  font-size: 18px;
  font-weight: 600;
  letter-spacing: -.01em;
  line-height: 1.4;
  color: var(--text);
}

.noah-modal-close-btn {
  flex: none;
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  transition: background-color .15s ease, color .15s ease, transform .2s ease;
}
.noah-modal-close-btn:hover {
  background-color: var(--hover);
  color: var(--text);
}
.noah-modal-close-btn:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 2px; }

.noah-modal-body {
  padding: 12px 26px 22px;
  overflow-y: auto;
  max-height: calc(85vh - 140px);
  color: var(--text);
  font-size: var(--text-control);
  line-height: 1.7;
}

.noah-modal-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  padding: 4px 26px 24px;
}

/* Open: the backdrop fades and blurs in, the dialog pops up from slightly below. Close plays it back faster. */
.noah-modal-fade-enter-active { transition: opacity .24s ease, backdrop-filter .24s ease; }
.noah-modal-fade-leave-active { transition: opacity .18s ease, backdrop-filter .18s ease; }
.noah-modal-fade-enter-from,
.noah-modal-fade-leave-to {
  opacity: 0;
  backdrop-filter: blur(0);
}
.noah-modal-fade-enter-active .noah-modal-dialog {
  transition: transform .36s cubic-bezier(.2, .9, .25, 1.12), opacity .24s ease, height 240ms cubic-bezier(.2, .8, .2, 1), max-width 240ms cubic-bezier(.2, .8, .2, 1);
}
.noah-modal-fade-leave-active .noah-modal-dialog { transition: transform .18s cubic-bezier(.4, 0, 1, 1), opacity .18s ease; }
.noah-modal-fade-enter-from .noah-modal-dialog { transform: translateY(18px) scale(.94); opacity: 0; }
.noah-modal-fade-leave-to .noah-modal-dialog { transform: translateY(8px) scale(.97); opacity: 0; }
.noah-modal-fade-enter-active .noah-modal-body > *,
.noah-modal-fade-enter-active .noah-modal-footer { animation: noah-modal-rise .4s cubic-bezier(.2, .8, .2, 1) backwards; animation-delay: 70ms; }
.noah-modal-fade-enter-active .noah-modal-footer { animation-delay: 120ms; }
@keyframes noah-modal-rise { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: none; } }
@media (prefers-reduced-motion: reduce) {
  .noah-modal-dialog, .noah-modal-fade-enter-active .noah-modal-dialog, .noah-modal-fade-leave-active .noah-modal-dialog { transition: none; }
  .noah-modal-fade-enter-active .noah-modal-body > *, .noah-modal-fade-enter-active .noah-modal-footer { animation: none; }
}
</style>
