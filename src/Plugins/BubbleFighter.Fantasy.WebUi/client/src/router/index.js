import { createRouter, createWebHashHistory } from 'vue-router'

/** Pages in sidebar order. `title` is the translation key of the page name. */
export const PAGES = [
  { path: 'overview', title: 'nav.overview', icon: 'overview', group: 'server', component: () => import('../views/OverviewView.vue') },
  { path: 'players', title: 'nav.players', icon: 'players', group: 'server', component: () => import('../views/PlayersView.vue') },
  { path: 'rooms', title: 'nav.rooms', icon: 'rooms', group: 'server', component: () => import('../views/RoomsView.vue') },
  { path: 'plugins', title: 'nav.plugins', icon: 'plugins', group: 'system', component: () => import('../views/PluginsView.vue') },
  { path: 'opcodes', title: 'nav.opcodes', icon: 'opcodes', group: 'system', component: () => import('../views/OpcodesView.vue') },
  { path: 'logs', title: 'nav.logs', icon: 'logs', group: 'system', component: () => import('../views/LogsView.vue') },
  { path: 'settings', title: 'nav.settings', icon: 'gear', group: 'system', component: () => import('../views/SettingsView.vue') }
]

// Hash routing keeps the server free of rewrite rules and works under any mount path.
export default createRouter({
  history: createWebHashHistory(),
  routes: [
    ...PAGES.map((p) => ({ path: '/' + p.path, name: p.path, component: p.component, meta: { title: p.title } })),
    { path: '/:pathMatch(.*)*', redirect: '/overview' }
  ]
})
