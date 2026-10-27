<script setup>
import { computed, ref, watch } from 'vue'
import SectionCard from './SectionCard.vue'
import ErrorCard from './ErrorCard.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import { api, jsonRequest } from '../api/client'
import { useResource } from '../composables/useResource'
import { useServerStore } from '../stores/server'
import { useUiStore } from '../stores/ui'
import { useI18n } from '../i18n'

const props = defineProps({ catalog: { type: Array, default: () => [] } })
const { t } = useI18n()
const server = useServerStore()
const ui = useUiStore()
const { data, error, loading, reload } = useResource(() => api('setup/client-plugins'))
const mode = ref('profile')
const draft = ref([])
const catalogChoice = ref('')
const busy = ref(false)
const failure = ref(null)
const submitted = ref(false)
let nextKey = 0
watch(data, value => {
  if (!value) return
  mode.value = value.policies === null ? 'profile' : 'custom'
  draft.value = (value.policies || []).map(policy => ({ key: nextKey++, id: policy.id, builds: [...policy.builds], state: policy.disabled ? 'disabled' : policy.required ? 'required' : 'recommended' }))
})
const ready = computed(() => !busy.value && !loading.value && server.setupStatus?.phase === 'ready')
const modes = computed(() => ['profile', 'custom'].map(value => ({ value, label: t('store.policy.' + value) })))
const states = computed(() => ['recommended', 'required', 'disabled'].map(value => ({ value, label: t('store.policy.' + value) })))
const builds = computed(() => (server.setupStatus?.profiles || []).filter(profile => profile.id !== 'management'))
const choices = computed(() => [{ value: '', label: t('store.policy.choose') }, ...[...new Set(props.catalog
  .filter(entry => entry.identity.target === 'client' && entry.identity.kind === 'plugin' && entry.identity.game === 'bubblefighter')
  .map(entry => entry.identity.id))].sort().map(id => ({ value: id, label: id }))])

function add(id = '') {
  mode.value = 'custom'
  draft.value.push({ key: nextKey++, id, builds: [], state: 'recommended' })
  catalogChoice.value = ''
  submitted.value = false
}

async function save() {
  if (!ready.value) return
  busy.value = true
  failure.value = null
  submitted.value = false
  try {
    const policies = mode.value === 'profile' ? null : draft.value.map(row => ({
      id: row.id.trim(), builds: row.builds, required: row.state === 'required', disabled: row.state === 'disabled'
    }))
    await api('setup/client-plugins', jsonRequest('PUT', { policies }))
    submitted.value = true
    await server.refreshSetup()
    if (server.setupStatus.phase !== 'ready') server.setupOpen = true
    await reload()
  } catch (error) { failure.value = error }
  finally { busy.value = false }
}
</script>

<template>
  <SectionCard :title="t('store.policy.title')" :sub="t('store.policy.hint')">
    <ErrorCard v-if="error" :error="error" @retry="reload" />
    <NoahSkeleton v-else-if="loading && !data" :rows="3" />
    <form v-else class="policy-editor" @submit.prevent="save">
      <label class="policy-field">
        <span>{{ t('store.policy.source') }}</span>
        <NoahSelect v-model="mode" :options="modes" :disabled="!ready" :aria-label="t('store.policy.source')" />
      </label>
      <template v-if="mode === 'custom'">
        <p class="text-caption text-muted">{{ t('store.policy.empty') }}</p>
        <fieldset v-for="(row, index) in draft" :key="row.key" :disabled="!ready" class="policy-row">
          <label class="policy-field"><span>{{ t('store.policy.id') }}</span><NoahInput v-model="row.id" required /></label>
          <label class="policy-field"><span>{{ t('store.policy.state') }}</span><NoahSelect v-model="row.state" :options="states" :disabled="!ready" :aria-label="t('store.policy.state')" /></label>
          <details class="policy-builds">
            <summary>{{ row.builds.length ? t('store.policy.scoped', { count: row.builds.length }) : t('store.policy.allBuilds') }}</summary>
            <div class="policy-build-options">
              <label v-for="build in builds" :key="build.id">
                <input v-model="row.builds" type="checkbox" :value="build.id" />
                <span>{{ build.displayNames?.[ui.lang] || build.displayNames?.en || build.id }}</span>
              </label>
            </div>
          </details>
          <NoahButton variant="glass" :disabled="!ready" @click="draft.splice(index, 1)">{{ t('store.remove') }}</NoahButton>
        </fieldset>
        <div class="policy-actions">
          <NoahSelect v-model="catalogChoice" :options="choices" :disabled="!ready" :aria-label="t('store.policy.choose')" />
          <NoahButton :disabled="!ready || !catalogChoice" @click="add(catalogChoice)">{{ t('store.policy.addSelected') }}</NoahButton>
          <NoahButton :disabled="!ready" @click="add()">{{ t('store.policy.add') }}</NoahButton>
        </div>
      </template>
      <p class="text-caption text-muted">{{ t('store.policy.applyHint') }}</p>
      <p v-if="failure" role="alert">{{ failure.message }}</p>
      <p v-if="submitted" role="status">{{ t('common.saved') }}</p>
      <NoahButton html-type="submit" variant="primary" :loading="busy" :disabled="!ready">{{ t('common.save') }}</NoahButton>
    </form>
  </SectionCard>
</template>

<style scoped>
.policy-editor { display: grid; gap: var(--space-4); }
.policy-field { display: grid; gap: var(--space-2); min-width: 0; }
.policy-row { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) auto; align-items: end; gap: var(--space-3); border: 1px solid var(--noah-border-color, #8883); border-radius: 12px; padding: var(--space-4); min-width: 0; }
.policy-builds { grid-column: 1 / -1; grid-row: 2; }
.policy-builds summary { cursor: pointer; }
.policy-build-options { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: var(--space-2); padding-top: var(--space-3); }
.policy-build-options label { display: flex; align-items: center; gap: var(--space-2); }
.policy-actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); }
.policy-editor > .noah-btn { justify-self: start; }
@media (max-width: 720px) { .policy-row { grid-template-columns: minmax(0, 1fr); } .policy-builds { grid-column: auto; grid-row: auto; } .policy-row > .noah-btn { justify-self: start; } }
</style>
