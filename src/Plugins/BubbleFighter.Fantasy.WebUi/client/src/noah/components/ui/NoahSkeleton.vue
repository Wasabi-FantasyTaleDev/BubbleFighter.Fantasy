<template>
  <div v-if="loading" class="noah-skeleton" :class="{ 'is-animated': animated }">
    <div v-if="avatar" class="noah-skeleton-avatar" />
    <div class="noah-skeleton-paragraph">
      <div v-for="i in rows" :key="i" class="noah-skeleton-row" :style="{ width: getRowWidth(i) }" />
    </div>
  </div>
  <div v-else>
    <slot />
  </div>
</template>

<script setup>
const props = defineProps({
  loading: { type: Boolean, default: true },
  animated: { type: Boolean, default: true },
  rows: { type: Number, default: 3 },
  avatar: { type: Boolean, default: false }
})

const getRowWidth = (index) => {
  if (index === 1 && props.rows > 1) return '45%'
  if (index === props.rows && props.rows > 1) return '65%'
  return '100%'
}
</script>

<style scoped>
.noah-skeleton {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  width: 100%;
  padding: 4px 0;
}

.noah-skeleton-avatar {
  width: 38px;
  height: 38px;
  border-radius: 50%;
  background-color: var(--border);
  flex-shrink: 0;
}

.noah-skeleton-paragraph {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.noah-skeleton-row {
  height: 14px;
  border-radius: var(--radius-2xs);
  background-color: var(--border);
}

.noah-skeleton.is-animated .noah-skeleton-avatar,
.noah-skeleton.is-animated .noah-skeleton-row {
  background: linear-gradient(
    90deg,
    var(--border) 25%,
    var(--surface-hover) 50%,
    var(--border) 75%
  );
  background-size: 200% 100%;
  animation: noah-skeleton-loading 1.5s infinite ease-in-out;
}

@keyframes noah-skeleton-loading {
  0% { background-position: 200% 0; }
  100% { background-position: -200% 0; }
}
</style>
