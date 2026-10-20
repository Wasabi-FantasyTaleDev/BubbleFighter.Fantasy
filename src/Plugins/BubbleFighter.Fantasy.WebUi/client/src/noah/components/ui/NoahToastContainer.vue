<template>
  <Teleport to="body">
    <div class="noah-toast-host">
      <TransitionGroup name="noah-toast-slide">
        <div
          v-for="toast in toasts"
          :key="toast.id"
          :class="[
            'noah-toast-item',
            `is-${toast.type}`,
            'glass-panel'
          ]"
          @click="remove(toast.id)"
        >
          <div class="noah-toast-icon-wrap">
            <NoahIcon :name="getIcon(toast.type)" :size="15" />
          </div>

          <div class="noah-toast-content">
            <div v-if="toast.title" class="noah-toast-title">{{ toast.title }}</div>
            <div class="noah-toast-msg">{{ toast.message }}</div>
          </div>

          <button type="button" class="noah-toast-close" @click.stop="remove(toast.id)">
            <NoahIcon name="close" :size="12" />
          </button>
        </div>
      </TransitionGroup>
    </div>
  </Teleport>
</template>

<script setup>
import { useNoahToast } from './useNoahToast'
import NoahIcon from '../icons/NoahIcon.vue'

const { toasts, remove } = useNoahToast()

const getIcon = (type) => {
  switch (type) {
    case 'ok': return 'check'
    case 'warn': return 'alert'
    case 'err': return 'alert'
    default: return 'terminal'
  }
}
</script>

<style scoped>
.noah-toast-host {
  position: fixed;
  bottom: 24px;
  right: 24px;
  z-index: 2000;
  display: flex;
  flex-direction: column-reverse;
  gap: 10px;
  pointer-events: none;
  max-width: 380px;
  width: 100%;
}

.noah-toast-item {
  pointer-events: auto;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px 14px;
  border-radius: var(--radius-sm);
  box-shadow: var(--shadow-lg);
  cursor: pointer;
  user-select: none;
}

.noah-toast-icon-wrap {
  margin-top: 1px;
  flex-shrink: 0;
}

.is-ok .noah-toast-icon-wrap { color: var(--green); }
.is-warn .noah-toast-icon-wrap { color: var(--amber); }
.is-err .noah-toast-icon-wrap { color: var(--red); }
.is-info .noah-toast-icon-wrap { color: var(--blue); }

.noah-toast-content {
  flex: 1;
  min-width: 0;
}

.noah-toast-title {
  font-size: var(--text-label);
  font-weight: 600;
  color: var(--text);
  margin-bottom: 2px;
}

.noah-toast-msg {
  font-size: var(--text-caption);
  color: var(--text-muted);
  line-height: 1.4;
  word-break: break-word;
}

.noah-toast-close {
  border: none;
  background: transparent;
  color: var(--text-faint);
  cursor: pointer;
  padding: 2px;
  border-radius: var(--radius-pill);
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.12s ease;
  margin-top: -2px;
  margin-right: -4px;
}
.noah-toast-close:hover {
  color: var(--text);
}

/* Animations */
.noah-toast-slide-enter-active,
.noah-toast-slide-leave-active {
  transition: all 0.25s cubic-bezier(0.16, 1, 0.3, 1);
}

.noah-toast-slide-enter-from {
  opacity: 0;
  transform: translateY(16px) scale(0.95);
}

.noah-toast-slide-leave-to {
  opacity: 0;
  transform: translateX(30px);
}
</style>
