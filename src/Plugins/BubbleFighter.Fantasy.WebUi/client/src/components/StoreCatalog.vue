<script setup>
import { computed, ref, watch } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import StoreTabs from './StoreTabs.vue'
import { useI18n } from '../i18n'

const props = defineProps({
  entries: { type: Array, default: () => [] },
  sources: { type: Array, default: () => [] },
  source: { type: String, default: '' },
  focusPlugin: { type: String, default: '' },
  loading: Boolean,
  busy: Boolean,
  policyAvailable: Boolean,
  policyMode: Boolean,
  offerFor: { type: Function, required: true },
  canDownload: { type: Function, required: true },
  assets: { type: Object, default: () => ({}) }
})
const emit = defineEmits(['source', 'download-source', 'asset', 'download', 'policy'])
const { t } = useI18n()
const category = ref('server-plugins')
const query = ref('')
const selected = ref('')
const mobileDetails = ref(false)
const key = entry => JSON.stringify(entry.identity)
const categories = computed(() => [
  { id: 'server-plugins', label: t('store.category.serverPlugins') },
  { id: 'client-plugins', label: t('store.category.clientPlugins') }
  // { id: 'server', label: t('store.category.server') }
])
const matchesCategory = entry => props.policyMode ? entry.identity.target === 'client' && entry.identity.kind === 'plugin' : category.value === 'server'
  ? entry.identity.target === 'server' && entry.identity.kind === 'application'
  : entry.identity.kind === 'plugin' && entry.identity.target === (category.value === 'client-plugins' ? 'client' : 'server')
const entries = computed(() => {
  const search = query.value.trim().toLocaleLowerCase()
  return props.entries.filter(entry => entry.downloads.length && matchesCategory(entry) &&
    (!search || [entry.identity.id, entry.identity.game, ...entry.downloads.map(choice => choice.package.name)]
      .some(text => String(text).toLocaleLowerCase().includes(search))))
})
const current = computed(() => entries.value.find(entry => key(entry) === selected.value) || entries.value[0])
const offer = computed(() => current.value ? props.offerFor(current.value) : null)
const selectedAsset = computed(() => offer.value?.package.repositoryRelease?.assets?.find(asset => String(asset.id) === props.assets[offer.value.selectionFingerprint]))
const downloadOptions = computed(() => current.value?.downloads.map(choice => ({ value: choice.sourceId, label: `${choice.sourceName} · ${choice.package.version}` })) || [])
const assetOptions = computed(() => [{ value: '', label: t('store.chooseAsset') }, ...(offer.value?.package.repositoryRelease?.assets || [])
  .map(asset => ({ value: String(asset.id), label: `${asset.name} · ${size(asset.size)}` }))])
function size(bytes) {
  if (bytes == null) return t('store.unspecifiedCompatibility')
  if (bytes < 1024) return `${bytes.toLocaleString()} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
}
watch(category, () => { selected.value = ''; mobileDetails.value = false })
watch(entries, value => {
  if (!value.some(entry => key(entry) === selected.value)) {
    selected.value = value[0] ? key(value[0]) : ''
    mobileDetails.value = false
  }
}, { immediate: true })
watch([() => props.focusPlugin, () => props.entries], ([id]) => {
  if (!id) return
  const entry = props.entries.find(item => item.identity.id === id && item.identity.target === (props.policyMode ? 'client' : 'server') && item.identity.kind === 'plugin' && item.downloads.length)
  if (!entry) return
  category.value = 'server-plugins'
  query.value = ''
  selected.value = key(entry)
  mobileDetails.value = true
}, { immediate: true, flush: 'post' })
</script>

<template>
  <section class="catalog-browser" :class="{ 'mobile-details-open': mobileDetails }" :aria-label="t(policyMode ? 'plugins.clientPolicy' : 'nav.store')">
    <div class="catalog-toolbar" :class="{ 'policy-toolbar': policyMode }">
      <template v-if="policyMode">
        <NoahInput v-model="query" class="policy-search" prefix-icon="search" :placeholder="t('plugins.search')" :aria-label="t('plugins.search')" />
        <div class="policy-toolbar-actions"><slot name="policy-actions" /></div>
      </template>
      <template v-else>
      <StoreTabs v-model="category" class="catalog-categories" :items="categories" :label="t('store.categories')" />
      <div class="catalog-filters">
        <NoahSelect v-if="!policyMode" :model-value="source" :options="sources" :disabled="busy" :aria-label="t('store.sources')" @change="emit('source', $event)" />
        <NoahInput v-model="query" prefix-icon="search" :placeholder="t(policyMode ? 'plugins.search' : 'store.search')" :aria-label="t(policyMode ? 'plugins.search' : 'store.search')" />
      </div>
      </template>
    </div>
    <div class="catalog-layout" :aria-busy="loading">
      <div class="catalog-list">
        <div class="catalog-list-heading"><span>{{ t(policyMode ? 'nav.plugins' : 'store.allPackages') }}</span><span class="text-muted">{{ entries.length }}</span></div>
        <div v-if="loading && !entries.length" class="catalog-grid">
          <div v-for="index in 6" :key="index" class="catalog-placeholder"><NoahSkeleton :rows="3" /></div>
        </div>
        <div v-else-if="entries.length" class="catalog-grid">
          <button v-for="entry in entries" :key="key(entry)" type="button" class="catalog-package"
            :class="{ selected: current && key(current) === key(entry), 'catalog-policy-card': policyMode }" :aria-pressed="current && key(current) === key(entry)"
            @click="selected = key(entry); mobileDetails = true">
            <slot v-if="$slots.card" name="card" :entry="entry" :offer="offerFor(entry)" />
            <template v-else>
            <span class="catalog-package-main">
              <span class="package-icon" aria-hidden="true"><NoahIcon :name="entry.identity.kind === 'plugin' ? 'plugins' : 'server'" :size="26" /></span>
              <span class="package-text"><span class="text-caption text-muted">{{ entry.identity.game }}</span>
                <strong>{{ offerFor(entry).package.name }}</strong><span class="text-caption text-muted">{{ offerFor(entry).package.version }}</span></span>
            </span>
            <span class="catalog-package-footer"><span>{{ offerFor(entry).sourceName }}</span><span v-if="!policyMode">{{ size(offerFor(entry).package.size) }}</span></span>
            </template>
          </button>
        </div>
        <div v-else class="catalog-empty"><NoahIcon name="search" :size="28" /><p>{{ query ? t('store.noMatches') : t(policyMode ? 'plugins.policy.empty' : 'store.noPackages') }}</p></div>
      </div>
      <aside class="catalog-detail" :aria-label="t('store.packageDetails')">
        <slot v-if="$slots.details && current && offer" name="details" :entry="current" :offer="offer" :back="() => mobileDetails = false" />
        <template v-else-if="current && offer">
          <NoahButton class="catalog-back" icon="chevron-left" variant="glass" @click="mobileDetails = false">{{ t('store.backToResults') }}</NoahButton>
          <div class="package-detail-scroll">
          <header class="package-detail-heading">
            <span class="package-icon package-icon-large" aria-hidden="true"><NoahIcon :name="current.identity.kind === 'plugin' ? 'plugins' : 'server'" :size="38" /></span>
            <span class="text-caption text-muted">{{ current.identity.game }}</span>
            <h2>{{ offer.package.name }}</h2>
            <code class="text-caption text-muted">{{ current.identity.id }}</code>
          </header>
          <div class="package-detail-body">
            <p v-if="offer.package.description || offer.package.repositoryRelease?.notes" class="store-notes">{{ offer.package.description || offer.package.repositoryRelease.notes }}</p>
            <div class="catalog-field"><span>{{ t(policyMode ? 'plugins.configurationSource' : 'store.downloadSource') }}</span>
              <span v-if="policyMode">{{ offer.sourceName }}</span>
              <NoahSelect v-else :model-value="offer.sourceId" :disabled="busy" :options="downloadOptions" :aria-label="t('store.downloadSource')" @change="emit('download-source', current, $event)" />
            </div>
            <dl class="package-facts"><dt>{{ t('col.version') }}</dt><dd>{{ offer.package.version }}</dd>
              <template v-if="policyMode && offer.package.author"><dt>{{ t('plugins.author') }}</dt><dd>{{ offer.package.author }}</dd></template>
              <template v-if="!policyMode"><dt>{{ t('store.totalDownload') }}</dt><dd>{{ size(offer.package.size) }}</dd></template></dl>
            <div class="catalog-field"><strong>{{ t('store.compatibleBuilds') }}</strong>
              <div class="package-builds"><NoahTag v-for="build in offer.package.builds || []" :key="build" size="sm">{{ build }}</NoahTag>
                <span v-if="!offer.package.builds?.length" class="text-muted text-caption">{{ t('store.unspecifiedCompatibility') }}</span></div>
            </div>
            <div v-if="offer.package.platforms?.length" class="catalog-field"><strong>{{ t('store.platforms') }}</strong>
              <div class="package-builds"><NoahTag v-for="platform in offer.package.platforms" :key="platform" size="sm">{{ platform }}</NoahTag></div>
            </div>
            <div v-if="!policyMode && offer.package.repositoryRelease" class="catalog-field"><span>{{ t('store.attachment') }}</span>
              <NoahSelect :model-value="assets[offer.selectionFingerprint] || ''" :disabled="busy" :options="assetOptions" :aria-label="t('store.attachment')" @change="emit('asset', offer.selectionFingerprint, $event)" />
              <p v-if="selectedAsset && !selectedAsset.sha256" class="text-caption text-muted">{{ t('store.noDigest') }}</p>
            </div>
          </div>
          </div>
          <footer class="package-detail-actions">
            <NoahButton v-if="current.identity.target === 'server'" variant="primary" :loading="busy" :disabled="loading || !canDownload(offer)" @click="emit('download', current)">{{ t('store.downloadStaging') }}</NoahButton>
            <div v-else class="client-policy-actions">
              <NoahButton variant="primary" :disabled="!policyAvailable" :title="!policyAvailable ? t('common.unavailable') : undefined" @click="emit('policy', current, 'recommended')">{{ t('store.policy.addRecommendation') }}</NoahButton>
              <NoahButton variant="glass" :disabled="!policyAvailable" :title="!policyAvailable ? t('common.unavailable') : undefined" @click="emit('policy', current, 'disabled')">{{ t('store.policy.disablePlugin') }}</NoahButton>
            </div>
          </footer>
        </template>
        <div v-else class="catalog-empty"><NoahIcon name="plugins" :size="32" /><p>{{ t('store.choosePackage') }}</p></div>
      </aside>
    </div>
  </section>
</template>

<style scoped>
.catalog-toolbar { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 20px; margin-bottom: 22px; }
.policy-search { flex: 1 1 200px; max-width: 340px; }
.policy-toolbar-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-left: auto; }
.catalog-toolbar.policy-toolbar { display: flex; align-items: center; gap: 12px; min-height: 48px; }
.catalog-package-footer .noah-tag, .package-detail-heading > .noah-tag { padding: 5px 9px; line-height: 1.2; }
.catalog-categories { min-width: 0; }
.catalog-filters { display: grid; grid-template-columns: minmax(130px, 190px) minmax(160px, 260px); gap: 12px; }
.catalog-layout { display: grid; grid-template-columns: minmax(0, 1.5fr) minmax(290px, 1fr); gap: 24px; align-items: start; }
.catalog-list { min-width: 0; min-height: 560px; }
.catalog-list-heading { display: flex; align-items: center; justify-content: space-between; padding: 0 0 16px; font-size: var(--text-label); }
.catalog-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.catalog-package, .catalog-placeholder { min-width: 0; min-height: 148px; border: 1px solid var(--border); border-radius: 8px; padding: 18px; background: var(--surface); color: var(--text); }
.catalog-package { display: flex; flex-direction: column; justify-content: space-between; gap: 18px; text-align: left; font: inherit; cursor: pointer; transition: background-color .15s, border-color .15s; }
.catalog-package:hover { background: var(--hover); }
.catalog-package.selected { border-color: var(--accent); background: var(--accent-soft); }
.catalog-package.catalog-policy-card { justify-content: space-between; gap: 16px; height: 136px; min-height: 136px; box-sizing: border-box; padding: 16px; overflow: hidden; }
.catalog-package:focus-visible, .catalog-tab:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.catalog-package-main { display: flex; align-items: center; gap: 14px; min-width: 0; }
.package-icon { width: 52px; height: 52px; flex: none; display: grid; place-items: center; color: var(--text-muted); background: var(--surface-raised); border: 1px solid var(--border-soft); border-radius: 8px; }
.package-text { min-width: 0; display: grid; gap: 4px; overflow-wrap: anywhere; }
.package-text strong { font-size: var(--text-body); line-height: 1.4; }
.catalog-package-footer { display: flex; justify-content: space-between; gap: 12px; padding-top: 12px; border-top: 1px solid var(--border-soft); color: var(--text-muted); font-size: var(--text-caption); }
.catalog-package-footer span { overflow-wrap: anywhere; }
.catalog-package-footer span:last-child { flex: none; }
.catalog-detail { min-width: 0; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); display: flex; flex-direction: column; }
.package-detail-heading { display: grid; justify-items: center; gap: 8px; text-align: center; padding: 22px 20px 18px; border-bottom: 1px solid var(--border-soft); overflow-wrap: anywhere; }
.package-detail-heading h2 { font-size: 22px; margin: 0; line-height: 1.4; max-width: 100%; }
.package-detail-heading code { overflow-wrap: anywhere; max-width: 100%; }
.package-icon-large { width: 60px; height: 60px; margin-bottom: 4px; }
.package-detail-body { display: grid; gap: 18px; padding: 20px; }
.catalog-field { display: grid; gap: 10px; font-size: var(--text-control); min-width: 0; }
.package-facts { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 14px; margin: 0; font-size: var(--text-control); }
.package-facts dt { color: var(--text-muted); }
.package-facts dd { margin: 0; text-align: right; overflow-wrap: anywhere; }
.package-builds { display: flex; gap: 8px; flex-wrap: wrap; }
.package-detail-actions { margin-top: auto; padding: 18px 24px; border-top: 1px solid var(--border-soft); }
.package-detail-actions .noah-btn { width: 100%; min-height: 38px; }
.client-policy-actions { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 10px; }
.client-policy-actions .noah-btn { height: auto; padding-top: 10px; padding-bottom: 10px; white-space: normal; }
.package-detail-actions p { margin-bottom: 12px; }
.catalog-empty { min-height: 300px; display: flex; align-items: center; justify-content: center; flex-direction: column; color: var(--text-muted); text-align: center; padding: 24px; }
.catalog-back { display: none; }
.package-detail-scroll { min-width: 0; }
@media (min-width: 861px) {
  .catalog-browser { display: flex; flex-direction: column; flex: 1; min-height: 0; }
  .catalog-toolbar { flex: none; }
  .catalog-layout { flex: 1; min-height: 0; align-items: stretch; }
  .catalog-list { min-height: 0; display: flex; flex-direction: column; }
  .catalog-list-heading { flex: none; }
  .catalog-grid { overflow-y: auto; min-height: 0; align-content: start; grid-auto-rows: max-content; padding-right: 6px; scrollbar-gutter: stable; overscroll-behavior: contain; }
  .catalog-detail { min-height: 0; overflow: hidden; }
  .package-detail-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; }
  .package-detail-actions { flex: none; margin-top: 0; padding: 14px 20px; }
}
.store-notes { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: var(--text-control); }
@media (max-width: 1180px) { .catalog-grid { grid-template-columns: minmax(0, 1fr); } }
@media (max-width: 760px) {
  .catalog-toolbar { display: grid; gap: 14px; }
  .catalog-categories { gap: 18px; overflow-x: auto; }
  .catalog-filters { grid-template-columns: minmax(0, 1fr) minmax(0, 1.3fr); }
  .catalog-layout { grid-template-columns: minmax(0, 1fr); }
  .catalog-list { min-height: 380px; }
  .catalog-detail { display: none; position: static; }
  .mobile-details-open .catalog-detail { display: flex; }
  .mobile-details-open .catalog-list { display: none; }
  .catalog-back { display: inline-flex; align-self: start; margin: 16px 16px 0; }
  .package-detail-body { padding: 20px; }
}
</style>
