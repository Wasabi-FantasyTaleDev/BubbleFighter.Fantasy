// A small translation layer: four dictionaries, {name} placeholders, English as the fallback.
// The language comes from the stored preference, or from the browser when the preference is "auto".
import en from './locales/en.js'
import zhHant from './locales/zh-Hant.js'
import zhHans from './locales/zh-Hans.js'
import ko from './locales/ko.js'
import { useUiStore } from './stores/ui'

const DICTIONARIES = { en, 'zh-Hant': zhHant, 'zh-Hans': zhHans, ko }

function format(text, vars) {
  return vars ? text.replace(/\{(\w+)\}/g, (_, name) => vars[name] ?? '') : text
}

/**
 * Translation helpers bound to the current language. `t` and `te` read the reactive preference,
 * so templates and computed values that call them re-render when the language changes.
 */
export function useI18n() {
  const ui = useUiStore()
  const lookup = (key) => DICTIONARIES[ui.lang]?.[key] ?? DICTIONARIES.en[key]
  return {
    /** Translates a key; an unknown key comes back as itself. */
    t: (key, vars) => format(lookup(key) ?? key, vars),
    /** Whether a key has a translation (in the current language or English). */
    te: (key) => lookup(key) !== undefined
  }
}
