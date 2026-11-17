<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import Brand from '../../kit/Brand.vue'
import { cues, easeInOut } from '../../cue'
import { osText } from '../../kit/os'
import type { Code, Step } from '../../types'

// The Microsoft Store page the ChatGPT download button opens: Get, a progress bar, then Open.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const os = computed(() => osText(props.lang))
const progress = computed(() => easeInOut(c.value.progress('get', c.value.time('getDone') - c.value.time('get'))))
const state = computed(() => (on('getDone') ? 'done' : on('get') ? 'busy' : 'idle'))
</script>

<template>
  <div class="tp-win ms-store is-open">
    <div class="ms-title"><TpIcon name="back" :size="15" /><Brand name="store" :size="18" /><span>Microsoft Store</span><div class="ms-search">chatgpt<TpIcon name="search" :size="14" /></div><span class="ms-avatar"><TpIcon name="bot" :size="15" /></span><span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span></div>
    <aside class="ms-rail">
      <span><TpIcon name="home" :size="19" />{{ os.storeHome }}</span>
      <span class="is-on"><TpIcon name="grid" :size="19" />{{ os.storeApps }}</span>
      <span><TpIcon name="cup" :size="19" />{{ os.storeGaming }}</span>
      <span><TpIcon name="sparkle" :size="19" />AI Hub</span>
      <span class="ms-rail-end"><TpIcon name="download" :size="19" />{{ os.downloads }}</span>
      <span><TpIcon name="book" :size="19" />{{ os.storeLibrary }}</span>
    </aside>
    <main class="ms-main">
      <div class="ms-hero">
        <span class="ms-icon"><Brand name="chatgpt" :size="52" tone="#111" /></span>
        <div><h2>ChatGPT</h2><a>OpenAI</a><small>4.0 ★　{{ os.storeRatings(159) }}</small></div>
      </div>
      <p class="ms-lead">Bring ChatGPT to your desktop with ChatGPT Work and Codex, plus context from your email, screenshots, files, and anything on your screen.</p>
      <div class="ms-action">
        <span class="ms-get" data-tp="store-get" :class="{ 'is-pressed': c.between('getPress', 'get'), 'is-busy': state === 'busy' }">
          <Transition name="tp-swap" mode="out-in">
            <span v-if="state === 'idle'" key="get">{{ os.storeGet }}</span>
            <span v-else-if="state === 'busy'" key="busy" class="ms-busy"><small>{{ progress < .82 ? os.storeDownloading : os.storeInstalling }}… {{ Math.round(progress * 100) }}%</small><i><b :style="{ transform: `scaleX(${progress})` }" /></i></span>
            <span v-else key="open">{{ os.storeOpen }}</span>
          </Transition>
        </span>
        <TpIcon name="share" :size="18" />
      </div>
      <section class="ms-shots">
        <header>{{ os.storeShots }}<TpIcon name="chevronRight" :size="16" /></header>
        <div><span v-for="n in 2" :key="n" class="ms-shot"><i /></span></div>
      </section>
    </main>
  </div>
</template>
