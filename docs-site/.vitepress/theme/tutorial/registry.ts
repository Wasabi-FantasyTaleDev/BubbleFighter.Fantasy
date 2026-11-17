import { defineAsyncComponent, type Component } from 'vue'
import type { Tutorial } from './types'
import list from '../../../tutorials.json'

/** Published tutorials with their per-language titles; `tutorials.json` is also the sync script's input. */
export const tutorialList = list as { slug: string; languages?: string[]; title: Record<string, string>; description: Record<string, string> }[]
/** Whether a tutorial is published in a page language; a tutorial without `languages` is in all. */
export const tutorialIn = (item: { languages?: string[] }, code: string) => !item.languages || item.languages.includes(code)

/** Each tutorial is one data module, loaded only on its own page. */
export const tutorialData: Record<string, () => Promise<{ default: Tutorial }>> = {
  'install-script': () => import('./data/install-script'),
  'install-windows-workbuddy': () => import('./data/install-windows-workbuddy'),
  'install-windows-dsh': () => import('./data/install-windows-dsh'),
  'install-windows-codex': () => import('./data/install-windows-codex'),
}

/** Scene drawings a tutorial's steps can name; new tutorials reuse these or add one here. */
export const scenes: Record<string, Component> = {
  script: defineAsyncComponent(() => import('./scenes/ScriptScene.vue')),
  linux: defineAsyncComponent(() => import('./scenes/LinuxScene.vue')),
  browser: defineAsyncComponent(() => import('./scenes/BrowserScene.vue')),
  workbuddy: defineAsyncComponent(() => import('./scenes/WorkBuddyScene.vue')),
  dsh: defineAsyncComponent(() => import('./scenes/DSHScene.vue')),
  codex: defineAsyncComponent(() => import('./scenes/CodexScene.vue')),
}
