<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../../kit/TpIcon.vue'
import Brand from '../../kit/Brand.vue'
import { cues } from '../../cue'
import type { Code, Step } from '../../types'

// The ChatGPT download page. Its Windows button hands over to the Microsoft Store.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
</script>

<template>
  <div class="cg-site">
    <header class="cg-head">
      <span class="cg-word"><Brand name="chatgpt" :size="26" tone="#111" />ChatGPT</span>
      <nav><span v-for="item in ['About', 'Features', 'Learn', 'Codex', 'Business', 'Pricing', 'Download']" :key="item">{{ item }}</span></nav>
      <b>Log in</b><span class="cg-signup">Sign up for free</span>
    </header>
    <div class="cg-card">
      <div class="cg-ask">Look across my <em>Calendar</em>, <em>Chat</em> and <em>Drive</em> and create a leadership brief</div>
      <div class="cg-doc"><div class="cg-doc-side"><span v-for="n in 5" :key="n" /></div><div class="cg-doc-main"><i /><b>Leadership Brief</b></div></div>
    </div>
    <div class="cg-copy">
      <h1>Download<br>ChatGPT<br>for desktop</h1>
      <p>Bring ChatGPT to your desktop with ChatGPT Work and Codex, plus context from your email, screenshots, files, and anything on your screen.</p>
      <p>Existing Codex app users can update to ChatGPT and open Codex.</p>
      <span class="cg-cta" data-tp="site-download" :class="{ 'is-pressed': c.between('dlClick', 'extPrompt') }">Download for Windows</span>
      <small>Download Chrome extension <TpIcon name="chevronRight" :size="13" /></small>
    </div>
  </div>
</template>
