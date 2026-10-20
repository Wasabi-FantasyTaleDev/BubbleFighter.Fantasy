import { defineStore } from 'pinia'
import { watch } from 'vue'
import { store } from '../storage'
import { resolveLang } from '../langs'

const LANG_KEY = 'bff_lang'
const THEME_KEY = 'noah_theme'

function storedTheme() {
  const value = store.get(THEME_KEY)
  return value === 'dark' || value === 'light' ? value : null
}

/**
 * Interface preferences. Language ("auto" or a code) and theme (null follows the system) are kept
 * in this browser only, under the keys the first-paint script in index.html also reads.
 */
export const useUiStore = defineStore('ui', {
  state: () => ({
    langPref: store.get(LANG_KEY) || 'auto',
    themePref: storedTheme(),
    systemDark: typeof matchMedia === 'function' && matchMedia('(prefers-color-scheme: dark)').matches,
    sideOpen: false
  }),
  getters: {
    lang: (state) => resolveLang(state.langPref),
    theme: (state) => state.themePref || (state.systemDark ? 'dark' : 'light'),
    /** The theme choice as the settings select shows it. */
    themeChoice: (state) => state.themePref || 'system'
  },
  actions: {
    setLang(pref) {
      this.langPref = pref
      store.set(LANG_KEY, pref === 'auto' ? null : pref)
    },
    setTheme(choice) {
      this.themePref = choice === 'system' ? null : choice
      store.set(THEME_KEY, this.themePref)
    },
    toggleTheme() {
      this.setTheme(this.theme === 'dark' ? 'light' : 'dark')
    },
    /** Mirrors the preferences onto the document and follows the system theme while no theme is chosen. */
    init() {
      const media = matchMedia('(prefers-color-scheme: dark)')
      media.addEventListener('change', () => { this.systemDark = media.matches })
      watch(
        () => [this.theme, this.lang],
        ([theme, lang]) => {
          document.documentElement.setAttribute('data-theme', theme)
          document.documentElement.lang = lang
        },
        { immediate: true }
      )
    }
  }
})
