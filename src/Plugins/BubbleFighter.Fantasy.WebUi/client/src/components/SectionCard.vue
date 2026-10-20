<script setup>
import NoahCard from '../noah/components/ui/NoahCard.vue'

/** A NOAH II card with the interface's section heading: title and subtitle on the left, an optional extra on the right. */
defineProps({
  title: { type: String, default: '' },
  sub: { type: String, default: '' }
})
</script>

<template>
  <NoahCard body-padding="20px">
    <div v-if="title || $slots.extra" class="section-head">
      <div>
        <h2 class="section-title">{{ title }}</h2>
        <p v-if="sub" class="section-sub">{{ sub }}</p>
      </div>
      <div v-if="$slots.extra" class="section-extra">
        <slot name="extra" />
      </div>
    </div>
    <slot />
  </NoahCard>
</template>
