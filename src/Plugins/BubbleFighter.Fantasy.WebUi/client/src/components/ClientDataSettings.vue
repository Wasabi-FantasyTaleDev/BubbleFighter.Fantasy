<script setup>
import { computed, onMounted, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import SetupClients from './SetupClients.vue'
import SectionCard from './SectionCard.vue'
import { api, jsonRequest } from '../api/client'
import { useServerStore } from '../stores/server'
import { useI18n } from '../i18n'

const server = useServerStore()
const { t, te } = useI18n()
const saved = ref(null)
const clients = ref([])
const importingClients = ref(false)
const valid = computed(() => !importingClients.value && clients.value.length > 0 && clients.value.some(c => !c.deferClientDataImport) && clients.value.every(c => c.deferClientDataImport || c.clientDataPath.trim()))
const busy = ref(false)
const error = ref('')
onMounted(async () => {
  try {
    saved.value = await api('setup/configuration')
    clients.value = saved.value.settings.clients || [{ profile: saved.value.settings.profile,
      clientDataPath: saved.value.settings.clientDataPath, deferClientDataImport: saved.value.settings.deferClientDataImport }]
  } catch { error.value = t('setup.connection_failed') }
})
async function apply() {
  if (!saved.value || busy.value || !valid.value) return
  busy.value = true; error.value = ''
  try {
    await api('setup/configuration', jsonRequest('PUT', {
      settings: { ...saved.value.settings, clients: clients.value.map(c => ({ ...c, clientDataPath: c.clientDataPath.trim(), deferClientDataImport: !c.clientDataPath.trim() })), profile: 'management', clientDataPath: '', deferClientDataImport: false },
      retainDatabasePassword: saved.value.hasDatabasePassword
    }))
    await server.refreshSetup()
    server.setupOpen = true
  } catch (e) { error.value = t(te('setup.' + e.message) ? 'setup.' + e.message : 'setup.operation_failed') }
  finally { busy.value = false }
}
</script>

<template>
  <SectionCard :title="t('setup.importClient')">
    <form class="setup-form" @submit.prevent="apply">
      <p class="setup-hint">{{ t('setup.importDescription') }}</p>
      <SetupClients v-model="clients" :profiles="server.setupStatus?.profiles || []" @busy="importingClients = $event" />
      <p v-if="error" role="alert" class="setup-error">{{ error }}</p>
      <NoahButton html-type="submit" variant="primary" :loading="busy" :disabled="!saved || !valid || busy">{{ t('setup.importNow') }}</NoahButton>
    </form>
  </SectionCard>
</template>
