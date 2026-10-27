<script setup>
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import SectionCard from '../components/SectionCard.vue'
import ErrorCard from '../components/ErrorCard.vue'
import DistributionJobs from '../components/DistributionJobs.vue'
import ClientPluginPolicyEditor from '../components/ClientPluginPolicyEditor.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import { api, jsonRequest } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { useServerStore } from '../stores/server'

const { t } = useI18n()
const server = useServerStore()
const { data, error, loading, reload } = useResource(() => api('distribution/v1/sources'))
const selectedSource = ref('')
const choices = ref({})
const assets = ref({})
const admitting = ref(false)
const admissionError = ref(null)
const jobs = ref(null)
const catalog = useResource(() => api('distribution/v1/catalog' + (selectedSource.value ? '?sourceId=' + encodeURIComponent(selectedSource.value) : '')))
const sourceOptions = computed(() => [{ value: '', label: t('store.all') }, ...(data.value?.sources || []).map(source => ({ value: source.id, label: source.name }))])
const cardKey = entry => JSON.stringify(entry.identity)
const selectedOffer = entry => entry.downloads.find(choice => choice.sourceId === choices.value[cardKey(entry)]) || entry.downloads[0]
const downloadOptions = entry => entry.downloads.map(choice => ({ value: choice.sourceId, label: `${choice.sourceName} · ${choice.package.version}` }))
const assetOptions = offer => [{ value: '', label: t('store.chooseAsset') }, ...offer.package.repositoryRelease.assets.map(asset => ({ value: String(asset.id), label: `${asset.name} · ${asset.size.toLocaleString()} B` }))]
const selectedAsset = offer => offer.package.repositoryRelease?.assets.find(asset => String(asset.id) === assets.value[offer.selectionFingerprint])
const downloadable = offer => offer.package.identity.target === 'server' && (!offer.package.repositoryRelease || !!selectedAsset(offer)?.sha256)
watch(selectedSource, () => catalog.reload())
const repository = ref('')
const tag = ref('')
const release = ref(null)
const releaseError = ref(null)
const resolving = ref(false)
const editing = ref(false)
const draft = ref([])
const editRevision = ref(0)
const saveError = ref(null)
const saving = ref(false)
const kindOptions = ['http', 'alist', 'github'].map(kind => ({ value: kind, label: kind === 'alist' ? 'AList' : kind === 'github' ? 'GitHub' : 'HTTP' }))
let request
onBeforeUnmount(() => request?.abort())

async function download(entry) {
  if (admitting.value) return
  const offer = selectedOffer(entry)
  if (!downloadable(offer)) return
  const selection = {
    sourceRevision: catalog.data.value.sourceRevision,
    sourceId: offer.sourceId,
    package: entry.identity,
    selectionFingerprint: offer.selectionFingerprint,
    repositoryAssetId: selectedAsset(offer)?.id ?? null
  }
  admitting.value = true
  admissionError.value = null
  let plan
  try {
    plan = await api('distribution/v1/plans', jsonRequest('POST', selection))
    await api('distribution/v1/jobs', jsonRequest('POST', { planId: plan.id }))
    await jobs.value?.reload()
  } catch (e) {
    admissionError.value = e
    // A lost response can hide an accepted job; leave its plan available to the worker.
    if (plan && e.status >= 400 && e.status < 500) {
      try { await api('distribution/v1/plans/' + encodeURIComponent(plan.id), { method: 'DELETE' }) }
      catch { /* The plan also expires on the server. */ }
    }
  } finally { admitting.value = false }
}

function editSources() {
  draft.value = (data.value?.sources || []).map(source => ({ ...source }))
  editRevision.value = data.value?.revision ?? 0
  saveError.value = null
  editing.value = true
}
function moveSource(index, delta) {
  const destination = index + delta
  if (destination < 0 || destination >= draft.value.length) return
  ;[draft.value[index], draft.value[destination]] = [draft.value[destination], draft.value[index]]
}
async function saveSources() {
  saving.value = true
  saveError.value = null
  try {
    await api('distribution/v1/sources', jsonRequest('PUT', { revision: editRevision.value, sources: draft.value }))
    editing.value = false
    selectedSource.value = ''
    choices.value = {}
    await reload()
    await catalog.reload()
  } catch (e) { saveError.value = e }
  finally { saving.value = false }
}

async function inspect() {
  request?.abort()
  const current = new AbortController()
  request = current
  resolving.value = true
  release.value = null
  releaseError.value = null
  try {
    const result = await api('distribution/v1/repository-release', {
      ...jsonRequest('POST', { repositoryUrl: repository.value.trim(), tag: tag.value.trim() || null }),
      signal: current.signal
    })
    if (!current.signal.aborted) release.value = result
  } catch (e) {
    if (!current.signal.aborted) releaseError.value = e
  } finally {
    if (request === current) resolving.value = false
  }
}
</script>

<template>
  <ClientPluginPolicyEditor v-if="server.installation" :catalog="catalog.data.value?.entries || []" />
  <ErrorCard v-if="error" :error="error" @retry="reload" />
  <SectionCard v-else-if="loading && !data"><NoahSkeleton :rows="3" /></SectionCard>
  <template v-else>
    <SectionCard :title="t('nav.store')" :sub="t('store.downloadHint')">
      <div class="store-filter">
        <NoahSelect v-model="selectedSource" :disabled="admitting" :options="sourceOptions" :aria-label="t('store.sources')" />
        <NoahButton :loading="catalog.loading.value" :disabled="admitting" @click="catalog.reload">{{ t('store.refresh') }}</NoahButton>
      </div>
      <p v-if="admissionError" role="alert">{{ admissionError.message }}</p>
      <NoahSkeleton v-if="catalog.loading.value" :rows="3" />
      <ErrorCard v-else-if="catalog.error.value" :error="catalog.error.value" @retry="catalog.reload" />
      <template v-else>
        <p v-for="failure in catalog.data.value?.failures || []" :key="failure.sourceId" role="status" class="text-muted">{{ failure.sourceId }}: {{ failure.code }}</p>
        <div v-for="entry in catalog.data.value?.entries || []" :key="cardKey(entry)" class="store-source">
          <h3>{{ selectedOffer(entry).package.name }}</h3>
          <p class="text-muted mono">{{ entry.identity.id }} · {{ entry.identity.target }} · {{ selectedOffer(entry).package.version }}</p>
          <div class="store-download-source"><span>{{ t('store.downloadSource') }}</span>
            <NoahSelect :model-value="selectedOffer(entry).sourceId" :disabled="admitting" :options="downloadOptions(entry)" :aria-label="t('store.downloadSource')" @change="choices[cardKey(entry)] = $event" />
          </div>
          <div v-if="selectedOffer(entry).package.repositoryRelease" class="store-release">
            <p class="store-notes">{{ selectedOffer(entry).package.repositoryRelease.notes }}</p>
            <div class="store-download-source"><span>{{ t('store.attachment') }}</span>
              <NoahSelect v-model="assets[selectedOffer(entry).selectionFingerprint]" :disabled="admitting" :options="assetOptions(selectedOffer(entry))" :aria-label="t('store.attachment')" />
            </div>
            <p v-if="selectedAsset(selectedOffer(entry)) && !selectedAsset(selectedOffer(entry)).sha256" class="text-muted">{{ t('store.noDigest') }}</p>
            <div v-for="asset in selectedOffer(entry).package.repositoryRelease.assets" :key="asset.id" class="store-asset">
              <strong>{{ asset.name }}</strong><code class="store-url">{{ asset.downloadUrl }}</code>
            </div>
          </div>
          <NoahButton v-if="entry.identity.target === 'server'" variant="primary" :loading="admitting" :disabled="catalog.loading.value || !downloadable(selectedOffer(entry))" @click="download(entry)">{{ t('store.downloadStaging') }}</NoahButton>
          <p v-else class="text-caption text-muted">{{ t('store.clientPreview') }}</p>
        </div>
        <p v-if="!catalog.data.value?.entries?.length" class="text-muted">{{ t('store.noPackages') }}</p>
      </template>
    </SectionCard>
    <DistributionJobs ref="jobs" />
    <SectionCard :title="t('store.sources')" :sub="t('store.sourcesHint')">
      <NoahButton :disabled="admitting" @click="editSources">{{ t('store.manageSources') }}</NoahButton>
      <div v-for="source in data?.sources || []" :key="source.id" class="store-source">
        <div><strong>{{ source.name }}</strong> <NoahTag size="sm">{{ source.kind }}</NoahTag></div>
        <div class="text-muted mono store-url">{{ source.baseUrl }} · {{ source.catalogPath }}</div>
      </div>
      <p v-if="!data?.sources?.length" class="text-muted">{{ t('store.noSources') }}</p>
      <p class="text-caption text-muted">{{ t('store.mirrorTodo') }}</p>
    </SectionCard>
    <SectionCard :title="t('store.repository')" :sub="t('store.repositoryHint')">
      <form class="store-form" @submit.prevent="inspect">
        <label>{{ t('store.repositoryUrl') }}<NoahInput v-model="repository" placeholder="https://github.com/owner/plugin" required /></label>
        <label>{{ t('store.tag') }}<NoahInput v-model="tag" :placeholder="t('store.latest')" /></label>
        <NoahButton html-type="submit" :loading="resolving" :disabled="!repository.trim()">{{ t('store.inspect') }}</NoahButton>
      </form>
      <NoahSkeleton v-if="resolving" :rows="3" />
      <ErrorCard v-else-if="releaseError" :error="releaseError" @retry="inspect" />
      <div v-else-if="release" class="store-release">
        <h3>{{ release.name }} <NoahTag>{{ release.tag }}</NoahTag></h3>
        <p class="text-muted mono">{{ release.repository }} · {{ release.id }}</p>
        <p class="store-notes">{{ release.notes }}</p>
        <a :href="release.pageUrl" target="_blank" rel="noopener noreferrer">{{ t('store.openRelease') }}</a>
        <p class="text-caption text-muted">{{ t('store.previewOnly') }}</p>
        <div v-for="asset in release.assets" :key="asset.id" class="store-asset">
          <strong>{{ asset.name }}</strong><span class="text-muted">{{ asset.size.toLocaleString() }} B</span>
          <code class="store-url">{{ asset.downloadUrl }}</code>
          <span class="text-caption mono">{{ asset.sha256 ? `SHA-256: ${asset.sha256}` : t('store.noDigest') }}</span>
        </div>
        <p v-if="!release.assets.length" class="text-muted">{{ t('store.noAssets') }}</p>
      </div>
    </SectionCard>
  </template>
  <NoahModal v-model="editing" :title="t('store.manageSources')" width="760px">
    <form class="store-editor" @submit.prevent="saveSources">
      <p class="text-muted">{{ t('store.sourceOrderHint') }}</p>
      <fieldset v-for="(source, index) in draft" :key="index" :disabled="saving" class="store-source-editor">
        <label>{{ t('store.sourceId') }}<NoahInput v-model="source.id" required /></label>
        <label>{{ t('store.sourceName') }}<NoahInput v-model="source.name" required /></label>
        <div class="store-download-source"><span>{{ t('store.sourceKind') }}</span><NoahSelect v-model="source.kind" :options="kindOptions" :aria-label="t('store.sourceKind')" /></div>
        <label>{{ t('store.sourceRoot') }}<NoahInput v-model="source.baseUrl" required placeholder="https://example.org/" /></label>
        <label>{{ t('store.catalogPath') }}<NoahInput v-model="source.catalogPath" required placeholder="source.toml" /></label>
        <div class="store-filter">
          <NoahButton :disabled="index === 0" @click="moveSource(index, -1)">{{ t('store.moveUp') }}</NoahButton>
          <NoahButton :disabled="index === draft.length - 1" @click="moveSource(index, 1)">{{ t('store.moveDown') }}</NoahButton>
          <NoahButton variant="danger" @click="draft.splice(index, 1)">{{ t('store.remove') }}</NoahButton>
        </div>
      </fieldset>
      <NoahButton :disabled="saving" @click="draft.push({ id: '', name: '', kind: 'http', baseUrl: '', catalogPath: 'source.toml' })">{{ t('store.addSource') }}</NoahButton>
      <p v-if="saveError" role="alert">{{ saveError.message }}</p>
      <div class="store-filter">
        <NoahButton variant="glass" :disabled="saving" @click="editing = false">{{ t('store.cancel') }}</NoahButton>
        <NoahButton html-type="submit" variant="primary" :loading="saving">{{ t('store.save') }}</NoahButton>
      </div>
    </form>
  </NoahModal>
</template>

<style scoped>
.store-form { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) auto; align-items: end; gap: 12px; margin-bottom: 20px; }
.store-form label { display: grid; gap: 8px; }
.store-filter { display: flex; gap: 12px; align-items: center; margin-bottom: 16px; }
.store-download-source { display: grid; gap: 8px; max-width: 440px; }
.store-editor, .store-source-editor { display: grid; gap: 14px; }
.store-source-editor { min-width: 0; border: 1px solid var(--noah-border-color, #8883); border-radius: 12px; padding: 16px; }
.store-source-editor label { display: grid; gap: 6px; }
.store-source, .store-asset { padding: 14px 0; border-bottom: 1px solid var(--noah-border-color, #8883); }
.store-asset { display: grid; gap: 8px; }
.store-notes { white-space: pre-wrap; overflow-wrap: anywhere; }
.store-url { overflow-wrap: anywhere; }
@media (max-width: 720px) { .store-form { grid-template-columns: minmax(0, 1fr); } }
</style>
