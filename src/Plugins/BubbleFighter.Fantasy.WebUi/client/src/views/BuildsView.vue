<script setup>
import { computed, ref, watch } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import StoreTabs from '../components/StoreTabs.vue'
import ErrorCard from '../components/ErrorCard.vue'
import { clientBuildLabel as versionLabel } from '../utils/clientBuildLabel'
import { api, jsonRequest } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { useUiStore } from '../stores/ui'
import { useServerStore } from '../stores/server'

const { t } = useI18n()
const ui = useUiStore()
const server = useServerStore()
// Running builds come from the overview; a configured client whose data is not imported yet runs nothing, so it is
// listed from the installation configuration instead, where its data can be imported.
const { data, loading, error, reload } = useResource(async () => {
  const [overview, configuration, status] = await Promise.all([api('overview'), api('setup/configuration'), api('setup/status')])
  return { overview, settings: configuration.settings, hasDatabasePassword: configuration.hasDatabasePassword, profiles: status.profiles || [] }
})
const configuredClients = settings => settings.clients || (settings.profile !== 'management' ? [{ profile: settings.profile, clientDataPath: settings.clientDataPath, deferClientDataImport: settings.deferClientDataImport }] : [])
const builds = computed(() => {
  if (!data.value) return []
  const profile = id => data.value.profiles.find(item => item.id === id)
  const running = (data.value.overview.builds || []).map(build => ({ displayNames: profile(build.id)?.displayNames, experimental: !!profile(build.id)?.experimental, ...build }))
  const waiting = configuredClients(data.value.settings).filter(client => !running.some(build => build.id === client.profile)).map(client => {
    const profile = data.value.profiles.find(item => item.id === client.profile)
    return { id: client.profile, region: client.profile.split('-')[0], displayNames: profile?.displayNames, experimental: !!profile?.experimental, listening: false, awaitingData: true }
  })
  return [...running, ...waiting]
})
const query = ref('')
const region = ref('')
const selected = ref('')
const tab = ref('information')
const mobileDetails = ref(false)
const folderInput = ref(null)
const zipInput = ref(null)
const imports = ref({})
const importError = ref('')
const importedPath = ref('')
const importing = ref(false)
const dragging = ref(false)
const addOpen = ref(false)
const profileChoices = ref([])
const draft = ref({ profile: '' })
const draftError = ref('')
const allBuilds = computed(() => builds.value || [])
async function openAdd() {
  draft.value = { profile: '' }; draftError.value = ''
  try {
    const [status, configuration] = await Promise.all([api('setup/status'), api('setup/configuration')])
    const clients = configuredClients(configuration.settings)
    const existing = new Set([...allBuilds.value.map(build => build.id), ...clients.map(client => client.profile)])
    profileChoices.value = (status.profiles || []).filter(profile => profile.setupEnabled && !existing.has(profile.id))
      .map(profile => ({ value: profile.id, label: profile.displayNames?.[ui.lang] || profile.id }))
    draft.value.profile = profileChoices.value[0]?.value || ''
  } catch (error) { draftError.value = error.message }
  addOpen.value = true
}
async function addClient() {
  if (!draft.value.profile) return
  draftError.value = ''
  try {
    await api('clients', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ profile: draft.value.profile }) })
    const id = draft.value.profile
    await reload()
    query.value = ''; region.value = ''; selected.value = id; tab.value = 'data'; mobileDetails.value = true; addOpen.value = false
  } catch (error) { draftError.value = error.message }
}
const name = build => build.displayNames?.[ui.lang] || build.release || build.id
const regionName = code => t('region.' + code)
const regions = computed(() => [{ value: '', label: t('builds.allRegions') }, ...[...new Set(allBuilds.value.map(b => b.region))].sort().map(value => ({ value, label: regionName(value) }))])
const tabs = computed(() => [{ id: 'information', label: t('builds.information') }, { id: 'data', label: t('builds.data') }])
const filtered = computed(() => allBuilds.value.filter(b => (!region.value || b.region === region.value) && [name(b), b.id, regionName(b.region)].some(value => String(value).toLocaleLowerCase().includes(query.value.trim().toLocaleLowerCase()))))
const current = computed(() => filtered.value.find(b => b.id === selected.value) || filtered.value[0])
const pending = computed(() => current.value ? imports.value[current.value.id] : null)
watch(filtered, rows => { if (!rows.some(b => b.id === selected.value)) { selected.value = rows[0]?.id || ''; mobileDetails.value = false } })
watch(() => current.value?.id, () => { importError.value = ''; importedPath.value = ''; dragging.value = false })
function choose(build) { selected.value = build.id; mobileDetails.value = true }
function openImport() { if (current.value) { tab.value = 'data'; mobileDetails.value = true } }
function selectFiles(event, kind) { prepare(Array.from(event.target.files || []), kind); event.target.value = '' }
function prepare(files, kind) {
  if (!current.value || !files.length) return
  importError.value = ''; importedPath.value = ''
  if (kind === 'zip' && (files.length !== 1 || !/\.zip$/i.test(files[0].name))) { importError.value = t('builds.zipOnly'); return }
  // Retain File handles until the owner explicitly submits the upload.
  imports.value[current.value.id] = { kind, name: kind === 'folder' ? files[0].webkitRelativePath.split('/')[0] : files[0].name,
    count: files.length, bytes: files.reduce((total, file) => total + file.size, 0), fileObjects: files,
    files: files.slice(0, 6).map(file => ({ name: file.webkitRelativePath || file.name, bytes: file.size })) }
}
function drop(event) { dragging.value = false; prepare(Array.from(event.dataTransfer.files), 'zip') }
function clearSelection() { if (current.value) delete imports.value[current.value.id]; importedPath.value = ''; importError.value = '' }
async function submitImport() {
  if (!current.value || !pending.value || importing.value) return
  importing.value = true; importError.value = ''; importedPath.value = ''
  try {
    const form = new FormData()
    for (const file of pending.value.fileObjects) form.append('files', file, file.webkitRelativePath || file.name)
    const id = current.value.id
    const result = await api(`setup/client-data?profile=${encodeURIComponent(id)}&kind=${pending.value.kind}`, { method: 'POST', body: form })
    // The upload only stores the files; the client starts once its configuration names the imported folder.
    const configuration = await api('setup/configuration')
    const clients = configuredClients(configuration.settings).map(client => client.profile === id ? { ...client, clientDataPath: result.path, deferClientDataImport: false } : client)
    await api('setup/configuration', jsonRequest('PUT', {
      settings: { ...configuration.settings, clients, profile: 'management', clientDataPath: '', deferClientDataImport: false },
      retainDatabasePassword: configuration.hasDatabasePassword
    }))
    importedPath.value = result.path
    delete imports.value[id]
    await server.refreshSetup()
    server.setupOpen = true
    await reload()
  } catch (error) { importError.value = error.message }
  finally { importing.value = false }
}
function size(bytes) {
  if (bytes >= 1073741824) return `${(bytes / 1073741824).toFixed(2)} GiB`
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MiB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${bytes} B`
}
</script>

<template>
  <section class="build-browser" :class="{ 'mobile-details-open': mobileDetails }" :aria-label="t('overview.builds')">
    <header class="build-toolbar">
      <div class="build-toolbar-title"><span>{{ t('builds.manage') }}</span></div>
      <div class="build-filter-end"><span class="text-caption text-muted">{{ t('builds.count', { shown: filtered.length, total: allBuilds.length }) }}</span><NoahInput v-model="query" prefix-icon="search" :placeholder="t('builds.search')" :aria-label="t('builds.search')" /><NoahSelect v-model="region" :options="regions" :aria-label="t('col.region')" /></div>
      <div class="build-tools"><NoahButton variant="glass" icon="refresh" icon-only :loading="loading" :title="t('store.refresh')" :aria-label="t('store.refresh')" @click="reload" /><NoahButton variant="glass" @click="openAdd">{{ t('builds.add') }}</NoahButton></div>
    </header>
    <ErrorCard v-if="error" :error="error" @retry="reload" />
    <div class="build-layout" :aria-busy="loading">
      <div class="build-list">
        <div v-if="loading && !builds.length" class="build-grid"><div v-for="index in 4" :key="index" class="build-placeholder"><NoahSkeleton :rows="3" /></div></div>
        <div v-else-if="filtered.length" class="build-grid">
          <button v-for="build in filtered" :key="build.id" type="button" class="build-card" :class="{ selected: current?.id === build.id }" :aria-pressed="current?.id === build.id" @click="choose(build)">
            <span class="build-card-main"><span class="build-icon" aria-hidden="true"><NoahIcon name="builds" :size="25" /></span><span class="build-card-copy"><strong>{{ name(build) }}</strong><span class="build-card-version">{{ versionLabel(build) }}</span></span></span>
            <span class="build-card-footer"><span class="build-card-status"><span class="build-status-tags"><NoahTag pill size="sm" :tone="build.listening ? 'success' : 'default'">{{ t(build.listening ? 'status.listening' : 'builds.inactive') }}</NoahTag><NoahTag v-if="build.awaitingData" pill size="sm" tone="warning">{{ t('builds.awaitingData') }}</NoahTag><NoahTag v-if="build.experimental" pill size="sm" tone="info">{{ t('builds.experimental') }}</NoahTag></span></span></span>
          </button>
        </div>
        <div v-else-if="!loading" class="build-empty"><NoahIcon name="search" :size="30" /><p>{{ t(query || region ? 'store.noMatches' : 'builds.empty') }}</p></div>
      </div>
      <aside class="build-detail" :aria-label="t('builds.details')">
        <template v-if="current">
          <NoahButton class="build-back" variant="glass" icon="chevron-left" @click="mobileDetails = false">{{ t('builds.back') }}</NoahButton>
          <header class="build-detail-heading"><span class="text-caption text-muted">{{ regionName(current.region) }}</span><div class="build-title-row"><h2>{{ name(current) }}</h2><NoahTag pill size="sm" :tone="current.listening ? 'success' : 'default'">{{ t(current.listening ? 'status.listening' : 'builds.inactive') }}</NoahTag><NoahTag v-if="current.awaitingData" pill size="sm" tone="warning">{{ t('builds.awaitingData') }}</NoahTag><NoahTag v-if="current.experimental" pill size="sm" tone="info">{{ t('builds.experimental') }}</NoahTag></div><code class="text-caption text-muted">{{ current.id }}</code></header>
          <StoreTabs v-model="tab" class="build-detail-tabs" :items="tabs" :label="t('builds.details')" />
          <div class="build-detail-body">
            <template v-if="tab === 'information'">
              <dl class="build-facts"><dt>{{ t('builds.profile') }}</dt><dd><code>{{ current.id }}</code></dd><dt>{{ t('col.region') }}</dt><dd>{{ regionName(current.region) }}</dd><dt>{{ t('col.version') }}</dt><dd>{{ current.id.split('-').at(-1) }}</dd><dt>{{ t('col.game_port') }}</dt><dd>{{ current.gamePort ?? '—' }}</dd><dt>{{ t('col.relay_port') }}</dt><dd>{{ current.relayPort ?? '—' }}</dd><dt>{{ t('col.players') }}</dt><dd>{{ current.players ?? '—' }}</dd></dl>
              <div class="build-data-summary"><NoahIcon name="builds" :size="20" /><div><strong>{{ t('builds.clientData') }}</strong><p>{{ pending ? t('builds.pendingSummary', { count: pending.count.toLocaleString(), size: size(pending.bytes) }) : t('builds.dataUnknown') }}</p></div></div>
            </template>
            <template v-else>
              <p class="build-import-hint">{{ t('builds.importHint', { name: name(current) }) }}</p>
              <input ref="folderInput" class="build-file-input" type="file" webkitdirectory multiple :aria-label="t('setup.chooseLocalFolder')" @change="selectFiles($event, 'folder')" />
              <input ref="zipInput" class="build-file-input" type="file" accept=".zip,application/zip" :aria-label="t('setup.chooseDataPackage')" @change="selectFiles($event, 'zip')" />
              <div class="build-dropzone" :class="{ dragging }" @dragover.prevent="dragging = true" @dragleave.self="dragging = false" @drop.prevent="drop"><NoahIcon name="upload" :size="30" /><strong>{{ t('builds.dropTitle') }}</strong><span>{{ t('builds.dropHint') }}</span><div class="build-pick-actions"><NoahButton variant="glass" @click="folderInput?.click()">{{ t('setup.chooseLocalFolder') }}</NoahButton><NoahButton variant="glass" @click="zipInput?.click()">{{ t('setup.chooseDataPackage') }}</NoahButton></div></div>
              <div v-if="pending" class="build-selection"><div class="build-selection-heading"><strong>{{ pending.name }}</strong><NoahButton variant="ghost" icon="trash" icon-only :aria-label="t('setup.clearClientData')" @click="clearSelection" /></div><p>{{ t('builds.pendingSummary', { count: pending.count.toLocaleString(), size: size(pending.bytes) }) }}</p><ul><li v-for="file in pending.files" :key="file.name"><span :title="file.name">{{ file.name }}</span><span>{{ size(file.bytes) }}</span></li></ul><span v-if="pending.count > pending.files.length" class="text-caption text-muted">{{ t('builds.moreFiles', { count: pending.count - pending.files.length }) }}</span></div>
              <p v-if="importError" class="build-error" role="alert">{{ importError }}</p><p v-if="importedPath" class="build-preview-notice" role="status">{{ t('builds.importComplete', { path: importedPath }) }}</p>
            </template>
          </div>
          <footer class="build-detail-actions"><NoahButton v-if="tab === 'information'" class="build-import-button" variant="primary" @click="openImport">{{ t('builds.import') }}</NoahButton><template v-else><NoahButton variant="primary" :loading="importing" :disabled="!pending || importing" @click="submitImport">{{ t(importing ? 'builds.importing' : 'builds.import') }}</NoahButton></template></footer>
        </template>
        <div v-else class="build-empty"><NoahIcon name="builds" :size="32" /><p>{{ t('builds.choose') }}</p></div>
      </aside>
    </div>
  </section>
  <NoahModal v-model="addOpen" :title="t('builds.add')" :close-label="t('common.close')" width="480px">
    <form id="add-client-form" class="build-add-form" @submit.prevent="addClient">
      <label><span>{{ t('builds.profile') }}</span><NoahSelect v-model="draft.profile" :options="profileChoices" :aria-label="t('builds.profile')" /></label>
      <p class="text-caption text-muted">{{ t('builds.addDescription') }}</p><p v-if="draftError" role="alert" class="build-error">{{ draftError }}</p>
    </form>
    <template #footer><NoahButton variant="glass" @click="addOpen = false">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" html-type="submit" form="add-client-form" :disabled="!draft.profile">{{ t('builds.add') }}</NoahButton></template>
  </NoahModal>
</template>

<style scoped>
.build-browser { display:flex; flex-direction:column; gap:20px; min-width:0; }
.build-add-form { display:grid; gap:16px; }
.build-add-form label { display:grid; gap:8px; font-size:var(--text-control); }
.build-add-form p { margin:0; line-height:1.7; }
.build-toolbar, .build-tools, .build-toolbar-title, .build-filter-end { display:flex; align-items:center; gap:12px; }
.build-toolbar { flex-wrap:wrap; }
.build-filter-end { margin-left:auto; white-space:nowrap; min-width:0; flex-wrap:wrap; }
.build-tools, .build-toolbar-title { flex:none; }
.build-import-button.noah-btn { min-height:40px; padding:10px 16px; font-size:var(--text-control); }
.build-browser code { font-family:var(--font-ui); font-variant-numeric:tabular-nums; }
.build-toolbar-title { font-size:var(--text-body); font-weight:500; }
.build-filter-end > .noah-input-wrap { flex:1 1 180px; max-width:260px; min-width:160px; }
.build-filter-end > .noah-select { width:160px; }
.build-layout { display:grid; grid-template-columns:minmax(0,1.5fr) minmax(320px,1fr); gap:24px; align-items:start; }
.build-list { min-width:0; }
.build-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
.build-card, .build-placeholder { min-width:0; padding:16px; border:1px solid var(--border); border-radius:8px; background:var(--surface); }
.build-card { display:flex; flex-direction:column; gap:16px; text-align:left; color:var(--text); font:inherit; cursor:pointer; transition:background-color 160ms,border-color 160ms; }
.build-card:hover { background:var(--hover); }
.build-card.selected { background:var(--accent-soft); border-color:var(--accent); }
.build-card:focus-visible { outline:2px solid var(--accent); outline-offset:3px; }
.build-card-main, .build-card-status { display:flex; align-items:center; justify-content:space-between; gap:10px; }
.build-status-tags { display:flex; align-items:center; flex-direction:row-reverse; flex-wrap:wrap; justify-content:flex-start; gap:6px; margin-left:auto; }
.build-card-main { justify-content:flex-start; gap:12px; }
.build-icon { display:grid; place-items:center; flex:none; width:44px; height:44px; border:1px solid var(--border-soft); background:var(--surface-raised); border-radius:8px; color:var(--text-muted); }
.build-card-copy { display:grid; min-width:0; gap:6px; overflow-wrap:anywhere; }
.build-card-copy strong { font-size:var(--text-control); line-height:1.5; font-weight:550; }
.build-card-copy .build-card-version { font-size:var(--text-caption); color:var(--text-muted); }
.build-card-footer { display:grid; gap:12px; border-top:1px solid var(--border-soft); padding-top:12px; color:var(--text-muted); font-size:var(--text-caption); }
.build-card-footer .noah-icon { flex:none; }
.build-browser :deep(.noah-tag) { padding:5px 9px; line-height:1.2; white-space:nowrap; }
.build-detail { display:flex; flex-direction:column; min-width:0; border:1px solid var(--border); border-radius:8px; background:var(--surface); overflow:hidden; position:sticky; top:24px; }
.build-detail-heading { display:grid; gap:8px; padding:22px 20px 16px; }
.build-title-row { display:flex; align-items:center; flex-wrap:wrap; gap:10px; }
.build-title-row h2 { margin:0; font-size:20px; line-height:1.5; overflow-wrap:anywhere; }
.build-detail-tabs { margin:0 20px; flex:none; }
.build-detail-body { display:grid; align-content:start; gap:20px; padding:20px; min-width:0; }
.build-facts { display:grid; grid-template-columns:auto minmax(0,1fr); gap:16px; margin:0; font-size:var(--text-control); }
.build-facts dt { color:var(--text-muted); }
.build-facts dd { margin:0; text-align:right; overflow-wrap:anywhere; }
.build-data-summary { display:flex; align-items:center; gap:12px; padding:16px; border:1px solid var(--border-soft); border-radius:8px; background:var(--glass-panel); }
.build-data-summary > div { flex:1; min-width:0; font-size:var(--text-control); }
.build-data-summary p, .build-import-hint { font-size:var(--text-caption); line-height:1.8; color:var(--text-muted); margin:6px 0 0; }
.build-import-hint { margin:0; }
.build-file-input { display:none; }
.build-dropzone { display:flex; flex-direction:column; align-items:center; text-align:center; gap:12px; padding:24px 12px; border:1px dashed var(--border); border-radius:8px; background:var(--glass-panel); font-size:var(--text-control); }
.build-dropzone > .noah-icon, .build-dropzone > span { color:var(--text-muted); }
.build-dropzone > span { font-size:var(--text-caption); }
.build-dropzone.dragging { background:var(--accent-soft); border-color:var(--accent); }
.build-pick-actions { display:flex; justify-content:center; flex-wrap:wrap; gap:8px; }
.build-selection { padding:14px; border:1px solid var(--border-soft); border-radius:8px; min-width:0; }
.build-selection-heading { display:flex; align-items:center; justify-content:space-between; gap:10px; font-size:var(--text-control); }
.build-selection-heading strong { overflow-wrap:anywhere; min-width:0; }
.build-selection p { margin:4px 0 12px; color:var(--text-muted); font-size:var(--text-caption); }
.build-selection ul { list-style:none; margin:0; padding:0; display:grid; gap:8px; }
.build-selection li { display:flex; justify-content:space-between; gap:12px; font-size:var(--text-caption); }
.build-selection li > span:first-child { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.build-selection li > span:last-child { color:var(--text-muted); white-space:nowrap; }
.build-selection > .text-caption { display:block; margin-top:12px; }
.build-error { color:var(--red); font-size:var(--text-control); margin:0; }
.build-preview-notice { margin:0; padding:12px; border-radius:6px; background:var(--accent-soft); font-size:var(--text-control); line-height:1.7; }
.build-detail-actions { display:flex; align-items:center; justify-content:space-between; gap:12px; padding:16px 20px; border-top:1px solid var(--border-soft); margin-top:auto; }
.build-detail-actions > .noah-btn:only-child { width:100%; }
.build-empty { display:grid; justify-items:center; align-content:center; min-height:300px; padding:24px; color:var(--text-muted); text-align:center; }
.build-back { display:none; }
@media(min-width:861px) {
  .build-browser { flex:1; min-height:0; }
  .build-toolbar { flex:none; }
  .build-layout { flex:1; min-height:0; align-items:stretch; }
  .build-list { min-height:0; overflow-y:auto; padding:3px 6px 6px 3px; scrollbar-gutter:stable; overscroll-behavior:contain; }
  .build-detail { min-height:0; position:static; }
  .build-detail-body { min-height:0; overflow-y:auto; flex:1; overscroll-behavior:contain; }
  .build-detail-heading, .build-detail-actions { flex:none; }
}
@media(min-width:861px) and (max-width:1180px) { .build-grid { grid-template-columns:minmax(0,1fr); } }
@media(max-width:860px) {
  .build-layout { grid-template-columns:minmax(0,1fr); }
  .build-detail { display:none; position:static; }
  .mobile-details-open .build-detail { display:flex; }
  .mobile-details-open .build-list { display:none; }
  .build-back { display:inline-flex; align-self:flex-start; margin:16px 20px 0; }
  .build-toolbar-title { display:none; }
  .build-filter-end { order:1; justify-content:space-between; width:100%; margin-left:0; }
  .build-filter-end > .noah-input-wrap { max-width:none; }
  .build-tools { margin-left:auto; }
}
@media(max-width:480px) { .build-grid { grid-template-columns:minmax(0,1fr); } }
</style>
