<template>
  <button
    type="button"
    role="switch"
    :aria-checked="modelValue"
    :class="[
      'noah-switch',
      `noah-switch-${size}`,
      `noah-switch-${tone}`,
      { 'is-checked': modelValue, 'is-disabled': disabled }
    ]"
    :disabled="disabled"
    @click="handleToggle"
  >
    <span class="noah-switch-track">
      <span class="noah-switch-thumb" />
    </span>
  </button>
</template>

<script setup>
const props = defineProps({
  modelValue: {
    type: Boolean,
    default: false
  },
  tone: {
    type: String,
    default: 'default',
    validator: (v) => ['default', 'success', 'warning', 'danger'].includes(v)
  },
  disabled: {
    type: Boolean,
    default: false
  },
  size: {
    type: String,
    default: 'md',
    validator: (v) => ['sm', 'md'].includes(v)
  }
})

const emit = defineEmits(['update:modelValue', 'change'])

const handleToggle = () => {
  if (!props.disabled) {
    const nextVal = !props.modelValue
    emit('update:modelValue', nextVal)
    emit('change', nextVal)
  }
}
</script>

<style scoped>
.noah-switch {
  display: inline-flex;
  align-items: center;
  border: none;
  background: transparent;
  padding: 0;
  cursor: pointer;
  outline: none;
  user-select: none;
  vertical-align: middle;
}

.noah-switch-track {
  display: inline-flex;
  align-items: center;
  position: relative;
  background-color: var(--border);
  border-radius: var(--radius-pill);
  transition: background-color 0.2s cubic-bezier(0.16, 1, 0.3, 1);
  border: 1px solid rgba(0, 0, 0, 0.05);
}

.noah-switch-thumb {
  position: absolute;
  background-color: var(--surface);
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.2);
  transition: transform 0.2s cubic-bezier(0.16, 1, 0.3, 1), background-color 0.2s ease;
}

/* Sizes */
.noah-switch-md .noah-switch-track {
  width: 36px;
  height: 20px;
}
.noah-switch-md .noah-switch-thumb {
  width: 16px;
  height: 16px;
  left: 2px;
}
.noah-switch-md.is-checked .noah-switch-thumb {
  transform: translateX(16px);
}

.noah-switch-sm .noah-switch-track {
  width: 28px;
  height: 16px;
}
.noah-switch-sm .noah-switch-thumb {
  width: 12px;
  height: 12px;
  left: 2px;
}
.noah-switch-sm.is-checked .noah-switch-thumb {
  transform: translateX(12px);
}

/* Default Theme Accent Color: Monochrome Black & White */
.noah-switch.is-checked.noah-switch-default .noah-switch-track {
  background-color: var(--accent);
}
.noah-switch.is-checked.noah-switch-default .noah-switch-thumb {
  background-color: var(--accent-ink);
}

/* Optional Semantic Tones */
.noah-switch.is-checked.noah-switch-success .noah-switch-track {
  background-color: var(--green);
}
.noah-switch.is-checked.noah-switch-success .noah-switch-thumb {
  background-color: #ffffff;
}

.noah-switch.is-checked.noah-switch-warning .noah-switch-track {
  background-color: var(--amber);
}
.noah-switch.is-checked.noah-switch-warning .noah-switch-thumb {
  background-color: #ffffff;
}

.noah-switch.is-checked.noah-switch-danger .noah-switch-track {
  background-color: var(--red);
}
.noah-switch.is-checked.noah-switch-danger .noah-switch-thumb {
  background-color: #ffffff;
}

.noah-switch:focus-visible .noah-switch-track {
  box-shadow: 0 0 0 2px var(--bg), 0 0 0 4px var(--accent-ring);
}

.noah-switch.is-disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
</style>
