<script setup lang="ts">
import { ref } from 'vue'
// The signed-in user's picture in the drawn apps, loaded live; the slot is shown until it loads or when it cannot.
withDefaults(defineProps<{ size?: number }>(), { size: 24 })
const state = ref<'loading' | 'ok' | 'failed'>('loading')
</script>

<template>
  <span class="tp-photo" :style="{ width: `${size}px`, height: `${size}px` }">
    <img v-show="state === 'ok'" src="https://github.com/Wasabi-FantasyTaleDev.png?size=96" alt="" referrerpolicy="no-referrer" @load="state = 'ok'" @error="state = 'failed'">
    <slot v-if="state !== 'ok'" />
  </span>
</template>

<style scoped>
.tp-photo { display: inline-grid; place-items: center; flex: none; overflow: hidden; border-radius: 50%; }
.tp-photo img { width: 100%; height: 100%; object-fit: cover; }
</style>
