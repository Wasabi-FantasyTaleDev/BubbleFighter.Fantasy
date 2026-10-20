<template>
  <div
    class="noah-terminal"
    :class="{
      'is-collapsed': isCollapsed,
      'is-expanded': isExpanded
    }"
  >
    <div class="noah-terminal-bar">
      <div class="noah-terminal-header-left">
        <!-- Native macOS 3-dot Controls (Optional via controls prop) -->
        <div v-if="controls" class="noah-terminal-controls">
          <button
            type="button"
            class="control-dot dot-close"
            title="Close / Clear"
            aria-label="Close"
            @click="handleClose"
          >
            <svg class="control-icon" viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
          </button>
          <button
            type="button"
            class="control-dot dot-minimize"
            title="Minimize"
            aria-label="Minimize"
            @click="handleMinimize"
          >
            <svg class="control-icon" viewBox="0 0 10 10"><path d="M2 5h6" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
          </button>
          <button
            type="button"
            class="control-dot dot-maximize"
            title="Maximize"
            aria-label="Maximize"
            @click="handleMaximize"
          >
            <svg class="control-icon" viewBox="0 0 10 10"><path d="M2.5 7.5L7.5 2.5M7.5 2.5H4M7.5 2.5V6" stroke="currentColor" stroke-width="1.1" stroke-linecap="round" stroke-linejoin="round"/></svg>
          </button>
        </div>

        <span class="noah-terminal-title">{{ title }}</span>
      </div>

      <div class="noah-terminal-actions">
        <button
          v-if="copyable"
          type="button"
          class="noah-terminal-action-btn"
          @click="copyCode"
        >
          <NoahIcon :name="copied ? 'check' : 'copy'" :size="12" />
          <span>{{ copied ? 'Copied' : 'Copy' }}</span>
        </button>
      </div>
    </div>

    <!-- Terminal Body (Collapsible) -->
    <div v-show="!isCollapsed" class="noah-terminal-body">
      <pre class="noah-terminal-pre"><code><slot>{{ displayContent }}</slot></code></pre>
    </div>
  </div>
</template>

<script setup>
import { ref, computed } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  title: {
    type: String,
    default: 'bash'
  },
  content: {
    type: String,
    default: ''
  },
  controls: {
    type: Boolean,
    default: false
  },
  copyable: {
    type: Boolean,
    default: true
  }
})

const emit = defineEmits(['close', 'minimize', 'maximize'])

const copied = ref(false)
const isCollapsed = ref(false)
const isExpanded = ref(false)
const isCleared = ref(false)

const displayContent = computed(() => {
  if (isCleared.value) return '$ _'
  return props.content
})

const copyCode = () => {
  const text = props.content || ''
  navigator.clipboard.writeText(text).then(() => {
    copied.value = true
    setTimeout(() => {
      copied.value = false
    }, 1800)
  })
}

const handleClose = () => {
  isCleared.value = !isCleared.value
  emit('close')
}

const handleMinimize = () => {
  isCollapsed.value = !isCollapsed.value
  emit('minimize', isCollapsed.value)
}

const handleMaximize = () => {
  isExpanded.value = !isExpanded.value
  emit('maximize', isExpanded.value)
}
</script>

<style scoped>
.noah-terminal {
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background-color: #171717;
  overflow: hidden;
  box-shadow: var(--shadow);
  width: 100%;
  transition: all 0.25s cubic-bezier(0.16, 1, 0.3, 1);
}

.noah-terminal.is-expanded {
  min-height: 280px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.4);
}

.noah-terminal-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-bottom: 1px solid #303030;
  background-color: #1f1f1f;
  user-select: none;
}

.noah-terminal-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

/* Native macOS Controls with Interactive Hover Icons */
.noah-terminal-controls {
  display: flex;
  align-items: center;
  gap: 6px;
}

.control-dot {
  width: 11px;
  height: 11px;
  border-radius: 50%;
  border: none;
  padding: 0;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  position: relative;
  transition: transform 0.12s ease, filter 0.12s ease;
}

.control-dot:hover {
  transform: scale(1.1);
  filter: brightness(1.1);
}

.control-dot:active {
  transform: scale(0.95);
}

.control-icon {
  width: 7px;
  height: 7px;
  opacity: 0;
  color: rgba(0, 0, 0, 0.65);
  transition: opacity 0.15s ease;
}

.noah-terminal-controls:hover .control-icon {
  opacity: 1;
}

.dot-close {
  background-color: #ff5f56;
}
.dot-minimize {
  background-color: #ffbd2e;
}
.dot-maximize {
  background-color: #27c93f;
}

.noah-terminal-title {
  font-family: var(--font-code);
  font-size: var(--text-caption);
  color: #8f8f8f;
}

.noah-terminal-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.noah-terminal-action-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  border: none;
  background: transparent;
  color: #8f8f8f;
  font-family: var(--font-ui);
  font-size: 11px;
  cursor: pointer;
  padding: 2px 6px;
  border-radius: 4px;
  transition: all 0.12s ease;
}
.noah-terminal-action-btn:hover {
  background-color: rgba(255, 255, 255, 0.1);
  color: #ececec;
}

.noah-terminal-body {
  transition: max-height 0.25s ease;
}

.noah-terminal-pre {
  margin: 0;
  padding: 14px 16px;
  font-family: var(--font-code);
  font-size: var(--text-label);
  line-height: 1.7;
  color: #ececec;
  background: transparent !important;
  border: none !important;
  overflow-x: auto;
}

.noah-terminal-pre code {
  background: transparent !important;
  border: none !important;
  padding: 0 !important;
  border-radius: 0 !important;
  color: inherit !important;
  box-shadow: none !important;
  font-family: inherit !important;
  font-size: inherit !important;
}
</style>
