<script setup lang="ts">
import { computed } from 'vue'
import TpIcon from './TpIcon.vue'
import type { Code } from '../types'
import '../scenes/script.css'

// The server's setup assistant in a browser window, drawn after the WebUI's wide layout: its welcome
// screen, then the language card. The texts are the WebUI's own (its locale files), per page language.
const props = defineProps<{ lang: Code; open: boolean; address: string; pressed: boolean; assistant: boolean; picked: boolean; mac?: boolean }>()

const words: Record<Code, { welcome: string; assistant: string; title: string; description: string; later: string; next: string; label: string; home: string; lead: string; stages: string[] }> = {
  en: { welcome: 'Welcome to', assistant: 'Setup assistant', title: 'Hello. Welcome.', description: 'Choose the language you would like to use.', later: 'You can change the interface language later.', next: 'Continue', label: 'A place to begin', home: 'Make yourself at home.', lead: 'A few simple steps, and your server is ready for its next chapter.', stages: ['Server', 'Client', 'Database', 'Permissions', 'Done'] },
  'zh-CN': { welcome: '欢迎使用', assistant: '安装助理', title: '你好，欢迎。', description: '请选择你希望使用的语言。', later: '之后可以更改界面语言。', next: '继续', label: '从这里开始', home: '欢迎回到泡泡村。', lead: '只需几步，让你的服务器准备好迎接新的故事。', stages: ['服务器', '客户端', '数据库', '权限', '完成'] },
  'zh-TW': { welcome: '歡迎使用', assistant: '設定輔助程式', title: '你好，歡迎。', description: '請選擇你想使用的語言。', later: '之後可以變更介面語言。', next: '繼續', label: '從這裡開始', home: '歡迎回到泡泡村。', lead: '只需幾個步驟，讓你的伺服器準備好迎接新的故事。', stages: ['伺服器', '用戶端', '資料庫', '權限', '完成'] },
  ko: { welcome: '환영합니다', assistant: '설정 도우미', title: '안녕하세요. 반가워요.', description: '사용할 언어를 선택하세요.', later: '화면 언어는 나중에 변경할 수 있습니다.', next: '계속', label: '새로운 시작', home: '붐힐에 오신 것을 환영합니다.', lead: '간단한 설정으로 서버의 새로운 이야기를 시작하세요.', stages: ['서버', '클라이언트', '데이터베이스', '권한', '완료'] },
}
const u = computed(() => words[props.lang])
// The assistant lists its languages in this order.
const languages = ['中文（简体）', '中文（繁體）', '한국어', 'English']
const chosen = computed(() => ({ 'zh-CN': 0, 'zh-TW': 1, ko: 2, en: 3 })[props.lang])
</script>

<template>
  <div class="tp-win sc-browser" :class="{ 'is-open': open, 'is-mac': mac }">
    <div class="sc-tabs"><span v-if="mac" class="mac-lights"><i /><i /><i /></span><span class="sc-tab"><TpIcon name="globe" :size="13" />Bubble Fighter Fantasy</span><span v-if="!mac" class="bs-caption"><TpIcon name="min" :size="14" /><TpIcon name="max" :size="12" /><TpIcon name="close" :size="14" /></span></div>
    <div class="sc-address"><TpIcon name="back" :size="15" /><TpIcon name="forward" :size="15" /><TpIcon name="refresh" :size="15" /><span data-tp="sc-url">{{ address }}</span></div>
    <div class="sc-page">
      <div v-if="!assistant" class="sc-intro">
        <small class="sc-studio">Fantasy Tale Studio</small>
        <p>{{ u.welcome }}</p>
        <h3>Bubble Fighter Fantasy</h3>
        <span class="sc-intro-go" data-tp="sc-continue" :class="{ 'is-pressed': pressed }">{{ u.next }}</span>
      </div>
      <div v-else class="sc-assist">
        <header><b>Bubble Fighter Fantasy</b><small>Fantasy Tale Studio</small></header>
        <div class="sc-card" data-tp="sc-card">
          <aside>
            <small>{{ u.label }}</small>
            <h4>{{ u.home }}</h4>
            <p>{{ u.lead }}</p>
            <ol><li v-for="(name, n) in u.stages" :key="name"><span>{{ n + 1 }}</span>{{ name }}</li></ol>
          </aside>
          <div class="sc-card-body">
            <small>{{ u.assistant }}</small>
            <h3>{{ u.title }}</h3>
            <p>{{ u.description }}</p>
            <div class="sc-langs"><span v-for="(name, n) in languages" :key="name" :data-tp="n === chosen ? 'sc-pick' : undefined" :class="{ 'is-on': picked && n === chosen }">{{ name }}</span></div>
            <footer><small>{{ u.later }}</small><span class="sc-next">{{ u.next }}</span></footer>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
