<script setup>
import { computed, onBeforeUnmount, ref, shallowRef } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import { api } from '../api/client'
import { useI18n } from '../i18n'
import { useServerStore } from '../stores/server'

const props = defineProps({ profile: { type: String, required: true }, modelValue: { type: String, default: '' } })
const emit = defineEmits(['update:modelValue', 'busy'])
const { t, te } = useI18n()
const server = useServerStore()
const folder = ref(null)
const archive = ref(null)
const busy = ref(false)
const error = ref('')
const selection = ref('')
const pending = shallowRef(null)
const exceedsLimit = computed(() => {
  const limits = server.setupStatus?.importLimits
  return !!pending.value && !!limits && (pending.value.bytes > limits.uploadBytes || pending.value.files.length > limits.fileCount)
})
const limitMessage = computed(() => {
  const limits = server.setupStatus?.importLimits
  if (!pending.value || !limits) return ''
  if (pending.value.bytes > limits.uploadBytes) return t('setup.importSizeLimit', { size: size(limits.uploadBytes) })
  if (pending.value.files.length > limits.fileCount) return t('setup.importFileLimit', { count: limits.fileCount.toLocaleString() })
  return ''
})
let controller

function select(event, kind) {
  const files = Array.from(event.target.files || [])
  if (!files.length || busy.value) return
  error.value = ''
  const selected = kind === 'zip' ? files : files.filter(file => {
    const parts = file.webkitRelativePath.split('/')
    const marker = parts.findIndex(part => /^(data|dataraw)$/i.test(part))
    if (marker >= 0 && parts.slice(0, marker).some(part => part !== props.profile && server.setupStatus?.profiles?.some(profile => profile.id === part))) return false
    return parts.some(part => /^(data|dataraw)$/i.test(part)) || /\.(pkn|bfz)$/i.test(file.name)
  })
  if (!selected.length) { error.value = t('setup.client_data_invalid'); event.target.value = ''; return }
  pending.value = {
    kind, files: selected, bytes: selected.reduce((sum, file) => sum + file.size, 0),
    name: kind === 'zip' ? files[0].name : files[0].webkitRelativePath.split('/')[0]
  }
  event.target.value = ''
}
function size(bytes) {
  if (bytes >= 1073741824) return `${(bytes / 1073741824).toFixed(2)} GiB`
  if (bytes >= 1048576) return `${(bytes / 1048576).toFixed(1)} MiB`
  return `${Math.max(1, Math.round(bytes / 1024))} KiB`
}
async function submit() {
  if (!pending.value || busy.value || exceedsLimit.value) return
  const { files, kind, name } = pending.value
  controller = new AbortController()
  busy.value = true; emit('busy', true)
  try {
    const form = new FormData()
    for (const file of files) form.append('files', file, file.webkitRelativePath || file.name)
    const result = await api(`setup/client-data?profile=${encodeURIComponent(props.profile)}&kind=${kind}`, {
      method: 'POST', body: form, signal: controller.signal
    })
    if (!controller.signal.aborted) { selection.value = name; pending.value = null; emit('update:modelValue', result.path) }
  } catch (reason) {
    if (!controller.signal.aborted) error.value = reason.message?.startsWith('client_data_missing:')
      ? t('setup.client_data_missing', { files: reason.message.slice('client_data_missing:'.length) })
      : t(te('setup.' + reason.message) ? 'setup.' + reason.message : 'setup.importFailed')
  } finally {
    busy.value = false; emit('busy', false)
  }
}
function clear() { selection.value = ''; pending.value = null; error.value = ''; emit('update:modelValue', '') }
onBeforeUnmount(() => { controller?.abort(); emit('busy', false) })
</script>

<template>
  <div class="client-data-picker" :aria-busy="busy">
    <input ref="folder" tabindex="-1" aria-hidden="true" class="client-data-picker-input" type="file" webkitdirectory multiple @change="select($event, 'folder')" />
    <input ref="archive" tabindex="-1" aria-hidden="true" class="client-data-picker-input" type="file" accept=".zip,application/zip" @change="select($event, 'zip')" />
    <div class="client-data-picker-actions">
      <NoahButton :disabled="busy" @click="folder?.click()">{{ t('setup.chooseLocalFolder') }}</NoahButton>
      <NoahButton :disabled="busy" @click="archive?.click()">{{ t('setup.chooseDataPackage') }}</NoahButton>
      <NoahButton v-if="busy" variant="ghost" @click="controller?.abort()">{{ t('setup.client_data_cancel') }}</NoahButton>
      <NoahButton v-else-if="modelValue" variant="ghost" @click="clear">{{ t('setup.clearClientData') }}</NoahButton>
    </div>
    <div v-if="pending && !busy" class="client-data-pending">
      <p>{{ pending.name }}</p>
      <p class="setup-hint">{{ t('setup.importSelectionSummary', { count: pending.files.length.toLocaleString(), size: size(pending.bytes) }) }}</p>
      <p v-if="exceedsLimit" class="setup-error" role="alert">{{ limitMessage }}</p>
      <div class="client-data-picker-actions">
        <NoahButton :disabled="exceedsLimit" @click="submit">{{ t('setup.startDataImport') }}</NoahButton>
        <NoahButton variant="ghost" @click="pending = null">{{ t('setup.cancelFolder') }}</NoahButton>
      </div>
    </div>
    <p v-if="busy" class="setup-hint" role="status">{{ t('setup.importingClientData') }}</p>
    <p v-else-if="modelValue" class="setup-hint client-data-selection" role="status">{{ selection ? t('setup.importedClientData', { name: selection }) : modelValue }}</p>
    <p v-else-if="!pending" class="setup-hint">{{ t('setup.emptyPathDeferred') }}</p>
    <p v-if="error" class="setup-error" role="alert">{{ error }}</p>
  </div>
</template>

<style scoped>
.client-data-picker { min-width: 0; }
.client-data-picker-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.client-data-picker-input { display: none; }
.client-data-picker p { margin: 9px 0 0; }
.client-data-selection { font: inherit; font-size: 12px; overflow-wrap: anywhere; }
.client-data-pending { margin-top: 14px; padding: 12px; border-radius: 12px; background: #ffffff90; font-size: 13px; overflow-wrap: anywhere; }
.client-data-pending > p:first-child { margin-top: 0; }
.client-data-pending .client-data-picker-actions { margin-top: 12px; }
</style>
