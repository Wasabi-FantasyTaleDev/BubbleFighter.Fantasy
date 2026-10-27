<template>
  <svg
    :class="['noah-icon', customClass]"
    :width="size"
    :height="size"
    :viewBox="viewBox"
    :fill="fillMode"
    :stroke="strokeMode"
    :stroke-width="strokeWidth"
    stroke-linecap="round"
    stroke-linejoin="round"
  >
    <!-- Functional & System Icons -->
    <path v-if="name === 'terminal'" d="M4 17l6-6-6-6M12 19h8" />
    <path v-else-if="name === 'copy'" d="M8 4v12a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2h-8a2 2 0 0 0-2 2z M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h2" />
    <path v-else-if="name === 'check'" d="M20 6L9 17l-5-5" />
    <g v-else-if="name === 'check-circle'">
      <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" />
      <polyline points="22 4 12 14.01 9 11.01" />
    </g>
    <path v-else-if="name === 'alert'" d="M12 9v4m0 4h.01M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
    <g v-else-if="name === 'alert-triangle'">
      <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
      <line x1="12" y1="9" x2="12" y2="13" />
      <line x1="12" y1="17" x2="12.01" y2="17" />
    </g>
    <g v-else-if="name === 'alert-circle'">
      <circle cx="12" cy="12" r="10" />
      <line x1="12" y1="8" x2="12" y2="12" />
      <line x1="12" y1="16" x2="12.01" y2="16" />
    </g>
    <g v-else-if="name === 'info'">
      <circle cx="12" cy="12" r="10" />
      <line x1="12" y1="16" x2="12" y2="12" />
      <line x1="12" y1="8" x2="12.01" y2="8" />
    </g>
    <g v-else-if="name === 'upload'">
      <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
      <polyline points="17 8 12 3 7 8" />
      <line x1="12" y1="3" x2="12" y2="15" />
    </g>
    <g v-else-if="name === 'file'">
      <path d="M13 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z" />
      <polyline points="13 2 13 9 20 9" />
    </g>
    <g v-else-if="name === 'calendar'">
      <rect x="3" y="4" width="18" height="18" rx="2" ry="2" />
      <line x1="16" y1="2" x2="16" y2="6" />
      <line x1="8" y1="2" x2="8" y2="6" />
      <line x1="3" y1="10" x2="21" y2="10" />
    </g>
    <g v-else-if="name === 'x' || name === 'close'">
      <line x1="18" y1="6" x2="6" y2="18" />
      <line x1="6" y1="6" x2="18" y2="18" />
    </g>
    <path v-else-if="name === 'chevron-down'" d="M6 9l6 6 6-6" />
    <path v-else-if="name === 'chevron-right'" d="M9 18l6-6-6-6" />
    <path v-else-if="name === 'chevron-left'" d="M15 18l-6-6 6-6" />
    <path v-else-if="name === 'chevron-up'" d="M18 15l-6-6-6 6" />
    <path v-else-if="name === 'search'" d="M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z M21 21l-4.35-4.35" />
    <g v-else-if="name === 'sun'">
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
    </g>
    <path v-else-if="name === 'moon'" d="M20.5 14.5A8.5 8.5 0 0 1 9.5 3.5a8.5 8.5 0 1 0 11 11z" />
    <path v-else-if="name === 'globe'" d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm0 0a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10zM2 12h20" />
    <path v-else-if="name === 'dashboard'" d="M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z" />
    <path v-else-if="name === 'cpu'" d="M4 4h16v16H4z M9 9h6v6H9z M9 1v3 M15 1v3 M9 20v3 M15 20v3 M20 9h3 M20 14h3 M1 9h3 M1 14h3" />
    <path v-else-if="name === 'server'" d="M2 4h20v6H2z M2 14h20v6H2z M6 7h.01 M6 17h.01" />
    <path v-else-if="name === 'activity'" d="M22 12h-4l-3 9L9 3l-3 9H2" />
    <path v-else-if="name === 'layers'" d="M12 2L2 7l10 5 10-5-10-5z M2 17l10 5 10-5 M2 12l10 5 10-5" />
    <path v-else-if="name === 'code'" d="M16 18l6-6-6-6 M8 6l-6 6 6 6" />
    <path v-else-if="name === 'book'" d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20 M4 4.5A2.5 2.5 0 0 1 6.5 2H20v20H6.5A2.5 2.5 0 0 0 4 24.5v-20z" />
    <path v-else-if="name === 'rocket'" d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09ZM12 15l-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2Z" />
    <path v-else-if="name === 'settings'" d="M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
    <path v-else-if="name === 'plus'" d="M12 5v14M5 12h14" />
    <path v-else-if="name === 'trash'" d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
    <path v-else-if="name === 'edit'" d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7 M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
    <path v-else-if="name === 'refresh'" d="M23 4v6h-6 M1 20v-6h6 M3.51 9a9 9 0 0 1 14.85-3.36L23 10 M1 14l4.64 4.36A9 9 0 0 0 20.49 15" />
    <path v-else-if="name === 'user'" d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2 M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8z" />
    <path v-else-if="name === 'external-link'" d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6 M15 3h6v6 M10 14L21 3" />
    <path v-else-if="name === 'menu'" d="M3 12h18M3 6h18M3 18h18" />
    <path v-else-if="name === 'shield'" d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z" />
    <path v-else-if="name === 'key'" d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777Zm0 0L15.5 7.5m0 0 3 3L22 7l-3-3m-3.5 3.5L19 4" />
    <path v-else-if="name === 'zap'" d="M13 2L3 14h9l-1 8 10-12h-9l1-8z" />
    <path v-else-if="name === 'filter'" d="M22 3H2l8 9.46V19l4 2v-8.54L22 3z" />
    <path v-else-if="name === 'lock'" d="M19 11H5a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7a2 2 0 0 0-2-2zm-7-7a4 4 0 0 0-4 4v3h8V8a4 4 0 0 0-4-4z" />
    <path v-else-if="name === 'logout'" d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9" />
    <path v-else-if="name === 'eye'" d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z" />
    <path v-else-if="name === 'eye-off'" d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24M1 1l22 22" />

    <!-- Admin console glyphs (added for the Bubble Fighter Fantasy admin interface) -->
    <g v-else-if="name === 'overview'">
      <rect x="3" y="3" width="7" height="9" rx="1.5" />
      <rect x="14" y="3" width="7" height="5" rx="1.5" />
      <rect x="14" y="12" width="7" height="9" rx="1.5" />
      <rect x="3" y="16" width="7" height="5" rx="1.5" />
    </g>
    <g v-else-if="name === 'players'">
      <circle cx="9" cy="8" r="3.5" />
      <path d="M2.5 20c.8-3.4 3.4-5.5 6.5-5.5s5.7 2.1 6.5 5.5" />
      <path d="M16 4.6a3.5 3.5 0 0 1 0 6.8M18 14.8c1.8.7 3 2.4 3.5 5.2" />
    </g>
    <g v-else-if="name === 'person'">
      <circle cx="9" cy="8" r="3.5" />
      <path d="M2.5 20c.8-3.4 3.4-5.5 6.5-5.5s5.7 2.1 6.5 5.5" />
    </g>
    <path v-else-if="name === 'rooms'" d="M3 10.5 12 4l9 6.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z" />
    <path v-else-if="name === 'plugins'" d="M9 3v4M15 3v4M7 7h10v4a5 5 0 0 1-10 0zM12 16v5" />
    <g v-else-if="name === 'store'">
      <path d="M5 7h14l1 13H4L5 7z" />
      <path d="M9 8V6a3 3 0 0 1 6 0v2" />
    </g>
    <path v-else-if="name === 'opcodes'" d="m8 7-5 5 5 5M16 7l5 5-5 5M13.5 4l-3 16" />
    <path v-else-if="name === 'logs'" d="M5 4h14v16H5zM8.5 9h7M8.5 13h7M8.5 17h4" />
    <g v-else-if="name === 'builds'">
      <rect x="3" y="4" width="18" height="12" rx="2" />
      <path d="M8 20h8M12 16v4" />
    </g>
    <g v-else-if="name === 'gear'">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z" />
    </g>
    <path v-else-if="name === 'pause'" d="M6 4h4v16H6zM14 4h4v16h-4z" />
    <path v-else-if="name === 'play'" d="M6 4l14 8-14 8z" />

    <!-- Brand & Platform Logos -->
    <g v-else-if="name === 'github'">
      <path d="M9 19c-5 1.5-5-2.5-7-3m14 6v-3.87a3.37 3.37 0 0 0-.94-2.61c3.14-.35 6.44-1.54 6.44-7A5.44 5.44 0 0 0 20 4.77 5.07 5.07 0 0 0 19.91 1S18.73.65 16 2.48a13.38 13.38 0 0 0-7 0C6.27.65 5.09 1 5.09 1A5.07 5.07 0 0 0 5 4.77a5.44 5.44 0 0 0-1.5 3.78c0 5.42 3.3 6.61 6.44 7A3.37 3.37 0 0 0 9 18.13V22" />
    </g>
    <g v-else-if="name === 'anthropic'">
      <path fill="currentColor" stroke="none" d="M13.827 2.375l6.598 17.25h-3.414l-1.396-3.805H8.385l-1.396 3.805H3.575L10.173 2.375h3.654zm-1.066 3.753h-.105L9.56 13.068h4.88l-1.679-6.94z" />
    </g>
    <g v-else-if="name === 'openai'">
      <path fill="currentColor" stroke="none" d="M22.28 9.24a5.998 5.998 0 0 0-.54-4.52 6.03 6.03 0 0 0-6.17-2.9 6.04 6.04 0 0 0-4.66 2.29A6.03 6.03 0 0 0 3.76 6.8a6.04 6.04 0 0 0 .86 6.36 5.998 5.998 0 0 0 .54 4.52 6.03 6.03 0 0 0 6.17 2.9 6.04 6.04 0 0 0 4.66-2.29 6.03 6.03 0 0 0 7.15-2.69 6.04 6.04 0 0 0-.86-6.36zm-8.88 10.96a4.52 4.52 0 0 1-2.9-1.06l.14-.08 4.8-2.77a.78.78 0 0 0 .39-.68v-6.77l2.03 1.17a.07.07 0 0 1 .04.05v5.62a4.54 4.54 0 0 1-4.5 4.53z" />
    </g>
    <g v-else-if="name === 'google'">
      <path fill="currentColor" stroke="none" d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm3.8 14.5A5.6 5.6 0 0 1 12 17.6a5.6 5.6 0 1 1 5.6-5.6h-5.6v-2.3h7.8a6.8 6.8 0 0 1 .1 1.7 7.9 7.9 0 0 1-4.1 6.1z" />
    </g>
    <g v-else-if="name === 'deepseek'">
      <path fill="currentColor" stroke="none" d="M12 3a9 9 0 1 0 9 9 9 9 0 0 0-9-9zm4 9.5a4 4 0 1 1-4-4 4 4 0 0 1 4 4z" />
    </g>
    <g v-else-if="name === 'ollama'">
      <path fill="currentColor" stroke="none" d="M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 14.5h-2v-2h2v2zm0-4h-2V7h2v5.5z" />
    </g>

    <!-- Default generic icon -->
    <circle v-else cx="12" cy="12" r="10" />
  </svg>
</template>

<script setup>
import { computed } from 'vue'

const props = defineProps({
  name: {
    type: String,
    required: true
  },
  size: {
    type: [Number, String],
    default: 16
  },
  customClass: {
    type: String,
    default: ''
  },
  strokeWidth: {
    type: [Number, String],
    default: 1.8
  }
})

const isFillIcon = computed(() => {
  return ['github', 'anthropic', 'openai', 'google', 'deepseek', 'ollama'].includes(props.name)
})

const viewBox = computed(() => {
  if (['anthropic', 'openai'].includes(props.name)) return '0 0 24 24'
  return '0 0 24 24'
})

const fillMode = computed(() => {
  return isFillIcon.value ? 'currentColor' : 'none'
})

const strokeMode = computed(() => {
  return isFillIcon.value ? 'none' : 'currentColor'
})
</script>

<style scoped>
.noah-icon {
  display: inline-block;
  vertical-align: middle;
  flex-shrink: 0;
}
</style>
