<script setup lang="ts">
import { computed } from 'vue'
import Brand from './Brand.vue'
import { osText } from './os'
import type { Code } from '../types'
// A Windows-like desktop: soft wallpaper and a centred taskbar. `apps` lists running apps to mark;
// `brand` is the agent app pinned once installed.
const props = withDefaults(defineProps<{ apps?: string[]; showAgent?: boolean; brand?: string; lang?: Code }>(), { brand: 'workbuddy', lang: 'zh-CN' })
const os = computed(() => osText(props.lang))
</script>

<template>
  <div class="tp-desk">
    <div class="tp-wall" />
    <slot />
    <div class="tp-taskbar">
      <div class="tp-taskbar-apps">
        <span class="tp-tb-start"><i /><i /><i /><i /></span>
        <span class="tp-tb-search" data-tp="tb-search"><svg viewBox="0 0 24 24" width="18" height="18"><path d="M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zm9 2-4-4" fill="none" stroke="#333" stroke-width="2" stroke-linecap="round" /></svg>{{ os.search }}</span>
        <span class="tp-tb-app" :class="{ 'is-running': apps?.includes('explorer') }">
          <svg viewBox="0 0 24 24" width="22" height="22"><path d="M2 6a2 2 0 0 1 2-2h5l2 2h9a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z" fill="#f4c542" /><path d="M2 9h20v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2z" fill="#ffd866" /></svg>
        </span>
        <span class="tp-tb-app" :class="{ 'is-running': apps?.includes('browser') }">
          <svg viewBox="0 0 24 24" width="22" height="22"><defs><linearGradient id="tp-br" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#36c1f0" /><stop offset="1" stop-color="#1a6fd8" /></linearGradient></defs><circle cx="12" cy="12" r="10" fill="url(#tp-br)" /><path d="M5 13c1-4 4-6 8-6 3 0 6 2 6 5 0 2-2 3-4 3-1.5 0-2.5-1-2.5-2" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" /></svg>
        </span>
        <span v-if="apps?.includes('store')" class="tp-tb-app is-running"><Brand name="store" :size="24" /></span>
        <span v-if="showAgent" class="tp-tb-app" :class="{ 'is-running': apps?.includes('agent') }"><Brand :name="brand" :size="22" :tone="brand === 'dsh' ? '#4d6bfe' : '#111'" /></span>
      </div>
      <div class="tp-tray"><span>{{ os.ime }}</span><span class="tp-tray-time">{{ os.clock }}<br>{{ os.date }}</span></div>
    </div>
  </div>
</template>
