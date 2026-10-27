<script setup>
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import { useWebUiStore } from '../stores/webui'
import { useResource } from '../composables/useResource'
import { useI18n } from '../i18n'

const webui = useWebUiStore()
const router = useRouter()
const { t } = useI18n()
const open = ref(false)
const announced = new Set()
const updateKey = computed(() => webui.hasUpdate ? `${webui.plugin.version}:${webui.availableVersion}` : '')
useResource(() => webui.refresh(), { interval: 60000 })
watch(updateKey, key => {
  if (!key) { open.value = false; return }
  if (!announced.has(key)) { announced.add(key); open.value = true }
})
function showStore() {
  open.value = false
  router.push({ path: '/store', query: { plugin: webui.plugin.plugin } })
}
</script>

<template>
  <NoahModal v-model="open" :title="t('settings.webui.updateTitle')" :close-label="t('common.close')" width="440px">
    <p>{{ t('settings.webui.updateMessage') }}</p>
    <p class="webui-update-versions">{{ webui.plugin?.version?.split('+')[0] }} → {{ webui.availableVersion.split('+')[0] }}</p>
    <template #footer><NoahButton variant="glass" @click="open = false">{{ t('common.close') }}</NoahButton><NoahButton variant="primary" @click="showStore">{{ t('plugins.openStore') }}</NoahButton></template>
  </NoahModal>
</template>

<style scoped>
.webui-update-versions { margin-top: 16px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
</style>
