<script setup lang="ts">
import { computed, type PropType } from 'vue'
import TpIcon from '../kit/TpIcon.vue'
import MacDesktop from '../kit/MacDesktop.vue'
import SetupBrowser from '../kit/SetupBrowser.vue'
import { cues } from '../cue'
import type { Code, Step } from '../types'
import './script.css'

// The Linux install script over SSH from a Mac: Terminal signs in to the server, runs install.sh,
// answers its questions, and the setup page opens at the server's address. Every server line is what
// deploy/install.sh prints for a first install as root without options; the steps share one
// transcript, and each step shows what the earlier steps left on screen.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const between = (a: string, b: string) => c.value.between(a, b)

const phases = ['open', 'paste', 'install', 'answer', 'setup'] as const
type Phase = (typeof phases)[number]
const phase = computed(() => (props.step.props?.phase ?? 'open') as Phase)
const at = (p: Phase) => phases.indexOf(p)
const reached = (p: Phase, cue: string) => at(phase.value) > at(p) || (phase.value === p && on(cue))
const typed = (p: Phase, text: string, from: string, to: string) => (at(phase.value) > at(p) ? text : phase.value === p ? c.value.typed(text, from, to) : '')

// 203.0.113.10 is a documentation address (RFC 5737), standing in for the reader's server.
const server = '203.0.113.10'
const root = '/opt/bubblefighter-fantasy'
const script = 'https://github.com/Wasabi-FantasyTaleDev/BubbleFighter.Fantasy/releases/latest/download/install.sh'
interface Line { prompt?: string; text: string }

const lines = computed<Line[]>(() => {
  const out: Line[] = []
  if (!reached('open', 'term')) return out
  out.push({ text: 'Last login: Mon Nov 16 21:04:12 on ttys000' })
  out.push({ prompt: 'Wasabi@Wasabi-Mac ~ % ', text: typed('open', `ssh root@${server}`, 'sshStart', 'sshEnd') })
  if (!reached('open', 'login')) return out
  out.push({ text: 'Linux bff-server x86_64' }, { text: '' })
  out.push({ prompt: 'root@bff-server:~# ', text: typed('paste', `curl -fsSL ${script} | bash`, 'cmdStart', 'cmdEnd') })
  if (!reached('paste', 'intro')) return out
  out.push({ text: 'Bubble Fighter Fantasy installer. Press Enter to take the answer in capitals.' })
  out.push({ text: 'Run the server in Docker (no: install it directly on this system)? [y/N] ' })
  if (!reached('paste', 'dockerEnter')) return out
  out.push({ text: 'Install a systemd service so the server starts at boot? [Y/n] ' })
  if (!reached('paste', 'serviceEnter')) return out
  const steps: [Phase, string, string][] = [
    ['install', 'fetch', '==> Fetching bubblefighter-fantasy-linux-x64.tar.gz (v0.4.0)'],
    ['install', 'check', '==> Checking the download'],
    ['install', 'runtime', `==> Installing the ASP.NET Core Runtime 10 into ${root}/dotnet`],
    ['install', 'runtimeDone', 'dotnet-install: Installation finished successfully.'],
    ['install', 'unpack', `==> Unpacking into ${root}/releases/v0.4.0`],
  ]
  for (const [p, cue, text] of steps) if (reached(p, cue)) out.push({ text })
  if (!reached('answer', 'remoteQ')) return out
  out.push({ text: 'Allow other machines to open the WebUI (setup and administration)? [Y/n] ' })
  if (!reached('answer', 'remoteEnter')) return out
  out.push({ text: 'Require a setup token before the first-run wizard can be used? [y/N] ' })
  if (!reached('answer', 'tokenEnter')) return out
  if (!reached('answer', 'service')) return out
  out.push(
    { text: '==> Installing bubblefighter-fantasy.service' },
    { text: "Created symlink '/etc/systemd/system/multi-user.target.wants/bubblefighter-fantasy.service' → '/etc/systemd/system/bubblefighter-fantasy.service'." },
  )
  if (!reached('answer', 'summary')) return out
  out.push(
    { text: '' },
    { text: `Bubble Fighter Fantasy v0.4.0 is installed in ${root}.` },
    { text: `  Open http://${server}:7300/ to finish setup.` },
    { text: '  The WebUI accepts other machines; open TCP port 7300 in the firewall or cloud security group if needed.' },
    { text: `  Upgrade: run this installer again; ${root}/state is kept.` },
    { prompt: 'root@bff-server:~# ', text: '' },
  )
  return out
})
const visible = computed(() => lines.value.slice(-26))
const signedIn = computed(() => reached('open', 'login'))
const browser = computed(() => reached('setup', 'browser'))
// Terminal names its window "user — process — columns×rows".
const title = computed(() => (signedIn.value ? `Wasabi — ssh root@${server} — 120×30` : 'Wasabi — -zsh — 120×30'))
const termOpen = computed(() => on('term') || phase.value !== 'open')
const app = computed(() => (browser.value ? 'safari' : termOpen.value ? 'terminal' : 'finder'))
const running = computed(() => ['finder', ...(termOpen.value ? ['terminal'] : []), ...(browser.value ? ['safari'] : [])])
</script>

<template>
  <MacDesktop :lang="lang" :app="app" :running="running" :pressed="phase === 'open' && between('dockClick', 'term') ? 'terminal' : undefined">
    <div class="mac-term" data-tp="console" :class="{ 'is-open': termOpen, 'is-static': phase !== 'open', 'is-behind': browser }">
      <div class="mac-titlebar">
        <span class="mac-lights" :class="{ 'is-idle': browser }"><i /><i /><i /></span>
        <span class="mac-title"><TpIcon name="folder" :size="13" />{{ title }}</span>
      </div>
      <div class="mac-term-body">
        <div v-for="(line, n) in visible" :key="n" class="mac-line">{{ line.prompt }}{{ line.text }}<i v-if="n === visible.length - 1" class="mac-cursor" /></div>
      </div>
      <span class="sc-anchor" data-tp="sc-last" :style="{ top: `${40 + visible.length * 17}px` }" />
    </div>
    <SetupBrowser mac :lang="lang" :open="browser" :address="`${server}:7300`" :pressed="between('contClick', 'assist')" :assistant="on('assist')" :picked="on('pick')" />
  </MacDesktop>
</template>
