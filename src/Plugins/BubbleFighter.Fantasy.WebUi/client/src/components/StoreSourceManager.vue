<script setup>
import GithubMirrorSettings from './GithubMirrorSettings.vue'
import { computed, ref, watch } from 'vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import { api, jsonRequest } from '../api/client'
import { useI18n } from '../i18n'

const props = defineProps({ modelValue: Boolean, sources: { type: Array, default: () => [] }, revision: { type: Number, default: 0 } })
const emit = defineEmits(['update:modelValue', 'saved'])
const { t } = useI18n()
const sources = ref([])
const revision = ref(0)
const edit = ref(null)
const removing = ref(null)
const originalId = ref(null)
const busy = ref(false)
const failure = ref(null)
const kindOptions = ['http', 'alist', 'github'].map(value => ({ value, label: value === 'github' ? 'GitHub' : value === 'alist' ? 'AList' : 'HTTP' }))
const title = computed(() => removing.value ? t('store.removeSourceTitle') : edit.value ? t(originalId.value ? 'store.editSource' : 'store.addSource') : t('store.manageSources'))
watch(() => props.modelValue, open => {
  if (!open) return
  sources.value = props.sources.map(source => ({ ...source }))
  revision.value = props.revision
  edit.value = null; removing.value = null; failure.value = null
})
function begin(source) {
  originalId.value = source?.id ?? null
  edit.value = source ? { ...source } : { id: 'source-' + crypto.randomUUID().slice(0, 12), name: '', kind: 'http', baseUrl: '', catalogPath: 'source.toml' }
  failure.value = null
}
function back() { edit.value = null; removing.value = null; failure.value = null }
async function persist(candidate) {
  if (busy.value) return
  busy.value = true; failure.value = null
  try {
    const saved = await api('distribution/v1/sources', jsonRequest('PUT', { revision: revision.value, sources: candidate }))
    sources.value = saved.sources.map(source => ({ ...source }))
    revision.value = saved.revision
    back()
    emit('saved')
  } catch (error) { failure.value = error }
  finally { busy.value = false }
}
function save() {
  const candidate = sources.value.map(source => ({ ...source }))
  const value = { ...edit.value, name: edit.value.name.trim(), baseUrl: edit.value.baseUrl.trim() }
  if (originalId.value === null) candidate.push(value)
  else candidate[candidate.findIndex(source => source.id === originalId.value)] = value
  return persist(candidate)
}
function move(index, delta) {
  const candidate = [...sources.value]
  ;[candidate[index], candidate[index + delta]] = [candidate[index + delta], candidate[index]]
  return persist(candidate)
}
function close(open) {
  if (busy.value) return
  if (!open && (edit.value || removing.value)) back()
  else emit('update:modelValue', open)
}
</script>

<template>
  <NoahModal :model-value="modelValue" :title="title" :close-label="t(edit || removing ? 'common.cancel' : 'common.close')" :show-close="!busy" :close-on-click-overlay="!busy" width="680px" @update:model-value="close">
    <div class="source-manager">
      <template v-if="removing">
        <p>{{ t('store.removeSourceBody', { name: removing.name }) }}</p>
        <code class="source-address">{{ removing.baseUrl }}</code>
      </template>
      <form v-else-if="edit" id="store-source-form" class="source-edit-form" @submit.prevent="save">
        <fieldset :disabled="busy">
          <label>{{ t('store.sourceName') }}<NoahInput v-model="edit.name" required /></label>
          <div class="source-form-row">
            <div class="source-field"><span>{{ t('store.sourceKind') }}</span><NoahSelect v-model="edit.kind" :options="kindOptions" :aria-label="t('store.sourceKind')" :disabled="busy" /></div>
            <label>{{ t('store.sourceRoot') }}<NoahInput v-model="edit.baseUrl" type="url" required placeholder="https://example.org/" /></label>
          </div>
          <details class="source-advanced"><summary>{{ t('store.advanced') }}</summary>
            <label>{{ t('store.catalogPath') }}<NoahInput v-model="edit.catalogPath" required placeholder="source.toml" /></label>
            <label>{{ t('store.sourceId') }}<NoahInput v-model="edit.id" :readonly="originalId !== null" required /></label>
          </details>
        </fieldset>
      </form>
      <template v-else>
        <div class="source-list-heading"><p class="text-caption text-muted">{{ t('store.sourceOrderHint') }}</p><NoahButton icon="plus" :disabled="busy" @click="begin()">{{ t('store.addSource') }}</NoahButton></div>
        <div class="source-manager-list">
          <article v-for="(source, index) in sources" :key="source.id" class="source-manager-row">
            <span class="source-transport-icon" aria-hidden="true"><NoahIcon name="globe" :size="22" /></span>
            <div class="source-manager-copy"><strong>{{ source.name }}</strong><span class="source-address">{{ source.baseUrl }}</span><small>{{ kindOptions.find(kind => kind.value === source.kind)?.label }} · {{ source.catalogPath }}</small></div>
            <div class="source-manager-actions">
              <NoahButton icon="chevron-up" icon-only size="sm" :title="t('store.moveUp')" :aria-label="t('store.moveUp') + ': ' + source.name" :disabled="busy || index === 0" @click="move(index, -1)" />
              <NoahButton icon="chevron-down" icon-only size="sm" :title="t('store.moveDown')" :aria-label="t('store.moveDown') + ': ' + source.name" :disabled="busy || index === sources.length - 1" @click="move(index, 1)" />
              <NoahButton icon="edit" icon-only size="sm" :title="t('store.editSource')" :aria-label="t('store.editSource') + ': ' + source.name" :disabled="busy" @click="begin(source)" />
              <NoahButton icon="trash" icon-only size="sm" :title="t('store.remove')" :aria-label="t('store.remove') + ': ' + source.name" :disabled="busy" @click="removing = source; failure = null" />
            </div>
          </article>
        </div>
        <p v-if="!sources.length" class="text-muted">{{ t('store.noSources') }}</p>
        <GithubMirrorSettings />
      </template>
      <p v-if="failure" role="alert" class="source-error">{{ failure.message }}</p>
    </div>
    <template #footer>
      <NoahButton variant="glass" :disabled="busy" @click="edit || removing ? back() : close(false)">{{ t(edit || removing ? 'common.cancel' : 'common.close') }}</NoahButton>
      <NoahButton v-if="edit" variant="primary" html-type="submit" form="store-source-form" :loading="busy" :disabled="!edit.name.trim() || !edit.baseUrl.trim()">{{ t('common.save') }}</NoahButton>
      <NoahButton v-if="removing" variant="primary" :loading="busy" @click="persist(sources.filter(source => source.id !== removing.id))">{{ t('store.remove') }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
:global(.noah-modal-overlay:has(.source-manager)) { background: var(--backdrop-overlay); backdrop-filter: blur(8px); }
:global(.noah-modal-dialog:has(.source-manager)) { background: var(--surface); }
.source-list-heading { display: flex; align-items: center; justify-content: space-between; gap: 18px; margin-bottom: 18px; }
.source-list-heading p { margin: 0; line-height: 1.6; }
.source-list-heading .noah-btn { flex: none; }
.source-manager-list { display: grid; gap: 10px; }
.source-manager-row { display: flex; align-items: center; gap: 14px; padding: 16px; border: 1px solid var(--border); border-radius: 8px; }
.source-transport-icon { flex: none; color: var(--text-muted); }
.source-manager-copy { min-width: 0; flex: 1; display: grid; gap: 5px; font-size: var(--text-control); }
.source-manager-copy strong { overflow-wrap: anywhere; }
.source-manager-copy small, .source-address { color: var(--text-muted); font-size: var(--text-caption); overflow-wrap: anywhere; }
.source-manager-actions { display: flex; gap: 4px; flex: none; }
.source-edit-form fieldset { border: 0; padding: 0; margin: 0; min-width: 0; display: grid; gap: 20px; }
.source-edit-form label, .source-field { display: grid; gap: 8px; font-size: var(--text-control); min-width: 0; }
.source-form-row { display: grid; grid-template-columns: minmax(100px, 1fr) minmax(0, 2.4fr); gap: 14px; }
.source-advanced { border: 1px solid var(--border); border-radius: 6px; padding: 14px; font-size: var(--text-control); }
.source-advanced summary { cursor: pointer; color: var(--text-muted); }
.source-advanced label { margin-top: 16px; }
.source-error { color: var(--red); overflow-wrap: anywhere; font-size: var(--text-control); }
@media (max-width: 560px) { .source-manager-row { flex-wrap: wrap; } .source-manager-actions { margin-left: auto; } .source-list-heading { align-items: start; } .source-form-row { grid-template-columns: minmax(0, 1fr); } }
</style>
