<script setup>
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSwitch from '../noah/components/ui/NoahSwitch.vue'
import { api, jsonRequest } from '../api/client'
import { useI18n } from '../i18n'

const { t } = useI18n()
const state = ref(null)
const expanded = ref(true)
const saving = ref(false)
const testing = ref(false)
const dialog = ref(false)
const error = ref('')
let disposed = false
let poll
const options = computed(() => [{ value: 'auto', label: t('store.mirror.auto') }, ...(state.value?.providers || []).map(provider => ({ value: provider.id, label: provider.name }))])
const result = id => state.value?.results.find(row => row.id === id)
// While a test runs the list keeps its order so rows do not jump; afterwards the fastest available mirrors lead.
const rows = computed(() => (state.value?.providers || []).map(provider => ({ ...provider, result: result(provider.id) }))
  .sort((a, b) => testing.value ? 0 : (a.result?.state === 'available' ? 0 : 1) - (b.result?.state === 'available' ? 0 : 1) || (b.result?.bytesPerSecond ?? 0) - (a.result?.bytesPerSecond ?? 0)))
const inUse = id => state.value?.enabled && state.value.selection === id
const speed = row => `${((row.bytesPerSecond ?? 0) / 1048576).toFixed(2)} MB/s`

async function load() {
  try { state.value = await api('distribution/v1/github-mirror'); error.value = '' }
  catch (e) { if (!disposed) error.value = e.message }
}
async function save(change) {
  saving.value = true; error.value = ''
  try { state.value = await api('distribution/v1/github-mirror', jsonRequest('PUT', change)) }
  catch (e) { error.value = e.message }
  finally { saving.value = false }
}
async function follow() {
  await load()
  if (!disposed && testing.value) poll = setTimeout(follow, 400)
}
async function test() {
  dialog.value = true
  if (testing.value) return
  testing.value = true; error.value = ''
  follow()
  try { state.value = await api('distribution/v1/github-mirror/test', jsonRequest('POST', {})) }
  catch (e) { if (!disposed) error.value = e.message }
  finally { testing.value = false; clearTimeout(poll); if (!disposed) await load() }
}
onMounted(load)
onBeforeUnmount(() => { disposed = true; clearTimeout(poll) })
defineExpose({ load })
</script>

<template>
  <section v-if="state?.providers?.length" class="mirror-card">
    <div class="mirror-header">
      <NoahIcon name="github" :size="22" />
      <strong>{{ t('store.mirror.title') }}</strong>
      <NoahSwitch :model-value="!!state?.enabled" :disabled="!state || saving" :aria-label="t('store.mirror.title')" @update:model-value="save({ enabled: $event })" />
      <button type="button" class="mirror-expand" :aria-expanded="expanded" :aria-label="t('store.mirror.provider')" @click="expanded = !expanded"><NoahIcon :name="expanded ? 'chevron-up' : 'chevron-down'" :size="16" /></button>
    </div>
    <div v-if="expanded" class="mirror-choice">
      <span>{{ t('store.mirror.provider') }}</span>
      <div class="mirror-actions">
        <NoahSelect :model-value="state?.selection || 'auto'" :options="options" :disabled="!state || saving" :aria-label="t('store.mirror.provider')" @change="save({ selection: $event })" />
        <NoahButton variant="glass" :disabled="!state" @click="test">{{ t('store.mirror.test') }}</NoahButton>
      </div>
    </div>
    <p class="text-caption text-muted mirror-note">{{ t('store.mirror.note') }}</p>
    <p v-if="error && !dialog" class="mirror-error" role="alert">{{ error }}</p>
  </section>
  <NoahModal v-model="dialog" :title="t('store.mirror.testTitle')" width="560px" :close-label="t('common.close')">
    <div class="mirror-results">
      <article v-for="row in rows" :key="row.id" class="mirror-result">
        <span>{{ row.name }}</span>
        <div class="mirror-status">
          <span v-if="testing && (!row.result || ['pending', 'testing'].includes(row.result.state))" class="mirror-spinner" :aria-label="t('store.mirror.testing')" />
          <template v-else-if="row.result?.state === 'available'"><span>{{ speed(row.result) }}</span><small>{{ row.result.milliseconds }} ms</small></template>
          <span v-else class="text-muted">{{ t('store.mirror.' + (row.result?.state === 'failed' ? 'failed' : 'pending')) }}</span>
          <NoahButton size="sm" :variant="inUse(row.id) ? 'primary' : 'glass'" :disabled="saving || inUse(row.id)" @click="save({ enabled: true, selection: row.id })">{{ t(inUse(row.id) ? 'store.mirror.using' : 'store.mirror.use') }}</NoahButton>
        </div>
      </article>
    </div>
    <p v-if="error" class="mirror-error" role="alert">{{ error }}</p>
    <div class="mirror-test-footer"><NoahButton variant="glass" :disabled="testing" @click="test">{{ t(testing ? 'store.mirror.testing' : 'store.mirror.retest') }}</NoahButton></div>
  </NoahModal>
</template>

<style scoped>
.mirror-card { margin-top: 20px; border: 1px solid var(--border-soft); border-radius: 12px; overflow: hidden; }
.mirror-header { display: flex; align-items: center; gap: 14px; padding: 16px 18px; }
.mirror-header strong { flex: 1; font-weight: 500; font-size: var(--text-control); }
.mirror-expand { display: grid; place-items: center; width: 34px; height: 34px; border: 0; border-radius: 8px; background: var(--hover); color: var(--text); cursor: pointer; }
.mirror-choice { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 14px 18px; border-top: 1px solid var(--border-soft); background: var(--surface-raised); font-size: var(--text-control); }
.mirror-actions { display: flex; align-items: center; gap: 8px; min-width: 0; }
.mirror-actions > :first-child { width: 220px; max-width: 40vw; }
.mirror-note { margin: 0; padding: 10px 18px 14px; line-height: 1.6; }
.mirror-results { display: flex; flex-direction: column; gap: 6px; max-height: min(420px, 55vh); overflow-y: auto; }
.mirror-result { display: flex; align-items: center; justify-content: space-between; gap: 16px; min-height: 52px; padding: 8px 14px; border: 1px solid var(--border-soft); border-radius: 10px; background: var(--surface-raised); font-size: var(--text-control); }
.mirror-result > span { overflow-wrap: anywhere; min-width: 0; }
.mirror-status { display: flex; align-items: center; gap: 10px; flex: none; font-variant-numeric: tabular-nums; }
.mirror-status small { color: var(--text-muted); font-size: var(--text-caption); }
.mirror-spinner { width: 16px; height: 16px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: mirror-spin 0.8s linear infinite; }
.mirror-test-footer { display: flex; justify-content: flex-end; padding-top: 16px; }
.mirror-error { margin: 8px 18px; color: var(--danger, #c64343); font-size: var(--text-caption); overflow-wrap: anywhere; }
@keyframes mirror-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .mirror-spinner { animation: none; } }
@media (max-width: 560px) { .mirror-choice { flex-wrap: wrap; } .mirror-actions > :first-child { width: auto; flex: 1; max-width: none; } }
</style>
