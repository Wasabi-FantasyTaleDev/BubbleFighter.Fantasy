/** The language codes the interface is translated into. */
export const LANGS = ['en', 'zh-Hant', 'zh-Hans', 'ko']

/** The picker entries: each language is named in its own script, "auto" is translated separately. */
export const LANGUAGE_NAMES = { en: 'English', 'zh-Hant': '中文（繁體）', 'zh-Hans': '中文（简体）', ko: '한국어' }

/** Picks the first supported language from the browser's list; Hant, TW, HK and MO select Traditional Chinese. */
export function systemLang() {
  const list = (typeof navigator !== 'undefined' && navigator.languages?.length)
    ? navigator.languages
    : [(typeof navigator !== 'undefined' && navigator.language) || 'en']
  for (const raw of list) {
    const l = raw.toLowerCase()
    if (l.startsWith('zh')) return /hant|tw|hk|mo/.test(l) ? 'zh-Hant' : 'zh-Hans'
    if (l.startsWith('ko')) return 'ko'
    if (l.startsWith('en')) return 'en'
  }
  return 'en'
}

/** Resolves a stored preference ("auto" or a language code) to a supported language code. */
export function resolveLang(pref) {
  if (!pref || pref === 'auto') return systemLang()
  return LANGS.includes(pref) ? pref : 'en'
}
