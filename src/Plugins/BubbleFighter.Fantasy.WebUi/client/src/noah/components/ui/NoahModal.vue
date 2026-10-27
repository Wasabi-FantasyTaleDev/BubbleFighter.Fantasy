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
}

.noah-modal-dialog {
  width: 100%;
  border-radius: var(--radius);
  box-shadow: var(--shadow-lg);
  overflow: hidden;
  display: flex;
  flex-direction: column;
  transition: height 220ms cubic-bezier(.2, .8, .2, 1), max-width 220ms cubic-bezier(.2, .8, .2, 1);
}

.noah-modal-content { display: flex; flex-direction: column; flex: none; min-width: 0; }

.noah-modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid var(--border-soft);
}

.noah-modal-title {
  margin: 0;
  font-size: var(--text-subtitle);
  font-weight: 600;
  color: var(--text);
}

.noah-modal-close-btn {
  border: none;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  padding: 4px;
  border-radius: var(--radius-xs);
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.12s ease;
}
.noah-modal-close-btn:hover {
  background-color: var(--hover);
  color: var(--text);
}

.noah-modal-body {
  padding: 20px;
  overflow-y: auto;
  max-height: calc(85vh - 120px);
}

.noah-modal-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 10px;
  padding: 14px 20px;
  border-top: 1px solid var(--border-soft);
  background-color: var(--surface-raised);
}

/* Transitions */
.noah-modal-fade-enter-active,
.noah-modal-fade-leave-active {
  transition: opacity 0.2s ease;
}

.noah-modal-fade-enter-from,
.noah-modal-fade-leave-to {
  opacity: 0;
}

.noah-modal-fade-enter-active .noah-modal-dialog {
  transition: transform 0.2s cubic-bezier(0.16, 1, 0.3, 1), height 220ms cubic-bezier(.2, .8, .2, 1), max-width 220ms cubic-bezier(.2, .8, .2, 1);
}
.noah-modal-fade-enter-from .noah-modal-dialog {
  transform: scale(0.96) translateY(8px);
}
@media (prefers-reduced-motion: reduce) {
  .noah-modal-dialog, .noah-modal-fade-enter-active .noah-modal-dialog { transition: none; }
}
</style>
