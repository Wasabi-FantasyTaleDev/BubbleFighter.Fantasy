<template>
  <div
    class="noah-terminal"
    :class="{
      'is-collapsed': isCollapsed,
      'is-expanded': isExpanded,
      'noah-terminal-glass': variant === 'glass'
    }"
  >
    <div class="noah-terminal-bar">
      <div class="noah-terminal-header-left">
        <!-- Native macOS 3-dot Controls (Optional via controls prop) -->
        <div v-if="controls" class="noah-terminal-controls">
          <button
            type="button"
            class="control-dot dot-close"
            :title="isCleared ? words.restore : words.clear"
            :aria-label="isCleared ? words.restore : words.clear"
            @click="handleClose"
          >
            <svg class="control-icon" viewBox="0 0 10 10"><path d="M2.5 2.5l5 5M7.5 2.5l-5 5" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
          </button>
          <button
            type="button"
            class="control-dot dot-minimize"
            :title="isCollapsed ? words.restore : words.minimize"
            :aria-label="isCollapsed ? words.restore : words.minimize"
            :aria-expanded="!isCollapsed"
            @click="handleMinimize"
          >
            <svg class="control-icon" viewBox="0 0 10 10"><path d="M2 5h6" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
          </button>
          <button
            type="button"
            class="control-dot dot-maximize"
            :title="isExpanded ? words.reduce : words.expand"
            :aria-label="isExpanded ? words.reduce : words.expand"
            :aria-pressed="isExpanded"
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
          <span>{{ copied ? words.copied : words.copy }}</span>
        </button>
      </div>
    </div>

    <!-- Terminal Body (Collapsible) -->
    <div v-show="!isCollapsed" class="noah-terminal-body">
      <pre class="noah-terminal-pre"><code ref="code"><template v-if="isCleared">$ _</template><slot v-else>{{ content }}</slot></code></pre>
    </div>
    <p v-if="copyFailed" class="noah-terminal-status" role="status">{{ words.copyFailed }}</p>
    <span class="noah-terminal-sr" role="status">{{ copied ? words.copied : '' }}</span>
  </div>
</template>

<script setup>
import { ref, computed, onBeforeUnmount } from 'vue'
import NoahIcon from '../icons/NoahIcon.vue'

const props = defineProps({
  variant: { type: String, default: 'default', validator: value => ['default', 'glass'].includes(value) },
  labels: { type: Object, default: () => ({}) },
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

const emit = defineEmits(['close', 'minimize', 'maximize', 'copy-error'])

const copied = ref(false)
const isCollapsed = ref(false)
const isExpanded = ref(false)
const isCleared = ref(false)

const words = computed(() => ({
  clear: 'Clear', restore: 'Restore', minimize: 'Collapse', expand: 'Expand', reduce: 'Restore size',
  copy: 'Copy', copied: 'Copied', copyFailed: 'Unable to copy. Select the text to copy it manually.', ...props.labels,
}))
const code = ref()
const copyFailed = ref(false)
let resetTimer
let disposed = false
let copyRequest = 0
const copyCode = async () => {
  const request = ++copyRequest
  clearTimeout(resetTimer)
  copied.value = false
  copyFailed.value = false
  try {
    if (!navigator.clipboard) throw new Error('Clipboard unavailable')
    // An explicit source can exclude prompts and output from a rendered slot.
    await navigator.clipboard.writeText(props.content || code.value?.textContent || '')
    if (disposed || request !== copyRequest) return
    copied.value = true
    resetTimer = setTimeout(() => { copied.value = false }, 1800)
  } catch (error) {
    if (disposed || request !== copyRequest) return
    copyFailed.value = true
    emit('copy-error', error)
  }
}
onBeforeUnmount(() => { disposed = true; clearTimeout(resetTimer) })

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
  isCollapsed.value = false
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

.noah-terminal-controls:hover .control-icon, .control-dot:focus-visible .control-icon {
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

.noah-terminal.is-expanded .noah-terminal-body { min-height:420px; }
.noah-terminal :focus-visible { outline:2px solid #eee; outline-offset:4px; }
.control-dot::after { content:''; position:absolute; inset:-5px; }
.noah-terminal-status { margin:0; padding:12px 20px; font:12px/1.5 var(--font-ui,sans-serif); color:#ddd; border-top:1px solid #333; }
.noah-terminal-sr { position:absolute; width:1px; height:1px; padding:0; margin:-1px; overflow:hidden; clip:rect(0,0,0,0); white-space:nowrap; }
.noah-terminal-glass { border:1px solid rgba(255,255,255,.16); border-radius:16px; background:linear-gradient(155deg,#17181a,#0e0f10 65%); box-shadow:0 0 0 1px rgba(0,0,0,.65),0 1px 0 rgba(255,255,255,.08) inset,0 24px 64px rgba(0,0,0,.22),0 5px 16px rgba(0,0,0,.12); }
.noah-terminal-glass .noah-terminal-bar { min-height:48px; padding:11px 18px; background:linear-gradient(180deg,rgba(255,255,255,.065),rgba(255,255,255,.025)); border-bottom:1px solid rgba(255,255,255,.07); backdrop-filter:saturate(1.5) blur(24px); }
.noah-terminal-glass .noah-terminal-pre { padding:25px 28px 30px; font-size:12px; }
.noah-terminal-glass .control-dot { width:12px; height:12px; }
.noah-terminal-glass .noah-terminal-controls { gap:7px; }
.noah-terminal-header-left { min-width:0; }
.noah-terminal-title { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.noah-terminal-actions { flex:none; }
@media (max-width:600px) { .noah-terminal-glass .noah-terminal-pre { padding:20px 16px; font-size:11px; }.noah-terminal-title { font-size:10px; } }
@media (prefers-reduced-motion:reduce) { .noah-terminal, .noah-terminal * { transition:none; } }

</style>
