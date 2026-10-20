<template>
  <button
    :type="htmlType"
    :class="[
      'noah-btn',
      `noah-btn-${variant}`,
      `noah-btn-${size}`,
      {
        'is-pill': pill,
        'is-loading': loading,
        'is-disabled': disabled || loading,
        'is-icon-only': iconOnly || (!hasSlotContent && (icon || suffixIcon))
      }
    ]"
    :disabled="disabled || loading"
    @click="handleClick"
  >
    <span v-if="loading" class="noah-btn-spinner" />
    <NoahIcon
      v-else-if="icon"
      :name="icon"
      :size="iconSize"
      class="noah-btn-prefix-icon"
    />

    <span v-if="$slots.default" class="noah-btn-label">
      <slot />
    </span>

    <NoahIcon
      v-if="suffixIcon && !loading"
      :name="suffixIcon"
      :size="iconSize"
      class="noah-btn-suffix-icon"
    />
  </button>
</template>

<script setup>
import { computed, useSlots } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const slots = useSlots()

const props = defineProps({
  variant: {
    type: String,
    default: 'default', // 'default' | 'primary' | 'secondary' | 'danger' | 'ghost' | 'glass'
  },
  size: {
    type: String,
    default: 'md', // 'sm' | 'md' | 'lg'
  },
  pill: {
    type: Boolean,
    default: true
  },
  disabled: {
    type: Boolean,
    default: false
  },
  loading: {
    type: Boolean,
    default: false
  },
  icon: {
    type: String,
    default: ''
  },
  suffixIcon: {
    type: String,
    default: ''
  },
  iconOnly: {
    type: Boolean,
    default: false
  },
  htmlType: {
    type: String,
    default: 'button'
  }
})

const emit = defineEmits(['click'])

const hasSlotContent = computed(() => {
  return !!slots.default && slots.default().length > 0
})

const iconSize = computed(() => {
  if (props.size === 'xs') return 10
  if (props.size === 'sm') return 12
  if (props.size === 'lg') return 16
  return 14
})

const handleClick = (e) => {
  if (!props.disabled && !props.loading) {
    emit('click', e)
  }
}
</script>

<style scoped>
.noah-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  font-family: var(--font-ui);
  font-weight: 500;
  line-height: 1;
  text-decoration: none;
  cursor: pointer;
  border: 1px solid transparent;
  user-select: none;
  white-space: nowrap;
  transition: all 0.15s ease;
  position: relative;
  outline: none;
  vertical-align: middle;
}

.noah-btn.is-pill {
  border-radius: var(--radius-pill);
}

.noah-btn:not(.is-pill) {
  border-radius: var(--radius-xs);
}

.noah-btn-label {
  display: inline-flex;
  align-items: center;
  line-height: 1;
}

.noah-btn-prefix-icon,
.noah-btn-suffix-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

/* Sizes */
.noah-btn-xs {
  height: 22px;
  padding: 0 8px;
  font-size: 11px;
  gap: 4px;
}

.noah-btn-sm {
  height: 26px;
  padding: 0 10px;
  font-size: 12px;
  gap: 5px;
}

.noah-btn-md {
  height: 32px;
  padding: 0 14px;
  font-size: 13px;
  gap: 6px;
}

.noah-btn-lg {
  height: 38px;
  padding: 0 18px;
  font-size: 14px;
  gap: 8px;
}

/* Icon-Only Buttons */
.noah-btn-xs.is-icon-only {
  width: 22px;
  height: 22px;
  padding: 0;
}

.noah-btn-sm.is-icon-only {
  width: 26px;
  height: 26px;
  padding: 0;
}

.noah-btn-md.is-icon-only {
  width: 32px;
  height: 32px;
  padding: 0;
}

.noah-btn-lg.is-icon-only {
  width: 38px;
  height: 38px;
  padding: 0;
}

/* Variants */
.noah-btn-default {
  background-color: var(--surface-raised);
  border-color: var(--border);
  color: var(--text);
}
.noah-btn-default:hover:not(:disabled) {
  background-color: var(--surface-hover);
  border-color: var(--text-faint);
}

.noah-btn-primary {
  background-color: var(--accent);
  border-color: var(--accent);
  color: var(--accent-ink);
}
.noah-btn-primary:hover:not(:disabled) {
  background-color: var(--accent-hover);
  border-color: var(--accent-hover);
  box-shadow: 0 2px 8px var(--accent-soft);
}

.noah-btn-secondary {
  background-color: transparent;
  border-color: var(--border);
  color: var(--text);
}
.noah-btn-secondary:hover:not(:disabled) {
  background-color: var(--hover);
  border-color: var(--text-faint);
}

.noah-btn-danger {
  background-color: var(--red-soft);
  border-color: transparent;
  color: var(--red);
}
.noah-btn-danger:hover:not(:disabled) {
  background-color: var(--red);
  color: #ffffff;
}

.noah-btn-ghost {
  background-color: transparent;
  border-color: transparent;
  color: var(--text-muted);
}
.noah-btn-ghost:hover:not(:disabled) {
  background-color: var(--hover);
  color: var(--text);
}

.noah-btn-glass {
  background-color: var(--glass-panel);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  border-color: var(--border);
  color: var(--text);
}
.noah-btn-glass:hover:not(:disabled) {
  border-color: var(--text-faint);
  background-color: var(--surface-hover);
}

.noah-btn:focus-visible {
  box-shadow: 0 0 0 2px var(--bg), 0 0 0 4px var(--accent-ring);
}

.noah-btn.is-disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.noah-btn-spinner {
  width: 13px;
  height: 13px;
  border: 2px solid currentColor;
  border-right-color: transparent;
  border-radius: 50%;
  animation: spin 0.6s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
