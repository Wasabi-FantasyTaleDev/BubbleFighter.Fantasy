<script setup>
import { computed, ref, onBeforeUnmount } from 'vue'
import StoreTabs from './StoreTabs.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import { api, jsonRequest } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

const { t } = useI18n()
const props = defineProps({ sources: { type: Array, default: () => [] }, packages: { type: Array, default: () => [] } })
const entries = ref([])
const nextCursor = ref(null)
const busy = ref(false)
const actionError = ref(null)
const verification = ref(null)
const checking = ref(null)
const installing = ref(null)
const installTarget = ref(null)
const installOpen = computed({ get: () => installTarget.value !== null, set: value => { if (!value && !installing.value) installTarget.value = null } })
const emit = defineEmits(['installed'])
let revision = 0
let expanded = false
onBeforeUnmount(() => revision++)
const terminal = job => ['staged', 'failed', 'cancelled', 'interrupted'].includes(job.phase)
const sorted = computed(() => [...entries.value].sort((a, b) => b.id.localeCompare(a.id)))
const filter = ref('all')
const selectedId = ref('')
const detailsOpen = ref(false)
const matches = (job, category) => category === 'all' || (category === 'active' ? !terminal(job)
  : category === 'attention' ? ['failed', 'interrupted'].includes(job.phase) : ['staged', 'cancelled'].includes(job.phase))
const tabs = computed(() => ['all', 'active', 'attention', 'done'].map(id => ({ id, label: t('store.downloads.' + id), count: entries.value.filter(job => matches(job, id)).length })))
const visible = computed(() => sorted.value.filter(job => matches(job, filter.value)))
const selected = computed(() => visible.value.find(job => job.id === selectedId.value) || visible.value[0])
function name(job) {
  const entry = props.packages.find(entry => ['target', 'kind', 'game', 'id'].every(key => entry.identity[key] === job.package[key]))
  return entry?.downloads.find(offer => offer.sourceId === job.sourceId)?.package.name ?? entry?.downloads[0]?.package.name ?? job.package.id
}
const sourceName = job => props.sources.find(source => source.id === job.sourceId)?.name || job.sourceId
const percentage = job => job.totalBytes > 0 ? Math.max(0, Math.min(100, job.bytesReceived / job.totalBytes * 100)) : null
function size(bytes) {
  if (bytes < 1024) return `${bytes.toLocaleString()} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MiB`
}

function merge(jobs) {
  const byId = new Map(entries.value.map(job => [job.id, job]))
  for (const job of jobs) byId.set(job.id, job)
  entries.value = [...byId.values()]
}

async function refresh() {
  if (busy.value) return
  const mine = revision
  const page = await api('distribution/v1/jobs')
  const currentIds = new Set(page.entries.map(job => job.id))
  // Active entries on older pages still need progress after the first page fills.
  const updates = []
  const removed = new Set()
  for (const job of entries.value) {
    if (currentIds.has(job.id) || terminal(job)) continue
    try { updates.push(await api('distribution/v1/jobs/' + encodeURIComponent(job.id))) }
    catch (e) { if (e.status === 404) removed.add(job.id); else throw e }
  }
  if (mine !== revision) return
  entries.value = entries.value.filter(job => !removed.has(job.id))
  merge([...page.entries, ...updates])
  if (!expanded) nextCursor.value = page.nextCursor
  return page
}
const resource = useResource(refresh, { interval: 2000 })

async function older() {
  busy.value = true
  actionError.value = null
  revision++
  try {
    const page = await api('distribution/v1/jobs?after=' + encodeURIComponent(nextCursor.value))
    merge(page.entries)
    expanded = true
    nextCursor.value = page.nextCursor
  } catch (e) { actionError.value = e }
  finally { busy.value = false }
}

async function change(job, remove) {
  busy.value = true
  actionError.value = null
  verification.value = null
  revision++
  try {
    const path = 'distribution/v1/jobs/' + encodeURIComponent(job.id)
    if (remove) {
      await api(path, { method: 'DELETE' })
      entries.value = entries.value.filter(entry => entry.id !== job.id)
    } else merge([await api(path + '/cancel', { method: 'POST' })])
  } catch (e) { actionError.value = e }
  finally { revision++; busy.value = false; await resource.reload() }
}

async function verify(job) {
  busy.value = true
  checking.value = job.id
  actionError.value = null
  verification.value = null
  revision++
  try {
    verification.value = await api('distribution/v1/jobs/' + encodeURIComponent(job.id) + '/verify', { method: 'POST' })
  } catch (e) { actionError.value = e }
  finally { revision++; checking.value = null; busy.value = false }
}

async function install(job) {
  if (busy.value) return
  busy.value = true
  installing.value = job.id
  actionError.value = null
  revision++
  try {
    await api('distribution/v1/server-plugins/install', {
      ...jsonRequest('POST', { jobId: job.id })
    })
    emit('installed')
    installTarget.value = null
  } catch (e) { actionError.value = e }
  finally {
    revision++
    installing.value = null
    busy.value = false
    await resource.reload()
  }
}

defineExpose({ reload: resource.reload })
</script>

<template>
  <section class="downloads-panel" :aria-label="t('store.jobs')">
    <div class="downloads-toolbar"><StoreTabs v-model="filter" :items="tabs" :label="t('store.jobs')" /><NoahButton variant="glass" icon="refresh" icon-only :title="t('store.refresh')" :aria-label="t('store.refresh')" :loading="resource.loading.value" :disabled="busy" @click="resource.reload" /></div>
    <p v-if="actionError || resource.error.value" role="alert">{{ (actionError || resource.error.value).message }}</p>
    <p v-if="verification" role="status"><code>{{ verification.jobId }}</code> · {{ t('store.verifiedNow', { files: verification.files, bytes: verification.bytes.toLocaleString() }) }}</p>
    <NoahSkeleton v-if="resource.loading.value && !entries.length" :rows="3" />
    <div v-else-if="selected" class="download-workspace">
      <div class="download-task-list" role="group" :aria-label="t('store.jobs')">
        <button v-for="job in visible" :key="job.id" type="button" class="download-task" :aria-pressed="selected.id === job.id" @click="selectedId = job.id; detailsOpen = false">
          <span class="download-task-icon" aria-hidden="true"><NoahIcon name="plugins" :size="22" /></span>
          <span class="download-task-copy"><strong>{{ name(job) }}</strong><span>{{ job.version }} · {{ sourceName(job) }}</span></span>
          <span class="download-task-status"><span>{{ t('store.job.' + job.phase) }}</span><span>{{ percentage(job) == null ? '—' : Math.floor(percentage(job)) + '%' }}</span></span>
          <progress :value="job.totalBytes ? job.bytesReceived : undefined" :max="job.totalBytes || 1" :aria-label="name(job) + ': ' + t('store.progress')" />
        </button>
      </div>
      <article class="download-detail" :aria-label="name(selected)">
        <header class="download-detail-heading">
          <div class="download-detail-title"><h2>{{ name(selected) }}</h2><p class="text-caption text-muted">{{ selected.version }} · {{ sourceName(selected) }}</p></div>
          <NoahTag>{{ t('store.job.' + selected.phase) }}</NoahTag>
        </header>
        <div class="download-summary">
          <div class="download-total"><span>{{ t('store.progress') }}</span><strong>{{ percentage(selected) == null ? '—' : Math.floor(percentage(selected)) + '%' }}</strong></div>
          <progress :value="selected.totalBytes ? selected.bytesReceived : undefined" :max="selected.totalBytes || 1" :aria-label="t('store.progress')" />
          <div class="download-progress-meta"><span>{{ t('store.filesProgress', { done: selected.completedFiles, total: selected.fileCount }) }}</span><span>{{ size(selected.bytesReceived) }} / {{ size(selected.totalBytes) }}</span></div>
          <div v-if="selected.errorCode" role="status" class="download-failure"><NoahIcon name="alert-circle" :size="18" /><span>{{ t('store.downloads.failed') }}</span></div>
        </div>
          <div class="download-actions">
            <NoahButton v-if="selected.phase === 'staged' && selected.package.kind === 'plugin' && selected.package.target === 'server'" variant="primary" icon="plugins" :disabled="busy" @click="installTarget = selected">{{ t('store.installServerPlugin') }}</NoahButton>
            <NoahButton v-if="selected.phase === 'staged'" icon="check-circle" :loading="checking === selected.id" :disabled="busy" :title="t('store.verifyStaging')" @click="verify(selected)">{{ t('store.verifyStaging') }}</NoahButton>
            <NoahButton v-if="terminal(selected)" class="download-delete" icon="trash" icon-only :title="t('store.deleteStaging')" :aria-label="t('store.deleteStaging')" :disabled="busy" @click="change(selected, true)" />
            <NoahButton v-else icon="close" :disabled="busy || selected.phase === 'cleaning'" @click="change(selected, false)">{{ t('store.cancelDownload') }}</NoahButton>
          </div>
        <button type="button" class="download-details-toggle" :aria-expanded="detailsOpen" @click="detailsOpen = !detailsOpen"><span>{{ t('store.downloads.details') }}</span><NoahIcon :name="detailsOpen ? 'chevron-up' : 'chevron-down'" :size="14" /></button>
        <div v-if="detailsOpen" class="download-metadata"><dl><dt>{{ t('store.downloadSource') }}</dt><dd>{{ sourceName(selected) }}</dd><dt>{{ t('store.packageIdentity') }}</dt><dd>{{ selected.package.id }}</dd><dt>{{ t('store.downloads.taskId') }}</dt><dd class="mono">{{ selected.id }}</dd><template v-if="selected.errorCode"><dt>{{ t('common.error') }}</dt><dd class="mono download-error-code">{{ selected.errorCode }}</dd></template></dl></div>
      </article>
    </div>
    <p v-if="!entries.length && !resource.loading.value" class="text-muted">{{ t('store.noJobs') }}</p>
    <p v-else-if="!visible.length" class="text-muted">{{ t('store.downloads.emptyFilter') }}</p>
    <NoahButton v-if="nextCursor" :loading="busy" @click="older">{{ t('store.olderJobs') }}</NoahButton>
    <!-- <p class="downloads-boundary text-caption text-muted">{{ t('store.stagingHint') }}</p> -->
  </section>
  <NoahModal v-model="installOpen" :title="t('store.installServerPlugin')" :close-label="t('common.close')" :show-close="!installing" :close-on-click-overlay="!installing" width="520px">
    <div class="server-plugin-install-review">
    <p v-if="installTarget" class="mono">{{ installTarget.package.id }} · {{ installTarget.version }}</p>
    <p>{{ t('store.installConfirmation') }}</p>
    <p v-if="actionError" role="alert">{{ actionError.message }}</p>
    </div>
    <template #footer>
      <NoahButton variant="glass" :disabled="!!installing" @click="installTarget = null">{{ t('common.cancel') }}</NoahButton>
      <NoahButton variant="primary" :loading="!!installing" @click="install(installTarget)">{{ t('store.installServerPlugin') }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
.downloads-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 22px; }
.download-workspace { display: grid; grid-template-columns: minmax(220px, .7fr) minmax(0, 1.4fr); align-items: start; gap: 22px; min-height: 420px; }
.download-task-list { min-width: 0; display: grid; gap: 10px; }
.download-task { display: grid; grid-template-columns: 40px minmax(0, 1fr); gap: 12px; border: 1px solid var(--border); border-radius: 8px; padding: 18px; background: var(--surface); color: var(--text); text-align: left; font: inherit; cursor: pointer; }
.download-task:hover { background: var(--hover); }
.download-task[aria-pressed='true'] { border-color: var(--accent); background: var(--accent-soft); }
.download-task:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.download-task-icon { width: 40px; height: 40px; display: grid; place-items: center; border-radius: 6px; background: var(--surface-raised); color: var(--text-muted); }
.download-task-copy { min-width: 0; display: grid; gap: 5px; }
.download-task-copy strong { font-size: var(--text-control); overflow-wrap: anywhere; line-height: 1.5; }
.download-task-copy > span { color: var(--text-muted); font-size: var(--text-caption); overflow-wrap: anywhere; }
.download-task-status { display: flex; justify-content: space-between; gap: 10px; grid-column: 1 / -1; font-size: var(--text-caption); color: var(--text-muted); }
.download-task progress { grid-column: 1 / -1; }
.download-detail { min-width: 0; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); }
.download-detail-heading { display: flex; align-items: start; justify-content: space-between; gap: 20px; padding: 28px 28px 0; }
.download-detail-title { min-width: 0; }
.download-detail-heading > .noah-tag { flex: none; margin-top: 3px; }
.download-detail-heading h2 { font-size: 22px; line-height: 1.45; margin: 0; overflow-wrap: anywhere; }
.download-detail-heading p { margin: 10px 0 0; line-height: 1.6; overflow-wrap: anywhere; }
.download-summary { display: grid; gap: 16px; padding: 32px 28px 28px; }
.download-total { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; font-variant-numeric: tabular-nums; }
.download-total strong { font-size: 24px; font-weight: 500; line-height: 1.3; }
.download-total > span { color: var(--text-muted); font-size: var(--text-control); }
.download-progress-meta { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 10px 20px; color: var(--text-muted); font-size: var(--text-caption); line-height: 1.6; font-variant-numeric: tabular-nums; }
.download-failure { display: flex; align-items: center; gap: 10px; padding-top: 6px; color: var(--red); font-size: var(--text-control); line-height: 1.6; }
.download-failure .noah-icon { flex: none; }
.download-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; padding: 18px 28px; border-top: 1px solid var(--border-soft); }
.download-delete { margin-left: auto; }
.download-error-code { color: var(--red); }
.download-details-toggle { display: flex; justify-content: space-between; align-items: center; gap: 16px; width: 100%; padding: 16px 24px; border: 0; border-top: 1px solid var(--border-soft); background: none; color: var(--text-muted); font: inherit; font-size: var(--text-control); cursor: pointer; }
.download-details-toggle:hover { background: var(--hover); }
.download-metadata { padding: 8px 24px 20px; }
.download-metadata dl { display: grid; grid-template-columns: 100px minmax(0, 1fr); gap: 14px; font-size: var(--text-control); }
.download-metadata dt { color: var(--text-muted); }
.download-metadata dd { margin: 0; overflow-wrap: anywhere; }
.downloads-boundary { margin: 22px 0 0; line-height: 1.7; }
:global(.noah-modal-overlay:has(.server-plugin-install-review)) { background: var(--backdrop-overlay); backdrop-filter: blur(8px); }
:global(.noah-modal-dialog:has(.server-plugin-install-review)) { background: var(--surface); }
.job-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; overflow-wrap: anywhere; }
progress { width: 100%; height: 4px; display: block; accent-color: var(--accent); border: 0; border-radius: 4px; overflow: hidden; }
progress::-webkit-progress-bar { background: var(--border); }
progress::-webkit-progress-value { background: var(--accent); border-radius: 4px; transition: width 200ms ease; }
@media (max-width: 760px) { .download-workspace { grid-template-columns: minmax(0, 1fr); } .download-summary { padding: 26px 20px; } .download-detail-heading { padding: 20px 20px 0; gap: 12px; } .download-detail-heading h2 { font-size: 20px; } .download-actions { padding: 18px 20px; } .download-actions .noah-btn { white-space: normal; min-height: 36px; } .download-metadata dl { grid-template-columns: 80px minmax(0, 1fr); } }
@media (prefers-reduced-motion: reduce) { progress::-webkit-progress-value { transition: none; } }
</style>
