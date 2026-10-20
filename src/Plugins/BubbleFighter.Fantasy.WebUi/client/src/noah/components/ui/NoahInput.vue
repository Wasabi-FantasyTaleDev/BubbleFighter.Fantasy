<template>
  <div
    :style="attrs.style"
    :class="[
      attrs.class,
      'noah-input-wrap',
      `noah-input-${size}`,
      { 'is-focused': isFocused, 'is-disabled': disabled, 'has-prefix': !!prefixIcon, 'has-suffix': !!suffixIcon || clearable || type === 'password' }
    ]"
  >
    <NoahIcon
      v-if="prefixIcon"
      :name="prefixIcon"
      :size="iconSize"
      class="noah-input-prefix"
    />

    <input
      v-bind="inputAttributes()"
      :type="actualType"
      :value="modelValue"
      :placeholder="placeholder"
      :disabled="disabled"
      :readonly="readonly"
      class="noah-input-native"
      @input="handleInput"
      @focus="handleFocus"
      @blur="handleBlur"
      @keyup.enter="emit('enter', $event)"
    />

    <div class="noah-input-suffix-group">
      <!-- Clear Button -->
      <button
        v-if="clearable && modelValue && !disabled"
        type="button"
        class="noah-input-action-btn"
        :aria-label="clearLabel"
        @click.stop="handleClear"
      >
        <NoahIcon name="close" :size="12" />
      </button>

      <!-- Password Toggle -->
      <button
        v-if="type === 'password' && !disabled"
        type="button"
        class="noah-input-action-btn"
        :aria-label="showPassword ? hidePasswordLabel : showPasswordLabel"
        :aria-pressed="showPassword"
        @click.stop="showPassword = !showPassword"
      >
        <NoahIcon :name="showPassword ? 'eye-off' : 'eye'" :size="13" />
      </button>

      <NoahIcon
        v-if="suffixIcon && type !== 'password'"
        :name="suffixIcon"
        :size="iconSize"
        class="noah-input-suffix"
      />
    </div>
  </div>
</template>

<script setup>
import { ref, computed, useAttrs } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

// Form semantics belong on the native input so labels, autofill and browser validation work.
defineOptions({ inheritAttrs: false })
const attrs = useAttrs()
function inputAttributes() {
  const { class: wrapperClass, style, ...input } = attrs
  return input
}

const props = defineProps({
  clearLabel: { type: String, default: 'Clear input' },
  showPasswordLabel: { type: String, default: 'Show password' },
  hidePasswordLabel: { type: String, default: 'Hide password' },
  modelValue: {
    type: [String, Number],
    default: ''
  },
  type: {
    type: String,
    default: 'text'
  },
  placeholder: {
    type: String,
    default: ''
  },
  size: {
    type: String,
    default: 'md' // 'sm' | 'md' | 'lg'
  },
  disabled: {
    type: Boolean,
    default: false
  },
  readonly: {
    type: Boolean,
    default: false
  },
  clearable: {
    type: Boolean,
    default: false
  },
  prefixIcon: {
    type: String,
    default: ''
  },
  suffixIcon: {
    type: String,
    default: ''
  }
})

const emit = defineEmits(['update:modelValue', 'change', 'focus', 'blur', 'clear', 'enter'])

const isFocused = ref(false)
const showPassword = ref(false)

const actualType = computed(() => {
  if (props.type === 'password') {
    return showPassword.value ? 'text' : 'password'
  }
  return props.type
})

const iconSize = computed(() => {
  if (props.size === 'sm') return 12
  if (props.size === 'lg') return 16
  return 14
})

const handleInput = (e) => {
  emit('update:modelValue', e.target.value)
}

const handleFocus = (e) => {
  isFocused.value = true
  emit('focus', e)
}

const handleBlur = (e) => {
  isFocused.value = false
  emit('blur', e)
}

const handleClear = () => {
  emit('update:modelValue', '')
  emit('clear')
}
</script>

<style scoped>
.noah-input-wrap {
  display: inline-flex;
  align-items: center;
  width: 100%;
  border: 1px solid var(--border);
  background-color: var(--surface);
  border-radius: var(--radius-xs);
  transition: all 0.15s ease;
  position: relative;
}

.noah-input-wrap.is-focused {
  border-color: var(--accent);
  box-shadow: 0 0 0 1px var(--accent);
}

.noah-input-native {
  width: 100%;
  height: 100%;
  border: none;
  background: transparent;
  color: var(--text);
  font-family: var(--font-ui);
  outline: none;
  padding: 0 10px;
}

.noah-input-native::placeholder {
  color: var(--text-faint);
}

/* Sizes */
.noah-input-sm {
  height: 28px;
  font-size: var(--text-caption);
}
.noah-input-md {
  height: 34px;
  font-size: var(--text-label);
}
.noah-input-lg {
  height: 40px;
  font-size: var(--text-body);
}

.noah-input-prefix {
  margin-left: 10px;
  color: var(--text-muted);
}
.noah-input-wrap.has-prefix .noah-input-native {
  padding-left: 6px;
}

.noah-input-suffix-group {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-right: 8px;
}

.noah-input-suffix {
  color: var(--text-muted);
}

.noah-input-action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  background: transparent;
  color: var(--text-faint);
  cursor: pointer;
  padding: 2px;
  border-radius: var(--radius-pill);
  outline: none;
  transition: color 0.12s ease;
}
.noah-input-action-btn:hover {
  color: var(--text);
}

.noah-input-wrap.is-disabled {
  opacity: 0.45;
  cursor: not-allowed;
  background-color: var(--surface-raised);
}
</style>
