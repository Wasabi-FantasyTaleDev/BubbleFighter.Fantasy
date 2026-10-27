import { api, jsonRequest } from './client'

/**
 * Reads the client policy for editing. A server still on its profile's policy (`mode: 'profile'`) is edited from the
 * policy launchers receive now; the first save turns it into a custom policy. Rule types come from the server, so a new
 * type appears here without a WebUI change.
 */
export async function loadClientPolicy() {
  const [settings, types] = await Promise.all([api('setup/client-policy'), api('setup/client-policy/rule-types')])
  const policy = settings.policy || settings.active || { sources: [], plugins: [] }
  return { mode: settings.mode, revision: settings.revision, policy: copyPolicy(policy), types }
}

/** A detached copy to edit; the policy is plain JSON, and Vue's reactive proxies cannot be structured-cloned. */
export const copyPolicy = policy => JSON.parse(JSON.stringify(policy))

/** Replaces the whole policy (null returns to the profile's). The revision makes a concurrent edit fail instead of being lost. */
export async function saveClientPolicy(revision, policy) {
  const request = jsonRequest('PUT', { policy })
  request.headers['If-Match'] = `"${revision}"`
  await api('setup/client-policy', request)
}

// The server's grammar: strict SemVer 2.0, compared the way the Launcher compares plugin versions.
const SEMVER = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/
export const validVersion = text => SEMVER.test(text)

export function compareVersions(left, right) {
  const a = SEMVER.exec(left)
  const b = SEMVER.exec(right)
  for (let index = 1; index <= 3; index++) {
    const x = BigInt(a[index])
    const y = BigInt(b[index])
    if (x !== y) return x > y ? 1 : -1
  }
  if (!a[4] || !b[4]) return a[4] ? -1 : b[4] ? 1 : 0
  const p = a[4].split('.')
  const q = b[4].split('.')
  for (let index = 0; index < Math.min(p.length, q.length); index++) {
    if (p[index] === q[index]) continue
    const numeric = [/^\d+$/.test(p[index]), /^\d+$/.test(q[index])]
    if (numeric[0] && numeric[1]) return BigInt(p[index]) > BigInt(q[index]) ? 1 : -1
    if (numeric[0] !== numeric[1]) return numeric[0] ? -1 : 1
    return p[index] < q[index] ? -1 : 1
  }
  return Math.sign(p.length - q.length)
}

/** Whether two build scopes share a build; an empty scope means every build. */
export const overlap = (left, right) => !left.length || !right.length || left.some(build => right.includes(build))

/** The server's conflict rule, so the editor can ask which rule to keep before saving. */
export function conflicts(types, a, b) {
  if (!overlap(a.builds, b.builds)) return false
  if (a.type === b.type) return true
  const [min, max] = a.type === 'min_version' ? [a, b] : [b, a]
  if (min.type === 'min_version' && max.type === 'max_version')
    return validVersion(min.version) && validVersion(max.version) && compareVersions(min.version, max.version) > 0
  const listed = (rule, other) => types.find(type => type.name === rule.type)?.conflicts.includes(other.type)
  return !!(listed(a, b) || listed(b, a))
}

/** A rule identity unused within its plugin. The Launcher remembers shown recommendations by it, so edits keep it. */
export function ruleId(rules, type) {
  const base = type.replace(/_/g, '-')
  for (let index = 1; ; index++) {
    const id = index === 1 ? base : `${base}-${index}`
    if (!rules.some(rule => rule.id === id)) return id
  }
}

/** A source name derived from its address: lower case, starting with a letter or digit, unique in the policy. */
export function sourceName(sources, url) {
  const base = new URL(url).hostname.toLowerCase().replace(/[^a-z0-9._-]/g, '-').replace(/^[^a-z0-9]+/, '') || 'source'
  for (let index = 1; ; index++) {
    const name = index === 1 ? base : `${base}-${index}`
    if (!sources.some(source => source.name === name)) return name
  }
}

/** The rule as the server stores it: fields the type does not use are left out. */
export function wireRule(rule, types) {
  const field = types.find(type => type.name === rule.type)?.field
  return {
    id: rule.id, type: rule.type, builds: [...rule.builds],
    ...(field === 'version' ? { version: rule.version.trim() } : {}),
    ...(field === 'sources' ? { sources: [...rule.sources] } : {})
  }
}
