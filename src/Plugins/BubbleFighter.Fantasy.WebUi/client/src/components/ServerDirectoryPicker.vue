<script setup>
import { nextTick, onBeforeUnmount, ref } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import { api, jsonRequest } from '../api/client'
import { useI18n } from '../i18n'

const props = defineProps({ modelValue: { type: String, default: '' } })
const emit = defineEmits(['update:modelValue'])
const { t } = useI18n()
const dialog = ref(null)
const location = ref('')
const page = ref(null)
const busy = ref(false)
const error = ref('')
let request
async function browse(path) {
  request?.abort()
  const current = new AbortController()
  request = current; busy.value = true; error.value = ''; page.value = null
  try {
    const result = await api('setup/directories', { ...jsonRequest('POST', { path }), signal: current.signal })
    if (current.signal.aborted) return
    page.value = result; location.value = result.path || ''
  } catch (e) {
    if (!current.signal.aborted) error.value = t(e.message === 'directory_unavailable' ? 'setup.directory_unavailable' : 'setup.connection_failed')
  } finally { if (request === current) busy.value = false }
}
async function open() {
  location.value = props.modelValue
  await nextTick()
  dialog.value.showModal()
  await browse(location.value)
}
function close() { request?.abort(); dialog.value.close() }
function choose() { if (page.value?.path) { emit('update:modelValue', page.value.path); close() } }
onBeforeUnmount(() => request?.abort())
</script>

<template>
  <NoahButton @click="open">{{ t('setup.chooseFolder') }}</NoahButton>
  <Teleport to="body">
    <dialog ref="dialog" class="setup-directory-dialog" :aria-label="t('setup.chooseFolder')" @cancel="request?.abort()" @click="e => { if (e.target === dialog) close() }">
      <header><h2>{{ t('setup.chooseFolder') }}</h2><p>{{ t('setup.serverFolderHint') }}</p></header>
      <form class="setup-directory-location" @submit.prevent="browse(location)">
        <NoahInput v-model="location" :aria-label="t('setup.clientData')" :placeholder="t('setup.absolutePath')" maxlength="2048" />
        <NoahButton html-type="submit" :disabled="busy">{{ t('setup.openFolder') }}</NoahButton>
      </form>
      <nav class="setup-directory-nav" :aria-label="t('setup.chooseFolder')">
        <NoahButton variant="ghost" :disabled="busy" @click="browse(null)">{{ t('setup.folderRoots') }}</NoahButton>
        <NoahButton variant="ghost" :disabled="busy || !page?.parent" @click="browse(page.parent)">{{ t('setup.parentFolder') }}</NoahButton>
      </nav>
      <div class="setup-directory-list" :aria-busy="busy">
        <p v-if="error" role="alert" class="setup-error">{{ error }}</p>
        <p v-else-if="busy" role="status">{{ t('setup.loadingFolders') }}</p>
        <template v-else>
          <button v-for="folder in page?.directories" :key="folder.path" type="button" @click="browse(folder.path)">
            <svg aria-hidden="true" viewBox="0 0 24 24"><path d="M3 6a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1Z" /></svg>
            <span>{{ folder.name }}</span>
          </button>
          <p v-if="!page?.directories.length">{{ t('setup.noFolders') }}</p>
          <p v-if="page?.truncated" role="status">{{ t('setup.foldersTruncated') }}</p>
        </template>
      </div>
      <footer><NoahButton variant="ghost" @click="close">{{ t('setup.cancelFolder') }}</NoahButton><NoahButton variant="primary" :disabled="busy || !page?.path" @click="choose">{{ t('setup.useFolder') }}</NoahButton></footer>
    </dialog>
  </Teleport>
</template>

<style>
.setup-directory-dialog { width: min(600px, calc(100vw - 36px)); max-height: calc(100dvh - 40px); box-sizing: border-box; padding: 28px; color: var(--text-primary, #202124); border: 1px solid #ffffffc0; border-radius: 24px; background: linear-gradient(135deg, #fafcffff, #f1f3f8ff); box-shadow: 0 24px 80px #14233822; font: inherit; overflow: auto; }
.setup-directory-dialog::backdrop { background: #19233738; backdrop-filter: blur(8px); }
.setup-directory-dialog header h2 { margin: 0 0 8px; font-size: 23px; font-weight: 500; }
.setup-directory-dialog p { font-size: 13px; line-height: 1.7; }
.setup-directory-location { display: flex; align-items: center; gap: 10px; margin: 20px 0 10px; }
.setup-directory-location > :first-child { flex: 1; min-width: 0; }
.setup-directory-nav { display: flex; gap: 8px; }
.setup-directory-list { height: min(300px, 40dvh); overflow: auto; margin: 12px 0; }
.setup-directory-list > button { display: flex; align-items: center; gap: 12px; padding: 11px 12px; width: 100%; border: 0; border-radius: 10px; background: transparent; color: inherit; text-align: left; font: inherit; font-size: 13px; cursor: pointer; }
.setup-directory-list > button:hover { background: #151b2308; }
.setup-directory-list > button span { overflow-wrap: anywhere; }
.setup-directory-list svg { flex-shrink: 0; width: 20px; height: 20px; fill: #e5e9ef; stroke: #75808e; stroke-width: 1.3; }
.setup-directory-dialog footer { display: flex; justify-content: flex-end; gap: 10px; }
</style>
