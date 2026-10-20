<template>
  <div
    :class="[
      'noah-card',
      `noah-card-${variant}`,
      { 'is-hoverable': hoverable, 'has-header': !!title || $slots.title || $slots.extra }
    ]"
  >
    <div v-if="title || $slots.title || $slots.extra" class="noah-card-header">
      <div class="noah-card-title-wrap">
        <slot name="title">
          <h3 class="noah-card-title">{{ title }}</h3>
        </slot>
      </div>
      <div v-if="$slots.extra" class="noah-card-extra">
        <slot name="extra" />
      </div>
    </div>

    <div class="noah-card-body" :style="{ padding: bodyPadding }">
      <slot />
    </div>

    <div v-if="$slots.footer" class="noah-card-footer">
      <slot name="footer" />
    </div>
  </div>
</template>

<script setup>
defineProps({
  title: {
    type: String,
    default: ''
  },
  variant: {
    type: String,
    default: 'surface' // 'surface' | 'raised' | 'glass' | 'bordered'
  },
  hoverable: {
    type: Boolean,
    default: false
  },
  bodyPadding: {
    type: String,
    default: ''
  }
})
</script>

<style scoped>
.noah-card {
  border-radius: var(--radius);
  border: 1px solid var(--border);
  background-color: var(--surface);
  transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
  overflow: hidden;
  position: relative;
}

.noah-card-surface {
  background-color: var(--surface);
  border-color: var(--border);
}

.noah-card-raised {
  background-color: var(--surface-raised);
  border-color: var(--border);
  box-shadow: var(--shadow-sm);
}

.noah-card-glass {
  background-color: var(--glass-panel);
  backdrop-filter: var(--glass-blur);
  -webkit-backdrop-filter: var(--glass-blur);
  border-color: var(--border);
}

.noah-card.is-hoverable:hover {
  border-color: var(--text-faint);
  box-shadow: var(--shadow);
  transform: translateY(-2px);
}

.noah-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-bottom: 1px solid var(--border-soft);
  gap: 12px;
}

.noah-card-title {
  margin: 0;
  font-size: var(--text-subtitle);
  font-weight: 600;
  color: var(--text);
  line-height: var(--leading-tight);
}

.noah-card-extra {
  display: flex;
  align-items: center;
  gap: 8px;
}

.noah-card-body {
  padding: 16px;
}

.noah-card-footer {
  padding: 12px 16px;
  border-top: 1px solid var(--border-soft);
  background-color: var(--surface-raised);
}
</style>
