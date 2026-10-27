<script setup>
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { STORE_PAGES } from '../router'
import ErrorCard from '../components/ErrorCard.vue'
import DistributionJobs from '../components/DistributionJobs.vue'
import DistributionPlanReview from '../components/DistributionPlanReview.vue'
import StoreCatalog from '../components/StoreCatalog.vue'
import StoreTabs from '../components/StoreTabs.vue'
import StoreSourceManager from '../components/StoreSourceManager.vue'
import ClientPluginPolicyDialog from '../components/ClientPluginPolicyDialog.vue'
import ServerPluginConfigurationDialog from '../components/ServerPluginConfigurationDialog.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import { api, jsonRequest } from '../api/client'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'
import { useServerStore } from '../stores/server'

const { t } = useI18n()
const server = useServerStore()
const route = useRoute()
const router = useRouter()
const { data, error, loading, reload } = useResource(() => api('distribution/v1/sources'))
const selectedSource = ref('')
const view = computed({
  get: () => STORE_PAGES.some(page => page.id === route.query.view && (!page.installation || server.installation))
    ? route.query.view : 'browse',
  set: value => router.push({ path: '/store', query: { ...route.query, view: value === 'browse' ? undefined : value } })
})
const repositoryOpen = ref(false)
const choices = ref({})
const assets = ref({})
const admitting = ref(false)
const admissionError = ref(null)
const jobs = ref(null)
const installations = useResource(() => api('distribution/v1/server-plugins'), { interval: 5000 })
const configurationSelection = ref(null)
const installationBusy = ref(null)
const installationError = ref(null)
const pendingPlan = ref(null)
const confirming = ref(false)
const planError = ref(null)
const catalog = useResource(() => api('distribution/v1/catalog' + (selectedSource.value ? '?sourceId=' + encodeURIComponent(selectedSource.value) : '')))
const sourceOptions = computed(() => [{ value: '', label: t('store.all') }, ...(data.value?.sources || []).map(source => ({ value: source.id, label: source.name }))])
const cardKey = entry => JSON.stringify(entry.identity)
const selectedOffer = entry => entry.downloads.find(choice => choice.sourceId === choices.value[cardKey(entry)]) || entry.downloads[0]
const selectedAsset = offer => offer.package.repositoryRelease?.assets.find(asset => String(asset.id) === assets.value[offer.selectionFingerprint])
const downloadable = offer => offer.package.identity.target === 'server' && !pendingPlan.value && (!offer.package.repositoryRelease || !!selectedAsset(offer)?.sha256)
watch(selectedSource, () => catalog.reload())
const repository = ref('')
const tag = ref('')
const release = ref(null)
const releaseError = ref(null)
const resolving = ref(false)
const editing = ref(false)
const policySelection = ref(null)
const views = computed(() => STORE_PAGES.filter(page => !page.installation || server.installation)
  .map(page => ({ id: page.id, label: t(page.title) })))
let request
let disposed = false
onBeforeUnmount(() => {
  disposed = true
  request?.abort()
  if (pendingPlan.value && !confirming.value)
    api('distribution/v1/plans/' + encodeURIComponent(pendingPlan.value.selection.id), { method: 'DELETE' }).catch(() => {})
})

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
    pendingPlan.value = await api('distribution/v1/plans/' + encodeURIComponent(plan.id) + '/files', { method: 'POST' })
    planError.value = null
  } catch (e) {
    admissionError.value = e
    // No download has been admitted while preparing this review.
    if (plan) {
      try { await api('distribution/v1/plans/' + encodeURIComponent(plan.id), { method: 'DELETE' }) }
      catch { /* The plan also expires on the server. */ }
    }
  } finally {
    admitting.value = false
    if (disposed && plan) {
      pendingPlan.value = null
      try { await api('distribution/v1/plans/' + encodeURIComponent(plan.id), { method: 'DELETE' }) }
      catch { /* An unsubmitted plan also expires on the server. */ }
    }
  }
}

async function cancelPlan() {
  if (confirming.value) return
  const plan = pendingPlan.value
  pendingPlan.value = null
  planError.value = null
  if (!plan) return
  try { await api('distribution/v1/plans/' + encodeURIComponent(plan.selection.id), { method: 'DELETE' }) }
  catch (e) { admissionError.value = e }
}

async function confirmDownload() {
  if (!pendingPlan.value || confirming.value) return
  confirming.value = true
  planError.value = null
  try {
    await api('distribution/v1/jobs', jsonRequest('POST', { planId: pendingPlan.value.selection.id }))
    pendingPlan.value = null
    view.value = 'downloads'
    await jobs.value?.reload()
  } catch (e) { planError.value = e }
  finally { confirming.value = false }
}

async function sourcesSaved() {
  selectedSource.value = ''
  choices.value = {}
  await reload()
  await catalog.reload()
}

async function setInstallationEnabled(installation, enabled) {
  if (installationBusy.value) return
  installationBusy.value = installation.installationId
  installationError.value = null
  try {
    await api('distribution/v1/server-plugins/' + encodeURIComponent(installation.installationId) + '/enabled',
      jsonRequest('PUT', { enabled, revision: installation.revision }))
    await installations.reload()
  } catch (e) { installationError.value = e }
  finally { installationBusy.value = null }
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
  <header class="store-heading">
    <StoreTabs v-model="view" class="store-views" :items="views" :label="t('nav.store')" />
    <div class="store-tools">
      <NoahButton variant="glass" icon="refresh" icon-only :loading="catalog.loading.value" :disabled="admitting" :aria-label="t('store.refresh')" :title="t('store.refresh')" @click="catalog.reload" />
      <NoahButton variant="glass" icon="globe" :disabled="admitting || loading || !!error" @click="editing = true">{{ t('store.manageSources') }}</NoahButton>
      <NoahButton variant="glass" icon="code" icon-only :aria-label="t('store.repository')" :title="t('store.repository')" @click="repositoryOpen = true" />
    </div>
  </header>
  <ErrorCard v-if="error" :error="error" @retry="reload" />
  <section v-if="view === 'browse'" class="store-browse">
    <p v-if="admissionError" role="alert">{{ admissionError.message }}</p>
    <ErrorCard v-if="catalog.error.value" :error="catalog.error.value" @retry="catalog.reload" />
    <p v-for="failure in catalog.data.value?.failures || []" :key="failure.sourceId" role="status" class="text-muted">{{ failure.sourceId }}: {{ failure.code }}</p>
    <StoreCatalog :entries="catalog.data.value?.entries || []" :focus-plugin="typeof route.query.plugin === 'string' ? route.query.plugin : ''" :sources="sourceOptions" :source="selectedSource" :loading="loading || catalog.loading.value"
      :busy="admitting" :offer-for="selectedOffer" :can-download="downloadable" :assets="assets" :policy-available="server.installation"
      @source="selectedSource = $event" @download-source="(entry, sourceId) => choices[cardKey(entry)] = sourceId" @asset="(fingerprint, id) => assets[fingerprint] = id"
      @download="download" @policy="(entry, mode) => policySelection = { id: entry.identity.id, name: selectedOffer(entry).package.name, mode }" />
  </section>
  <div v-show="view === 'downloads'">
    <DistributionJobs ref="jobs" :sources="data?.sources || []" :packages="catalog.data.value?.entries || []" @installed="installations.reload(); view = 'installed'" />
  </div>
  <section v-if="view === 'installed'" class="store-installed">
      <div class="store-section-heading"><h2>{{ t('store.serverPlugins') }}</h2><NoahButton variant="glass" icon="refresh" icon-only :title="t('store.refresh')" :aria-label="t('store.refresh')" :loading="installations.loading.value" @click="installations.reload" /></div>
      <p class="text-caption text-muted">{{ t('store.serverPluginsHint') }}</p>
      <p v-if="installationError" role="alert">{{ installationError.message }}</p>
      <NoahSkeleton v-if="installations.loading.value && !installations.data.value" :rows="2" />
      <ErrorCard v-else-if="installations.error.value" :error="installations.error.value" @retry="installations.reload" />
      <template v-else>
        <article v-for="installation in installations.data.value || []" :key="installation.installationId" class="store-source server-plugin-installation">
          <div class="job-heading">
            <strong>{{ installation.package.id }} · {{ installation.version }}</strong>
            <NoahTag size="sm">{{ t('store.installation.' + installation.state) }}</NoahTag>
          </div>
          <p class="text-caption text-muted mono">{{ installation.directory }}</p>
          <p class="text-caption">{{ t('store.pluginObserved', { state: t('store.observed.' + installation.observedState) }) }}</p>
          <p v-if="installation.restartRequired" class="text-caption text-muted">{{ t('store.restartRequired') }}</p>
          <p v-if="installation.failure" class="text-caption" role="alert">{{ t('store.runtimeFailure') }} <code>{{ installation.failure }}</code></p>
          <div class="store-filter">
            <NoahButton :loading="installationBusy === installation.installationId" :disabled="!!installationBusy" :variant="installation.desiredEnabled ? 'glass' : 'primary'" @click="setInstallationEnabled(installation, !installation.desiredEnabled)">
              {{ installation.desiredEnabled ? t('store.disableServerPlugin') : t('store.enableServerPlugin') }}
            </NoahButton>
            <NoahButton variant="glass" :disabled="!!installationBusy" @click="configurationSelection = installation">{{ t('store.configurePlugin') }}</NoahButton>
          </div>
        </article>
        <p v-if="!installations.data.value?.length" class="text-muted">{{ t('store.noServerPlugins') }}</p>
      </template>
  </section>
  <NoahModal v-model="repositoryOpen" :title="t('store.repository')" :close-label="t('common.close')" width="760px">
    <div class="store-repository-content">
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
    </div>
  </NoahModal>
  <StoreSourceManager v-model="editing" :sources="data?.sources || []" :revision="data?.revision || 0" @saved="sourcesSaved" />
  <ClientPluginPolicyDialog :selection="policySelection" @close="policySelection = null" />
  <ServerPluginConfigurationDialog :installation="configurationSelection" @close="configurationSelection = null" @saved="installations.reload" />
  <DistributionPlanReview :plan="pendingPlan" :busy="confirming" :error="planError" @cancel="cancelPlan" @confirm="confirmDownload" />
</template>

<style scoped>
.store-form { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 1fr) auto; align-items: end; gap: 12px; margin-bottom: 20px; }
.store-heading { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; padding-bottom: 18px; border-bottom: 1px solid var(--border); }
.store-tools { display: flex; gap: 8px; align-items: center; }
.store-views { flex: none; }
.store-browse, .store-installed { min-width: 0; }
@media (min-width: 861px) {
  .store-heading { flex: none; }
  .store-browse { display: flex; flex-direction: column; flex: 1; min-height: 0; }
}
.store-section-heading { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
.store-section-heading h2 { font-size: var(--text-subtitle); margin: 0; }
.job-heading { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; overflow-wrap: anywhere; }
:global(.noah-modal-overlay:has(.store-editor, .store-repository-content)) { background: var(--backdrop-overlay); backdrop-filter: blur(8px); }
:global(.noah-modal-dialog:has(.store-editor, .store-repository-content)) { background: var(--surface); }
.store-form label { display: grid; gap: 8px; }
.store-filter { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 16px; }
.store-download-source { display: grid; gap: 8px; max-width: 440px; }
.store-editor, .store-source-editor { display: grid; gap: 14px; }
.store-source-editor { min-width: 0; border: 1px solid var(--border); border-radius: 8px; padding: 16px; }
.store-source-editor label { display: grid; gap: 6px; }
.store-source, .store-asset { padding: 14px 0; border-bottom: 1px solid var(--noah-border-color, #8883); }
.store-asset { display: grid; gap: 8px; }
.store-notes { white-space: pre-wrap; overflow-wrap: anywhere; }
.store-url { overflow-wrap: anywhere; }
@media (max-width: 720px) { .store-form { grid-template-columns: minmax(0, 1fr); } }
</style>
