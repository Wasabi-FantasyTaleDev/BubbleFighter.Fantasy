<script setup>
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { BfTutorialDialogue } from '../game-ui'
import { useI18n } from '../i18n'
import { pagesForStage, nextStagePage } from './setupDialogue'
import approvedCopy from '../game-ui/fantasy-setup.zh.json'
import englishCopy from '../game-ui/fantasy-setup.en.json'
import traditionalCopy from '../game-ui/fantasy-setup.zh-Hant.json'
import koreanCopy from '../game-ui/fantasy-setup.ko.json'
const dialogueCopy = { en: englishCopy, 'zh-Hans': approvedCopy, 'zh-Hant': traditionalCopy, ko: koreanCopy }

const props = defineProps({ stage: { type: Number, required: true }, language: { type: String, required: true }, audioContext: { default: null } })
const { t } = useI18n()
const narrator = ref(null)
const dock = ref(null)
const pages = ref([])
const progress = ref(1)
const failed = ref(false)
const revision = ref(0)
const scale = ref(1)
const entered = ref(false)
const portraitMedia = window.matchMedia('(orientation: portrait)')
const portrait = ref(portraitMedia.matches)
function orientationChanged(event) { portrait.value = event.matches; entered.value = false }
let observer
const root = computed(() => `./tutorial/fantasy-setup/${props.language}/`)
const text = computed(() => dialogueCopy[props.language]?.find(page => page.progress === props.stage)?.text)
function selectPage() {
  const first = pagesForStage(pages.value, props.stage)[0]
  if (first) progress.value = first.progress
  else if (pages.value.length) failed.value = true
}
function ready(value) { pages.value = value; selectPage(); entered.value = true }
function advance(requested) { progress.value = nextStagePage(pages.value, props.stage, progress.value, requested) }
function retry() { failed.value = false; pages.value = []; revision.value++ }
watch(() => props.stage, selectPage)
watch(() => props.language, retry)
onMounted(() => {
  portraitMedia.addEventListener('change', orientationChanged)
  observer = new ResizeObserver(entries => { scale.value = entries[0].contentRect.width / 341 })
  observer.observe(dock.value)
})
onBeforeUnmount(() => { observer?.disconnect(); portraitMedia.removeEventListener('change', orientationChanged) })
</script>

<template>
  <aside v-show="!portrait" ref="dock" class="setup-narrator" :class="{ 'is-entered': entered || failed }" :inert="portrait || (!entered && !failed)" :aria-label="t('setup.guide')" :style="{ '--narrator-scale': scale }">
    <div v-if="!portrait && !failed" class="setup-narrator-canvas">
      <BfTutorialDialogue :key="`${language}-${revision}`" ref="narrator" stage="FantasySetup"
        :progress="progress" :resource-root="root" :sound="true" :volume="0.4" :audio-context="audioContext" :autoplay="false" :loop="false"
        :advance-label="t('setup.dialogueNext')" :reveal-label="t('setup.revealDialogue')"
        @ready="ready" @advance="advance" @error="failed = true" />
    </div>
    <div v-else-if="!portrait" class="setup-narrator-fallback">
      <p>{{ text }}</p>
      <p class="setup-hint" role="status">{{ t('setup.narratorUnavailable') }}</p>
      <button type="button" @click="retry">{{ t('setup.retry') }}</button>
    </div>
  </aside>
</template>

<style scoped>
.setup-narrator { position: fixed; right: 16px; bottom: 0; width: clamp(190px, 22vw, 341px); aspect-ratio: 341 / 390; pointer-events: none; z-index: 2; transform: translateX(calc(100% + 24px)); opacity: 0; transition: transform .8s cubic-bezier(.22, 1, .36, 1), opacity .3s ease; }
.setup-narrator.is-entered { transform: translateX(0); opacity: 1; }
.setup-narrator.setup-narrator-dock-leave-active { transition: transform .65s cubic-bezier(.4, 0, .8, .3), opacity .45s ease; }
.setup-narrator.setup-narrator-dock-leave-to { transform: translateX(calc(100% + 24px)); opacity: 0; }
.setup-narrator-canvas { position: absolute; right: 0; bottom: 0; width: 1024px; height: 768px; transform: scale(var(--narrator-scale)); transform-origin: bottom right; pointer-events: none; }
.setup-narrator-fallback button { pointer-events: auto; font: inherit; font-size: 11px; color: #18364b; border: 1px solid #c8dce7; border-radius: 12px; background: #ffffffb3; padding: 7px 12px; cursor: pointer; }
.setup-narrator-fallback { pointer-events: auto; padding: 20px; border: 1px solid #c8dce7; border-radius: 18px; background: #ffffffc9; color: #18364b; font-size: 13px; line-height: 1.8; }
.setup-narrator-fallback > p:first-child { white-space: pre-line; }
@media (max-height: 600px) { .setup-narrator { width: 175px; right: 8px; } }
@media (max-width: 760px) { .setup-narrator { width: clamp(125px, 28vw, 190px); right: 4px; } }
@media (orientation: portrait) { .setup-narrator { display: none; } }
@media (prefers-reduced-motion: reduce) { .setup-narrator { transition: none; } }
</style>
