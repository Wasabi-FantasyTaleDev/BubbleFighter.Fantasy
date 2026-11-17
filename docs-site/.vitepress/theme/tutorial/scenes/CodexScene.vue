<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch, type PropType } from 'vue'
import Desktop from '../kit/Desktop.vue'
import UserPhoto from '../kit/UserPhoto.vue'
import TpIcon from '../kit/TpIcon.vue'
import Brand from '../kit/Brand.vue'
import FolderPicker from '../kit/FolderPicker.vue'
import { cues } from '../cue'
import { streamReply } from '../kit/stream'
import type { Code, Localized, ReplyBlock, Step } from '../types'
import './codex.css'

// The ChatGPT desktop app and its Codex side. Prompts and the agent's answer follow the page language.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const between = (a: string, b: string) => c.value.between(a, b)
const phase = computed(() => props.step.props?.phase as 'open' | 'switch' | 'workspace' | 'permission' | 'chat')
const p = computed(() => props.step.props ?? {})
const name = 'BubbleFighterFantasy'

// The app's own wording in each page language, taken from the app's own interfaces.
const en = {
  file: 'File', edit: 'Edit', view: 'View', help: 'Help', newChat: 'New chat', projects: 'Projects', noChats: 'No chats',
  chatTab: 'Chat', workTab: 'Work', hello: 'What’s on your mind today?', ask: 'Ask ChatGPT', instant: 'Instant',
  ideas: ['Help me organize my notes so I can find things fast', 'Help me prepare for my next meeting'],
  chatgptSub: 'Create, learn, and explore', codexSub: 'Build, debug, and ship',
  build: 'What should we build?', workOn: ['What should we work on in ', '?'],
  computer: 'This computer', choose: 'Choose project', anything: 'Do anything', full: 'Full access', medium: 'Medium',
  search: 'Search projects', newProject: 'New project', create: 'Create project', projectName: 'Project name', sources: 'Source folders',
  addOn: ['Add a folder on ', 'this computer', ''], add: 'Add', thisPc: 'This computer', primary: 'Primary', addFolder: 'Add folder', cancel: 'Cancel',
  trustTitle: 'Trust this folder?', trustBody: 'ChatGPT can read, edit, and run files in these folders. Folder settings can also run code automatically, even without a model request. Continue only if you trust every folder listed.', trust: 'Trust folders',
  starting: 'Starting chat', thinking: 'Thinking', workWith: 'Work with Codex', running: 'Running', ran: 'Ran',
  worked: (s: string) => `Worked for ${s}`, time: (s: number) => (s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`),
  permAsk: 'How should ChatGPT actions be approved?', learn: 'Learn more',
  modes: [['Ask for approval', 'Always ask before editing outside files and using the internet'], ['Approve for me', 'Only ask for actions detected as possibly unsafe'], ['Full access', 'Unrestricted access to the internet and any file on your computer']],
  fullTitle: 'Turn on Full Access?', fullBody: 'Codex will be able to run commands, use the internet, and create and edit files anywhere on this computer without your permission. This includes but is not limited to:',
  fullItems: [['Files and folders', 'Read, create, modify, upload, or delete files anywhere on this computer'], ['Terminal commands', 'Run commands, install software, and change system settings'], ['Internet and connected apps', 'Access websites, send data, and use enabled plugins']],
  fullRisk: 'This comes with risks like loss or exposure of sensitive data and prompt injection. You can turn this off.', confirm: 'Confirm',
}
const zh: typeof en = {
  file: '文件', edit: '编辑', view: '视图', help: '帮助', newChat: '新聊天', projects: '项目', noChats: '暂无聊天',
  chatTab: '聊天', workTab: '工作', hello: '有什么可以帮忙的？', ask: '询问任何问题', instant: '即时',
  ideas: ['帮我整理笔记，方便查找所需内容', '帮我准备下一场会议，梳理关键讨论要点'],
  chatgptSub: '创作、学习和探索', codexSub: '构建、调试和发布',
  build: '我们要构建什么？', workOn: ['我们应该在', '中做些什么？'],
  computer: '此计算机', choose: '选择项目', anything: '随心输入', full: '完全访问', medium: '中',
  search: '搜索项目', newProject: '新建项目', create: '创建项目', projectName: '项目名称', sources: '源文件夹',
  addOn: ['在', '此电脑', '上添加文件夹'], add: '添加', thisPc: '此电脑', primary: '主要', addFolder: '添加文件夹', cancel: '取消',
  trustTitle: '信任此文件夹吗？', trustBody: 'ChatGPT 可以读取、编辑和运行这些文件夹中的文件。即使模型未发出请求，文件夹设置也可能触发代码自动运行。请仅在你信任列出的每个文件夹时继续。', trust: '信任文件夹',
  starting: '正在创建聊天', thinking: '正在思考', workWith: '和 Codex 一起工作', running: '正在运行', ran: '已运行',
  worked: s => `用时 ${s}`, time: s => (s >= 60 ? `${Math.floor(s / 60)}分钟 ${s % 60}秒` : `${s}秒`),
  permAsk: 'ChatGPT 操作应如何核准？', learn: '了解更多',
  modes: [['要求核准', '一律先询问是否要编辑外部文件及使用互联网'], ['代我核准', '仅对检测为可能不安全的操作要求核准'], ['完全访问', '可无限制访问互联网和你电脑上的任何文件']],
  fullTitle: '要开启完整访问权限吗？', fullBody: 'Codex 将能够在未经您许可的情况下，在这台计算机上的任何位置运行命令、使用互联网，以及创建和编辑文件。这包括但不限于：',
  fullItems: [['文件和文件夹', '读取、创建、修改、上传或删除此计算机上任意位置的文件'], ['终端命令', '运行命令、安装软件和更改系统设置'], ['互联网和已连接的应用', '访问网站、发送数据并使用已启用的插件']],
  fullRisk: '这会带来敏感数据丢失或泄露、提示注入等风险。你可以将其关闭。', confirm: '确认',
}
const tw: typeof en = {
  file: '檔案', edit: '編輯', view: '檢視', help: '說明', newChat: '新對話', projects: '專案', noChats: '沒有對話',
  chatTab: '聊天', workTab: '工作', hello: '有什麼可以幫忙的？', ask: '詢問任何問題', instant: '即時',
  ideas: ['幫我整理筆記，方便查找需要的內容', '幫我準備下一場會議，整理重點討論事項'],
  chatgptSub: '建立、了解與探索', codexSub: '建置、偵錯與發布',
  build: '我們要建置什麼？', workOn: ['我們該在', '中進行什麼作業？'],
  computer: '這台電腦', choose: '選擇專案', anything: '想做什麼都可以', full: '完整存取權', medium: '中',
  search: '搜尋專案', newProject: '新專案', create: '建立專案', projectName: '專案名稱', sources: '來源資料夾',
  addOn: ['在', '這台電腦', '上新增資料夾'], add: '新增', thisPc: '這台電腦', primary: '主要', addFolder: '新增資料夾', cancel: '取消',
  trustTitle: '要信任這個資料夾嗎？', trustBody: 'ChatGPT 可以讀取、編輯及執行這些資料夾中的檔案。即使模型未發出請求，資料夾設定也能自動執行程式碼。請僅在你信任列出的所有資料夾時繼續。', trust: '信任資料夾',
  starting: '正在建立對話', thinking: '正在思考', workWith: '與 Codex 協作', running: '正在執行', ran: '已執行',
  worked: s => `用時 ${s}`, time: s => (s >= 60 ? `${Math.floor(s / 60)}分鐘 ${s % 60}秒` : `${s}秒`),
  permAsk: 'ChatGPT 動作應如何核准？', learn: '了解更多',
  modes: [['要求核准', '一律先詢問是否要編輯外部檔案及使用網際網路'], ['代我核准', '僅對偵測為可能不安全的操作要求核准'], ['完整存取權', '可無限制存取網際網路和您電腦上的任何檔案']],
  fullTitle: '要開啟完整存取權限嗎？', fullBody: 'Codex 將能在未經你許可的情況下執行指令、使用網際網路，並在這台電腦上的任何位置建立和編輯檔案。這包括但不限於：',
  fullItems: [['檔案和資料夾', '讀取、建立、修改、上傳或刪除這台電腦任何位置的檔案'], ['終端機指令', '執行指令、安裝軟體及變更系統設定'], ['網際網路和已連結的應用程式', '存取網站、傳送資料，並使用已啟用的外掛程式']],
  fullRisk: '這會帶來敏感資料遺失或外洩、提示注入等風險。你可以將此關閉。', confirm: '確認',
}
const ko: typeof en = {
  file: '파일', edit: '편집', view: '보기', help: '도움말', newChat: '새 채팅', projects: '프로젝트', noChats: '채팅 없음',
  chatTab: '채팅', workTab: '작업', hello: '무엇을 도와드릴까요?', ask: '무엇이든 물어보세요', instant: '즉시',
  ideas: ['필요한 내용을 빨리 찾도록 메모를 정리해 줘', '다음 회의 준비와 핵심 논의 사항 정리를 도와줘'],
  chatgptSub: '만들고, 배우고, 탐색하세요', codexSub: '빌드하고, 디버깅하고, 배포하세요',
  build: '무엇을 만들까요?', workOn: ['', '에서 무엇을 작업할까요?'],
  computer: '이 컴퓨터', choose: '프로젝트 선택', anything: '무엇이든 요청하세요', full: '전체 액세스', medium: 'Medium',
  search: '프로젝트 검색', newProject: '새 프로젝트', create: '프로젝트 만들기', projectName: '프로젝트 이름', sources: '소스 폴더',
  addOn: ['', '이 컴퓨터', '에 폴더 추가'], add: '추가', thisPc: '이 컴퓨터', primary: '기본', addFolder: '폴더 추가', cancel: '취소',
  trustTitle: '이 폴더를 신뢰할까요?', trustBody: 'ChatGPT는 이 폴더들에 있는 파일을 읽고 편집하고 실행할 수 있습니다. 또한 폴더 설정에 따라 모델의 요청 없이도 코드가 자동으로 실행될 수 있습니다. 목록에 있는 모든 폴더를 신뢰하는 경우에만 계속 진행하세요.', trust: '폴더 신뢰',
  starting: '채팅 시작 중', thinking: '생각 중', workWith: 'Codex와 함께 작업하세요', running: '실행 중', ran: '실행함',
  worked: s => `${s} 동안 작업함`, time: s => (s >= 60 ? `${Math.floor(s / 60)}분 ${s % 60}초` : `${s}초`),
  permAsk: 'ChatGPT 작업을 어떻게 승인할까요?', learn: '자세히 알아보기',
  modes: [['승인 요청', '외부 파일을 편집하거나 인터넷을 사용하기 전에 항상 묻습니다'], ['자동 승인', '안전하지 않을 수 있는 작업만 승인을 요청합니다'], ['전체 액세스', '인터넷과 컴퓨터의 모든 파일에 제한 없이 액세스합니다']],
  fullTitle: '전체 액세스를 켤까요?', fullBody: 'Codex는 사용자의 허가 없이 이 컴퓨터 어디서나 명령을 실행하고 인터넷을 사용하며 파일을 만들고 편집할 수 있습니다. 여기에는 다음이 포함되지만 이에 국한되지 않습니다.',
  fullItems: [['파일 및 폴더', '이 컴퓨터의 모든 위치에서 파일을 읽고, 작성하고, 수정하고, 업로드하거나 삭제합니다'], ['터미널 명령어', '명령을 실행하고, 소프트웨어를 설치하고, 시스템 설정을 변경합니다'], ['인터넷 및 연결된 앱', '웹사이트에 액세스하고, 데이터를 보내고, 활성화된 플러그인을 사용합니다']],
  fullRisk: '이 경우 민감한 데이터의 손실 또는 노출, 프롬프트 인젝션 같은 위험이 따릅니다. 이 기능을 끌 수 있습니다.', confirm: '확인',
}
const u = computed(() => ({ 'zh-CN': zh, 'zh-TW': tw, ko, en })[props.lang] ?? en)

const codex = computed(() => phase.value === 'workspace' || phase.value === 'permission' || phase.value === 'chat' || (phase.value === 'switch' && on('switch')))
const appMenu = computed(() => phase.value === 'switch' && between('menu', 'switch'))
const projMenu = computed(() => phase.value === 'workspace' && between('projMenu', 'dialog'))
const createDialog = computed(() => phase.value === 'workspace' && between('dialog', 'ready'))
const projectName = computed(() => c.value.typed(name, 'nameStart', 'nameEnd'))
const picker = computed(() => phase.value === 'workspace' && between('picker', 'pickerClose'))
const folderName = computed(() => c.value.typed(name, 'fnStart', 'fnEnd'))
const folderAdded = computed(() => phase.value === 'workspace' && on('pickerClose'))
const trust = computed(() => phase.value === 'workspace' && between('trust', 'ready'))
const project = computed(() => phase.value === 'permission' || phase.value === 'chat' || (phase.value === 'workspace' && on('ready')))
// Permission: the default asks for approval; the permission step turns on full access.
const permMenu = computed(() => phase.value === 'permission' && between('permMenu', 'permDialog'))
const permDialog = computed(() => phase.value === 'permission' && between('permDialog', 'allow'))
const full = computed(() => phase.value === 'chat' || (phase.value === 'permission' && on('allow')))

const prompt = computed(() => (p.value.prompt ? c.value.l(p.value.prompt as Localized) : ''))
const typedPrompt = computed(() => (phase.value === 'chat' ? c.value.typed(prompt.value, 'typeStart', 'typeEnd') : ''))
const sending = computed(() => phase.value === 'chat' && between('send', 'chat'))
const chat = computed(() => phase.value === 'chat' && on('chat'))
const done = computed(() => on('done'))
const status = computed(() => (on('think') ? u.value.thinking : u.value.starting))
const reply = computed(() => streamReply((p.value.reply as ReplyBlock[] | undefined) ?? [], props.t, props.lang))
const replyStarted = computed(() => reply.value.length > 0)
// The real install takes minutes; the counter runs four times faster than the animation.
const worked = computed(() => {
  return u.value.time(Math.max(1, Math.round(((c.value.time('done') - c.value.time('chat')) / 1000) * 4)))
})
const projects = [
  { name: 'Notes', chats: ['Weekly meeting notes', 'Plan a team offsite'] },
  { name: 'Website', chats: [] },
  { name: 'Photos', chats: [] },
  { name: 'Projects', chats: ['Fix the login page', 'Explain this error', 'Dark theme for the home page'] },
]

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
watch([threadInner, inputText], (elements, previous) => {
  for (const element of previous ?? []) if (element) observer?.unobserve(element)
  for (const element of elements) if (element) observer?.observe(element)
})
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <Desktop :apps="['agent']" show-agent brand="chatgpt" :lang="lang">
    <div class="tp-win cx-app" :class="{ 'is-open': phase !== 'open' || on('open'), 'is-static': phase !== 'open' }">
      <div class="cx-titlebar">
        <TpIcon name="back" :size="15" /><TpIcon name="forward" :size="15" class="is-dim" /><TpIcon name="sidebar" :size="15" />
        <span>{{ u.file }}</span><span>{{ u.edit }}</span><span>{{ u.view }}</span><span>{{ u.help }}</span>
        <span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span>
      </div>

      <aside class="cx-rail">
        <span class="is-on"><TpIcon name="home" :size="17" /><i /></span><span><TpIcon name="palette" :size="17" /></span><span><TpIcon name="clock" :size="17" /></span><span><TpIcon name="book" :size="17" /></span><span><TpIcon name="globe" :size="17" /></span><span><TpIcon name="more" :size="17" :stroke="3" /></span>
        <hr><span><TpIcon name="grid" :size="17" /></span>
        <span class="cx-rail-end"><TpIcon name="loop" :size="17" /></span><UserPhoto :size="22"><b>W</b></UserPhoto>
      </aside>

      <aside class="cx-side">
        <div class="cx-side-head">
          <span class="cx-app-btn" data-tp="cx-app" :class="{ 'is-open': appMenu }"><Transition name="tp-swap" mode="out-in"><span :key="String(codex)">{{ codex ? 'Codex' : 'ChatGPT' }}</span></Transition><TpIcon name="chevronDown" :size="13" /></span>
          <span class="cx-bell"><TpIcon name="bell" :size="16" /><i /></span><TpIcon name="search" :size="16" />
        </div>
        <span class="cx-newchat"><TpIcon name="chatPlus" :size="16" />{{ u.newChat }}</span>
        <div class="cx-label">{{ u.projects }}</div>
        <div class="cx-projects">
          <template v-if="project">
            <span class="cx-project is-new" :class="{ 'is-current': !chat }"><TpIcon name="folder" :size="15" />{{ name }}</span>
            <span v-if="chat" class="cx-chat-item is-new is-current">{{ c.l(p.taskTitle as Localized) }}<i v-if="!done" class="cx-spin" /><i v-else class="cx-unread" /></span>
          </template>
          <template v-for="item in projects" :key="item.name">
            <span class="cx-project"><TpIcon name="folder" :size="15" />{{ item.name }}</span>
            <span v-for="title in item.chats" :key="title" class="cx-chat-item">{{ title }}</span>
            <span v-if="!item.chats.length" class="cx-chat-item is-empty">{{ u.noChats }}</span>
          </template>
        </div>

        <!-- ChatGPT / Codex switch -->
        <div class="cx-pop cx-appmenu" :class="{ 'is-open': appMenu }">
          <span><b>ChatGPT</b><small>{{ u.chatgptSub }}</small><TpIcon name="check" :size="15" /></span>
          <span data-tp="cx-codex" :class="{ 'is-hover': on('hoverCodex') }"><b>Codex</b><small>{{ u.codexSub }}</small></span>
        </div>
      </aside>

      <main class="cx-main">
        <span class="cx-corner"><TpIcon name="panel" :size="16" /></span>
        <!-- ChatGPT home -->
        <div v-if="!codex" class="cx-home is-chatgpt" :class="{ 'is-leaving': appMenu && on('pressCodex') }">
          <div class="cx-seg"><b>{{ u.chatTab }}</b><span>{{ u.workTab }}</span></div>
          <h2>{{ u.hello }}</h2>
          <div class="cx-ask"><TpIcon name="plus" :size="18" /><span>{{ u.ask }}</span><small>{{ u.instant }}<TpIcon name="chevronDown" :size="12" /></small><TpIcon name="mic" :size="17" /><span class="cx-voice"><i /><i /><i /><i /></span></div>
          <div class="cx-ideas"><span><TpIcon name="book" :size="15" />{{ u.ideas[0] }}</span><span><TpIcon name="clock" :size="15" />{{ u.ideas[1] }}</span></div>
        </div>

        <!-- Codex home and composer -->
        <div v-else class="cx-home is-codex" :class="{ 'is-gone': chat }">
          <div class="cx-hero">
            <Brand name="codex" :size="58" tone="#999" />
            <Transition name="tp-swap" mode="out-in">
              <h2 :key="String(project)"><template v-if="project">{{ u.workOn[0] }}<u>{{ name }}</u>{{ u.workOn[1] }}</template><template v-else>{{ u.build }}</template></h2>
            </Transition>
          </div>
        </div>
        <div v-if="codex" class="cx-dock" :class="{ 'is-chat': chat }">
          <div class="cx-tray">
            <span><TpIcon name="desktop" :size="15" />{{ u.computer }}</span>
            <span class="cx-choose" data-tp="cx-project" :class="{ 'is-open': projMenu, 'is-set': project }"><TpIcon name="folder" :size="15" /><Transition name="tp-swap" mode="out-in"><span :key="String(project)">{{ project ? name : u.choose }}</span></Transition></span>
          </div>
          <div class="cx-composer" data-tp="composer" :class="{ 'is-focus': on('focus') && !chat }">
            <div ref="inputBox" class="cx-input">
              <span v-if="!typedPrompt || chat" class="cx-hint">{{ chat ? (on('think') ? u.workWith : u.starting) : u.anything }}</span>
              <span v-if="!chat" ref="inputText" class="cx-typed" :style="{ transform: `translateY(${-inputScroll}px)` }">{{ typedPrompt }}<i v-if="on('focus') && !on('send')" class="tp-caret" /></span>
            </div>
            <div class="cx-row">
              <TpIcon name="plus" :size="18" />
              <span class="cx-full" data-tp="cx-perm" :class="{ 'is-full': full, 'is-open': permMenu }"><TpIcon :name="full ? 'alert' : 'shieldCheck'" :size="15" /><Transition name="tp-swap" mode="out-in"><span :key="String(full)">{{ full ? u.full : u.modes[0][0] }}</span></Transition></span>
              <span class="wb-spacer" />
              <span class="cx-model">GPT-6.1 Sol <small>{{ u.medium }}</small><TpIcon name="chevronDown" :size="12" /></span>
              <TpIcon name="mic" :size="17" />
              <span class="cx-send" data-tp="send" :class="{ 'is-pressed': between('sendPress', 'send') }">
                <Transition name="tp-fade" mode="out-in">
                  <i v-if="sending" key="spin" class="cx-spin is-light" />
                  <TpIcon v-else-if="chat && !done" key="stop" name="stop" :size="14" :fill="true" />
                  <TpIcon v-else-if="typedPrompt && !chat" key="up" name="arrowUp" :size="17" :stroke="2.2" />
                  <span v-else key="voice" class="cx-voice is-small"><i /><i /><i /><i /></span>
                </Transition>
              </span>
            </div>
          </div>

          <!-- Permission menu -->
          <div class="cx-pop cx-permmenu" :class="{ 'is-open': permMenu }">
            <header>{{ u.permAsk }}<u>{{ u.learn }}</u></header>
            <span v-for="(mode, n) in u.modes" :key="mode[0]" :data-tp="n === 2 ? 'cx-perm-full' : undefined" :class="{ 'is-full': n === 2, 'is-hover': n === 2 && on('permHover') }">
              <TpIcon :name="['shieldCheck', 'terminal', 'alert'][n]" :size="16" /><b>{{ mode[0] }}<small>{{ mode[1] }}</small></b><TpIcon v-if="n === 0" name="check" :size="15" />
            </span>
          </div>

          <!-- Project picker -->
          <div class="cx-pop cx-projmenu" :class="{ 'is-open': projMenu }">
            <span class="cx-search"><TpIcon name="search" :size="14" />{{ u.search }}</span>
            <span v-for="item in projects" :key="item.name"><TpIcon name="folder" :size="15" />{{ item.name }}</span>
            <hr>
            <span data-tp="cx-newproj" :class="{ 'is-hover': on('newHover') }"><TpIcon name="plus" :size="15" />{{ u.newProject }}</span>
          </div>
        </div>

        <!-- Conversation -->
        <div v-if="chat" class="cx-chat">
          <header class="cx-chat-head"><TpIcon name="folder" :size="16" /><b>{{ c.l(p.taskTitle as Localized) }}</b><TpIcon name="chevronDown" :size="13" /><span><TpIcon name="share" :size="16" /><TpIcon name="view" :size="16" /><TpIcon name="panel" :size="16" /></span></header>
          <div ref="thread" class="cx-thread">
            <div ref="threadInner" class="cx-thread-inner" :style="{ transform: `translateY(${-scroll}px)` }">
              <div class="cx-user-msg">{{ prompt }}</div>
              <div class="cx-agent">
                <div v-if="!replyStarted" class="cx-status-line"><Transition name="tp-swap" mode="out-in"><span :key="status" class="cx-status">{{ status }}</span></Transition></div>
                <Transition name="tp-swap"><div v-if="done" class="cx-worked">{{ u.worked(worked) }}<TpIcon name="chevronRight" :size="13" /></div></Transition>
                <template v-for="part in reply" :key="part.n">
                  <div v-if="part.kind === 'tool'" class="cx-tool" :class="{ 'is-running': part.running }"><TpIcon name="terminal" :size="15" /><span>{{ part.running ? u.running : u.ran }} {{ part.cmd }}</span><TpIcon v-if="part.running" name="chevronRight" :size="13" /></div>
                  <div v-else class="wb-md cx-md">
                    <template v-for="(block, i) in part.blocks" :key="i">
                      <component :is="block.kind === 'h' ? 'h4' : block.kind === 'li' ? 'li' : block.kind === 'pre' ? 'pre' : 'p'">
                        <template v-for="(inline, j) in block.parts" :key="j"><b v-if="inline.kind === 'bold'">{{ inline.text }}</b><code v-else-if="inline.kind === 'code'">{{ inline.text }}</code><a v-else-if="inline.kind === 'link'">{{ inline.text }}</a><template v-else>{{ inline.text }}</template></template><i v-if="part.streaming && i === part.blocks.length - 1" class="wb-stream-dot" />
                      </component>
                    </template>
                  </div>
                </template>
                <div v-if="replyStarted && !done" class="cx-status-line"><span class="cx-status">{{ u.thinking }}</span></div>
                <div v-if="done" class="cx-actions"><TpIcon name="copy" :size="15" /><TpIcon name="thumbUp" :size="15" /><TpIcon name="speaker" :size="15" /><TpIcon name="more" :size="15" :stroke="3" /></div>
              </div>
            </div>
          </div>
        </div>
      </main>

      <!-- Create project -->
      <div class="cx-mask" :class="{ 'is-open': createDialog }">
        <div class="cx-dialog" :class="{ 'is-behind': trust }">
          <header><b>{{ u.create }}</b><TpIcon name="close" :size="16" /></header>
          <div class="cx-name" data-tp="cx-name" :class="{ 'is-focus': on('nameFocus') && !on('addPress') }"><span><TpIcon name="folder" :size="16" /></span><em v-if="!projectName">{{ u.projectName }}</em>{{ projectName }}<i v-if="between('nameFocus', 'addPress')" class="tp-caret" /></div>
          <div class="cx-src-head"><b>{{ u.sources }}</b><small v-if="folderAdded"><TpIcon name="desktop" :size="14" />{{ u.thisPc }}</small></div>
          <div class="cx-src">
            <template v-if="!folderAdded">
              <span class="cx-src-where">{{ u.addOn[0] }}<b>{{ u.addOn[1] }}</b>{{ u.addOn[2] }}<TpIcon name="chevronDown" :size="12" /></span>
              <span class="cx-add" data-tp="cx-add" :class="{ 'is-pressed': between('addPress', 'picker') }"><TpIcon name="folder" :size="14" />{{ u.add }}</span>
            </template>
            <template v-else>
              <span class="cx-src-row is-new"><TpIcon name="folder" :size="16" />{{ name }}<em class="cx-primary">{{ u.primary }}</em><TpIcon name="close" :size="14" /></span>
              <span class="cx-src-row is-add"><TpIcon name="folder" :size="16" />{{ u.addFolder }}</span>
            </template>
          </div>
          <footer><span>{{ u.cancel }}</span><span class="cx-create" data-tp="cx-create" :class="{ 'is-pressed': between('createPress', 'trust'), 'is-busy': on('trust') }"><i v-if="on('trust')" class="cx-spin is-light" />{{ u.create }}</span></footer>
        </div>
        <div class="cx-trust" :class="{ 'is-open': trust }">
          <TpIcon name="close" :size="16" />
          <b>{{ u.trustTitle }}</b>
          <code>C:\Users\Player\Documents\{{ name }}</code>
          <p>{{ u.trustBody }}</p>
          <span class="cx-trust-ok" data-tp="cx-trust" :class="{ 'is-pressed': between('trustPress', 'ready') }">{{ u.trust }}</span>
          <span class="cx-trust-cancel">{{ u.cancel }}</span>
        </div>
      </div>

      <!-- Full access confirmation -->
      <div class="cx-mask" :class="{ 'is-open': permDialog }">
        <div class="cx-dialog cx-fulldlg">
          <header><b><TpIcon name="warning" :size="19" />{{ u.fullTitle }}</b></header>
          <p>{{ u.fullBody }}</p>
          <ul>
            <li v-for="(item, n) in u.fullItems" :key="item[0]"><span :class="`is-${n}`"><TpIcon :name="['folder', 'terminal', 'globe'][n]" :size="16" :fill="n === 0" /></span><b>{{ item[0] }}<small>{{ item[1] }}</small></b></li>
          </ul>
          <p class="cx-risk">{{ u.fullRisk }} <u>{{ u.learn }}</u></p>
          <footer><span class="cx-cancel">{{ u.cancel }}</span><span class="cx-confirm" data-tp="cx-confirm" :class="{ 'is-pressed': between('confirmPress', 'allow') }"><TpIcon name="warning" :size="15" />{{ u.confirm }}</span></footer>
        </div>
      </div>

      <FolderPicker v-if="picker" :lang="lang" :new-pressed="between('newPress', 'newFolder')" :created="on('newFolder')" :typed="folderName" :named="on('fnEnd')" :ok-pressed="between('okPress', 'pickerClose')"><template #icon><Brand name="chatgpt" :size="15" tone="#111" /></template></FolderPicker>
    </div>
  </Desktop>
</template>
