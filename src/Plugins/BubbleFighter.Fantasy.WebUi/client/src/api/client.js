import { useServerStore } from '../stores/server'

/** A failed API call: `status` is the HTTP status, or 0 when the server could not be reached. */
export class ApiError extends Error {
  constructor(message, status) {
    super(message)
    this.status = status
  }
}

/**
 * Calls `api/<path>` relative to the page, so the interface works under any mount path. The
 * administrator session travels as a same-origin cookie; a 401 shows the sign-in view. Any HTTP answer proves the server is reachable; only a failed fetch
 * marks it offline.
 */
export async function api(path, options = {}) {
  const server = useServerStore()
  const { allowUnauthorized = false, ...request } = options
  let res
  try {
    const headers = { Accept: 'application/json', ...request.headers }
    if (request.method && request.method !== 'GET') headers['X-BFF-Request'] = '1'
    if (server.bootstrapToken) headers['X-BFF-Setup-Token'] = server.bootstrapToken
    res = await fetch('api/' + path, { ...request, credentials: 'same-origin', headers })
  } catch (e) {
    if (request.signal?.aborted) throw e
    server.markOffline()
    throw new ApiError(e.message, 0)
  }
  server.markOnline()
  if (res.status === 401) {
    if (!allowUnauthorized) server.requireSignIn()
    throw new ApiError('unauthorized', 401)
  }
  if (!res.ok) {
    let detail = res.statusText
    try {
      const body = await res.json()
      detail = body.detail || body.title || body.error || detail
    } catch { /* not JSON */ }
    throw new ApiError(detail, res.status)
  }
  const text = await res.text()
  return text ? JSON.parse(text) : null
}

/** JSON mutations share the request guard and never put credentials in URLs or local storage. */
export const jsonRequest = (method, body) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
