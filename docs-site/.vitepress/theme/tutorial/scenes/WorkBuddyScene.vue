<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch, type PropType } from 'vue'
import Desktop from '../kit/Desktop.vue'
import UserPhoto from '../kit/UserPhoto.vue'
import TpIcon from '../kit/TpIcon.vue'
import AppMark from '../kit/AppMark.vue'
import Robot from '../kit/Robot.vue'
import FolderPicker from '../kit/FolderPicker.vue'
import { cues } from '../cue'
import { streamReply } from '../kit/stream'
import type { Code, Localized, ReplyBlock, Step } from '../types'

// Runtime declaration: the docs site compiles without TypeScript, which imported prop types need.
const props = defineProps({ t: { type: Number, required: true }, step: { type: Object as PropType<Step>, required: true }, lang: { type: String as PropType<Code>, required: true } })
const c = computed(() => cues(props))
const on = (name: string) => c.value.on(name)
const between = (a: string, b: string) => c.value.between(a, b)
const phase = computed(() => props.step.props?.phase as 'setup' | 'workspace' | 'permission' | 'chat')
const p = computed(() => props.step.props ?? {})

const dev = computed(() => phase.value !== 'setup' || on('dev'))
const modelMenu = computed(() => phase.value === 'setup' && between('menu', 'menuClose'))
const modelHover = computed(() => (on('hoverDeep') ? 'deep' : on('hoverFree') ? 'free' : ''))
const wsMenu = computed(() => phase.value === 'workspace' && between('wsMenu', 'dialog'))
const folderDialog = computed(() => phase.value === 'workspace' && between('dialog', 'dialogClose'))
const folderName = computed(() => c.value.typed('BubbleFighterFantasy', 'nameStart', 'nameEnd'))
const workspace = computed(() => (phase.value === 'permission' || phase.value === 'chat' || (phase.value === 'workspace' && on('dialogClose')) ? 'BubbleFighterFantasy' : ''))
const permPop = computed(() => phase.value === 'permission' && between('permPop', 'permDialog'))
const permDialog = computed(() => phase.value === 'permission' && between('permDialog', 'allow'))
const danger = computed(() => phase.value === 'chat' || (phase.value === 'permission' && on('allow')))

const prompt = computed(() => (p.value.prompt ? c.value.l(p.value.prompt as Localized) : ''))
const typedPrompt = computed(() => (phase.value === 'chat' ? c.value.typed(prompt.value, 'typeStart', 'typeEnd') : ''))
const chat = computed(() => phase.value === 'chat' && on('send'))
const done = computed(() => on('done'))
const status = computed(() => (on('respond') ? '等待模型响应 | 内容越短越明确，越不容易浪费上下文空间' : on('think') ? '思考中' : '正在启动对话运行环境...'))
const elapsed = computed(() => {
  // The real install takes minutes; the counter runs four times faster than the animation.
  const end = done.value ? c.value.time('done') : props.t
  const s = Math.max(1, Math.round(((end - c.value.time('send')) / 1000) * 4))
  return s >= 60 ? `${Math.floor(s / 60)}m ${s % 60}s` : `${s}s`
})

const models = [
  { name: '均衡', price: '0.50x', icon: 'check' },
  { name: '极致', price: '1.00x', icon: 'sparkle' },
  { name: 'Hy4', price: '0.29x', icon: 'loop' },
  { name: 'Hy4 flash', price: '0.00x', icon: 'loop', tag: '限时免费', id: 'free' },
  { name: 'DeepSeek-V4.1-Flash', price: '0.11x', icon: 'bot', tag: '夜间折扣', id: 'deep' },
  { name: 'GLM-5.3', price: '0.69x', icon: 'codeTag', tag: '订阅优先' },
  { name: 'GLM-5.3-Flash', price: '0.08x', icon: 'codeTag', tag: '订阅优先' },
  { name: 'GLM-5.2', price: '0.79x', icon: 'codeTag', tag: '夜间折扣' },
]
const chips = computed(() => (dev.value ? ['日常开发', '开发网站', '小程序', 'Agent 应用', '开发 Skill', '技术文档'] : ['创作文档', '金融服务', '分析数据', '制作PPT', '深度研究', '策划方案']))

// --- streamed reply ------------------------------------------------------------------------------
const reply = computed(() => streamReply((p.value.reply as ReplyBlock[] | undefined) ?? [], props.t, props.lang))
const replyStarted = computed(() => reply.value.length > 0)

// Keep the newest output in view the way a chat scrolls, with an eased glide instead of jumps.
const thread = ref<HTMLElement>()
const threadInner = ref<HTMLElement>()
const scroll = ref(0)
// The prompt box fills from its top line; once the text is taller than the box it scrolls up.
const inputBox = ref<HTMLElement>()
const inputText = ref<HTMLElement>()
const inputScroll = ref(0)
let observer: ResizeObserver | undefined
onMounted(() => {
  observer = new ResizeObserver(() => {
    if (inputBox.value && inputText.value) inputScroll.value = Math.max(0, inputText.value.offsetHeight - inputBox.value.clientHeight)
    if (!thread.value || !threadInner.value) return
    scroll.value = Math.max(0, threadInner.value.offsetHeight - thread.value.clientHeight + 24)
  })
  if (inputText.value) observer.observe(inputText.value)
})
// The conversation only exists after the prompt is sent, so attach when its element appears.
watch(threadInner, (element, previous) => {
  if (previous) observer?.unobserve(previous)
  if (element) observer?.observe(element)
})
onUnmounted(() => observer?.disconnect())
</script>

<template>
  <Desktop :apps="['agent']" show-agent>
    <div class="tp-win wb-app" :class="{ 'is-open': phase !== 'setup' || on('open'), 'is-static': phase !== 'setup' }">
      <!-- Title bar -->
      <div class="wb-titlebar">
        <TpIcon name="sidebar" :size="15" /><TpIcon name="search" :size="15" /><TpIcon name="filter" :size="15" />
        <span class="wb-menu"><span>编辑(E)</span><span>窗口(W)</span><span>帮助(H)</span><span>关于(A)</span></span>
        <span class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span>
      </div>

      <!-- Sidebar -->
      <aside class="wb-side">
        <div class="wb-brand"><div><b>WorkBuddy</b><small>V5.11.3</small></div><span class="wb-pill"><TpIcon name="grid" :size="13" />发现应用<TpIcon name="chevronDown" :size="12" /></span></div>
        <nav class="wb-nav">
          <span :class="{ 'is-active': !chat }"><TpIcon name="chatPlus" :size="17" />新建任务</span>
          <span><TpIcon name="bot" :size="17" />助理 <em>BETA</em></span>
          <span><TpIcon name="puzzle" :size="17" />专家·技能·连接器</span>
          <span><TpIcon name="alarm" :size="17" />定时任务</span>
          <span><TpIcon name="book" :size="17" />资料库</span>
          <span><TpIcon name="grid" :size="17" />更多</span>
        </nav>
        <div class="wb-group">任务 ({{ chat ? 3 : 2 }})</div>
        <div class="wb-tasks">
          <span v-if="chat" class="wb-task is-active is-new">{{ c.l(p.taskTitle as Localized) }}<i v-if="!done" class="wb-spinner" /><small v-else class="is-now">刚刚</small></span>
          <span class="wb-task">整理本周会议纪要<small>11月2日</small></span>
          <span class="wb-task">做个今日待办清单<small>10月30日</small></span>
        </div>
        <div class="wb-group">空间 (0)</div>
        <div class="wb-empty">暂无任务</div>
        <div class="wb-card"><div><b>做任务赢积分好礼</b><small>连登抽取 Buddy 周边</small></div><span>立即前往</span></div>
        <div class="wb-user"><span class="wb-avatar"><UserPhoto :size="30"><AppMark :size="30" /></UserPhoto></span><b>Wasabi</b><TpIcon name="bell" :size="17" /><TpIcon name="devices" :size="17" /></div>
      </aside>

      <!-- Main: home -->
      <main class="wb-main">
        <div class="wb-home" :class="{ 'is-gone': chat }">
          <span class="wb-corner"><TpIcon name="panel" :size="17" /></span>
          <h2 class="wb-hello">WorkBuddy, 我帮你</h2>
          <div class="wb-seg" :class="{ 'is-dev': dev }">
            <i class="wb-seg-thumb" />
            <span><TpIcon name="cup" :size="15" />办公</span><span data-tp="seg-dev"><TpIcon name="code" :size="15" />开发</span><span><TpIcon name="palette" :size="15" />设计</span>
          </div>
          <div class="wb-chips"><Transition name="tp-swap" mode="out-in"><div :key="String(dev)" class="wb-chips-row"><span v-for="chip in chips" :key="chip">{{ chip }}</span><span v-if="!dev" class="is-arrow"><TpIcon name="chevronRight" :size="14" /></span></div></Transition></div>
          <Robot class="wb-robot" :size="70" />
          <div class="wb-composer-wrap">
            <div class="wb-composer" data-tp="composer" :class="{ 'is-focus': on('focus') }">
              <div ref="inputBox" class="wb-input">
                <span v-if="!typedPrompt" class="wb-placeholder">今天帮你做些什么？@ 添加上下文，/调用技能与指令</span>
                <span ref="inputText" class="wb-typed" :style="{ transform: `translateY(${-inputScroll}px)` }">{{ typedPrompt }}<i v-if="on('focus') && !chat" class="tp-caret" /></span>
              </div>
              <div class="wb-composer-row">
                <TpIcon name="plus" :size="19" /><span class="wb-connect"><i /><i /></span>
                <span class="wb-spacer" />
                <TpIcon v-if="typedPrompt" name="sparkle" :size="17" />
                <span class="wb-model" data-tp="model-btn" :class="{ 'is-open': modelMenu }"><TpIcon name="bot" :size="15" />DeepSeek-V4.1-Flash<TpIcon :name="modelMenu ? 'chevronUp' : 'chevronDown'" :size="12" /></span>
                <TpIcon name="mic" :size="17" />
                <span class="wb-send" data-tp="send" :class="{ 'is-ready': typedPrompt, 'is-pressed': between('sendPress', 'send') }"><TpIcon name="arrowUp" :size="17" :stroke="2.4" /></span>
              </div>
            </div>
            <div class="wb-tray">
              <span class="wb-ws" data-tp="ws-btn" :class="{ 'is-set': workspace }"><TpIcon name="folder" :size="15" />{{ workspace || '选择工作空间' }}<TpIcon :name="wsMenu ? 'chevronUp' : 'chevronDown'" :size="12" /></span>
              <span class="wb-perm" data-tp="perm-btn" :class="{ 'is-danger': danger }"><TpIcon :name="danger ? 'alert' : 'shieldCheck'" :size="15" />{{ danger ? '允许完全访问' : '默认权限' }}<TpIcon :name="permPop ? 'chevronUp' : 'chevronDown'" :size="12" /></span>
            </div>

            <!-- Model picker -->
            <div class="wb-pop wb-models" :class="{ 'is-open': modelMenu }">
              <span v-for="model in models" :key="model.name" class="wb-model-row" :data-tp="model.id ? `model-${model.id}` : undefined"
                :class="{ 'is-hover': model.id && model.id === modelHover, 'is-suggested': model.id && on('suggest') && !on('menuClose') }">
                <TpIcon :name="model.icon" :size="15" /><b>{{ model.name }}</b><em v-if="model.tag" :class="{ 'is-free': model.id === 'free' }">{{ model.tag }}</em>
                <span class="wb-spacer" /><small>{{ model.price }}</small><TpIcon v-if="model.id === 'deep'" name="check" :size="14" />
              </span>
              <span class="wb-model-row is-config"><TpIcon name="sparkle" :size="15" />配置自定义模型</span>
            </div>

            <!-- Workspace menu -->
            <div class="wb-pop wb-wsmenu" :class="{ 'is-open': wsMenu }">
              <span class="wb-ws-search"><TpIcon name="search" :size="14" />搜索工作空间</span>
              <span v-for="d in ['2026-11-02-09-51-14', '2026-10-30-09-34-46']" :key="d"><TpIcon name="folder" :size="15" />{{ d }}</span>
              <hr>
              <span><TpIcon name="plus" :size="15" />新建工作空间</span>
              <span data-tp="ws-open" :class="{ 'is-hover': on('wsHover') }"><TpIcon name="folder" :size="15" />打开本地文件夹</span>
            </div>

            <!-- Permission popover -->
            <div class="wb-pop wb-permpop" :class="{ 'is-open': permPop }">
              <p>当前为默认权限，所有操作都会在安全沙箱约束内进行，超出范围会请求你的允许。</p>
              <hr>
              <span>允许完全访问<i class="wb-toggle" data-tp="perm-toggle" :class="{ 'is-on': on('toggle') }" /></span>
            </div>
          </div>
          <div class="wb-suggestions">
            <span>做个今日待办清单的网站<TpIcon name="arrowUpRight" :size="14" /></span>
            <span>做个能直接问数据且自动出分析图表的 Agent<TpIcon name="arrowUpRight" :size="14" /></span>
            <span>做个能整理想法、资料和待办的个人助手 Agent<TpIcon name="arrowUpRight" :size="14" /></span>
          </div>
          <p class="wb-foot">内容由 AI 生成，请核实重要信息</p>
        </div>

        <!-- Main: conversation -->
        <div v-if="chat" class="wb-chat">
          <header class="wb-chat-head"><b>{{ c.l(p.taskTitle as Localized) }}</b><span><TpIcon name="search" :size="17" /><TpIcon name="share" :size="17" /><TpIcon name="panel" :size="17" /></span></header>
          <div ref="thread" class="wb-thread">
            <div ref="threadInner" class="wb-thread-inner" :style="{ transform: `translateY(${-scroll}px)` }">
              <div class="wb-user-msg">{{ prompt }}</div>
              <div class="wb-agent">
                <div class="wb-agent-head"><span class="wb-agent-avatar"><Robot :size="22" /></span><b>WorkBuddy</b><small v-if="!done">预计消耗 ◇ 1.69 ~ 73.74</small></div>
                <div class="wb-elapsed">{{ done ? '已完成' : '已处理' }} {{ elapsed }}</div>
                <!-- Status texts cross-fade, but the line leaves at once when the reply starts so it never holds an empty gap. -->
                <div v-if="!replyStarted" class="wb-status-line"><Transition name="tp-swap" mode="out-in"><div :key="status" class="wb-status">{{ status }}</div></Transition></div>
                <template v-for="part in reply" :key="part.n">
                  <div v-if="part.kind === 'tool'" class="wb-tool" :class="{ 'is-open': part.open, 'is-running': part.running }">
                    <div class="wb-tool-head"><TpIcon name="terminal" :size="15" />{{ part.title }}<TpIcon name="chevronDown" :size="12" /><small>{{ part.summary }}</small><i v-if="part.running" class="wb-spinner is-small" /><TpIcon v-else name="check" :size="14" /></div>
                    <div class="wb-tool-body"><div><pre><template v-for="(line, i) in part.lines" :key="i"><span v-if="line.cmd" class="is-cmd">&gt; {{ line.cmd }}</span><span v-else class="is-out">{{ line.out }}</span></template></pre></div></div>
                  </div>
                  <div v-else class="wb-md" :class="{ 'is-final': part.kind === 'md' }">
                    <template v-for="(block, i) in part.blocks" :key="i">
                      <component :is="block.kind === 'h' ? 'h4' : block.kind === 'li' ? 'li' : block.kind === 'pre' ? 'pre' : 'p'">
                        <template v-for="(inline, j) in block.parts" :key="j"><b v-if="inline.kind === 'bold'">{{ inline.text }}</b><code v-else-if="inline.kind === 'code'">{{ inline.text }}</code><a v-else-if="inline.kind === 'link'">{{ inline.text }}</a><template v-else>{{ inline.text }}</template></template><i v-if="part.streaming && i === part.blocks.length - 1" class="wb-stream-dot" />
                      </component>
                    </template>
                  </div>
                </template>
                <div v-if="done" class="wb-actions"><TpIcon name="copy" :size="16" /><TpIcon name="thumbUp" :size="16" /><TpIcon name="thumbDown" :size="16" /><TpIcon name="speaker" :size="16" /><TpIcon name="retry" :size="16" /><TpIcon name="share" :size="16" /><TpIcon name="more" :size="16" :stroke="3" /><span>共消耗 ◇ {{ p.cost }}</span><span><TpIcon name="bot" :size="14" />DeepSeek-V4.1-Flash</span><span>16:22</span></div>
              </div>
            </div>
          </div>
          <Robot class="wb-robot is-chat" :size="62" :busy="!done" />
          <div class="wb-composer is-bottom">
            <div class="wb-input"><span class="wb-placeholder">今天帮你做些什么？@ 添加上下文，/调用技能与指令</span></div>
            <div class="wb-composer-row">
              <TpIcon name="plus" :size="19" />
              <span class="wb-perm is-danger is-inline"><TpIcon name="alert" :size="15" />允许完全访问<TpIcon name="chevronDown" :size="12" /></span>
              <span class="wb-connect"><i /><i /></span>
              <span class="wb-spacer" />
              <i v-if="!done" class="wb-spinner is-grey" />
              <span class="wb-model"><TpIcon name="bot" :size="15" />DeepSeek-V4.1-Flash<TpIcon name="chevronDown" :size="12" /></span>
              <TpIcon name="mic" :size="17" />
              <span class="wb-send" :class="{ 'is-stop': !done }"><TpIcon :name="done ? 'arrowUp' : 'stop'" :size="15" :stroke="2.4" :fill="!done" /></span>
            </div>
          </div>
          <p class="wb-foot">内容由 AI 生成，请核实重要信息</p>
        </div>
      </main>

      <!-- Folder picker -->
      <FolderPicker v-if="folderDialog" lang="zh-CN" :new-pressed="between('newPress', 'newFolder')" :created="on('newFolder')" :typed="folderName" :named="on('nameEnd')" :ok-pressed="between('okPress', 'dialogClose')"><template #icon><AppMark :size="16" /></template></FolderPicker>

      <!-- Full access confirmation -->
      <div class="wb-mask" :class="{ 'is-open': permDialog }">
        <div class="wb-dialog">
          <header><TpIcon name="shield" :size="19" /><b>确认允许完全访问？</b><TpIcon name="close" :size="16" /></header>
          <p>开启完全访问后，所有任务将减少确认步骤并直接操作您的电脑，请谨慎操作。包括以下内容：</p>
          <ul>
            <li><TpIcon name="folder" :size="16" /><b>文件操作</b><small>读取、创建、修改、上传或删除此计算机上任意位置的文件</small></li>
            <li><TpIcon name="terminal" :size="16" /><b>终端命令</b><small>运行命令、安装软件和更改系统设置</small></li>
            <li><TpIcon name="globe" :size="16" /><b>访问互联网</b><small>访问网站、发送数据并使用已启用的连接器</small></li>
          </ul>
          <label class="wb-check"><i data-tp="perm-check" :class="{ 'is-on': on('check') }"><TpIcon name="check" :size="12" :stroke="3" /></i>我已了解风险，并对自己的数据安全负责</label>
          <footer><span>取消</span><span class="wb-allow" data-tp="perm-allow" :class="{ 'is-ready': on('check'), 'is-pressed': between('allowPress', 'allow') }">允许完全访问</span></footer>
        </div>
      </div>
    </div>
  </Desktop>
</template>
