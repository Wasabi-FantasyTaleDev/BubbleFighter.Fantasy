<script setup>
import { computed } from 'vue'
import rankAtlas from '../assets/player-ranks.png'

const props = defineProps({ level: { type: Number, required: true } })
const position = computed(() => {
  const index = Math.max(0, Math.trunc(props.level) - 1)
  return `${-(index % 5) * 23}px ${-Math.floor(index / 5) * 26}px`
})
</script>

<template><span class="player-level-icon" aria-hidden="true" :style="{ backgroundImage: `url(${rankAtlas})`, backgroundPosition: position }" /></template>

<style scoped>
.player-level-icon { display:inline-block; flex:none; width:23px; height:26px; background-repeat:no-repeat; }
</style>
