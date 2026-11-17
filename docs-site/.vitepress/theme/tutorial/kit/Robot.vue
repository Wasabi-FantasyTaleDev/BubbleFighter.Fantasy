<script setup lang="ts">
// A small generic helper robot that peeks over the chat box; drawn for the tutorial.
defineProps<{ size?: number; busy?: boolean }>()
</script>

<template>
  <svg class="tp-robot" :class="{ 'is-busy': busy }" :width="size ?? 64" :height="size ?? 64" viewBox="0 0 64 64" aria-hidden="true">
    <path d="M14 22 10 8l13 8M50 22l4-14-13 8" fill="#2b2d31" />
    <rect x="9" y="14" width="46" height="36" rx="16" fill="#3a3d42" />
    <rect x="14" y="21" width="36" height="20" rx="9" fill="#0f1114" />
    <g class="tp-robot-eyes">
      <rect x="22" y="26" width="5" height="10" rx="2.5" fill="#35e1d0" />
      <rect x="37" y="26" width="5" height="10" rx="2.5" fill="#35e1d0" />
    </g>
    <rect x="5" y="25" width="6" height="12" rx="3" fill="#2b2d31" />
    <rect x="53" y="25" width="6" height="12" rx="3" fill="#2b2d31" />
    <path d="M18 50h28v6a6 6 0 0 1-6 6H24a6 6 0 0 1-6-6z" fill="#e7e8ea" />
    <rect x="27" y="53" width="10" height="4" rx="2" fill="#9aa1a9" />
  </svg>
</template>

<style>
.tp-robot-eyes { transform-box: fill-box; transform-origin: center; animation: tp-robot-blink 4.2s infinite; }
.tp-robot.is-busy .tp-robot-eyes { animation: tp-robot-look 1.6s ease-in-out infinite; }
@keyframes tp-robot-blink { 0%, 92%, 100% { transform: scaleY(1); } 95% { transform: scaleY(.1); } }
@keyframes tp-robot-look { 0%, 100% { transform: translateX(-2px); } 50% { transform: translateX(2px); } }
</style>
