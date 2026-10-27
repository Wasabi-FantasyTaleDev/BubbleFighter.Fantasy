<script setup>
import { computed, ref, onBeforeUnmount } from 'vue'
import SectionCard from './SectionCard.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import { api } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

const { t } = useI18n()
const entries = ref([])
const nextCursor = ref(null)
const busy = ref(false)
const actionError = ref(null)
const verification = ref(null)
const checking = ref(null)
let revision = 0
let expanded = false
onBeforeUnmount(() => revision++)
const terminal = job => ['staged', 'failed', 'cancelled', 'interrupted'].includes(job.phase)
const sorted = computed(() => [...entries.value].sort((a, b) => b.id.localeCompare(a.id)))

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

defineExpose({ reload: resource.reload })
</script>

<template>
  <SectionCard :title="t('store.jobs')" :sub="t('store.stagingHint')">
    <NoahButton :loading="resource.loading.value" :disabled="busy" @click="resource.reload">{{ t('store.refresh') }}</NoahButton>
    <p v-if="actionError || resource.error.value" role="alert">{{ (actionError || resource.error.value).message }}</p>
    <p v-if="verification" role="status"><code>{{ verification.jobId }}</code> · {{ t('store.verifiedNow', { files: verification.files, bytes: verification.bytes.toLocaleString() }) }}</p>
    <NoahSkeleton v-if="resource.loading.value && !entries.length" :rows="3" />
    <article v-for="job in sorted" :key="job.id" class="download-job" :aria-label="job.package.id">
      <div class="job-heading"><strong>{{ job.package.id }} · {{ job.version }}</strong><NoahTag>{{ t('store.job.' + job.phase) }}</NoahTag></div>
      <p class="text-caption text-muted">{{ t('store.downloadSource') }}: {{ job.sourceId }}</p>
      <progress v-if="!terminal(job)" :value="job.totalBytes ? job.bytesReceived : undefined" :max="job.totalBytes || 1" :aria-label="t('store.progress')" />
      <p class="text-caption mono">{{ job.bytesReceived.toLocaleString() }} / {{ job.totalBytes.toLocaleString() }} B · {{ t('store.filesProgress', { done: job.completedFiles, total: job.fileCount }) }}</p>
      <p v-if="job.errorCode" role="status" class="text-caption mono">{{ job.errorCode }}</p>
      <NoahButton v-if="job.phase === 'staged'" :loading="checking === job.id" :disabled="busy" @click="verify(job)">{{ t('store.verifyStaging') }}</NoahButton>
      <NoahButton v-if="terminal(job)" :disabled="busy" @click="change(job, true)">{{ t('store.deleteStaging') }}</NoahButton>
      <NoahButton v-else :disabled="busy || job.phase === 'cleaning'" @click="change(job, false)">{{ t('store.cancelDownload') }}</NoahButton>
    </article>
    <p v-if="!entries.length && !resource.loading.value" class="text-muted">{{ t('store.noJobs') }}</p>
    <NoahButton v-if="nextCursor" :loading="busy" @click="older">{{ t('store.olderJobs') }}</NoahButton>
  </SectionCard>
</template>

<style scoped>
.download-job { padding: 18px 0; border-bottom: 1px solid var(--noah-border-color, #8883); }
.job-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; overflow-wrap: anywhere; }
progress { width: 100%; accent-color: var(--noah-color-primary, #367bf5); }
</style>
