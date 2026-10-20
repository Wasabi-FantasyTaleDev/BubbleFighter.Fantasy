<template>
  <div class="noah-metric-card">
    <div class="noah-metric-header">
      <span class="noah-metric-label">{{ label }}</span>
      <div v-if="icon || $slots.icon" class="noah-metric-icon-wrap">
        <slot name="icon">
          <NoahIcon :name="icon" :size="16" />
        </slot>
      </div>
    </div>

    <div class="noah-metric-value-row">
      <span class="noah-metric-value">{{ value }}</span>
      <span v-if="unit" class="noah-metric-unit">{{ unit }}</span>
    </div>

    <div v-if="subtext || trend" class="noah-metric-footer">
      <span
        v-if="trend"
        :class="[
          'noah-metric-trend',
          `is-${trendDirection}`
        ]"
      >
        {{ trend }}
      </span>
      <span v-if="subtext" class="noah-metric-subtext">{{ subtext }}</span>
    </div>
  </div>
</template>

<script setup>
import NoahIcon from '../icons/NoahIcon.vue'

defineProps({
  label: {
    type: String,
    required: true
  },
  value: {
    type: [String, Number],
    required: true
  },
  unit: {
    type: String,
    default: ''
  },
  icon: {
    type: String,
    default: ''
  },
  subtext: {
    type: String,
    default: ''
  },
  trend: {
    type: String,
    default: ''
  },
  trendDirection: {
    type: String,
    default: 'up' // 'up' | 'down' | 'neutral'
  }
})
</script>

<style scoped>
.noah-metric-card {
  padding: 16px;
  background-color: var(--surface);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  display: flex;
  flex-direction: column;
  gap: 8px;
  transition: all 0.15s ease;
}
.noah-metric-card:hover {
  border-color: var(--text-faint);
  box-shadow: var(--shadow-sm);
}

.noah-metric-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.noah-metric-label {
  font-size: var(--text-caption);
  color: var(--text-muted);
  font-weight: 500;
}

.noah-metric-icon-wrap {
  color: var(--text-muted);
}

.noah-metric-value-row {
  display: flex;
  align-items: baseline;
  gap: 4px;
}

.noah-metric-value {
  font-size: var(--text-title);
  font-weight: 700;
  color: var(--text);
  font-family: var(--font-ui);
  line-height: 1.1;
  letter-spacing: -0.02em;
}

.noah-metric-unit {
  font-size: var(--text-caption);
  color: var(--text-muted);
}

.noah-metric-footer {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: var(--text-caption);
}

.noah-metric-trend.is-up {
  color: var(--green);
  font-weight: 600;
}
.noah-metric-trend.is-down {
  color: var(--red);
  font-weight: 600;
}
.noah-metric-trend.is-neutral {
  color: var(--text-muted);
}

.noah-metric-subtext {
  color: var(--text-faint);
}
</style>
