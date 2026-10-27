<script setup>
import { computed } from 'vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import { useI18n } from '../i18n'

const props = defineProps({ plan: { type: Object, default: null }, busy: Boolean, error: { type: Object, default: null } })
const emit = defineEmits(['cancel', 'confirm'])
const { t } = useI18n()
const offer = computed(() => props.plan?.selection.selection.package)
const size = computed(() => {
  const bytes = props.plan?.totalBytes ?? 0
  if (bytes < 1024) return `${bytes.toLocaleString()} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
})
</script>

<template>
  <NoahModal :model-value="!!plan" :title="t('store.downloadStaging')" width="520px"
    :close-label="t('common.close')" :show-close="!busy" :close-on-click-overlay="!busy"
    @update:model-value="!$event && !busy && emit('cancel')">
    <div v-if="plan" class="distribution-plan-content">
      <div class="plan-package"><span class="plan-package-icon" aria-hidden="true"><NoahIcon name="plugins" :size="26" /></span>
        <div><h3>{{ offer.name }}</h3><span class="text-caption text-muted">{{ offer.version }}</span></div>
      </div>
      <dl class="plan-details">
        <dt>{{ t('store.downloadSource') }}</dt><dd>{{ plan.selection.source.name }}</dd>
        <dt>{{ t('store.totalDownload') }}</dt><dd>{{ size }}</dd>
      </dl>
      <p v-if="plan.format === 'repository-asset'" role="status" class="text-caption text-muted">{{ t('store.repositoryDownloadBoundary') }}</p>
      <p v-if="error" role="alert">{{ error.message }}</p>
    </div>
    <template #footer>
      <NoahButton variant="glass" :disabled="busy" @click="emit('cancel')">{{ t('common.cancel') }}</NoahButton>
      <NoahButton variant="primary" :loading="busy" @click="emit('confirm')">{{ t('store.confirmDownload') }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
.distribution-plan-content { display: grid; gap: 24px; }
.plan-package { display: flex; align-items: center; gap: 16px; min-width: 0; }
.plan-package > div { min-width: 0; display: grid; gap: 6px; }
.plan-package h3 { font-size: var(--text-body); font-weight: 550; margin: 0; line-height: 1.5; overflow-wrap: anywhere; }
.plan-package-icon { display: grid; place-items: center; width: 52px; height: 52px; flex: none; background: var(--surface-raised); border: 1px solid var(--border-soft); border-radius: 8px; color: var(--text-muted); }
:global(.noah-modal-overlay:has(.distribution-plan-content)) { background: var(--backdrop-overlay); backdrop-filter: blur(8px); }
:global(.noah-modal-dialog:has(.distribution-plan-content)) { background: var(--surface); }
.plan-details { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 2fr); gap: 16px; margin: 0; font-size: var(--text-control); }
.plan-details dt { color: var(--text-muted); }
.plan-details dd { margin: 0; text-align: right; overflow-wrap: anywhere; }
.distribution-plan-content p { margin: 0; line-height: 1.7; overflow-wrap: anywhere; }
</style>
