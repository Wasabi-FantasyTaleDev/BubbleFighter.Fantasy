import { PLUGIN_CATEGORIES, pluginCategory, pluginName } from './pluginNames'

// The server reports one row per plugin and client build. The page shows one card per plugin; its rows stay intact
// and every action still targets one row id.

const ICONS = { gameplay: 'play', items: 'store', social: 'players', network: 'globe', launcher: 'rocket', storage: 'layers',
  cluster: 'server', admin: 'shield', distribution: 'upload', core: 'cpu' }
export const categoryIcon = category => ICONS[category] || 'plugins'

export const stateName = (state, t, te) => te('state.' + state) ? t('state.' + state) : state
export const stateTone = state => state === 'Active' ? 'success'
  : ['Failed', 'CleanupFailed', 'WorkFailed'].includes(state) ? 'danger'
    : ['Pending', 'Unloading', 'CleanupIncomplete'].includes(state) ? 'warning' : 'default'

const FAILED = ['Failed', 'CleanupFailed', 'WorkFailed']

// One label for all of a plugin's rows: a single state when they agree, otherwise how many run and how many failed.
export function groupStatus(rows, t, te = () => true) {
  if (!rows.length) return { label: '', tone: 'default' }
  const failed = rows.filter(row => FAILED.includes(row.state)).length
  const states = new Set(rows.map(row => row.state))
  if (states.size === 1) { const [state] = states; return { label: te('state.' + state) ? t('state.' + state) : state, tone: stateTone(state) } }
  const running = rows.filter(row => row.state === 'Active' || row.state === 'Sleeping').length
  if (failed) return { label: t('plugins.groupFailed', { failed, total: rows.length }), tone: 'danger' }
  return { label: t('plugins.groupRunning', { running, total: rows.length }), tone: running ? 'warning' : 'default' }
}

export function decorateRow(row, language) {
  return { ...row, name: pluginName(row, language), category: pluginCategory(row), build: row.activationGroup || null }
}

// Rows of one plugin become one group; shared rows (no client build) come first in its build list.
export function buildGroups(rows) {
  const groups = new Map()
  for (const row of rows) {
    let group = groups.get(row.plugin)
    if (!group) groups.set(row.plugin, group = { plugin: row.plugin, name: row.name, category: row.category, description: row.description,
      version: row.version, author: row.author, builtIn: row.builtIn, storeEntry: row.storeEntry, availableVersion: row.availableVersion, rows: [] })
    group.rows.push(row)
    if (!group.description && row.description) group.description = row.description
  }
  for (const group of groups.values()) {
    group.rows.sort((a, b) => (a.build ? 1 : 0) - (b.build ? 1 : 0) || String(a.build).localeCompare(String(b.build)))
    group.builds = group.rows.map(row => row.build).filter(Boolean)
  }
  return [...groups.values()]
}

// Groups split into category sections in a stable order; categories a plugin invents follow the known ones.
export function sectionGroups(groups, language) {
  const sections = new Map()
  for (const group of groups) {
    if (!sections.has(group.category)) sections.set(group.category, [])
    sections.get(group.category).push(group)
  }
  const rank = id => { const index = PLUGIN_CATEGORIES.indexOf(id); return index < 0 ? (id === 'other' ? 1000 : 500) : index }
  return [...sections.entries()].sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b))
    .map(([id, items]) => ({ id, groups: items.sort((a, b) => a.name.localeCompare(b.name, language)) }))
}
