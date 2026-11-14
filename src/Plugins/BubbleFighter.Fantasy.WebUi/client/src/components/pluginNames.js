// A plugin declares its own optional display names and category in its [Plugin] declaration; /api/plugins reports
// them as `displayNames` (keyed by language) and `category`. The WebUI keeps no table of its own.

// The interface language's name, then the English one, then the plugin id, which every plugin has.
export function pluginName(row, language) {
  const names = row?.displayNames || {}
  return names[language] || names.en || row?.plugin || ''
}

export function pluginCategory(row) {
  return row?.category || 'other'
}

// The order groups appear in; a category a plugin invents is shown after these, before `other`.
export const PLUGIN_CATEGORIES = ['gameplay', 'items', 'social', 'network', 'launcher', 'storage', 'cluster', 'admin', 'distribution', 'core']
