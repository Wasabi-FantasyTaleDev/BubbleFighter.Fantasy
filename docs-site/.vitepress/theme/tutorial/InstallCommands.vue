<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { withBase } from 'vitepress'
import TpIcon from './kit/TpIcon.vue'
import type { Code, Platform } from './types'

// The install commands under the script tutorial, like a docs page: one tab per system, a copy
// button per command and links to the scripts themselves. The commands match the installation guide
// and skills/deploy-server/SKILL.md; change them together.
const props = defineProps<{ lang: Code; platform: Platform }>()
const { t: label } = useI18n()
const w = (key: string) => label(`tutorial.commands.${key}`)

const project = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy'
const release = `${project}/releases/latest/download`
const unattended = (options: string) => `& ([scriptblock]::Create((irm ${release}/install.ps1))) ${options}`
const tabs = computed(() => [
  {
    id: 'windows', name: w('windows'), shell: 'PowerShell', script: `${project}/blob/main/deploy/install.ps1`,
    rows: [
      { note: w('winAsk'), cmd: `irm ${release}/install.ps1 | iex` },
      { note: w('winPc'), cmd: unattended('-NoPrompt -Start') },
      { note: w('winServer'), cmd: unattended('-NoPrompt -Service') },
    ],
  },
  {
    id: 'linux', name: 'Linux', shell: 'bash', script: `${project}/blob/main/deploy/install.sh`,
    rows: [
      { note: w('linuxAsk'), cmd: `curl -fsSL ${release}/install.sh | bash` },
      { note: w('linuxService'), cmd: `curl -fsSL ${release}/install.sh | sudo bash -s -- --no-prompt --service` },
      { note: w('linuxDocker'), cmd: `curl -fsSL ${release}/install.sh | sudo bash -s -- --no-prompt --docker --install-docker --with-mysql` },
    ],
  },
])
// The system follows the switch beside the page title.
const active = computed(() => (props.platform === 'windows' ? 0 : 1))
const copied = ref(-1)
const failed = ref(-1)
const prefix: Record<Code, string> = { 'zh-CN': '/', en: '/en/', 'zh-TW': '/zh-tw/', ko: '/ko/' }
const guide = computed(() => withBase(`${prefix[props.lang]}getting-started/installation`))
const skill = `${project}/blob/main/skills/deploy-server/SKILL.md`

// The Clipboard API needs a focused, secure page; the older copy command covers the rest, and when
// both fail the command stays on screen to be selected by hand.
async function copy(text: string, n: number) {
  failed.value = -1
  try { await navigator.clipboard.writeText(text); copied.value = n }
  catch {
    const area = document.createElement('textarea')
    area.value = text
    area.setAttribute('readonly', '')
    area.style.cssText = 'position:fixed;opacity:0;pointer-events:none'
    document.body.append(area)
    area.select()
    let done = false
    try { done = document.execCommand('copy') } catch { /* unsupported */ }
    area.remove()
    if (done) copied.value = n
    else failed.value = n
  }
  if (copied.value === n) setTimeout(() => { if (copied.value === n) copied.value = -1 }, 1800)
}
watch(() => props.platform, () => { copied.value = -1; failed.value = -1 })
</script>

<template>
  <section class="ic" :aria-label="w('title')">
    <p class="ic-lead">{{ w('lead') }}</p>
    <h3 class="ic-system"><TpIcon :name="tabs[active].id === 'windows' ? 'windows' : 'terminal'" :size="16" />{{ tabs[active].name }}</h3>
    <div class="ic-panel">
      <div v-for="(row, n) in tabs[active].rows" :key="row.cmd" class="ic-row">
        <p class="ic-note">{{ row.note }}</p>
        <div class="ic-code">
          <span class="ic-shell">{{ tabs[active].shell }}</span>
          <pre><code>{{ row.cmd }}</code></pre>
          <button type="button" class="ic-copy" :aria-label="w('copy')" :title="w('copy')" @click="copy(row.cmd, n)">
            <TpIcon :name="copied === n ? 'check' : 'copy'" :size="15" />{{ copied === n ? label('tutorial.copied') : w('copy') }}
          </button>
        </div>
        <p v-if="failed === n" class="ic-failed" role="status">{{ label('tutorial.copyFailed') }}</p>
      </div>
      <p class="ic-links">
        <a :href="tabs[active].script" target="_blank" rel="noopener"><TpIcon name="code" :size="14" />{{ w('viewScript') }}</a>
        <a :href="guide"><TpIcon name="book" :size="14" />{{ w('guide') }}</a>
        <a :href="skill" target="_blank" rel="noopener"><TpIcon name="bot" :size="14" />{{ w('skill') }}</a>
      </p>
    </div>
  </section>
</template>
