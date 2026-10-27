<script setup>
import { computed, ref, watch } from 'vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import PluginConfigurationFields from './PluginConfigurationFields.vue'
import ErrorCard from './ErrorCard.vue'
import { api, jsonRequest } from '../api/client'
import { useI18n } from '../i18n'

const props = defineProps({ installation: { type: Object, default: null } })
const emit = defineEmits(['close', 'saved'])
const { t } = useI18n()
const open = computed({ get: () => !!props.installation, set: value => { if (!value && !saving.value) emit('close') } })
const loading = ref(false)
const saving = ref(false)
const error = ref(null)
const revision = ref(0)
const rows = ref([])
const advanced = ref(false)
const text = ref('')
let requestId = 0
watch(() => props.installation, async installation => {
  const id = ++requestId
  rows.value = []
  error.value = null
  advanced.value = false
  if (!installation) return
  loading.value = true
  try {
    const result = await api('distribution/v1/server-plugins/' + encodeURIComponent(installation.installationId) + '/configuration')
    if (id !== requestId) return
    revision.value = result.revision
    rows.value = result.rows.map(row => ({ ...row, config: row.config || {} }))
  } catch (failure) { if (id === requestId) error.value = failure }
  finally { if (id === requestId) loading.value = false }
})
function toggleAdvanced() {
  if (advanced.value) {
    try {
      const values = JSON.parse(text.value)
      if (values === null || Array.isArray(values) || typeof values !== 'object' ||
          Object.keys(values).some(id => !rows.value.some(row => row.id === id)) ||
          rows.value.some(row => !values[row.id] || Array.isArray(values[row.id]) || typeof values[row.id] !== 'object'))
        throw new Error(t('store.configurationInvalid'))
      rows.value = rows.value.map(row => ({ ...row, config: values[row.id] }))
    } catch (failure) { error.value = failure; return false }
  } else text.value = JSON.stringify(Object.fromEntries(rows.value.map(row => [row.id, row.config])), null, 2)
  error.value = null
  advanced.value = !advanced.value
  return true
}
async function save() {
  if (!props.installation || saving.value || (advanced.value && !toggleAdvanced())) return
  saving.value = true
  error.value = null
  try {
    const result = await api('distribution/v1/server-plugins/' + encodeURIComponent(props.installation.installationId) + '/configuration',
      jsonRequest('PUT', { revision: revision.value, rows: Object.fromEntries(rows.value.map(row => [row.id, row.config])) }))
    emit('saved', result)
    emit('close')
  } catch (failure) { error.value = failure }
  finally { saving.value = false }
}
</script>

<template>
  <NoahModal v-model="open" :title="t('store.configurePlugin')" width="640px" :close-label="t('common.close')">
    <div class="plugin-configuration-dialog">
      <p v-if="installation" class="text-muted">{{ installation.package.id }} · {{ installation.version }}</p>
      <NoahSkeleton v-if="loading" :rows="3" />
      <template v-else>
        <ErrorCard v-if="error" :error="error" />
        <textarea v-if="advanced" v-model="text" :disabled="saving" class="plugin-config-json mono" :aria-label="t('store.configurationJson')" spellcheck="false" />
        <template v-else>
          <section v-for="row in rows" :key="row.id" class="plugin-config-row">
            <h3>{{ row.id }}</h3><p class="text-caption text-muted mono">{{ row.plugin }}</p>
            <PluginConfigurationFields v-model="row.config" :disabled="saving" />
            <p v-if="!Object.keys(row.config).length" class="text-muted">{{ t('store.noConfigurationDefaults') }}</p>
          </section>
        </template>
        <NoahButton v-if="rows.length" variant="glass" :disabled="saving" @click="toggleAdvanced">{{ t(advanced ? 'store.configurationForm' : 'store.configurationJson') }}</NoahButton>
      </template>
    </div>
    <template #footer>
      <NoahButton variant="glass" :disabled="saving" @click="open = false">{{ t('common.cancel') }}</NoahButton>
      <NoahButton variant="primary" :disabled="loading || !rows.length" :loading="saving" @click="save">{{ t('common.save') }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
.plugin-configuration-dialog { display: grid; gap: 20px; }
.plugin-config-row { padding: 16px; border: 1px solid var(--border); border-radius: 16px; min-width: 0; }
.plugin-config-row h3 { margin: 0; }
.plugin-config-json { width: 100%; min-height: 280px; resize: vertical; color: var(--text-primary); background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 14px; box-sizing: border-box; }
</style>
