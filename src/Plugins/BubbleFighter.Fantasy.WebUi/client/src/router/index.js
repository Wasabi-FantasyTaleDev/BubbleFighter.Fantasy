import { createRouter, createWebHashHistory } from 'vue-router'

/** Store navigation shared by the sidebar, breadcrumb and page tabs. */
export const STORE_PAGES = [
  { id: 'browse', title: 'store.view.browse' },
  { id: 'installed', title: 'store.view.installed' },
  { id: 'downloads', title: 'store.view.downloads' }
]

/** Pages in sidebar order. `title` is the translation key of the page name. */
export const PAGES = [
  { path: 'overview', title: 'nav.overview', icon: 'overview', group: 'server', component: () => import('../views/OverviewView.vue') },
  { path: 'players', title: 'nav.players', menuTitle: 'overview.players', icon: 'players', group: 'server', parent: 'overview', component: () => import('../views/PlayersView.vue') },
  { path: 'rooms', title: 'nav.rooms', menuTitle: 'overview.rooms', icon: 'rooms', group: 'server', parent: 'overview', component: () => import('../views/RoomsView.vue') },
  { path: 'builds', title: 'overview.builds', icon: 'builds', group: 'server', parent: 'overview', component: () => import('../views/BuildsView.vue') },
  { path: 'plugins', title: 'nav.plugins', icon: 'plugins', group: 'system', component: () => import('../views/PluginsView.vue') },
  { path: 'store', title: 'nav.store', icon: 'store', group: 'system', component: () => import('../views/StoreView.vue') },
  // { path: 'opcodes', title: 'nav.opcodes', icon: 'opcodes', group: 'system', component: () => import('../views/OpcodesView.vue') },
  { path: 'logs', title: 'nav.logs', icon: 'logs', group: 'system', component: () => import('../views/LogsView.vue') },
  { path: 'settings', title: 'nav.settings', icon: 'gear', group: 'system', component: () => import('../views/SettingsView.vue') }
]

// Hash routing keeps the server free of rewrite rules and works under any mount path.
export default createRouter({
  history: createWebHashHistory(),
  routes: [
    ...PAGES.map((p) => ({ path: '/' + p.path, name: p.path, component: p.component, meta: { title: p.title, group: p.group, parent: p.parent } })),
    { path: '/opcodes', name: 'opcodes', component: () => import('../views/OpcodesView.vue'), meta: { title: 'nav.opcodes', group: 'system' } },
    { path: '/:pathMatch(.*)*', redirect: '/overview' }
  ]
})
