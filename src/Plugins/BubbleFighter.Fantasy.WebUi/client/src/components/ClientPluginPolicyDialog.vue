<script setup>
import { computed, ref, watch } from 'vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahSkeleton from '../noah/components/ui/NoahSkeleton.vue'
import { conflicts, copyPolicy, loadClientPolicy, ruleId, saveClientPolicy, wireRule } from '../api/clientPolicy'
import { useServerStore } from '../stores/server'
import { useI18n } from '../i18n'
import { useNoahToast } from '../noah/components/ui/useNoahToast'

const props = defineProps({ selection: { type: Object, default: null } })
const emit = defineEmits(['close', 'saved'])
const { t, te } = useI18n()
const server = useServerStore()
const toast = useNoahToast()
const mode = ref('recommended')
const snapshot = ref(null)
const loading = ref(false)
const busy = ref(false)
const failure = ref(null)
let generation = 0
watch(() => props.selection, async selection => {
  const mine = ++generation
  snapshot.value = null
  failure.value = null
  if (!selection) return
  mode.value = selection.mode
  loading.value = true
  try {
    const settings = await loadClientPolicy()
    if (mine === generation) snapshot.value = settings
  } catch (error) { if (mine === generation) failure.value = error }
  finally { if (mine === generation) loading.value = false }
})
const ready = computed(() => !!snapshot.value && !loading.value && !busy.value && server.setupStatus?.phase === 'ready')
const TYPES = { recommended: 'recommend', required: 'require', disabled: 'disable' }
function close() { if (!busy.value) emit('close') }
// Adds one rule for every build; a rule that would conflict is refused here and edited on the Plugins page instead.
async function save() {
  if (!ready.value) return
  busy.value = true
  failure.value = null
  try {
    const policy = copyPolicy(snapshot.value.policy)
    let plugin = policy.plugins.find(entry => entry.id === props.selection.id)
    if (!plugin) policy.plugins.push(plugin = { id: props.selection.id, name: props.selection.name || null, description: null, rules: [] })
    const type = TYPES[mode.value]
    const rule = wireRule({ id: ruleId(plugin.rules, type), type, builds: [], version: '', sources: [] }, snapshot.value.types)
    if (plugin.rules.some(existing => conflicts(snapshot.value.types, rule, existing))) throw new Error(t('store.policy.conflicting'))
    plugin.rules.push(rule)
    await saveClientPolicy(snapshot.value.revision, policy)
    emit('saved')
    emit('close')
    toast.ok(t('common.saved'))
  } catch (error) {
    const key = 'plugins.policy.error.' + error.body?.error
    failure.value = te(key) ? new Error(t(key, { plugin: error.body.plugin || '', rule: error.body.rule || '', other: error.body.other || '' })) : error
  }
  finally { busy.value = false }
}
</script>

<template>
  <NoahModal :model-value="!!selection" :title="t(selection?.mode === 'disabled' ? 'store.policy.disablePlugin' : 'store.policy.addRecommendation')"
    :close-label="t('common.cancel')" :show-close="!busy" :close-on-click-overlay="!busy" width="520px" @update:model-value="!$event && close()">
    <div class="client-policy-dialog">
      <template v-if="selection">
        <strong>{{ selection.name }}</strong><code class="text-caption text-muted">{{ selection.id }}</code>
        <NoahSkeleton v-if="loading" :rows="2" />
        <template v-else-if="snapshot">
          <fieldset v-if="selection.mode !== 'disabled'" class="client-policy-options" :disabled="busy">
            <legend>{{ t('store.policy.chooseRecommendation') }}</legend>
            <label v-for="value in ['recommended', 'required']" :key="value" :class="{ selected: mode === value }">
              <input v-model="mode" type="radio" name="client-policy-choice" :value="value" />
              <span>{{ t(value === 'required' ? 'store.policy.requiredPlugin' : 'store.policy.addRecommendation') }}</span>
            </label>
          </fieldset>
          <p v-else>{{ t('store.policy.disableConfirmation') }}</p>
          <p class="text-caption text-muted">{{ t('store.policy.globalChange') }}</p>
          <p v-if="snapshot.mode === 'profile'" class="text-caption text-muted">{{ t('store.policy.replaceInherited') }}</p>
        </template>
        <p v-if="failure" class="policy-error" role="alert">{{ failure.message }}</p>
      </template>
    </div>
    <template #footer>
      <NoahButton variant="glass" :disabled="busy" @click="close">{{ t('common.cancel') }}</NoahButton>
      <NoahButton variant="primary" :disabled="!ready" :loading="busy" @click="save">{{ t(selection?.mode === 'disabled' ? 'store.policy.disablePlugin' : mode === 'required' ? 'store.policy.requiredPlugin' : 'store.policy.addRecommendation') }}</NoahButton>
    </template>
  </NoahModal>
</template>

<style scoped>
.client-policy-dialog { display: grid; gap: 12px; }
.client-policy-dialog > strong, .client-policy-dialog > code { overflow-wrap: anywhere; }
.client-policy-dialog p { margin: 0; line-height: 1.7; }
.client-policy-options { display: grid; gap: 10px; border: 0; padding: 0; margin: 8px 0; min-width: 0; }
.client-policy-options legend { padding: 0 0 12px; font-size: var(--text-control); color: var(--text-muted); }
.client-policy-options label { display: flex; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 8px; padding: 16px; cursor: pointer; font-size: var(--text-control); }
.client-policy-options label.selected { border-color: var(--accent); background: var(--accent-soft); }
.client-policy-options input { accent-color: var(--accent); flex: none; }
.policy-error { color: var(--red); overflow-wrap: anywhere; }
:global(.noah-modal-overlay:has(.client-policy-dialog)) { background: var(--backdrop-overlay); backdrop-filter: blur(8px); }
:global(.noah-modal-dialog:has(.client-policy-dialog)) { background: var(--surface); }
</style>
