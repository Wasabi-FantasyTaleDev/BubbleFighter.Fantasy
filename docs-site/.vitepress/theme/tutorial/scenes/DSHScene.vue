<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch, type PropType } from 'vue'
import Desktop from '../kit/Desktop.vue'
import TpIcon from '../kit/TpIcon.vue'
import Brand from '../kit/Brand.vue'
import FolderPicker from '../kit/FolderPicker.vue'
import UserPhoto from '../kit/UserPhoto.vue'
import { cues } from '../cue'
import { streamReply } from '../kit/stream'
import type { Code, Localized, ReplyBlock, Step } from '../types'
import './dsh.css'

// DeepSeek Harness. Its interface exists in Simplified Chinese and English only: Chinese pages show
// the Chinese interface and the others the English one, while prompts follow the page language.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const between = (a: string, b: string) => c.value.between(a, b)
const phase = computed(() => props.step.props?.phase as 'open' | 'workspace' | 'permission' | 'chat')
const p = computed(() => props.step.props ?? {})
const zh = computed(() => props.lang === 'zh-CN' || props.lang === 'zh-TW')

const en = {
  app: 'Application', edit: 'Edit', session: 'New Session', plugins: 'Plugins', auto: 'Automation tasks', more: 'More', spaces: 'Workspaces',
  hello: 'Into the Unknown', preview: 'Preview', mode: 'Standard mode', hint: 'Describe what you want to build, / commands, @ files or sessions',
  chatHint: 'Message or run a task, / commands, @ files or sessions', readOnly: 'Read Only', write: 'Workspace Write', full: 'Full access', add: 'Add workspace...', update: 'Update',
  dlgTitle: 'Enable Full access?', dlgBody: 'Full access reduces confirmation steps and lets the agent perform more actions directly, including sensitive operations, file changes, or external commands. Only use it when you trust the current task.',
  dlgCheck: 'I understand the risks and want to continue', cancel: 'Cancel', enable: 'Enable Full access', chat: 'Chat', trail: 'Trajectory', think: 'Think', tool: 'Pwsh',
  diving: (s: string) => `Deep diving for ${s}`, completed: (s: string) => `Completed in ${s}`, analysis: 'Analysis completed', ran: 'Ran commands, searched code, read files',
  usage: 'Usage', cache: 'Cache hit', turns: '1 turns 7 steps', loading: 'Loading plugins...', now: 'now', days: (n: number) => `${n}d`,
  secs: (s: number) => (s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`),
}
const cn: typeof en = {
  app: '应用', edit: '编辑', session: '新会话', plugins: '插件', auto: '自动化任务', more: '更多', spaces: '工作区',
  hello: '探索未至之境', preview: '预览版', mode: '标准模式', hint: '描述你想做的事，/ 调用指令，@ 文件或对话',
  chatHint: '发消息或创建任务，/ 调用指令，@ 文件或对话', readOnly: '仅可查看', write: '工作区内修改', full: '完全权限', add: '添加工作区...', update: '新版本',
  dlgTitle: '确认启用完全权限？', dlgBody: '启用完全权限后，智能体将减少确认步骤，并且可以直接执行更多操作，包括敏感操作、文件修改或外部命令。仅建议在你信任当前任务时使用。',
  dlgCheck: '我已了解风险，并愿意继续', cancel: '取消', enable: '启用完全权限', chat: '对话', trail: '轨迹', think: '思考', tool: '运行命令',
  diving: s => `深度求索中，用时 ${s}`, completed: s => `已完成，用时 ${s}`, analysis: '分析完成', ran: '已运行命令、搜索代码、读取文件',
  usage: '用量', cache: '缓存命中', turns: '1 轮 7 步', loading: '正在加载插件...', now: '刚刚', days: n => `${n}天`,
  secs: s => (s >= 60 ? `${Math.floor(s / 60)} 分 ${s % 60} 秒` : `${s} 秒`),
}
const u = computed(() => (zh.value ? cn : en))

// --- state per phase ----------------------------------------------------------------------------
const loading = computed(() => phase.value === 'open' && !on('loaded'))
const wsMenu = computed(() => phase.value === 'workspace' && between('wsMenu', 'dialog'))
const folderDialog = computed(() => phase.value === 'workspace' && between('dialog', 'dialogClose'))
const folderName = computed(() => c.value.typed('BubbleFighterFantasy', 'nameStart', 'nameEnd'))
const added = computed(() => phase.value === 'permission' || phase.value === 'chat' || (phase.value === 'workspace' && on('dialogClose')))
const permMenu = computed(() => phase.value === 'permission' && between('permMenu', 'permDialog'))
const permDialog = computed(() => phase.value === 'permission' && between('permDialog', 'allow'))
const full = computed(() => phase.value === 'chat' || (phase.value === 'permission' && on('allow')))

const prompt = computed(() => (p.value.prompt ? c.value.l(p.value.prompt as Localized) : ''))
const typedPrompt = computed(() => (phase.value === 'chat' ? c.value.typed(prompt.value, 'typeStart', 'typeEnd') : ''))
const chat = computed(() => phase.value === 'chat' && on('send'))
const done = computed(() => on('done'))
// The real install takes minutes; the counter runs four times faster than the animation.
const elapsed = computed(() => {
  const end = done.value ? c.value.time('done') : props.t
  return u.value.secs(Math.max(1, Math.round(((end - c.value.time('send')) / 1000) * 4)))
})
const reply = computed(() => streamReply((p.value.reply as ReplyBlock[] | undefined) ?? [], props.t, props.lang))
const tokens = computed(() => (done.value ? '35.8K' : on('respond') ? `${Math.min(35, 10 + Math.floor((props.t - c.value.time('respond')) / 700))}K` : '10K'))

const workspaces = computed(() => [
  { name: 'Notes', sessions: [[zh.value ? '整理本周会议纪要' : 'Weekly meeting notes', u.value.days(4)]] },
  { name: 'Projects', sessions: [[zh.value ? '做个今日待办清单' : 'A to-do list for today', u.value.days(5)]] },
  { name: 'temp', sessions: [[zh.value ? '解释这个报错' : 'Explain this error', u.value.days(7)]] },
  { name: 'Website', sessions: [[zh.value ? '首页改成深色主题' : 'Dark theme for the home page', u.value.days(6)]] },
])

// Keep the newest output in view the way a chat scrolls, with an eased glide instead of jumps;
// the prompt box keeps its height and scrolls its own text the same way.
const thread = ref<HTMLElement>()
const threadInner = ref<HTMLElement>()
const scroll = ref(0)
const inputBox = ref<HTMLElement>()
const inputText = ref<HTMLElement>()
const inputScroll = ref(0)
let observer: ResizeObserver | undefined
onMounted(() => {
  observer = new ResizeObserver(() => {
    if (inputBox.value && inputText.value) inputScroll.value = Math.max(0, inputText.value.offsetHeight - inputBox.value.clientHeight)
    if (thread.value && threadInner.value) scroll.value = Math.max(0, threadInner.value.offsetHeight - thread.value.clientHeight + 24)
  })
  if (inputText.value) observer.observe(inputText.value)
})
watch(threadInner, (element, previous) => {
  if (previous) observer?.unobserve(previous)
  if (element) observer?.observe(element)
})
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <Desktop :apps="['agent']" show-agent brand="dsh" :lang="lang">
    <div class="tp-win ds-app" :class="{ 'is-open': phase !== 'open' || on('open'), 'is-static': phase !== 'open' }">
      <div class="ds-titlebar">
        <TpIcon name="sidebar" :size="16" /><span>{{ u.app }}</span><span>{{ u.edit }}</span>
        <span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span>
      </div>

      <aside class="ds-side">
        <span class="ds-logo"><Brand name="dsh" :size="24" tone="#1a1a1a" />deepseek<em>HARNESS</em></span>
        <span class="ds-new"><TpIcon name="plusCircle" :size="15" />{{ u.session }}</span>
        <nav class="ds-nav"><span><TpIcon name="puzzle" :size="16" />{{ u.plugins }}</span><span><TpIcon name="clock" :size="16" />{{ u.auto }}</span><span><TpIcon name="grid" :size="16" />{{ u.more }}</span></nav>
        <div class="ds-group">{{ u.spaces }}<span><TpIcon name="search" :size="15" /><TpIcon name="filter" :size="15" /><TpIcon name="folder" :size="15" /></span></div>
        <div class="ds-spaces">
          <template v-if="added">
            <span class="ds-space is-new is-current"><TpIcon name="folder" :size="15" />BubbleFighterFantasy</span>
            <span v-if="chat" class="ds-session is-new is-active"><i v-if="!done" class="ds-spin" />{{ c.l(p.taskTitle as Localized) }}<small>{{ u.now }}</small></span>
          </template>
          <template v-for="space in workspaces" :key="space.name">
            <span class="ds-space"><TpIcon name="folder" :size="15" />{{ space.name }}</span>
            <span v-for="[title, age] in space.sessions" :key="title" class="ds-session">{{ title }}<small>{{ age }}</small></span>
          </template>
        </div>
        <div class="ds-user"><span class="ds-avatar"><UserPhoto :size="24"><TpIcon name="bot" :size="15" /></UserPhoto></span><b>Wasabi</b><!-- The app also shows an Update button here; left out so the tutorial does not suggest updating. <span class="ds-update"><TpIcon name="download" :size="13" />{{ u.update }}</span> --></div>
      </aside>

      <main class="ds-main">
        <span class="ds-corner"><TpIcon name="panel" :size="16" /></span>
        <!-- Home -->
        <div class="ds-home" :class="{ 'is-gone': chat }">
          <h2 class="ds-hello"><Brand name="dsh" :size="40" tone="#1a1a1a" />{{ u.hello }}<em>{{ u.preview }}</em></h2>
          <div class="ds-pills">
            <span class="ds-pill" data-tp="ds-ws" :class="{ 'is-open': wsMenu }"><TpIcon name="folder" :size="14" /><Transition name="tp-swap" mode="out-in"><span :key="String(added)">{{ added ? 'BubbleFighterFantasy' : 'Notes' }}</span></Transition><TpIcon name="chevronDown" :size="11" /></span>
            <span class="ds-pill"><TpIcon name="loop" :size="14" />{{ u.mode }}<TpIcon name="chevronDown" :size="11" /></span>
          </div>
          <div class="ds-composer" data-tp="composer" :class="{ 'is-focus': on('focus') }">
            <div ref="inputBox" class="ds-input">
              <span v-if="!typedPrompt" class="ds-hint"><i v-if="!on('focus')" class="tp-caret is-soft" />{{ u.hint }}</span>
              <span ref="inputText" class="ds-typed" :style="{ transform: `translateY(${-inputScroll}px)` }">{{ typedPrompt }}<i v-if="on('focus') && !chat" class="tp-caret" /></span>
            </div>
            <div class="ds-row">
              <span class="ds-round"><TpIcon name="plus" :size="16" /></span>
              <span class="ds-perm" data-tp="ds-perm" :class="{ 'is-open': permMenu, 'is-full': full }"><TpIcon :name="full ? 'shield' : 'shieldCheck'" :size="14" /><Transition name="tp-swap" mode="out-in"><span :key="String(full)">{{ full ? u.full : u.write }}</span></Transition><TpIcon :name="permMenu ? 'chevronUp' : 'chevronDown'" :size="11" /></span>
              <span class="wb-spacer" />
              <span class="ds-model">DeepSeek V4.1 Flash <small>Default</small><TpIcon name="chevronDown" :size="11" /></span>
              <span class="ds-send" data-tp="send" :class="{ 'is-ready': typedPrompt, 'is-pressed': between('sendPress', 'send') }"><TpIcon name="arrowUp" :size="17" :stroke="2.2" /></span>
            </div>
          </div>

          <!-- Workspace menu -->
          <div class="ds-pop ds-wsmenu" :class="{ 'is-open': wsMenu }">
            <span v-for="space in ['Notes', 'Projects', 'temp', 'Website', 'Games', 'Work']" :key="space"><TpIcon name="folder" :size="14" />{{ space }}<TpIcon v-if="space === 'Notes'" name="check" :size="13" /></span>
            <hr>
            <span data-tp="ds-ws-add" :class="{ 'is-hover': on('wsHover') }"><TpIcon name="plus" :size="14" />{{ u.add }}</span>
          </div>

          <!-- Permission menu -->
          <div class="ds-pop ds-permmenu" :class="{ 'is-open': permMenu }">
            <span><TpIcon name="shieldCheck" :size="14" />{{ u.readOnly }}</span>
            <span><TpIcon name="shieldCheck" :size="14" />{{ u.write }}<TpIcon name="check" :size="13" /></span>
            <span data-tp="ds-perm-full" :class="{ 'is-hover': on('permHover') }"><TpIcon name="shield" :size="14" />{{ u.full }}</span>
          </div>
        </div>

        <!-- Conversation -->
        <div v-if="chat" class="ds-chat">
          <header class="ds-chat-head">
            <div><b>{{ c.l(p.taskTitle as Localized) }}</b><small><TpIcon name="loop" :size="13" />{{ u.mode }}</small><span class="ds-chat-tools"><span class="ds-folder-btn"><TpIcon name="folder" :size="15" /><TpIcon name="chevronDown" :size="11" /></span><TpIcon name="more" :size="16" :stroke="3" /><TpIcon name="panel" :size="16" /></span></div>
            <nav><b>{{ u.chat }}</b><span>{{ u.trail }}</span><span>···</span></nav>
          </header>
          <div ref="thread" class="ds-thread">
            <div ref="threadInner" class="ds-thread-inner" :style="{ transform: `translateY(${-scroll}px)` }">
              <div class="ds-user-msg">{{ prompt }}</div>
              <div class="ds-msg-meta">16:22<TpIcon name="copy" :size="14" /></div>
              <div class="ds-agent">
                <Transition name="tp-swap"><div v-if="done" class="ds-completed">{{ u.completed(elapsed) }}<TpIcon name="chevronUp" :size="13" /></div></Transition>
                <div v-if="on('think')" class="ds-step-row is-think"><TpIcon name="sparkle" :size="14" /><b>{{ done ? u.analysis : u.think }}</b><template v-if="!done"> · <span>{{ c.l(p.thought as Localized) }}</span></template></div>
                <template v-for="part in reply" :key="part.n">
                  <div v-if="part.kind === 'tool'" class="ds-step-row" :class="{ 'is-running': part.running }"><TpIcon name="terminal" :size="14" /><b>{{ u.tool }}</b> · <span>{{ part.summary }}</span></div>
                  <div v-else class="wb-md ds-md">
                    <template v-for="(block, i) in part.blocks" :key="i">
                      <component :is="block.kind === 'h' ? 'h4' : block.kind === 'li' ? 'li' : block.kind === 'pre' ? 'pre' : 'p'">
                        <template v-for="(inline, j) in block.parts" :key="j"><b v-if="inline.kind === 'bold'">{{ inline.text }}</b><code v-else-if="inline.kind === 'code'">{{ inline.text }}</code><a v-else-if="inline.kind === 'link'">{{ inline.text }}</a><template v-else>{{ inline.text }}</template></template><i v-if="part.streaming && i === part.blocks.length - 1" class="wb-stream-dot" />
                      </component>
                    </template>
                  </div>
                </template>
                <div v-if="!done" class="ds-diving"><TpIcon name="arrowUpRight" :size="13" /><span>{{ u.diving(elapsed) }} ···</span></div>
                <div v-else class="ds-actions"><TpIcon name="copy" :size="15" /><TpIcon name="thumbUp" :size="15" /><TpIcon name="thumbDown" :size="15" /><TpIcon name="share" :size="15" /><span><TpIcon name="book" :size="14" />{{ u.usage }} 35.8K tok</span><span>16:26</span></div>
              </div>
            </div>
          </div>
          <div class="ds-composer is-bottom">
            <div class="ds-input"><span class="ds-hint">{{ u.chatHint }}</span></div>
            <div class="ds-row">
              <span class="ds-round"><TpIcon name="plus" :size="16" /></span>
              <span class="ds-perm is-full"><TpIcon name="shield" :size="14" />{{ u.full }}<TpIcon name="chevronDown" :size="11" /></span>
              <span class="wb-spacer" />
              <span class="ds-model">DeepSeek V4.1 Flash <small>Default</small><TpIcon name="chevronDown" :size="11" /></span>
              <span class="ds-send" :class="{ 'is-stop': !done }"><TpIcon :name="done ? 'arrowUp' : 'stop'" :size="15" :stroke="2.2" :fill="!done" /></span>
            </div>
          </div>
          <div class="ds-stats"><span v-if="done"><TpIcon name="clock" :size="13" />{{ u.turns }} · 97 tok/s</span><span><TpIcon name="book" :size="13" />{{ tokens }} tok · {{ u.cache }} {{ done ? '89' : '92' }}%</span><span><i class="ds-ring" />1%</span></div>
        </div>
      </main>

      <!-- Loading screen while plugins load -->
      <div class="ds-loading" :class="{ 'is-gone': !loading }"><b>HARNESS</b><i class="ds-spin is-big" /><small>{{ u.loading }}</small></div>

      <FolderPicker v-if="folderDialog" :lang="lang" :new-pressed="between('newPress', 'newFolder')" :created="on('newFolder')" :typed="folderName" :named="on('nameEnd')" :ok-pressed="between('okPress', 'dialogClose')"><template #icon><Brand name="dsh" :size="16" tone="#4d6bfe" /></template></FolderPicker>

      <!-- Full access confirmation -->
      <div class="ds-mask" :class="{ 'is-open': permDialog }">
        <div class="ds-dialog">
          <header><b>{{ u.dlgTitle }}</b><TpIcon name="close" :size="16" /></header>
          <p><TpIcon name="alert" :size="17" /><span>{{ u.dlgBody }}</span></p>
          <label class="ds-check"><i data-tp="ds-check" :class="{ 'is-on': on('check') }"><TpIcon name="check" :size="12" :stroke="3" /></i>{{ u.dlgCheck }}</label>
          <footer><span>{{ u.cancel }}</span><span class="ds-enable" data-tp="ds-enable" :class="{ 'is-ready': on('check'), 'is-pressed': between('allowPress', 'allow') }">{{ u.enable }}</span></footer>
        </div>
      </div>
    </div>
  </Desktop>
</template>
