<script setup lang="ts">
import { ref } from 'vue'
// The owner chose to show the product's own logo, linked from its official CDN instead of copied
// into this repository. The file is the tile plus wordmark (1620x560); `tile` crops to the square.
// If the link stops resolving, a drawn stand-in takes its place.
const logoUrl = 'https://assets.workbuddy.cn/web/workbuddy/8e036eb06f87c6b08437546b3cbd58e60af45b10/assets/workbuddy-logo-WhgOvEF7.png'
const props = withDefaults(defineProps<{ size?: number; full?: boolean }>(), { size: 20, full: false })
const failed = ref(false)
</script>

<template>
  <img v-if="!failed" class="tp-appmark" :class="{ 'is-full': props.full }" :src="logoUrl" alt="" referrerpolicy="no-referrer"
    :style="props.full ? { height: `${props.size}px`, width: `${props.size * 1620 / 560}px` } : { height: `${props.size}px`, width: `${props.size}px` }"
    @error="failed = true">
  <span v-else class="tp-appmark-fallback" :style="{ height: `${props.size}px` }">
    <svg :width="props.size" :height="props.size" viewBox="0 0 32 32" aria-hidden="true">
      <defs><linearGradient id="tp-appmark-g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#23c99b" /><stop offset="1" stop-color="#8be06a" /></linearGradient></defs>
      <rect x="1" y="1" width="30" height="30" rx="8" fill="url(#tp-appmark-g)" />
      <path d="M4 26V14c0-3 2-5 5-5h3l4-5 2 5h3c4 0 7 3 7 7v10z" fill="#fff" />
      <rect x="12" y="17" width="2.6" height="5" rx="1.3" fill="#1aa37c" /><rect x="19" y="17" width="2.6" height="5" rx="1.3" fill="#1aa37c" />
    </svg>
    <b v-if="props.full" :style="{ fontSize: `${props.size * .36}px` }">WORK<br>BUDDY&gt;_</b>
  </span>
</template>

<style>
.tp-appmark { display: block; flex: none; object-fit: cover; object-position: left center; border-radius: 22%; }
.tp-appmark.is-full { border-radius: 0; object-fit: contain; }
.tp-appmark-fallback { display: inline-flex; align-items: center; gap: 4px; line-height: .95; letter-spacing: .02em; }
</style>
