<template>
  <div class="noah-empty">
    <div class="noah-empty-image">
      <slot name="image">
        <svg class="noah-empty-svg" viewBox="0 0 64 64" fill="none">
          <circle cx="32" cy="32" r="28" stroke="currentColor" stroke-width="1.5" stroke-dasharray="3 3" opacity="0.4"/>
          <path d="M22 36h20M26 28h12M28 20h8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
        </svg>
      </slot>
    </div>

    <div class="noah-empty-description text-control">
      <slot>{{ description || 'No Data Available' }}</slot>
    </div>

    <div v-if="$slots.extra" class="noah-empty-extra">
      <slot name="extra" />
    </div>
  </div>
</template>

<script setup>
defineProps({
  description: { type: String, default: '' },
  image: { type: String, default: '' }
})
</script>

<style scoped>
.noah-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  padding: 36px 16px;
  box-sizing: border-box;
}

.noah-empty-image {
  margin-bottom: 12px;
  color: var(--text-faint);
}

.noah-empty-svg {
  width: 56px;
  height: 56px;
}

.noah-empty-description {
  color: var(--text-muted);
  line-height: 1.5;
}

.noah-empty-extra {
  margin-top: 16px;
}
</style>
