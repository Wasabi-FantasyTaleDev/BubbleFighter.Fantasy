<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import { useI18n } from '../i18n'

const props = defineProps({ items: { type: Array, default: () => [] } })
const { t } = useI18n()
const host = ref(null)
const measurement = ref(null)
const moreMeasurement = ref(null)
const visibleCount = ref(0)
const hiddenItems = computed(() => props.items.slice(visibleCount.value))
let observer
function measure() {
  if (!host.value || !measurement.value) return
  const width = host.value.clientWidth
  if (!width) return
  const elements = [...measurement.value.querySelectorAll('[data-rule-badge]')]
  const widths = elements.map(element => Math.min(element.getBoundingClientRect().width, width))
  const rowHeight = elements[0]?.getBoundingClientRect().height || 27
  const rows = Math.max(1, Math.floor((host.value.clientHeight + 6) / (rowHeight + 6)))
  function fits(values) {
    let row = 1
    let used = 0
    for (const value of values) {
      if (used && used + 6 + value > width + 0.5) { row++; used = 0 }
      if (row > rows) return false
      used += (used ? 6 : 0) + value
    }
    return true
  }
  if (fits(widths)) { visibleCount.value = props.items.length; return }
  for (let count = props.items.length - 1; count >= 0; count--) {
    moreMeasurement.value.textContent = '+' + (props.items.length - count)
    const moreWidth = moreMeasurement.value.parentElement.getBoundingClientRect().width
    if (fits([...widths.slice(0, count), moreWidth])) { visibleCount.value = count; return }
  }
  visibleCount.value = 0
}
watch(() => props.items, () => nextTick(measure), { deep: true })
onMounted(() => { observer = new ResizeObserver(measure); observer.observe(host.value); measure() })
onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <span ref="host" class="policy-badges">
    <span class="policy-badges-visible">
      <NoahTag v-for="item in items.slice(0, visibleCount)" :key="item.key" pill size="sm" :tone="item.tone" :title="item.label">{{ item.label }}</NoahTag>
      <NoahTag v-if="hiddenItems.length" pill size="sm" :title="hiddenItems.map(item => item.label).join('\n')">+{{ hiddenItems.length }}</NoahTag>
      <NoahTag v-if="!items.length" pill size="sm">{{ t('plugins.policy.unset') }}</NoahTag>
    </span>
    <span ref="measurement" class="policy-badges-measure" aria-hidden="true" inert>
      <NoahTag v-for="item in items" :key="item.key" data-rule-badge pill size="sm" :tone="item.tone">{{ item.label }}</NoahTag>
      <NoahTag pill size="sm"><span ref="moreMeasurement">+{{ items.length }}</span></NoahTag>
    </span>
  </span>
</template>

<style scoped>
.policy-badges { display: block; position: relative; width: 100%; height: 27px; min-height: 27px; overflow: hidden; }
.policy-badges-visible { display: flex; flex-direction: row-reverse; align-items: flex-start; justify-content: flex-start; flex-wrap: wrap; gap: 6px; }
.policy-badges :deep(.noah-tag) { padding: 5px 9px; font-size: var(--text-caption); line-height: 1.2; max-width: 100%; box-sizing: border-box; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.policy-badges-measure { position: absolute; top: 0; left: 0; visibility: hidden; pointer-events: none; display: flex; width: max-content; gap: 6px; }
.policy-badges-measure :deep(.noah-tag) { flex: none; max-width: none; }
</style>
