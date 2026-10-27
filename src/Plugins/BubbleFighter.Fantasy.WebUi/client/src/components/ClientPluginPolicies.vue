<script setup>
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import StoreCatalog from './StoreCatalog.vue'
import ErrorCard from './ErrorCard.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import PolicyRuleBadges from './PolicyRuleBadges.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import ClientPluginPolicyDetails from './ClientPluginPolicyDetails.vue'
import { api } from '../api/client'
import { copyPolicy, loadClientPolicy, saveClientPolicy } from '../api/clientPolicy'
import { useNoahToast } from '../noah/components/ui/useNoahToast'
import { useI18n } from '../i18n'

const { t, te } = useI18n()
const toast = useNoahToast()
const state = ref(null)
const loadError = ref(null)
const saveError = ref('')
const busy = ref(false)
const builds = ref([])
const storeEntries = ref([])
const focusId = ref('')
const createOpen = ref(false)
const storeOpen = ref(false)
const resetOpen = ref(false)
const storeQuery = ref('')
const draft = reactive({ id: '', name: '', description: '' })
const formError = ref('')

async function reload() {
  try { state.value = await loadClientPolicy(); loadError.value = null }
  catch (error) { loadError.value = error }
}
onMounted(() => {
  reload()
  // Build names and store descriptions only decorate the editor; the policy stays editable without them.
  api('overview').then(overview => { builds.value = overview.builds || [] }).catch(() => {})
  api('distribution/v1/catalog').then(catalog => {
    storeEntries.value = (catalog.entries || []).filter(entry => entry.identity.kind === 'plugin' && entry.identity.target === 'client' && entry.downloads.length)
  }).catch(() => {})
})

const plugins = computed(() => state.value?.policy.plugins || [])
const storeOffer = id => storeEntries.value.find(entry => entry.identity.id === id)?.downloads[0]
const catalog = computed(() => plugins.value.map(plugin => {
  const offer = storeOffer(plugin.id)
  return {
    identity: { id: plugin.id, game: 'bubblefighter', target: 'client', kind: 'plugin' },
    downloads: [{ sourceId: 'policy', sourceName: offer?.sourceName || t('plugins.policy.custom'), package: {
      name: plugin.name || offer?.package.name || plugin.id, version: offer?.package.version || null, author: offer?.package.author || null,
      builds: offer?.package.builds || [], description: plugin.description || offer?.package.description || ''
    } }]
  }
}))
const pluginFor = entry => plugins.value.find(plugin => plugin.id === entry.identity.id)
const storeChoices = computed(() => {
  const search = storeQuery.value.trim().toLocaleLowerCase()
  return storeEntries.value.filter(entry => [entry.identity.id, entry.downloads[0].package.name].some(value => String(value).toLocaleLowerCase().includes(search)))
})
const typeLabel = type => te('plugins.policy.type.' + type) ? t('plugins.policy.type.' + type) : type
const ruleTone = rule => rule.type === 'require' ? 'success' : ['recommend', 'latest'].includes(rule.type) ? 'info' : ['min_version', 'max_version'].includes(rule.type) ? 'warning' : 'default'
const ruleLabel = rule => typeLabel(rule.type) + (rule.version ? ` · ${rule.version}` : '')
const badges = entry => (pluginFor(entry)?.rules || []).map(rule => ({ key: rule.id, label: ruleLabel(rule), tone: ruleTone(rule) }))

function describe(error) {
  const body = error.body || {}
  const key = 'plugins.policy.error.' + body.error
  return te(key) ? t(key, { plugin: body.plugin || '', rule: body.rule || '', other: body.other || '' }) : error.message
}
// Every change is one whole-policy write against the revision it was made on; a stale revision reloads and says so.
async function commit(change) {
  if (!state.value || busy.value) return false
  busy.value = true
  saveError.value = ''
  try {
    const candidate = copyPolicy(state.value.policy)
    change(candidate)
    await saveClientPolicy(state.value.revision, candidate)
    await reload()
    toast.ok(t('common.saved'))
    return true
  } catch (error) {
    saveError.value = describe(error)
    if (error.status === 409) await reload()
    return false
  } finally { busy.value = false }
}
async function saveRules(id, { rules, sources, done }) {
  const saved = await commit(policy => {
    policy.sources.push(...sources.filter(source => !policy.sources.some(existing => existing.name === source.name)))
    policy.plugins.find(plugin => plugin.id === id).rules = rules
  })
  if (saved) done()
}
async function removePlugin(id) {
  await commit(policy => { policy.plugins = policy.plugins.filter(plugin => plugin.id !== id) })
}
async function resetToProfile() {
  resetOpen.value = false
  if (!state.value || busy.value) return
  busy.value = true
  saveError.value = ''
  try { await saveClientPolicy(state.value.revision, null); await reload(); toast.ok(t('common.saved')) }
  catch (error) { saveError.value = describe(error); if (error.status === 409) await reload() }
  finally { busy.value = false }
}

function openCreate() {
  Object.assign(draft, { id: '', name: '', description: '' })
  formError.value = ''
  createOpen.value = true
}
function openStore() { storeQuery.value = ''; storeOpen.value = true }
async function focusEntry(id) {
  focusId.value = ''
  await nextTick()
  focusId.value = id
}
async function addPlugin(id, name, description) {
  if (plugins.value.some(plugin => plugin.id === id)) { focusEntry(id); return true }
  const saved = await commit(policy => { policy.plugins.push({ id, name: name || null, description: description || null, rules: [] }) })
  if (saved) focusEntry(id)
  return saved
}
async function create() {
  const id = draft.id.trim()
  if (!id) { formError.value = t('plugins.policy.idRequired'); return }
  if (!/^[A-Za-z0-9._-]+$/.test(id)) { formError.value = t('plugins.policy.invalidId'); return }
  if (plugins.value.some(plugin => plugin.id === id)) { formError.value = t('plugins.policy.duplicate'); return }
  if (await addPlugin(id, draft.name.trim(), draft.description.trim())) createOpen.value = false
}
async function chooseStore(entry) {
  storeOpen.value = false
  await addPlugin(entry.identity.id, null, null)
}
</script>

<template>
  <ErrorCard v-if="loadError && !state" :error="loadError" @retry="reload" />
  <template v-else>
    <p v-if="state?.mode === 'profile'" class="policy-mode-note text-caption text-muted">{{ t('plugins.policy.profileMode') }}</p>
    <p v-if="saveError" class="policy-save-error" role="alert">{{ saveError }}</p>
    <StoreCatalog class="client-policy-browser" :entries="catalog" :focus-plugin="focusId" :loading="!state" policy-mode :offer-for="entry => entry.downloads[0]" :can-download="() => false">
      <template #policy-actions>
        <NoahButton v-if="state?.mode === 'custom'" variant="glass" :disabled="busy" @click="resetOpen = true">{{ t('plugins.policy.resetProfile') }}</NoahButton>
        <NoahButton variant="glass" :disabled="!state || busy" @click="openStore">{{ t('plugins.policy.fromStore') }}</NoahButton>
        <NoahButton variant="glass" :disabled="!state || busy" @click="openCreate">{{ t('plugins.policy.create') }}</NoahButton>
      </template>
      <template #card="{ entry, offer }">
        <span class="policy-card-main"><span class="policy-card-icon" aria-hidden="true"><NoahIcon name="plugins" :size="25" /></span><span class="policy-card-heading"><strong :title="offer.package.name">{{ offer.package.name }}</strong><span :title="entry.identity.id">{{ entry.identity.id }}</span></span></span>
        <span class="policy-card-footer"><PolicyRuleBadges :items="badges(entry)" /></span>
      </template>
      <template #details="{ entry, offer, back }">
        <ClientPluginPolicyDetails :key="entry.identity.id" :entry="entry" :offer="offer" :plugin="pluginFor(entry)" :types="state.types" :sources="state.policy.sources" :builds="builds" :busy="busy"
          @back="back" @save="saveRules(entry.identity.id, $event)" @remove-plugin="removePlugin(entry.identity.id)" />
      </template>
    </StoreCatalog>
  </template>
  <NoahModal v-model="createOpen" :title="t('plugins.policy.create')" width="500px" :close-label="t('common.close')">
    <form id="client-policy-create" class="policy-create-form" @submit.prevent="create">
      <label><span>{{ t('plugins.policy.pluginId') }} <span class="policy-required">*</span></span><NoahInput v-model="draft.id" required :aria-label="t('plugins.policy.pluginId')" placeholder="bubblefighter.example.plugin" /></label>
      <label><span>{{ t('plugins.name') }} <small>{{ t('plugins.policy.optional') }}</small></span><NoahInput v-model="draft.name" :aria-label="t('plugins.name')" /></label>
      <label><span>{{ t('plugins.description') }} <small>{{ t('plugins.policy.optional') }}</small></span><textarea v-model="draft.description" rows="4" :aria-label="t('plugins.description')" /></label>
      <p v-if="formError" class="policy-form-error" role="alert">{{ formError }}</p>
    </form>
    <template #footer><NoahButton variant="glass" @click="createOpen = false">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" html-type="submit" form="client-policy-create" :loading="busy">{{ t('plugins.policy.create') }}</NoahButton></template>
  </NoahModal>
  <NoahModal v-model="storeOpen" :title="t('plugins.policy.fromStore')" width="540px" :close-label="t('common.close')">
    <div class="policy-store-picker">
      <NoahInput v-model="storeQuery" prefix-icon="search" :placeholder="t('plugins.search')" :aria-label="t('plugins.search')" />
      <div class="policy-store-choices"><button v-for="entry in storeChoices" :key="entry.identity.id" type="button" @click="chooseStore(entry)"><strong>{{ entry.downloads[0].package.name }}</strong><span>{{ entry.identity.id }}</span></button></div>
      <p v-if="!storeChoices.length" class="text-muted">{{ t(storeEntries.length ? 'plugins.noMatches' : 'plugins.policy.noStorePlugins') }}</p>
    </div>
  </NoahModal>
  <NoahModal v-model="resetOpen" :title="t('plugins.policy.resetProfile')" width="480px" :close-label="t('common.close')">
    <p class="policy-reset-hint">{{ t('plugins.policy.resetHint') }}</p>
    <template #footer><NoahButton variant="glass" @click="resetOpen = false">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" @click="resetToProfile">{{ t('plugins.policy.resetProfile') }}</NoahButton></template>
  </NoahModal>
</template>

<style scoped>
.client-policy-browser { min-width: 0; min-height: 0; }
.policy-mode-note { margin: 0 0 12px; }
.policy-save-error { margin: 0 0 12px; color: var(--danger, #c64343); font-size: var(--text-control); line-height: 1.6; }
.policy-card-main { display: flex; align-items: center; gap: 12px; min-width: 0; width: 100%; }
.policy-card-icon { display: grid; place-items: center; flex: none; width: 44px; height: 44px; border: 1px solid var(--border-soft); border-radius: 8px; background: var(--surface-raised); color: var(--text-muted); }
.policy-card-heading { display: grid; gap: 6px; min-width: 0; flex: 1; }
.policy-card-heading strong { font-size: var(--text-control); font-weight: 550; line-height: 1.5; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.policy-card-heading > span { font-size: var(--text-caption); color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; line-height: 1.4; }
.policy-card-footer { display: block; width: 100%; min-width: 0; border-top: 1px solid var(--border-soft); padding-top: 12px; }
.policy-create-form label { display: grid; gap: 8px; min-width: 0; font-size: var(--text-control); }
.policy-create-form { display: grid; gap: 20px; }
.policy-create-form small { color: var(--text-muted); font-size: var(--text-caption); margin-left: 4px; }
.policy-required, .policy-form-error { color: var(--danger, #c64343); }
.policy-form-error { font-size: var(--text-caption); margin: 0; line-height: 1.5; }
.policy-create-form textarea { resize: vertical; min-height: 90px; max-height: 240px; border: 1px solid var(--border); border-radius: 12px; padding: 12px 14px; background: var(--surface); color: var(--text); font: inherit; line-height: 1.6; }
.policy-create-form textarea:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 2px; }
.policy-store-picker { display: grid; gap: 16px; }
.policy-store-choices { display: grid; gap: 10px; max-height: 320px; overflow-y: auto; }
.policy-store-choices button { display: grid; gap: 8px; border: 1px solid var(--border); background: var(--surface); color: var(--text); padding: 16px; border-radius: 8px; font: inherit; text-align: left; cursor: pointer; overflow-wrap: anywhere; }
.policy-store-choices button:hover { background: var(--hover); border-color: var(--accent); }
.policy-store-choices button span { font-size: var(--text-caption); color: var(--text-muted); }
.policy-reset-hint { margin: 0; font-size: var(--text-control); line-height: 1.7; }
</style>
