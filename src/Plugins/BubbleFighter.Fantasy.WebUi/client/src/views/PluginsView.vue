<script setup>
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTable from '../noah/components/ui/NoahTable.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import { useNoahToast } from '../noah/components/ui/useNoahToast'
import SectionCard from '../components/SectionCard.vue'
import StoreTabs from '../components/StoreTabs.vue'
import PluginDetails from '../components/PluginDetails.vue'
import ClientPluginPolicies from '../components/ClientPluginPolicies.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { comparePluginVersions } from '../utils/pluginVersion'

// The row that serves this page cannot reload itself from here: the request would cut its own connection.
const SELF_PLUGIN = 'admin.http'

const { t, te } = useI18n()
const router = useRouter()
const toast = useNoahToast()
// The server reports rows, not packages: a row's id is the name operators give it in profiles, and rows composed
// from packaged profiles ship with the server. Store versions are read once, not on every five-second refresh.
const catalog = ref([])
async function loadCatalog() {
  try { catalog.value = ((await api('distribution/v1/catalog')).entries || []).filter(entry => entry.identity.target === 'server' && entry.identity.kind === 'plugin') }
  catch { catalog.value = [] }
}
loadCatalog()
function decorate(row) {
  const builtIn = /^(bundle|group):/.test(row.source || '')
  const entry = catalog.value.find(item => item.identity.id === row.plugin)
  const newest = (entry?.downloads || []).map(offer => offer.package.version).reduce((best, version) => comparePluginVersions(version, best) === 1 ? version : best, row.version || '')
  return { ...row, name: row.plugin, builtIn, author: builtIn ? 'Bubble Fighter Fantasy' : null,
    storeEntry: entry ? { id: entry.identity.id } : null, availableVersion: newest !== (row.version || '') ? newest : '' }
}
const resource = useResource(() => api('plugins'), { interval: 5000 })
const { error, loading } = resource
const data = computed(() => resource.data.value ? resource.data.value.map(decorate) : null)
function reload() { loadCatalog(); return resource.reload() }
// Without a description the row's own id says where it comes from; when that equals the plugin id, its source does.
const subtitle = row => row.description || (row.rowId !== row.plugin ? row.rowId : row.source)
const view = ref('browse')
const page = ref('information')
const policyVisited = ref(false)
watch(page, value => { if (value === 'policy') policyVisited.value = true })
function browseStore() { router.push('/store') }
const pages = computed(() => [
  { id: 'information', label: t('plugins.information') },
  { id: 'policy', label: t('plugins.clientPolicy') }
])
const query = ref('')
const stateFilter = ref('')
const category = ref('all')
const categories = computed(() => ['all', 'enabled', 'disabled', 'updates', 'builtIn'].map(id => ({ id, label: t('plugins.category.' + id) })))
const categoryIndicator = computed(() => ({ width: `calc((100% - 8px) / ${categories.value.length})`, transform: `translateX(${categories.value.findIndex(item => item.id === category.value) * 100}%)` }))
function matchesCategory(row) {
  if (category.value === 'enabled') return row.state === 'Active'
  if (category.value === 'disabled') return row.disabled === true || row.state === 'Unloaded'
  if (category.value === 'updates') return !!row.availableVersion?.trim()
  if (category.value === 'builtIn') return row.builtIn === true
  return true
}
const selectedId = ref(null)
const mobileDetails = ref(false)
const filtered = computed(() => {
  const search = query.value.trim().toLocaleLowerCase()
  return (data.value || []).filter(row => matchesCategory(row) && (!stateFilter.value || row.state === stateFilter.value)
    && [row.name, row.description, row.plugin, row.rowId, row.source, row.version, stateName(row.state)].some(value => String(value ?? '').toLocaleLowerCase().includes(search)))
})
const current = computed(() => filtered.value.find(row => row.rowId === selectedId.value) || filtered.value[0])
watch(current, row => { selectedId.value = row?.rowId ?? null })
watch([query, stateFilter, category], () => { mobileDetails.value = false })
const stateOptions = computed(() => [
  { value: '', label: t('plugins.allStates') },
  ...[...new Set((data.value || []).map(row => row.state))].map(state => ({ value: state, label: stateName(state) }))
])

const columns = computed(() => [
  { key: 'name', title: t('plugins.name'), width: '16%' },
  { key: 'plugin', title: t('plugins.code'), width: '23%' },
  { key: 'rowId', title: t('plugins.configurationId'), width: '23%' },
  { key: 'version', title: t('col.version'), width: '16%' },
  { key: 'state', title: t('col.status'), width: '22%' }
])
const drawerRowId = ref(null)
const drawerRow = computed(() => (data.value || []).find(row => row.rowId === drawerRowId.value))
const drawerOpen = computed(() => !!drawerRow.value)
const drawerPanel = ref(null)
let drawerTrigger
function openDetails(row, event) {
  drawerTrigger = event?.currentTarget || document.activeElement
  drawerRowId.value = row.rowId
}
function closeDetails() { drawerRowId.value = null }
watch(drawerOpen, async open => {
  await nextTick()
  if (open) drawerPanel.value?.querySelector('button')?.focus()
  else if (drawerTrigger?.isConnected) drawerTrigger.focus()
})
watch([view, page], closeDetails)
function drawerKeydown(event) {
  if (event.key === 'Escape') { event.preventDefault(); closeDetails(); return }
  if (event.key !== 'Tab') return
  const controls = [...drawerPanel.value.querySelectorAll('button:not(:disabled), [href], input, select, textarea, [tabindex="0"]')]
    .filter(element => element.getClientRects().length)
  const first = controls[0]
  const last = controls.at(-1)
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
}
onBeforeUnmount(() => { if (drawerTrigger?.isConnected) drawerTrigger.focus() })

const stateName = (state) => (te('state.' + state) ? t('state.' + state) : state)
const stateTone = (state) => {
  if (state === 'Active') return 'success'
  if (['Failed', 'CleanupFailed', 'WorkFailed'].includes(state)) return 'danger'
  if (['Pending', 'Unloading', 'CleanupIncomplete'].includes(state)) return 'warning'
  return 'default'
}

// A version reads `1.0.0+<commit>`; the table shows the first 7 hex characters of the commit and the title keeps the whole string.
function shortVersion(version) {
  if (!version) return '-'
  const plus = version.indexOf('+')
  return plus < 0 ? version : version.slice(0, plus + 1) + version.slice(plus + 1, plus + 8)
}


const target = ref(null)
const targetAction = ref('reload')
const reloading = ref(false)
const confirmOpen = computed({
  get: () => target.value !== null,
  set: (open) => { if (!open && !reloading.value) target.value = null }
})

async function confirmReload() {
  const row = target.value
  reloading.value = true
  try {
    const result = await api(`plugins/${encodeURIComponent(row.rowId)}/${targetAction.value}`, { method: 'POST' })
    const changed = result.reloadedRowIds || result.changedRowIds || [row.rowId]
    toast.ok(targetAction.value === 'reload'
      ? t('plugins.reload.done', { rows: changed.join(', ') })
      : t('plugins.actionDone', { action: t('plugins.' + targetAction.value), rows: changed.join(', ') }), t('common.saved'))
  } catch (e) {
    toast.err(t('plugins.actionFailed', { action: t('plugins.' + targetAction.value), row: row.rowId, reason: e.message }), t('common.error'))
  } finally {
    reloading.value = false
    target.value = null
    reload()
  }
}
function requestPluginAction(action, row) { targetAction.value = action; target.value = row }
</script>

<template>
  <ErrorCard v-if="error && !data" :error="error" @retry="reload" />
  <SectionCard v-else-if="!data || loading && !data">
    <NoahSkeleton :rows="3" />
  </SectionCard>
  <section v-else class="plugin-page" :inert="drawerOpen || undefined">
    <header class="plugin-heading">
      <StoreTabs v-model="page" :items="pages" :label="t('nav.plugins')" />
      <div v-if="page === 'information'" class="plugin-heading-tools">
        <div class="plugin-layout-switch" :class="{ 'is-table': view === 'table' }" role="group" :aria-label="t('plugins.view')">
          <span class="plugin-layout-highlight" aria-hidden="true" />
          <button v-for="layout in ['browse', 'table']" :key="layout" type="button" :aria-pressed="view === layout" :disabled="page !== 'information'" @click="view = layout">{{ t('plugins.' + layout) }}</button>
        </div>
        <div class="plugin-toolbar-actions">
          <NoahButton variant="glass" icon="refresh" icon-only :loading="loading" :disabled="page !== 'information'" :aria-label="t('store.refresh')" :title="t('store.refresh')" @click="reload" />
          <NoahButton variant="glass" icon="store" :disabled="page !== 'information'" @click="browseStore">{{ t('plugins.browseStore') }}</NoahButton>
        </div>
      </div>
    </header>
    <div class="plugin-page-body">
    <Transition name="plugin-section">
    <div v-show="page === 'information'" class="plugin-information-panel">
    <ErrorCard v-if="error" :error="error" @retry="reload" />
    <div class="plugin-toolbar">
      <NoahInput v-model="query" prefix-icon="search" :placeholder="t('plugins.search')" :aria-label="t('plugins.search')" />
      <NoahSelect v-model="stateFilter" :options="stateOptions" :aria-label="t('col.status')" />
      <div class="plugin-filter-summary">
        <span class="plugin-count text-muted">{{ t('plugins.count', { shown: filtered.length, total: data.length }) }}</span>
        <div class="plugin-layout-switch plugin-category-switch" role="group" :aria-label="t('plugins.categoryLabel')">
          <span class="plugin-layout-highlight" :style="categoryIndicator" aria-hidden="true" />
          <button v-for="item in categories" :key="item.id" type="button" :aria-pressed="category === item.id" @click="category = item.id">{{ item.label }}</button>
        </div>
      </div>
    </div>
    <div class="plugin-content">
    <Transition name="plugin-view">
    <div v-if="view === 'browse'" key="browse" class="plugin-browser" :class="{ 'show-details': mobileDetails }">
      <div class="plugin-list" :aria-label="t('nav.plugins')">
        <div class="plugin-grid">
          <button v-for="row in filtered" :key="row.rowId" type="button" class="plugin-item" :class="{ selected: current?.rowId === row.rowId }"
            :aria-pressed="current?.rowId === row.rowId" @click="selectedId = row.rowId; mobileDetails = true">
            <span class="plugin-icon" aria-hidden="true"><NoahIcon name="plugins" :size="22" /></span>
            <span class="plugin-summary"><strong :title="row.name">{{ row.name }}</strong><span :title="subtitle(row)">{{ subtitle(row) }}</span></span>
            <NoahTag pill size="sm" :tone="stateTone(row.state)">{{ stateName(row.state) }}</NoahTag>
          </button>
        </div>
        <p v-if="!filtered.length" class="plugin-empty text-muted">{{ t(data.length ? 'plugins.noMatches' : 'plugins.empty') }}</p>
      </div>
      <PluginDetails :current="current" @back="mobileDetails = false" @reload="requestPluginAction('reload', $event)" @unload="requestPluginAction('unload', $event)" @disable="requestPluginAction('disable', $event)" @load="requestPluginAction('load', $event)" />
    </div>
    <div v-else key="table" class="table-wrap plugin-table">
      <NoahTable :columns="columns" :data="filtered" :empty-text="t(data.length ? 'plugins.noMatches' : 'plugins.empty')" :loading-text="t('common.loading')">
        <template #name="{ row }"><span class="plugin-table-text cell-strong" :title="row.name">{{ row.name }}</span></template>
        <template #rowId="{ row }"><span class="plugin-table-text mono" :title="row.rowId">{{ row.rowId }}</span></template>
        <template #plugin="{ row }"><span class="plugin-table-text mono" :title="row.plugin">{{ row.plugin }}</span></template>
        <template #state="{ row }">
          <div class="plugin-table-state"><NoahTag pill size="sm" :tone="stateTone(row.state)">{{ stateName(row.state) }}</NoahTag><button type="button" class="plugin-table-open" :aria-label="t('plugins.details') + ': ' + (row.name || row.plugin)" :title="t('plugins.details')" @click="openDetails(row, $event)"><NoahIcon name="chevron-right" :size="18" /></button></div>
        </template>
        <template #version="{ row }"><span class="plugin-table-text mono" :title="row.version ?? ''">{{ shortVersion(row.version) }}</span></template>
      </NoahTable>
    </div>
    </Transition>
    </div>
    </div>
    </Transition>
    <Transition name="plugin-section">
      <div v-if="policyVisited" v-show="page === 'policy'" class="plugin-policy-panel"><ClientPluginPolicies /></div>
    </Transition>
    </div>
  </section>

  <Teleport to="body">
    <Transition name="plugin-drawer">
      <div v-if="drawerOpen" class="plugin-drawer-overlay" @click.self="closeDetails" @keydown="drawerKeydown">
        <div ref="drawerPanel" class="plugin-drawer-panel" role="dialog" aria-modal="true" :aria-label="t('plugins.details')">
          <PluginDetails :current="drawerRow" drawer @close="closeDetails" @reload="requestPluginAction('reload', $event); closeDetails()" @unload="requestPluginAction('unload', $event); closeDetails()" @disable="requestPluginAction('disable', $event); closeDetails()" @load="requestPluginAction('load', $event); closeDetails()" />
        </div>
      </div>
    </Transition>
  </Teleport>

  <NoahModal v-model="confirmOpen" :title="target ? (targetAction === 'reload' ? t('plugins.reload.title', { row: target.rowId }) : t('plugins.actionTitle', { action: t('plugins.' + targetAction), row: target.rowId })) : ''" width="460px" :close-label="t('common.close')">
    <p class="confirm-text">{{ targetAction === 'reload' ? t('plugins.reload.body') : t('plugins.actionConfirm', { action: t('plugins.' + targetAction) }) }}</p>
    <template #footer>
      <NoahButton size="sm" :disabled="reloading" @click="target = null">{{ t('common.cancel') }}</NoahButton>
      <NoahButton size="sm" variant="primary" :loading="reloading" @click="confirmReload">{{ t('plugins.' + targetAction) }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
.plugin-page { display: flex; flex-direction: column; gap: 20px; min-width: 0; }
.plugin-heading { min-height: 65px; box-sizing: border-box; flex: none; display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 16px; padding-bottom: 18px; border-bottom: 1px solid var(--border); }
.plugin-heading-tools { display: flex; align-items: center; gap: 12px; }
.plugin-toolbar-actions { display: flex; align-items: center; gap: 8px; }
.plugin-layout-switch { position: relative; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); isolation: isolate; padding: 4px; border-radius: var(--radius-pill); background: var(--glass-panel); border: 1px solid var(--border-soft); }
.plugin-layout-highlight { position: absolute; z-index: -1; top: 4px; bottom: 4px; left: 4px; width: calc((100% - 8px) / 2); border-radius: var(--radius-pill); background: var(--accent-soft); transition: transform 240ms cubic-bezier(.2,.8,.2,1); }
.plugin-layout-switch.is-table .plugin-layout-highlight { transform: translateX(100%); }
.plugin-layout-switch button { border: 0; background: transparent; color: var(--text-muted); font: inherit; font-size: var(--text-control); padding: 9px 16px; min-height: 38px; cursor: pointer; border-radius: var(--radius-pill); white-space: nowrap; transition: color 160ms; }
.plugin-layout-switch button[aria-pressed='true'] { color: var(--text); }
.plugin-layout-switch button:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: -2px; }
.plugin-layout-switch button:disabled { opacity: .45; cursor: default; }
.plugin-heading > .store-tabs { flex-shrink: 0; }
.plugin-policy-placeholder { flex: 1; min-height: 0; }
.plugin-toolbar { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; }
.plugin-toolbar > .noah-input-wrap { flex: 1 1 220px; max-width: 360px; }
.plugin-toolbar > .noah-select { width: 170px; }
.plugin-filter-summary { margin-left: auto; display: flex; align-items: center; justify-content: flex-end; flex-wrap: wrap; gap: 12px; min-width: 0; }
.plugin-category-switch { grid-template-columns: repeat(5, minmax(0, 1fr)); }
.plugin-category-switch button { padding-inline: 12px; }
.plugin-count { font-size: var(--text-caption); white-space: nowrap; }
.plugin-content { display: grid; position: relative; min-width: 0; min-height: 0; height: max(320px, calc(100dvh - 340px)); overflow: hidden; }
.plugin-content > * { grid-area: 1 / 1; min-width: 0; min-height: 0; }
.plugin-browser { display: grid; grid-template-columns: minmax(0, 1.7fr) minmax(270px, 1fr); gap: 24px; }
.plugin-table.table-wrap { margin: 0; overflow: hidden; }
.plugin-table :deep(.noah-table-container) { height: 100%; box-sizing: border-box; overflow: auto; scrollbar-gutter: stable; overscroll-behavior: contain; }
.plugin-table :deep(th) { background: transparent; }
.plugin-view-enter-active { transition: opacity 180ms ease, transform 180ms ease; }
.plugin-view-leave-active { transition: opacity 120ms ease; pointer-events: none; }
.plugin-view-enter-from { opacity: 0; transform: translateY(6px); }
.plugin-view-leave-to { opacity: 0; }
.plugin-list { min-width: 0; overflow-y: auto; scrollbar-gutter: stable; overscroll-behavior: contain; padding: 3px 8px 3px 3px; }
.plugin-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; align-content: start; }
.plugin-item { display: flex; align-items: center; gap: 10px; min-width: 0; padding: 10px 12px; min-height: 64px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); color: var(--text); text-align: left; font: inherit; cursor: pointer; transition: background-color .16s, border-color .16s; }
.plugin-item:hover { background: var(--hover); }
.plugin-item.selected { border-color: var(--accent); background: var(--accent-soft); }
.plugin-item:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 1px; }
.plugin-icon { display: grid; place-items: center; flex: none; width: 32px; height: 36px; color: var(--text-muted); }
.plugin-summary { display: grid; gap: 5px; min-width: 0; flex: 1; }
.plugin-summary strong, .plugin-summary > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.plugin-summary strong { font-size: var(--text-control); font-weight: 600; }
.plugin-summary > span { color: var(--text-muted); font-size: var(--text-caption); }
.plugin-item > .noah-tag { flex: none; padding: 5px 9px; line-height: 1.2; }
.plugin-reload.noah-btn { min-height: 40px; padding: 10px 18px; font-size: var(--text-control); }
@media (max-width: 1200px) { .plugin-grid { grid-template-columns: minmax(0, 1fr); } }
@media (min-width: 861px) {
  :global(.shell:has(.plugin-page)) { height: 100dvh; min-height: 0; overflow: hidden; }
  :global(.shell:has(.plugin-page) .main) { min-height: 0; }
  :global(.shell:has(.plugin-page) .breadcrumb-bar) { flex: none; }
  :global(.shell:has(.plugin-page) .view) { flex: 1; min-height: 0; padding-top: 28px; padding-bottom: 28px; }
  :global(.shell:has(.plugin-page) .route-page) { flex: 1; min-height: 0; }
  :global(.shell:has(.plugin-page) .page-heading) { flex: none; margin-bottom: 0; }
  .plugin-page, .plugin-content { flex: 1; min-height: 0; }
  .plugin-content { height: auto; }
  .plugin-heading, .plugin-toolbar { flex: none; }
}
@media (max-width: 760px) {
  .plugin-browser { grid-template-columns: minmax(0, 1fr); }
  .plugin-detail { display: none; }
  .show-details .plugin-detail { display: flex; }
  .show-details .plugin-list { display: none; }
  .plugin-back { display: inline-flex; align-self: flex-start; margin: 12px 12px 0; }
  .plugin-toolbar > .noah-input-wrap { max-width: none; }
  .plugin-filter-summary { margin-left: auto; }
  .plugin-category-switch button { padding-inline: 10px; }
}
@media (prefers-reduced-motion: reduce) {
  .plugin-item, .plugin-detail-enter-active, .plugin-detail-leave-active, .plugin-view-enter-active, .plugin-view-leave-active, .plugin-layout-highlight, .plugin-layout-switch button { transition: none; }
  .plugin-view-enter-from { transform: none; }
}

.plugin-table :deep(.noah-table) { table-layout: fixed; }
.plugin-table :deep(th), .plugin-table :deep(td) { padding: 12px 10px; }
.plugin-table-text { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.plugin-table-state { display: flex; align-items: center; justify-content: space-between; gap: 4px; }
.plugin-table-open { display: grid; place-items: center; flex: none; width: 30px; height: 32px; border: 0; border-radius: var(--radius-xs); background: transparent; color: var(--text-muted); cursor: pointer; transition: background-color 160ms, color 160ms; }
.plugin-table-open:hover { color: var(--text); background: var(--hover); }
.plugin-table-open:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 2px; }
.plugin-drawer-overlay { position: fixed; inset: 0; z-index: 1000; display: flex; justify-content: flex-end; background: rgb(0 0 0 / .22); }
.plugin-drawer-panel { width: min(460px, 100%); height: 100dvh; min-width: 0; box-shadow: var(--shadow-lg); }
.plugin-drawer-enter-active, .plugin-drawer-leave-active { transition: background-color 240ms ease; }
.plugin-drawer-enter-active .plugin-drawer-panel, .plugin-drawer-leave-active .plugin-drawer-panel { transition: transform 240ms cubic-bezier(.2,.8,.2,1); }
.plugin-drawer-enter-from, .plugin-drawer-leave-to { background: transparent; }
.plugin-drawer-enter-from .plugin-drawer-panel, .plugin-drawer-leave-to .plugin-drawer-panel { transform: translateX(100%); }
@media (max-width: 560px) {
  .plugin-table :deep(th), .plugin-table :deep(td) { padding: 10px 5px; font-size: 11px; }
  .plugin-table :deep(.noah-tag) { font-size: 10px; padding-inline: 4px; }
  .plugin-table-open { width: 20px; }
}
@media (prefers-reduced-motion: reduce) {
  .plugin-drawer-enter-active, .plugin-drawer-leave-active, .plugin-drawer-enter-active .plugin-drawer-panel, .plugin-drawer-leave-active .plugin-drawer-panel { transition: none; }
}
</style>

<style scoped>
.plugin-page-body { position: relative; display: grid; min-width: 0; min-height: 0; }
.plugin-page-body > * { grid-area: 1 / 1; min-width: 0; min-height: 0; }
.plugin-policy-panel { display: flex; flex-direction: column; }
.plugin-policy-panel > .client-policy-browser { flex: 1; min-height: 0; }
.plugin-information-panel { display: flex; flex-direction: column; gap: 20px; min-height: 0; min-width: 0; }
.plugin-section-enter-active, .plugin-section-leave-active { transition: opacity 160ms ease, transform 160ms ease; }
.plugin-section-leave-active { pointer-events: none; }
.plugin-section-enter-from, .plugin-section-leave-to { opacity: 0; transform: translateY(6px); }
@media (min-width: 861px) {
  .plugin-page { flex: 1; min-height: 0; }
  .plugin-page-body { flex: 1; }
  .plugin-information-panel, .plugin-policy-panel { flex: 1; min-height: 0; }
  .plugin-information-panel .plugin-toolbar { flex: none; min-height: 48px; }
  .plugin-information-panel .plugin-content { flex: 1; height: auto; }
}
</style>
