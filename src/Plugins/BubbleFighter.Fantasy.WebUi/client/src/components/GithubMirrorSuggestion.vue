<script setup>
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahIcon from '../noah/components/icons/NoahIcon.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahSwitch from '../noah/components/ui/NoahSwitch.vue'
import { api, jsonRequest } from '../api/client'
import { useI18n } from '../i18n'

// The server checks its own address once; when it is in mainland China, the first administrator to open the WebUI is
// asked once whether the store should download GitHub packages through mirrors. Nothing is enabled until confirmed.
const { t } = useI18n()
const open = ref(false)
const enabled = ref(true)
const saving = ref(false)
const error = ref('')
let disposed = false
let timer

async function refresh() {
  clearTimeout(timer)
  try {
    const state = await api('distribution/v1/github-mirror')
    if (disposed) return
    if (state.suggestionPending && !open.value) { enabled.value = true; open.value = true }
    else if (state.detectionPending) timer = setTimeout(refresh, 1000)
  } catch { /* A store without the mirror feature simply never suggests it. */ }
}
// Shown once means shown: a reload or a second administrator does not see it again.
watch(open, visible => { if (visible) api('distribution/v1/github-mirror/suggestion-shown', jsonRequest('POST', {})).catch(() => {}) })
async function confirm() {
  saving.value = true; error.value = ''
  try { await api('distribution/v1/github-mirror', jsonRequest('PUT', { enabled: enabled.value })); open.value = false }
  catch (e) { error.value = e.message }
  finally { saving.value = false }
}
onMounted(refresh)
onBeforeUnmount(() => { disposed = true; clearTimeout(timer) })
</script>

<template>
  <NoahModal :model-value="open" :title="t('store.mirror.title')" width="480px" :show-close="false" :close-on-click-overlay="false">
    <p class="mirror-suggestion-copy">{{ t('store.mirror.suggestion') }}</p>
    <div class="mirror-suggestion-card">
      <NoahIcon name="github" :size="22" /><strong>{{ t('store.mirror.title') }}</strong>
      <NoahSwitch v-model="enabled" :disabled="saving" :aria-label="t('store.mirror.title')" />
    </div>
    <p v-if="error" class="mirror-suggestion-error" role="alert">{{ error }}</p>
    <template #footer><NoahButton variant="primary" :loading="saving" @click="confirm">{{ t('store.mirror.confirm') }}</NoahButton></template>
  </NoahModal>
</template>

<style scoped>
.mirror-suggestion-copy { margin: 0 0 16px; line-height: 1.7; font-size: var(--text-control); }
.mirror-suggestion-card { display: flex; align-items: center; gap: 14px; padding: 16px 18px; border: 1px solid var(--border-soft); border-radius: 12px; background: var(--surface-raised); }
.mirror-suggestion-card strong { flex: 1; font-weight: 500; font-size: var(--text-control); }
.mirror-suggestion-error { margin: 12px 0 0; color: var(--danger, #c64343); font-size: var(--text-caption); overflow-wrap: anywhere; }
</style>
