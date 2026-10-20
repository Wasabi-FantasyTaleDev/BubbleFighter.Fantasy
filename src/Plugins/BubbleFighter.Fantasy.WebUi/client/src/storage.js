// localStorage can be blocked (private windows, policies); a blocked store behaves as an empty one.
export const store = {
  get(key) {
    try { return localStorage.getItem(key) } catch { return null }
  },
  set(key, value) {
    try {
      if (value == null) localStorage.removeItem(key)
      else localStorage.setItem(key, value)
    } catch { /* blocked: the preference lasts for this page only */ }
  }
}

// The access token lives in sessionStorage only: it dies with the tab and is never written to
// localStorage or the URL.
export const session = {
  get(key) {
    try { return sessionStorage.getItem(key) } catch { return null }
  },
  set(key, value) {
    try {
      if (value == null) sessionStorage.removeItem(key)
      else sessionStorage.setItem(key, value)
    } catch { /* blocked: the token lasts for this page only */ }
  }
}
