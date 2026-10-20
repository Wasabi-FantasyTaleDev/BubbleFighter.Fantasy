<script setup>
import { onMounted, onBeforeUnmount, ref } from 'vue'
import { observeReveal } from '../../utils/reveal.js'
import '../../assets/styles/motion.css'

defineProps({
  as: { type: String, default: 'div' },
  delay: { type: Number, default: 0, validator: value => Number.isFinite(value) && value >= 0 && value <= 600 },
})
const element = ref()
let dispose
onMounted(() => { dispose = observeReveal(element.value) })
onBeforeUnmount(() => dispose?.())
</script>

<template>
  <component :is="as" ref="element" class="noah-reveal" :style="{ '--noah-reveal-delay': `${delay}ms` }"><slot /></component>
</template>
