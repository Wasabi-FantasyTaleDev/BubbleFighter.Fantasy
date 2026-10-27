<script setup>
import { computed, reactive, ref } from 'vue'
import StoreTabs from './StoreTabs.vue'
import NoahButton from '../noah/components/ui/NoahButton.vue'
import NoahInput from '../noah/components/ui/NoahInput.vue'
import NoahSelect from '../noah/components/ui/NoahSelect.vue'
import NoahTag from '../noah/components/ui/NoahTag.vue'
import NoahModal from '../noah/components/ui/NoahModal.vue'
import { conflicts, ruleId, sourceName, validVersion, wireRule } from '../api/clientPolicy'
import { useI18n } from '../i18n'
import { useUiStore } from '../stores/ui'

const props = defineProps({
  entry: { type: Object, required: true }, offer: { type: Object, required: true }, plugin: { type: Object, required: true },
  types: { type: Array, default: () => [] }, sources: { type: Array, default: () => [] }, builds: { type: Array, default: () => [] }, busy: Boolean
})
// `save` carries the plugin's complete new rule list plus any sources the edit introduced, so the parent writes both at once.
const emit = defineEmits(['back', 'save', 'remove-plugin'])
const { t, te } = useI18n()
const ui = useUiStore()
function buildName(id) {
  const build = props.builds.find(item => item.id === id)
  return build?.displayNames?.[ui.lang] || build?.release || id
}
// A plugin the stores do not offer has no author, version or description, so it opens on its rules instead.
const hasDetails = computed(() => !!(props.offer.package.author || props.offer.package.version || props.offer.package.description))
const page = ref(hasDetails.value ? 'details' : 'policy')
const tabs = computed(() => [
  ...(hasDetails.value ? [{ id: 'details', label: t('plugins.details') }] : []),
  { id: 'definition', label: t('plugins.definition') },
  { id: 'policy', label: t('plugins.policy.rules') }
])
// A type added on the server before the WebUI learns its name still shows, under its own identifier.
const typeLabel = type => te('plugins.policy.type.' + type) ? t('plugins.policy.type.' + type) : type
const ruleTypes = computed(() => [{ value: '', label: t('plugins.policy.chooseType') }, ...props.types.map(type => ({ value: type.name, label: typeLabel(type.name) }))])
const field = rule => props.types.find(type => type.name === rule.type)?.field
const versionError = rule => field(rule) === 'version' && !validVersion(rule.version.trim()) ? t(rule.version.trim() ? 'plugins.policy.invalidVersion' : 'plugins.policy.versionRequired') : ''

// Rows being edited, keyed by the saved rule's id or a local key for a rule not saved yet.
const drafts = ref([])
let nextDraft = 0
const errors = reactive(Object.create(null))
const draftFor = id => drafts.value.find(draft => draft.original === id)
const unsaved = computed(() => drafts.value.filter(draft => draft.original === null))
function addDraft() { drafts.value.push({ key: 'new-' + nextDraft++, original: null, type: '', version: '', sources: [], builds: [], added: [] }) }
function editRule(rule) {
  if (draftFor(rule.id)) return
  drafts.value.push({ key: rule.id, original: rule.id, type: rule.type, version: rule.version || '', sources: [...(rule.sources || [])], builds: [...rule.builds], added: [] })
}
function dropDraft(draft) { drafts.value = drafts.value.filter(item => item !== draft); delete errors[draft.key] }

// Builds: an empty selection applies the rule to every build.
const buildDraft = ref(null)
const selectedBuilds = ref([])
const buildDialogOpen = computed({ get: () => !!buildDraft.value, set: open => { if (!open) buildDraft.value = null } })
const buildChoices = computed(() => [...new Set([...props.builds.map(build => build.id), ...(buildDraft.value?.builds || [])])])
function openBuilds(draft) { buildDraft.value = draft; selectedBuilds.value = [...draft.builds] }
function confirmBuilds() { buildDraft.value.builds = [...selectedBuilds.value]; buildDraft.value = null }
const buildSummary = builds => builds.length ? builds.map(buildName).join(', ') : t('plugins.policy.allBuilds')

// Sources: rules name the policy's store sources; a new address becomes a source saved with the rule.
const sourceDraft = ref(null)
const selectedSources = ref([])
const addedSources = ref([])
const customSource = reactive({ url: '', kind: 'http', catalogPath: 'source.toml' })
const sourceError = ref('')
const sourceDialogOpen = computed({ get: () => !!sourceDraft.value, set: open => { if (!open) sourceDraft.value = null } })
const sourceKinds = computed(() => ['http', 'alist'].map(value => ({ value, label: t('plugins.policy.sourceKind.' + value) })))
const knownSources = computed(() => [...props.sources, ...addedSources.value])
function openSources(draft) {
  sourceDraft.value = draft
  selectedSources.value = [...draft.sources]
  addedSources.value = [...draft.added]
  Object.assign(customSource, { url: '', kind: 'http', catalogPath: 'source.toml' })
  sourceError.value = ''
}
function addSource() {
  const value = customSource.url.trim()
  if (!value) return true
  const path = customSource.catalogPath.trim()
  try {
    const url = new URL(value)
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) throw new Error('address')
    if (!path || /[\\:?#%]/.test(path) || path.split('/').some(part => !part || part === '.' || part === '..' || part !== part.trim())) throw new Error('path')
    const existing = knownSources.value.find(source => source.kind === customSource.kind && source.baseUrl === url.href && source.catalogPath === path)
    const name = existing?.name || sourceName(knownSources.value, url.href)
    if (!existing) addedSources.value.push({ name, kind: customSource.kind, baseUrl: url.href, catalogPath: path })
    if (!selectedSources.value.includes(name)) selectedSources.value.push(name)
    customSource.url = ''
    sourceError.value = ''
    return true
  } catch (error) {
    sourceError.value = t(error.message === 'path' ? 'plugins.policy.invalidCatalogPath' : 'plugins.policy.invalidSource')
    return false
  }
}
function confirmSources() {
  if (!addSource()) return
  sourceDraft.value.added = addedSources.value.filter(source => selectedSources.value.includes(source.name))
  sourceDraft.value.sources = [...selectedSources.value]
  sourceDraft.value = null
}
const sourceLabel = name => knownSources.value.find(source => source.name === name)?.baseUrl || name

const pending = ref(null)
const keep = ref('existing')
const conflictOpen = computed({ get: () => !!pending.value, set: open => { if (!open) pending.value = null } })
function saveDraft(draft) {
  const error = !draft.type ? t('plugins.policy.chooseType') : versionError(draft)
  if (error) { errors[draft.key] = error; return }
  delete errors[draft.key]
  const others = props.plugin.rules.filter(rule => rule.id !== draft.original)
  // A rule keeps its identity while its type stays, so the Launcher does not offer an edited recommendation again.
  const previous = props.plugin.rules.find(rule => rule.id === draft.original)
  const id = previous && previous.type === draft.type ? previous.id : ruleId(others, draft.type)
  const candidate = wireRule({ ...draft, id }, props.types)
  const clashing = others.filter(rule => conflicts(props.types, candidate, rule))
  if (clashing.length) { pending.value = { draft, candidate, clashing }; keep.value = 'existing'; return }
  commit(draft, candidate, [])
}
function commit(draft, candidate, dropped) {
  const rules = props.plugin.rules.flatMap(rule => rule.id === draft.original ? [candidate] : dropped.includes(rule.id) ? [] : [rule])
  if (draft.original === null) rules.push(candidate)
  emit('save', { rules, sources: draft.added.filter(source => candidate.sources?.includes(source.name)), done: () => dropDraft(draft) })
}
function resolveConflict() {
  const selection = pending.value
  pending.value = null
  if (keep.value === 'incoming') commit(selection.draft, selection.candidate, selection.clashing.map(rule => rule.id))
  else dropDraft(selection.draft)
}
function removeRule(rule) {
  const draft = draftFor(rule.id)
  emit('save', { rules: props.plugin.rules.filter(item => item.id !== rule.id), sources: [], done: () => draft && dropDraft(draft) })
}
function ruleLabel(rule) {
  return typeLabel(rule.type) + (rule.version ? ` · ${rule.version}` : '')
}
const ruleTone = rule => rule.type === 'require' ? 'success' : ['recommend', 'latest'].includes(rule.type) ? 'info' : ['min_version', 'max_version'].includes(rule.type) ? 'warning' : 'default'
</script>

<template>
  <section class="client-policy-detail">
    <NoahButton class="client-policy-back" variant="glass" icon="chevron-left" @click="$emit('back')">{{ t('plugins.back') }}</NoahButton>
    <header class="client-policy-title"><h3>{{ offer.package.name }}</h3><span>{{ entry.identity.id }}</span></header>
    <div class="client-policy-tabs"><StoreTabs v-model="page" :items="tabs" :label="t('plugins.details')" /></div>
    <div class="client-policy-scroll">
      <Transition name="policy-tab" mode="out-in">
        <div :key="page">
          <template v-if="page === 'details'">
            <dl v-if="offer.package.author || offer.package.version" class="policy-facts"><template v-if="offer.package.author"><dt>{{ t('plugins.author') }}</dt><dd>{{ offer.package.author }}</dd></template><template v-if="offer.package.version"><dt>{{ t('col.version') }}</dt><dd>{{ offer.package.version }}</dd></template></dl>
            <section v-if="offer.package.description" class="policy-description"><h4>{{ t('plugins.description') }}</h4><p>{{ offer.package.description }}</p></section>
          </template>
          <template v-else-if="page === 'definition'">
            <dl class="policy-facts"><dt>{{ t('plugins.name') }}</dt><dd>{{ offer.package.name }}</dd><dt>{{ t('plugins.code') }}</dt><dd>{{ entry.identity.id }}</dd><dt>{{ t('plugins.configurationSource') }}</dt><dd>{{ offer.sourceName }}</dd></dl>
            <section class="policy-description"><h4>{{ t('store.compatibleBuilds') }}</h4><div class="policy-builds"><NoahTag v-for="build in offer.package.builds" :key="build" pill size="sm" :title="build">{{ buildName(build) }}</NoahTag><span v-if="!offer.package.builds.length" class="text-muted">{{ t('store.unspecifiedCompatibility') }}</span></div></section>
          </template>
          <div v-else class="policy-rules">
            <p v-if="!plugin.rules.length && !unsaved.length" class="policy-empty">{{ t('plugins.policy.noRules') }}</p>
            <div v-for="(item, index) in [...plugin.rules.map(rule => ({ rule, draft: draftFor(rule.id) })), ...unsaved.map(draft => ({ rule: null, draft }))]" :key="item.draft?.key || item.rule.id" class="policy-rule">
              <template v-if="item.draft">
                <div class="policy-rule-line policy-rule-editor">
                  <NoahSelect v-model="item.draft.type" class="policy-type-select" :options="ruleTypes" :aria-label="t('plugins.policy.ruleNumber', { number: index + 1 })" />
                  <NoahInput v-if="field(item.draft) === 'version'" v-model="item.draft.version" class="policy-version-input" :aria-label="typeLabel(item.draft.type)" :aria-invalid="!!errors[item.draft.key]" placeholder="0.2.0" required />
                  <NoahButton v-if="field(item.draft) === 'sources'" class="policy-source-button" variant="primary" @click="openSources(item.draft)">{{ t('plugins.policy.sourcesButton') }}<span v-if="item.draft.sources.length"> ({{ item.draft.sources.length }})</span></NoahButton>
                </div>
                <button type="button" class="policy-build-scope" @click="openBuilds(item.draft)"><span>{{ t('plugins.policy.builds') }}</span><strong>{{ buildSummary(item.draft.builds) }}</strong></button>
                <p v-if="errors[item.draft.key]" class="policy-form-error" role="alert">{{ errors[item.draft.key] }}</p>
                <div class="policy-editor-actions"><NoahButton variant="primary" size="sm" :loading="busy" @click="saveDraft(item.draft)">{{ t('common.save') }}</NoahButton><NoahButton variant="secondary" size="sm" :disabled="busy" @click="dropDraft(item.draft)">{{ t('common.cancel') }}</NoahButton></div>
              </template>
              <div v-else class="policy-saved-line">
                <span class="policy-saved-tags">
                  <NoahTag pill size="sm" :tone="ruleTone(item.rule)" :title="item.rule.sources?.map(sourceLabel).join('\n') || undefined">{{ ruleLabel(item.rule) }}<span v-if="item.rule.sources?.length"> · {{ t('plugins.policy.sourceCount', { count: item.rule.sources.length }) }}</span></NoahTag>
                  <span class="policy-rule-builds">{{ buildSummary(item.rule.builds) }}</span>
                </span>
                <div class="policy-saved-actions"><button type="button" :disabled="busy" @click="editRule(item.rule)">{{ t('plugins.policy.edit') }}</button><button type="button" :disabled="busy" :aria-label="t('plugins.policy.removeRule', { number: index + 1 })" @click="removeRule(item.rule)">{{ t('plugins.policy.remove') }}</button></div>
              </div>
            </div>
          </div>
        </div>
      </Transition>
    </div>
    <footer v-if="page === 'policy'" class="client-policy-actions">
      <NoahButton variant="primary" :disabled="busy" @click="addDraft">{{ t('plugins.policy.add') }}</NoahButton>
      <button type="button" class="policy-remove-plugin" :disabled="busy" @click="$emit('remove-plugin')">{{ t('plugins.policy.removePlugin') }}</button>
    </footer>
    <NoahModal v-model="buildDialogOpen" :title="t('plugins.policy.builds')" width="540px" :close-label="t('common.close')">
      <div class="policy-source-dialog">
        <p class="policy-source-hint">{{ t('plugins.policy.buildsHint') }}</p>
        <div class="policy-source-options"><label v-for="id in buildChoices" :key="id" :class="{ selected: selectedBuilds.includes(id) }"><input v-model="selectedBuilds" type="checkbox" :value="id" /><span>{{ buildName(id) }}</span><code>{{ id }}</code></label><p v-if="!buildChoices.length" class="text-muted">{{ t('plugins.policy.noBuilds') }}</p></div>
      </div>
      <template #footer><NoahButton variant="glass" @click="buildDraft = null">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" @click="confirmBuilds">{{ t('plugins.policy.confirmSources') }}</NoahButton></template>
    </NoahModal>
    <NoahModal v-model="sourceDialogOpen" :title="t('plugins.policy.sourcesButton')" width="540px" :close-label="t('common.close')">
      <div class="policy-source-dialog">
        <p class="policy-source-hint">{{ t('plugins.policy.sourcesHint') }}</p>
        <section class="policy-source-section"><h4>{{ t('plugins.policy.existingSources') }}</h4>
          <div class="policy-source-options"><label v-for="source in knownSources" :key="source.name" :class="{ selected: selectedSources.includes(source.name) }"><input v-model="selectedSources" type="checkbox" :value="source.name" /><span>{{ source.baseUrl }}</span><code>{{ source.catalogPath }}</code></label><p v-if="!knownSources.length" class="text-muted">{{ t('plugins.policy.noSources') }}</p></div>
        </section>
        <section class="policy-source-section"><h4>{{ t('plugins.policy.enterSource') }}</h4>
          <form class="policy-source-form" @submit.prevent="addSource">
            <div class="policy-source-input"><NoahSelect v-model="customSource.kind" class="policy-kind-select" :options="sourceKinds" :aria-label="t('plugins.policy.sourceKindLabel')" /><NoahInput v-model="customSource.url" :aria-label="t('plugins.policy.enterSource')" :aria-invalid="!!sourceError" placeholder="https://example.com/plugins/" /></div>
            <div class="policy-source-input"><NoahInput v-model="customSource.catalogPath" :aria-label="t('plugins.policy.catalogPath')" :placeholder="t('plugins.policy.catalogPath')" /><NoahButton variant="primary" html-type="submit">{{ t('plugins.policy.addSource') }}</NoahButton></div>
          </form>
          <p v-if="sourceError" class="policy-form-error" role="alert">{{ sourceError }}</p>
        </section>
      </div>
      <template #footer><NoahButton variant="glass" @click="sourceDraft = null">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" @click="confirmSources">{{ t('plugins.policy.confirmSources') }}</NoahButton></template>
    </NoahModal>
    <NoahModal v-model="conflictOpen" :title="t('plugins.policy.conflictTitle')" width="580px" :close-label="t('common.close')">
      <div v-if="pending" class="policy-conflict-dialog">
        <p>{{ t('plugins.policy.conflictHint') }}</p>
        <div class="policy-conflict-cards" role="radiogroup" :aria-label="t('plugins.policy.conflictTitle')">
          <button type="button" role="radio" :aria-checked="keep === 'existing'" :class="{ selected: keep === 'existing' }" @click="keep = 'existing'"><strong>{{ t('plugins.policy.existing') }}</strong><span v-for="rule in pending.clashing" :key="rule.id">{{ ruleLabel(rule) }} · {{ buildSummary(rule.builds) }}</span></button>
          <button type="button" role="radio" :aria-checked="keep === 'incoming'" :class="{ selected: keep === 'incoming' }" @click="keep = 'incoming'"><strong>{{ t('plugins.policy.incoming') }}</strong><span>{{ ruleLabel(pending.candidate) }} · {{ buildSummary(pending.candidate.builds) }}</span></button>
        </div>
      </div>
      <template #footer><NoahButton variant="glass" @click="pending = null">{{ t('common.cancel') }}</NoahButton><NoahButton variant="primary" @click="resolveConflict">{{ t('plugins.policy.keepSelected') }}</NoahButton></template>
    </NoahModal>
  </section>
</template>

<style scoped>
.client-policy-detail { display: flex; flex-direction: column; min-height: 0; height: 100%; min-width: 0; }
.client-policy-back { display: none; }
.client-policy-title { display: grid; gap: 10px; padding: 20px; border-bottom: 1px solid var(--border-soft); }
.client-policy-title h3 { margin: 0; font-size: var(--text-title); line-height: 1.4; overflow-wrap: anywhere; }
.client-policy-title > span { font-size: var(--text-caption); color: var(--text-muted); overflow-wrap: anywhere; }
.client-policy-tabs { padding: 8px 20px 0; border-bottom: 1px solid var(--border-soft); }
.client-policy-tabs :deep(.store-tabs) { gap: 20px; }
.client-policy-tabs :deep(button) { white-space: nowrap; }
.client-policy-scroll { flex: 1; min-height: 0; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; }
.policy-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 16px 20px; padding: 24px 20px; margin: 0; font-size: var(--text-control); }
.policy-facts dt { color: var(--text-muted); }
.policy-facts dd { text-align: right; margin: 0; overflow-wrap: anywhere; }
.policy-description { padding: 0 20px 24px; }
.policy-description h4 { font-size: var(--text-control); font-weight: 500; margin: 0 0 12px; }
.policy-description p { margin: 0; color: var(--text-muted); font-size: var(--text-control); line-height: 1.7; overflow-wrap: anywhere; white-space: pre-line; }
.policy-builds { display: flex; flex-wrap: wrap; gap: 8px; font-size: var(--text-control); }
.policy-builds > .noah-tag { padding: 6px 11px; line-height: 1.3; border-radius: var(--radius-pill); }
.policy-rules { display: grid; padding: 8px 20px 20px; }
.policy-rule { display: grid; gap: 8px; border-bottom: 1px solid var(--border-soft); padding: 14px 0; min-width: 0; }
.policy-rule-line { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; min-width: 0; }
.policy-rule-editor { flex-wrap: nowrap; }
.policy-type-select { flex: 1 1 170px; min-width: 0; }
.policy-version-input { flex: 1 1 105px; min-width: 0; max-width: 160px; }
.policy-build-scope { display: flex; justify-content: space-between; align-items: center; gap: 12px; border: 1px solid var(--border); border-radius: 10px; background: var(--surface); color: var(--text); padding: 10px 14px; font: inherit; font-size: var(--text-control); cursor: pointer; text-align: left; min-width: 0; }
.policy-build-scope:hover { border-color: var(--accent); }
.policy-build-scope span { color: var(--text-muted); flex: none; }
.policy-build-scope strong { font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
.policy-editor-actions { display: flex; gap: 8px; }
.policy-editor-actions .noah-btn { min-height: 36px; padding: 8px 18px; }
.policy-saved-line { display: flex; justify-content: space-between; align-items: center; gap: 12px; min-width: 0; }
.policy-saved-tags { display: grid; gap: 6px; justify-items: start; min-width: 0; }
.policy-saved-tags > .noah-tag { padding: 6px 10px; line-height: 1.4; white-space: normal; overflow-wrap: anywhere; }
.policy-rule-builds { font-size: var(--text-caption); color: var(--text-muted); overflow-wrap: anywhere; }
.policy-saved-actions { display: flex; align-items: center; gap: 12px; flex: none; }
.policy-saved-actions button, .policy-remove-plugin { border: 0; background: none; color: var(--text-muted); padding: 6px 0; font: inherit; font-size: var(--text-caption); cursor: pointer; }
.policy-saved-actions button:hover, .policy-remove-plugin:hover { color: var(--text); }
.policy-saved-actions button:disabled, .policy-remove-plugin:disabled { cursor: default; opacity: 0.5; }
.policy-source-button.noah-btn { flex: none; min-height: 40px; padding: 10px 16px; }
.policy-source-dialog { display: grid; gap: 24px; }
.policy-source-hint { margin: 0; color: var(--text-muted); font-size: var(--text-control); line-height: 1.6; }
.policy-source-section { display: grid; gap: 12px; }
.policy-source-section h4 { margin: 0; font-size: var(--text-control); font-weight: 500; }
.policy-source-options { display: grid; gap: 8px; max-height: 260px; overflow-y: auto; }
.policy-source-options label { display: flex; gap: 12px; align-items: center; cursor: pointer; border: 1px solid var(--border); border-radius: 10px; padding: 14px; font-size: var(--text-control); min-width: 0; }
.policy-source-options label span { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.policy-source-options label code { font-size: var(--text-caption); color: var(--text-muted); flex: none; }
.policy-source-options label.selected { background: var(--accent-soft); border-color: var(--accent); }
.policy-source-options input { accent-color: var(--accent); }
.policy-source-form { display: grid; gap: 10px; }
.policy-source-input { display: flex; gap: 10px; min-width: 0; }
.policy-source-input > .noah-input-wrap { flex: 1; min-width: 0; }
.policy-source-input > .noah-btn { min-height: 40px; flex: none; }
.policy-kind-select { flex: 0 0 120px; }
.policy-conflict-dialog > p { margin: 0 0 20px; font-size: var(--text-control); line-height: 1.7; }
.policy-conflict-cards { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.policy-conflict-cards button { display: flex; flex-direction: column; align-items: flex-start; gap: 12px; padding: 20px; min-height: 120px; background: var(--surface); border: 1px solid var(--border); color: var(--text); border-radius: 12px; text-align: left; font: inherit; cursor: pointer; }
.policy-conflict-cards button.selected { background: var(--accent-soft); border-color: var(--accent); }
.policy-conflict-cards button:focus-visible { outline: 2px solid var(--accent-ring); outline-offset: 2px; }
.policy-conflict-cards strong { font-size: var(--text-control); font-weight: 500; }
.policy-conflict-cards span { font-size: var(--text-caption); color: var(--text-muted); line-height: 1.6; overflow-wrap: anywhere; }
@media (max-width: 480px) { .policy-conflict-cards { grid-template-columns: minmax(0, 1fr); } .policy-source-input { flex-wrap: wrap; } .policy-kind-select { flex: 1 1 100%; } }
.policy-empty { margin: 10px 0; font-size: var(--text-control); color: var(--text-muted); line-height: 1.7; }
.policy-form-error { font-size: var(--text-caption); color: var(--danger, #c64343); margin: 0; }
.client-policy-actions { flex: none; display: grid; gap: 8px; justify-items: center; border-top: 1px solid var(--border-soft); padding: 16px 20px; }
.client-policy-actions > .noah-btn { min-height: 40px; width: 100%; }
.policy-tab-enter-active, .policy-tab-leave-active { transition: opacity 140ms ease, transform 140ms ease; }
.policy-tab-enter-from, .policy-tab-leave-to { opacity: 0; transform: translateY(4px); }
@media (max-width: 760px) { .client-policy-back { display: inline-flex; align-self: start; margin: 16px 20px 0; } }
@media (prefers-reduced-motion: reduce) { .policy-tab-enter-active, .policy-tab-leave-active { transition: none; } }
</style>
