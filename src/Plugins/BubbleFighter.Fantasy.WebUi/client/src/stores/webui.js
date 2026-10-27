import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '../api/client'
import { comparePluginVersions } from '../utils/pluginVersion'

/** Runtime identity and catalog updates for the WebUI plugin, shared by settings and the notice. */
export const useWebUiStore = defineStore('webui-version', () => {
  const plugin = ref(null)
  const availableVersion = ref('')
  const error = ref(null)
  const catalogError = ref(null)
  const loading = ref(false)
  const hasUpdate = computed(() => comparePluginVersions(availableVersion.value, plugin.value?.version) === 1)
  let pending
  async function refresh() {
    if (pending) return pending
    pending = (async () => {
      loading.value = true
      try {
        const rows = await api('plugins')
        plugin.value = rows.find(row => row.plugin === 'webui' && row.state === 'Active') || rows.find(row => row.plugin === 'webui') || null
        error.value = null
      } catch (e) { error.value = e; return }
      try {
        const catalog = await api('distribution/v1/catalog')
        const versions = (catalog.entries || []).filter(entry => entry.identity.id === plugin.value?.plugin && entry.identity.target === 'server' && entry.identity.kind === 'plugin')
          .flatMap(entry => entry.downloads.map(offer => offer.package.version))
        availableVersion.value = versions.reduce((best, version) => comparePluginVersions(version, best) === 1 ? version : best, plugin.value?.version || '')
        catalogError.value = catalog.failures?.length ? catalog.failures.map(failure => `${failure.sourceId}: ${failure.code}`).join('; ') : null
      } catch (e) { catalogError.value = e.message }
      finally { loading.value = false }
    })().finally(() => { loading.value = false; pending = null })
    return pending
  }
  return { plugin, availableVersion, hasUpdate, error, catalogError, loading, refresh }
})
