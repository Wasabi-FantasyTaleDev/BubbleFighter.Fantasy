<template>
  <span
    class="noah-tag"
    :class="[
      `noah-tag-${tone}`,
      `noah-tag-${size}`,
      {
        'is-pill': pill,
        'is-checkable': checkable,
        'is-checked': checked,
        'is-hit': hit
      }
    ]"
    @click="handleClick"
  >
    <NoahIcon v-if="icon" :name="icon" :size="iconSize" class="noah-tag-icon" />
    <slot>{{ label }}</slot>
    <button
      v-if="closable"
      type="button"
      class="noah-tag-close-btn"
      aria-label="Close tag"
      @click.stop="handleClose"
    >
      <svg viewBox="0 0 10 10" width="8" height="8"><path d="M2 2l6 6M8 2l-6 6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
    </button>
  </span>
</template>

<script setup>
import { computed } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  label: { type: String, default: '' },
  tone: {
    type: String,
    default: 'default',
    validator: (v) => ['default', 'primary', 'success', 'warning', 'danger', 'info'].includes(v)
  },
  size: {
    type: String,
    default: 'md',
    validator: (v) => ['sm', 'md', 'lg'].includes(v)
  },
  pill: { type: Boolean, default: false },
  closable: { type: Boolean, default: false },
  checkable: { type: Boolean, default: false },
  checked: { type: Boolean, default: false },
  hit: { type: Boolean, default: false },
  icon: { type: String, default: '' }
})

const emit = defineEmits(['click', 'close', 'update:checked'])

const iconSize = computed(() => {
  if (props.size === 'sm') return 11
  if (props.size === 'lg') return 14
  return 12
})

const handleClick = (e) => {
  if (props.checkable) {
    emit('update:checked', !props.checked)
  }
  emit('click', e)
}

const handleClose = (e) => {
  emit('close', e)
}
</script>

<style scoped>
.noah-tag {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-family: var(--font-ui);
  font-weight: var(--weight-medium);
  border-radius: var(--radius-xs);
  border: 1px solid var(--border);
  background-color: var(--surface-raised);
  color: var(--text);
  line-height: 1;
  white-space: nowrap;
  transition: all 0.15s ease;
  user-select: none;
}

/* Sizes */
.noah-tag-sm {
  padding: 2px 7px;
  font-size: var(--text-micro);
}
.noah-tag-md {
  padding: 4px 9px;
  font-size: var(--text-caption);
}
.noah-tag-lg {
  padding: 6px 12px;
  font-size: var(--text-label);
}

.noah-tag.is-pill {
  border-radius: var(--radius-pill);
}

/* Tones */
.noah-tag-default {
  background-color: var(--surface-raised);
  border-color: var(--border);
  color: var(--text);
}

.noah-tag-primary {
  background-color: var(--accent-soft);
  border-color: var(--border-soft);
  color: var(--text);
}

.noah-tag-success {
  background-color: var(--green-soft);
  border-color: rgba(34, 197, 94, 0.25);
  color: var(--green);
}

.noah-tag-warning {
  background-color: var(--amber-soft);
  border-color: rgba(245, 158, 11, 0.25);
  color: var(--amber);
}

.noah-tag-danger {
  background-color: var(--red-soft);
  border-color: rgba(239, 68, 68, 0.25);
  color: var(--red);
}

.noah-tag-info {
  background-color: var(--blue-soft);
  border-color: rgba(59, 130, 246, 0.25);
  color: var(--blue);
}

/* Checkable */
.noah-tag.is-checkable {
  cursor: pointer;
}
.noah-tag.is-checkable:hover {
  border-color: var(--text-faint);
}
.noah-tag.is-checked {
  background-color: var(--accent);
  color: var(--accent-ink);
  border-color: var(--accent);
}

/* Close Button */
.noah-tag-close-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 13px;
  height: 13px;
  border-radius: 50%;
  border: none;
  background: transparent;
  color: inherit;
  opacity: 0.65;
  cursor: pointer;
  padding: 0;
  margin-left: 2px;
  transition: all 0.12s ease;
}
.noah-tag-close-btn:hover {
  opacity: 1;
  background-color: rgba(0, 0, 0, 0.1);
}
:root[data-theme='dark'] .noah-tag-close-btn:hover {
  background-color: rgba(255, 255, 255, 0.2);
}
</style>
