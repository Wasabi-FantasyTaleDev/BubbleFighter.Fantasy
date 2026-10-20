import { defineStore } from 'pinia'
import { api, jsonRequest } from '../api/client'

/** What the interface knows about the server it talks to: reachability and the live player count. */
export const useServerStore = defineStore('server', {
  state: () => ({
    /** 'connecting' until the first answer, then 'online' or 'offline'. */
    conn: 'connecting',
    /** Players online as of the last overview or session list; null until one arrived. */
    players: null,
    /** True once the server answered 401: the sign-in view replaces the shell. */
    signInRequired: false,
    authReady: false,
    authMode: 'none',
    hasAdministrator: false,
    authenticated: false,
    installation: false,
    setupStatus: null,
    setupOpen: false,
    startupError: '',
    bootstrapToken: ''
  }),
  actions: {
    /** The server refused the request: the administrator session ended, ask for the account again. */
    requireSignIn() {
      this.authenticated = false
      this.signInRequired = true
    },
    async signOut() {
      if (this.authMode === 'account') await api('auth/logout', jsonRequest('POST', {}))
      this.authenticated = false
      this.requireSignIn()
    },
    async initialize() {
      this.startupError = ''
      try {
        const auth = await api('auth/status', { allowUnauthorized: true })
        this.authMode = auth.mode
        this.installation = auth.installation
        this.hasAdministrator = !!auth.hasAdministrator
        this.authenticated = auth.authenticated
        this.signInRequired = this.hasAdministrator && !auth.authenticated
        if (this.installation && (!this.hasAdministrator || this.authenticated)) {
          await this.refreshSetup()
          if (this.setupStatus.phase !== 'ready') this.setupOpen = true
        }
      } catch (e) {
        this.startupError = e.status === 403 ? 'bootstrap_token_required' : 'connection_failed'
      } finally { this.authReady = true }
    },
    async refreshSetup() {
      this.setupStatus = await api('setup/status')
      this.hasAdministrator = this.setupStatus.hasAdministrator
      return this.setupStatus
    },
    async login(username, password) {
      await api('auth/login', { ...jsonRequest('POST', { username, password }), allowUnauthorized: true })
      this.authenticated = true
      this.signInRequired = false
      await this.initialize()
    },
    markOnline() { this.conn = 'online' },
    markOffline() { this.conn = 'offline' }
  }
})
