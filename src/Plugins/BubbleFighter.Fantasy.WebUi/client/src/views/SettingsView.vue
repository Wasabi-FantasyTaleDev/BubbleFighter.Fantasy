<script setup>
import { computed, onMounted, ref } from 'vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahSwitch from '../noah/components/ui/NoahSwitch.vue'
import { useNoahToast } from '../noah/components/ui/useNoahToast'
import SectionCard from '../components/SectionCard.vue'
import ClientDataSettings from '../components/ClientDataSettings.vue'
import { useServerStore } from '../stores/server'
import { api } from '../api/client'
import { useI18n } from '../i18n'
import { useUiStore } from '../stores/ui'
import { LANGS, LANGUAGE_NAMES } from '../langs'

const { t } = useI18n()
const ui = useUiStore()
const server = useServerStore()
const toast = useNoahToast()

// null means the server does not offer the setting (yet): the switch stays disabled.
const crossVersion = ref(null)
const loadError = ref(null)
const saving = ref(false)

onMounted(async () => {
  try {
    const settings = await api('settings')
    crossVersion.value = typeof settings.crossVersionPlay === 'boolean' ? settings.crossVersionPlay : null
  } catch (e) {
    loadError.value = e
  }
})

const pendingNote = computed(() => {
  if (crossVersion.value !== null) return ''
  if (!loadError.value) return t('settings.cross.pending')
  return loadError.value.status === 404 ? t('common.unavailable') : loadError.value.message
})

// The server is the source of truth: the switch is controlled and only moves after the PUT succeeds.
async function changeCrossVersion(next) {
  saving.value = true
  try {
    const saved = await api('settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ crossVersionPlay: next })
    })
    crossVersion.value = saved.crossVersionPlay
    toast.ok(t('settings.saved'), t('common.saved'))
  } catch (e) {
    toast.err(t('settings.failed', { reason: e.message }), t('common.error'))
  } finally {
    saving.value = false
  }
}

const languageOptions = computed(() => [
  { value: 'auto', label: t('lang.auto') },
  ...LANGS.map((code) => ({ value: code, label: LANGUAGE_NAMES[code], lang: code }))
])
const themeOptions = computed(() => [
  { value: 'system', label: t('theme.system') },
  { value: 'light', label: t('theme.light') },
  { value: 'dark', label: t('theme.dark') }
])
</script>

<template>
  <ClientDataSettings v-if="server.installation && server.setupStatus?.clientDataDeferred" />
  <SectionCard :title="t('settings.gameplay')">
    <div class="setting">
      <div class="setting-text">
        <div class="setting-name">{{ t('settings.cross.name') }}</div>
        <div class="setting-desc">{{ t('settings.cross.desc') }}</div>
        <div v-if="pendingNote" class="setting-desc setting-note">{{ pendingNote }}</div>
      </div>
      <div class="setting-control">
        <NoahSwitch
          :model-value="crossVersion === true"
          :disabled="crossVersion === null || saving"
          :aria-label="t('settings.cross.name')"
          @change="changeCrossVersion"
        />
      </div>
    </div>
  </SectionCard>
  <SectionCard :title="t('settings.interface')">
    <div class="setting">
      <div class="setting-text">
        <div class="setting-name">{{ t('settings.lang.name') }}</div>
        <div class="setting-desc">{{ t('settings.lang.desc') }}</div>
      </div>
      <div class="setting-control">
        <NoahSelect :model-value="ui.langPref" :options="languageOptions" @change="ui.setLang" />
      </div>
    </div>
    <div class="setting">
      <div class="setting-text">
        <div class="setting-name">{{ t('settings.theme.name') }}</div>
        <div class="setting-desc">{{ t('settings.theme.desc') }}</div>
      </div>
      <div class="setting-control">
        <NoahSelect :model-value="ui.themeChoice" :options="themeOptions" @change="ui.setTheme" />
      </div>
    </div>
  </SectionCard>
</template>
