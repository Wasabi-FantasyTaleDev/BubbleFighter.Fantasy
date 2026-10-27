<template>
  <div
    ref="selectRef"
    @focusout="handleFocusOut"
    :class="[
      'noah-select',
      `noah-select-${size}`,
      { 'is-open': isOpen, 'is-disabled': disabled }
    ]"
  >
    <button ref="triggerRef" v-bind="$attrs" type="button" class="noah-select-trigger" role="combobox"
      :disabled="disabled" aria-haspopup="listbox" :aria-expanded="isOpen" :aria-controls="listId"
      :aria-activedescendant="isOpen && activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined"
      @click="toggleDropdown" @keydown="handleKeydown">
      <span v-if="selectedLabel" class="noah-select-value language-name" :lang="selectedLanguage">{{ selectedLabel }}</span>
      <span v-else class="noah-select-placeholder">{{ placeholder }}</span>

      <NoahIcon
        name="chevron-down"
        :size="12"
        class="noah-select-caret"
        :class="{ 'is-rotated': isOpen }"
      />
    </button>

    <!-- Dropdown Panel -->
    <Teleport to="body">
    <Transition name="admin-select">
    <div v-if="isOpen" ref="dropdownRef" class="noah-select-dropdown glass-panel" :style="dropdownStyle">
      <ul :id="listId" class="noah-select-list" role="listbox" :aria-labelledby="$attrs['aria-labelledby']" :aria-label="$attrs['aria-label'] || placeholder">
        <li
          v-for="(item, index) in options"
          :key="item.value"
          :id="`${listId}-${index}`" role="option" :aria-selected="item.value === modelValue" :aria-disabled="!!item.disabled"
          :class="[
            'noah-select-item',
            { 'is-selected': item.value === modelValue, 'is-disabled': item.disabled, 'is-active': index === activeIndex }
          ]"
          @click="selectOption(item)"
          @mousedown.prevent
        >
          <span class="language-name" :lang="item.lang">{{ item.label }}</span>
          <NoahIcon
            v-if="item.value === modelValue"
            name="check"
            :size="12"
            class="noah-select-check"
          />
        </li>
      </ul>
    </div>
    </Transition>
    </Teleport>
  </div>
</template>

<script setup>
import { ref, computed, nextTick, onMounted, onUnmounted, useId, watch } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

defineOptions({ inheritAttrs: false })
const props = defineProps({
  modelValue: {
    type: [String, Number],
    default: ''
  },
  options: {
    type: Array,
    default: () => [] // [{ label: '', value: '', disabled: false }]
  },
  placeholder: {
    type: String,
    default: 'Select...'
  },
  size: {
    type: String,
    default: 'md' // 'sm' | 'md' | 'lg'
  },
  disabled: {
    type: Boolean,
    default: false
  }
})

const emit = defineEmits(['update:modelValue', 'change'])

const selectRef = ref(null)
const triggerRef = ref(null)
const dropdownRef = ref(null)
const dropdownStyle = ref({})
const isOpen = ref(false)
const activeIndex = ref(-1)
const listId = `noah-select-${useId()}`
let search = ''
let searchAt = 0

const selectedLabel = computed(() => {
  const found = props.options.find(o => o.value === props.modelValue)
  return found ? found.label : ''
})
const selectedLanguage = computed(() => props.options.find(o => o.value === props.modelValue)?.lang)

const toggleDropdown = () => {
  if (!props.disabled) {
    if (isOpen.value) isOpen.value = false
    else openDropdown()
  }
}

function openDropdown() {
  updatePosition()
  isOpen.value = true
  const selected = props.options.findIndex(o => o.value === props.modelValue && !o.disabled)
  setActive(selected >= 0 ? selected : props.options.findIndex(o => !o.disabled))
}

function setActive(index) {
  activeIndex.value = index
  nextTick(() => {
    const menu = dropdownRef.value
    const option = menu?.querySelector(`[id="${listId}-${index}"]`)
    if (!menu || !option) return
    // Scroll only the popup; scrolling ancestors moves the entire settings page.
    const item = option.getBoundingClientRect()
    const bounds = menu.getBoundingClientRect()
    if (item.top < bounds.top + 4) menu.scrollTop -= bounds.top + 4 - item.top
    else if (item.bottom > bounds.bottom - 4) menu.scrollTop += item.bottom - bounds.bottom + 4
  })
}

function updatePosition() {
  const trigger = triggerRef.value
  if (!trigger) return
  const rect = trigger.getBoundingClientRect()
  const above = Math.max(0, rect.top - 12)
  const below = Math.max(0, window.innerHeight - rect.bottom - 12)
  const upward = below < 220 && above > below
  const height = Math.min(220, upward ? above : below)
  const width = Math.min(rect.width, Math.max(0, window.innerWidth - 16))
  dropdownStyle.value = {
    left: `${Math.max(8, Math.min(rect.left, window.innerWidth - width - 8))}px`,
    width: `${width}px`,
    maxHeight: `${height}px`,
    ...(upward ? { bottom: `${window.innerHeight - rect.top + 4}px` } : { top: `${rect.bottom + 4}px` })
  }
}

function handleKeydown(event) {
  if (props.disabled) return
  const enabled = props.options.map((o, i) => o.disabled ? -1 : i).filter(i => i >= 0)
  if (event.key === 'Escape' || event.key === 'Tab') {
    if (isOpen.value && event.key === 'Escape') { event.preventDefault(); event.stopPropagation() }
    isOpen.value = false
    return
  }
  if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
    event.preventDefault()
    if (!isOpen.value) { openDropdown(); return }
    if (!enabled.length) return
    const position = enabled.indexOf(activeIndex.value)
    const target = event.key === 'Home' ? 0 : event.key === 'End' ? enabled.length - 1
      : (position + (event.key === 'ArrowDown' ? 1 : -1) + enabled.length) % enabled.length
    setActive(enabled[target])
  } else if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    if (!isOpen.value) openDropdown()
    else if (activeIndex.value >= 0) selectOption(props.options[activeIndex.value])
  } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
    event.preventDefault()
    const now = Date.now()
    search = (now - searchAt > 700 ? '' : search).slice(-63) + event.key.toLocaleLowerCase()
    searchAt = now
    if (!isOpen.value) openDropdown()
    const index = enabled.find(i => String(props.options[i].label).toLocaleLowerCase().startsWith(search))
    if (index !== undefined) setActive(index)
  }
}

const selectOption = (item) => {
  if (!item || item.disabled || props.disabled) return
  emit('update:modelValue', item.value)
  emit('change', item.value)
  isOpen.value = false
  triggerRef.value?.focus({ preventScroll: true })
}

function handleFocusOut(event) {
  if (!selectRef.value?.contains(event.relatedTarget) && !dropdownRef.value?.contains(event.relatedTarget)) isOpen.value = false
}
watch(() => props.disabled, value => { if (value) isOpen.value = false })
watch(() => props.options, () => { if (isOpen.value) openDropdown() })

const handleClickOutside = (e) => {
  if (selectRef.value && !selectRef.value.contains(e.target) && !dropdownRef.value?.contains(e.target)) {
    isOpen.value = false
  }
}

onMounted(() => {
  document.addEventListener('click', handleClickOutside)
  window.addEventListener('resize', reposition)
  document.addEventListener('scroll', reposition, true)
})

onUnmounted(() => {
  document.removeEventListener('click', handleClickOutside)
  window.removeEventListener('resize', reposition)
  document.removeEventListener('scroll', reposition, true)
})

function reposition() { if (isOpen.value) updatePosition() }
</script>

<style scoped>
.noah-select {
  position: relative;
  width: 100%;
  user-select: none;
}

.noah-select-trigger {
  width: 100%;
  font-family: inherit;
  text-align: left;
  display: flex;
  align-items: center;
  justify-content: space-between;
  border: 1px solid var(--border);
  background-color: var(--surface);
  border-radius: var(--radius-xs);
  cursor: pointer;
  padding: 0 10px;
  transition: all 0.15s ease;
}

.noah-select-trigger:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }

.noah-select.is-open .noah-select-trigger {
  border-color: var(--accent);
}

.noah-select-value {
  color: var(--text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.noah-select-placeholder {
  color: var(--text-faint);
}

.noah-select-caret {
  color: var(--text-muted);
  transition: transform 0.2s ease;
  flex-shrink: 0;
  margin-left: 6px;
}
.noah-select-caret.is-rotated {
  transform: rotate(180deg);
}

/* Sizes */
.noah-select-sm .noah-select-trigger {
  height: 28px;
  font-size: var(--text-caption);
}
.noah-select-md .noah-select-trigger {
  height: 34px;
  font-size: var(--text-label);
}
.noah-select-lg .noah-select-trigger {
  height: 40px;
  font-size: var(--text-body);
}

/* Dropdown */
.noah-select-dropdown {
  position: fixed;
  box-sizing: border-box;
  max-height: 220px;
  overflow-y: auto;
  border-radius: var(--radius-xs);
  z-index: 2000;
  overscroll-behavior: contain;
  padding: 4px;
  box-shadow: var(--shadow);
}

.noah-select-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.noah-select-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 10px;
  font-size: var(--text-label);
  border-radius: var(--radius-xs);
  color: var(--text);
  cursor: pointer;
  transition: background-color 0.12s ease;
}
.noah-select-item:hover, .noah-select-item.is-active {
  background-color: var(--hover);
}
.noah-select-item.is-selected {
  font-weight: 600;
  color: var(--accent);
  background-color: var(--surface-raised);
}
.noah-select-item.is-disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.noah-select.is-disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
.noah-select.is-disabled .noah-select-trigger {
  cursor: not-allowed;
  background-color: var(--surface-raised);
}
</style>
