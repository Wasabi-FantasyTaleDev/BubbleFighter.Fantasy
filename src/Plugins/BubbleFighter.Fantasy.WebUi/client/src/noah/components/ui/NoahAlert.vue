<template>
  <transition name="noah-fade">
    <div
      v-if="visible"
      class="noah-alert"
      :class="[
        `noah-alert-${tone}`,
        {
          'is-center': center,
          'has-description': !!description || $slots.description
        }
      ]"
      role="alert"
    >
      <div v-if="showIcon" class="noah-alert-icon-wrap">
        <NoahIcon :name="resolvedIcon" :size="iconSize" />
      </div>

      <div class="noah-alert-content">
        <div v-if="title || $slots.default" class="noah-alert-title font-medium">
          <slot>{{ title }}</slot>
        </div>
        <div v-if="description || $slots.description" class="noah-alert-description text-caption">
          <slot name="description">{{ description }}</slot>
        </div>
      </div>

      <button
        v-if="closable"
        type="button"
        class="noah-alert-close-btn"
        aria-label="Close alert"
        @click="handleClose"
      >
        <span v-if="closeText" class="close-text">{{ closeText }}</span>
        <svg v-else viewBox="0 0 12 12" width="10" height="10">
          <path d="M2.5 2.5l7 7M9.5 2.5l-7 7" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>
        </svg>
      </button>
    </div>
  </transition>
</template>

<script setup>
import { ref, computed } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  title: { type: String, default: '' },
  description: { type: String, default: '' },
  tone: {
    type: String,
    default: 'info',
    validator: (v) => ['info', 'success', 'warning', 'error'].includes(v)
  },
  closable: { type: Boolean, default: false },
  closeText: { type: String, default: '' },
  showIcon: { type: Boolean, default: true },
  center: { type: Boolean, default: false }
})

const emit = defineEmits(['close'])
const visible = ref(true)

const resolvedIcon = computed(() => {
  switch (props.tone) {
    case 'success': return 'check-circle'
    case 'warning': return 'alert-triangle'
    case 'error': return 'alert-circle'
    case 'info':
    default: return 'info'
  }
})

const iconSize = computed(() => {
  return (props.description || false) ? 18 : 15
})

const handleClose = (e) => {
  visible.value = false
  emit('close', e)
}
</script>

<style scoped>
.noah-alert {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 14px;
  border-radius: var(--radius);
  border: 1px solid var(--border);
  background-color: var(--surface-raised);
  color: var(--text);
  margin-bottom: 12px;
  position: relative;
  box-shadow: var(--shadow-sm);
  transition: all 0.2s ease;
}

.noah-alert.is-center {
  justify-content: center;
  text-align: center;
}

.noah-alert-icon-wrap {
  flex-shrink: 0;
  margin-top: 1px;
}

.noah-alert-content {
  flex: 1;
  min-width: 0;
}

.noah-alert-title {
  font-size: var(--text-control);
  line-height: 1.4;
}

.noah-alert-description {
  color: var(--text-muted);
  margin-top: 4px;
  line-height: 1.5;
}

.noah-alert-close-btn {
  border: none;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  padding: 2px;
  border-radius: var(--radius-2xs);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  opacity: 0.7;
  transition: opacity 0.12s ease, color 0.12s ease;
}
.noah-alert-close-btn:hover {
  opacity: 1;
  color: var(--text);
}

.close-text {
  font-size: var(--text-micro);
  font-weight: 500;
}

/* Tones */
.noah-alert-info {
  background-color: var(--blue-soft);
  border-color: rgba(59, 130, 246, 0.2);
  color: var(--text);
}
.noah-alert-info .noah-alert-icon-wrap { color: var(--blue); }

.noah-alert-success {
  background-color: var(--green-soft);
  border-color: rgba(34, 197, 94, 0.2);
  color: var(--text);
}
.noah-alert-success .noah-alert-icon-wrap { color: var(--green); }

.noah-alert-warning {
  background-color: var(--amber-soft);
  border-color: rgba(245, 158, 11, 0.2);
  color: var(--text);
}
.noah-alert-warning .noah-alert-icon-wrap { color: var(--amber); }

.noah-alert-error {
  background-color: var(--red-soft);
  border-color: rgba(239, 68, 68, 0.2);
  color: var(--text);
}
.noah-alert-error .noah-alert-icon-wrap { color: var(--red); }

.noah-fade-enter-active,
.noah-fade-leave-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}
.noah-fade-enter-from,
.noah-fade-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
