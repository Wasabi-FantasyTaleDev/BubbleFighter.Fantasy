<script setup lang="ts">
import AppMark from './AppMark.vue'
// Product marks for the tutorial scenes. WorkBuddy uses the owner-approved logo link; the others
// are simple drawn stand-ins that only suggest each product's mark.
withDefaults(defineProps<{ name: string; size?: number; tone?: string }>(), { size: 20, tone: 'currentColor' })
</script>

<template>
  <AppMark v-if="name === 'workbuddy'" :size="size" />
  <svg v-else-if="name === 'dsh'" class="tp-brand" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <path :fill="tone" d="M29.4 9.6c-.6-.3-1 .2-1.4.6-.9 1-1.8 1.4-3 .8-3-1.6-6.1-2-9.2-1.2C10.6 11.1 6.7 14.8 5 19.8c-.4 1.1-.6 2.3-.6 3.4-1-.7-1.9-1.6-2.6-2.7-.2-.3-.6-.3-.7.1-.4 3.3 1.6 6.3 4.7 7.4 4.6 1.6 10 .6 13.8-2.6 2.8-2.4 4.6-5.6 5.2-9.2.1-.7.6-1 1.2-1.3 1.6-.9 2.9-2.2 3.6-3.9.2-.5.1-1.1-.2-1.4zM21.4 15.2a1.2 1.2 0 1 1 0-2.4 1.2 1.2 0 0 1 0 2.4zM13 26.7c2.4-1.9 3.9-4.4 4.2-7.4 0 2.5-.6 5-2.3 7-.6.6-1.3.8-1.9.4z" />
  </svg>
  <svg v-else-if="name === 'chatgpt'" class="tp-brand" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <g fill="none" :stroke="tone" stroke-width="2.2" stroke-linejoin="round">
      <path v-for="r in [0, 60, 120, 180, 240, 300]" :key="r" :transform="`rotate(${r} 16 16)`" d="M16 4.5c3.6 0 6.2 2.7 6.2 6v6.2L16 20.3l-6.2-3.6" />
    </g>
  </svg>
  <svg v-else-if="name === 'codex'" class="tp-brand" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <path fill="none" :stroke="tone" stroke-width="1.6" d="M16 4.5c2.3 0 4.2 1.4 5 3.4 2.5-.3 4.8 1.4 5.3 3.9.4 1.8-.3 3.6-1.6 4.7 1 1.6 1 3.7-.2 5.3-1.2 1.6-3.2 2.2-5 1.8-.9 2-2.9 3.4-5.2 3.3-2.1 0-3.9-1.3-4.8-3.1-2 .4-4.1-.4-5.2-2.1-1-1.6-1-3.6 0-5.2-1.4-1.1-2.1-3-1.6-4.8.6-2.4 2.9-3.9 5.3-3.6.8-2.3 2.7-3.8 5-3.8z" />
    <path fill="none" :stroke="tone" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" d="m11.5 13 2.6 2.7-2.6 2.6M16.4 19h4" />
  </svg>
  <svg v-else-if="name === 'store'" class="tp-brand" :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true">
    <path d="M9 9V7a7 7 0 0 1 14 0v2" fill="none" stroke="#2d6bd8" stroke-width="2" />
    <rect x="4" y="9" width="24" height="20" rx="3" fill="#2d6bd8" />
    <rect x="10" y="13" width="5.5" height="5.5" fill="#f25022" /><rect x="16.5" y="13" width="5.5" height="5.5" fill="#7fba00" />
    <rect x="10" y="19.5" width="5.5" height="5.5" fill="#00a4ef" /><rect x="16.5" y="19.5" width="5.5" height="5.5" fill="#ffb900" />
  </svg>
</template>

<style>
.tp-brand { display: block; flex: none; }
</style>
