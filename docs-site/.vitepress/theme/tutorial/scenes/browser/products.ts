import type { Code } from '../../types'

/** What the browser step searches for and downloads, per product. */
export interface Product {
  id: 'workbuddy' | 'dsh' | 'chatgpt'
  query: string
  app: string
  /** Opens the site in a new tab (Baidu does) instead of the same one. */
  newTab: boolean
  /** The browser asks whether to keep the file before downloading it. */
  warn: boolean
  /** Searched on Google in every language. */
  googleOnly?: boolean
  /** Installs through the Microsoft Store instead of a downloaded installer. */
  store: boolean
  file: string
  totalMB: number
  kb: string
  description: string
  version?: string
  siteTitle: (lang: Code) => string
  url: (lang: Code) => string
}

// DeepSeek Harness publishes a Chinese and an English site; Chinese readers get the Chinese one.
const dshChinese = (lang: Code) => lang === 'zh-CN' || lang === 'zh-TW'

export const products: Record<Product['id'], Product> = {
  workbuddy: {
    id: 'workbuddy', query: 'workbuddy', app: 'WorkBuddy', newTab: true, warn: true, store: false,
    file: 'WorkBuddy-win32-x64-user-5.11.3.exe', totalMB: 439, kb: '449,047 KB',
    description: 'WorkBuddy Desktop - AI Agent Desktop Application', version: '5.11.3.0',
    siteTitle: () => 'WorkBuddy - AI Agent 办公新范式', url: () => 'https://www.workbuddy.cn/',
  },
  dsh: {
    id: 'dsh', query: 'deepseek harness', app: 'DeepSeek Harness', newTab: false, warn: false, store: false,
    file: 'deepseek-harness-0.2.2-rc.3-win-x64.exe', totalMB: 286, kb: '292,864 KB',
    description: 'DeepSeek Harness Setup',
    siteTitle: lang => (dshChinese(lang) ? 'DeepSeek Harness | 共探智能上限' : 'DeepSeek Harness | Explore the limits of intelligence'),
    url: lang => (dshChinese(lang) ? 'https://www.deepseek.com/harness/' : 'https://www.deepseek.com/en/harness/'),
  },
  chatgpt: {
    id: 'chatgpt', query: 'ChatGPT', app: 'ChatGPT', newTab: false, warn: false, store: true, googleOnly: true,
    file: '', totalMB: 0, kb: '', description: '',
    siteTitle: () => 'Download ChatGPT', url: () => 'https://chatgpt.com/download/',
  },
}
export { dshChinese }
