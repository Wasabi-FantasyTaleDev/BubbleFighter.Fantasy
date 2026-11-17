<script setup lang="ts">
import { computed, type PropType } from 'vue'
import Desktop from '../kit/Desktop.vue'
import TpIcon from '../kit/TpIcon.vue'
import SetupBrowser from '../kit/SetupBrowser.vue'
import { cues } from '../cue'
import type { Code, Step } from '../types'
import './script.css'

// The Windows install script in PowerShell: open PowerShell from the taskbar search, paste the command,
// answer the script's questions and land on the setup page. Every console line is what deploy/install.ps1
// prints for a first install without options; the tutorial's steps share one transcript, and each step
// shows what the earlier steps left on screen.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const between = (a: string, b: string) => c.value.between(a, b)

const phases = ['open', 'paste', 'install', 'answer', 'setup'] as const
type Phase = (typeof phases)[number]
const phase = computed(() => (props.step.props?.phase ?? 'open') as Phase)
const at = (p: Phase) => phases.indexOf(p)
/** True once a cue of phase `p` has passed: always for earlier phases, by the clock in the current one. */
const reached = (p: Phase, cue: string) => at(phase.value) > at(p) || (phase.value === p && on(cue))

const url = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.ps1'
interface Line { text: string; kind?: 'step' | 'ok' | 'dim' | 'ask' }
const folder = 'C:\\BubbleFighterFantasy'

/** Typed text of a line, all of it once its phase is over. */
const typed = (p: Phase, text: string, from: string, to: string) => (at(phase.value) > at(p) ? text : phase.value === p ? c.value.typed(text, from, to) : '')

const lines = computed<Line[]>(() => {
  const out: Line[] = []
  if (!reached('open', 'console')) return out
  out.push({ text: 'Windows PowerShell' }, { text: 'Copyright (C) Microsoft Corporation. All rights reserved.' }, { text: '' })
  out.push({ text: 'PS C:\\Users\\Wasabi> ' + typed('paste', `irm ${url} | iex`, 'cmdStart', 'cmdEnd') })
  if (!reached('paste', 'cmdEnter')) return out
  if (reached('paste', 'folderQ')) out.push({ text: `Install folder [${folder}]: `, kind: 'ask' })
  if (!reached('paste', 'folderEnter')) return out
  const steps: [Phase, string, Line][] = [
    ['install', 'fetch', { text: '==> Fetching bubblefighter-fantasy-win-x64.zip (v0.4.0)', kind: 'step' }],
    ['install', 'check', { text: '==> Checking the download', kind: 'step' }],
    ['install', 'runtime', { text: `==> Installing the ASP.NET Core Runtime 10 (x64) into ${folder}\\dotnet`, kind: 'step' }],
    ['install', 'runtimeDone', { text: 'dotnet-install: Installation finished successfully.', kind: 'dim' }],
    ['install', 'unpack', { text: `==> Unpacking into ${folder}\\releases\\v0.4.0`, kind: 'step' }],
  ]
  for (const [p, cue, line] of steps) if (reached(p, cue)) out.push(line)
  if (!reached('answer', 'remoteQ')) return out
  out.push({ text: 'Allow other machines to open the WebUI (setup and administration)? [Y/n]: ', kind: 'ask' })
  if (!reached('answer', 'remoteEnter')) return out
  out.push({ text: 'Require a setup token before the first-run wizard can be used? [y/N]: ', kind: 'ask' })
  if (!reached('answer', 'tokenEnter')) return out
  out.push({ text: 'Start the server now? [Y/n]: ', kind: 'ask' })
  if (!reached('answer', 'startEnter')) return out
  out.push({ text: '==> Starting the server in its own window', kind: 'step' })
  if (!reached('answer', 'summary')) return out
  out.push(
    { text: '' },
    { text: `Bubble Fighter Fantasy v0.4.0 (x64) is installed in ${folder}.`, kind: 'ok' },
    { text: '  Running in its own window; close that window to stop the server.' },
    { text: `  Start:   double-click ${folder}\\bff.cmd` },
    { text: "  Open http://<this-server's-address>:7300/ to finish setup." },
    { text: '  The WebUI accepts other machines; allow TCP port 7300 in the firewall or cloud security group if needed.' },
    { text: `  Upgrade: run this installer again; ${folder}\\state is kept.` },
    { text: 'PS C:\\Users\\Wasabi> ' },
  )
  return out
})
// The console shows its newest lines, the way a terminal scrolls.
const visible = computed(() => lines.value.slice(-24))
const waiting = computed(() => {
  // A blinking cursor sits where the console waits for input or output.
  if (phase.value === 'paste') return on('cmdStart') && !on('folderEnter')
  if (phase.value === 'answer') return between('remoteQ', 'startEnter') || on('summary')
  if (phase.value === 'install') return true
  return phase.value === 'open' ? on('console') : on('summary')
})

const search = computed(() => phase.value === 'open' && between('flyout', 'console'))
const query = computed(() => c.value.typed('PowerShell', 'typeStart', 'typeEnd'))
const serverWindow = computed(() => reached('answer', 'server'))
const browser = computed(() => reached('setup', 'browser'))

const words: Record<Code, { best: string; app: string; open: string; admin: string }> = {
  en: { best: 'Best match', app: 'App', open: 'Open', admin: 'Run as administrator' },
  'zh-CN': { best: '最佳匹配', app: '应用', open: '打开', admin: '以管理员身份运行' },
  'zh-TW': { best: '最符合項目', app: '應用程式', open: '開啟', admin: '以系統管理員身分執行' },
  ko: { best: '가장 일치하는 항목', app: '앱', open: '열기', admin: '관리자 권한으로 실행' },
}
const u = computed(() => words[props.lang])
const assistant = computed(() => on('assist'))
</script>

<template>
  <Desktop :apps="[...(on('console') || phase !== 'open' ? ['terminal'] : []), ...(browser ? ['browser'] : [])]" :lang="lang">
    <!-- Taskbar search -->
    <div class="sc-search" :class="{ 'is-open': search }">
      <div class="sc-search-box"><TpIcon name="search" :size="16" /><span>{{ query }}<i v-if="search && !on('resultClick')" class="tp-caret" /></span></div>
      <div v-if="on('typeEnd')" class="sc-search-body">
        <div class="sc-best">
          <small>{{ u.best }}</small>
          <span class="sc-result" data-tp="ps-result" :class="{ 'is-hover': on('resultHover'), 'is-pressed': between('resultClick', 'console') }"><span class="sc-ps-icon"><TpIcon name="terminal" :size="20" /></span><b>Windows PowerShell</b><small>{{ u.app }}</small></span>
        </div>
        <div class="sc-side"><span class="sc-ps-icon is-big"><TpIcon name="terminal" :size="34" /></span><b>Windows PowerShell</b><small>{{ u.app }}</small><hr><span><TpIcon name="arrowUpRight" :size="14" />{{ u.open }}</span><span><TpIcon name="shield" :size="14" />{{ u.admin }}</span></div>
      </div>
    </div>

    <!-- Windows PowerShell -->
    <div class="tp-win sc-console" data-tp="console" :class="{ 'is-open': on('console') || phase !== 'open', 'is-static': phase !== 'open', 'is-behind': browser }">
      <div class="sc-title"><span class="sc-ps-icon is-small"><TpIcon name="terminal" :size="12" /></span>Windows PowerShell<span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span></div>
      <div class="sc-body">
        <div v-for="(line, n) in visible" :key="n" class="sc-line" :class="line.kind && `is-${line.kind}`">{{ line.text }}<i v-if="n === visible.length - 1 && waiting" class="sc-cursor" /></div>
      </div>
      <span class="sc-anchor" data-tp="sc-last" :style="{ top: `${44 + visible.length * 20}px` }" />
    </div>

    <!-- The server's own window, started by the script -->
    <div class="tp-win sc-server" :class="{ 'is-open': serverWindow, 'is-static': phase === 'setup', 'is-behind': browser }">
      <div class="sc-title is-cmd"><TpIcon name="terminal" :size="13" />{{ folder }}\bff.cmd<span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span></div>
      <div class="sc-body is-cmd">
        <div class="sc-line">Row 'admin.http' admin API listening on 127.0.0.1:7300.</div>
        <div class="sc-line">WebUI: http://127.0.0.1:7300/</div>
      </div>
    </div>

    <SetupBrowser :lang="lang" :open="browser" address="127.0.0.1:7300" :pressed="between('contClick', 'assist')" :assistant="assistant" :picked="on('pick')" />
  </Desktop>
</template>
