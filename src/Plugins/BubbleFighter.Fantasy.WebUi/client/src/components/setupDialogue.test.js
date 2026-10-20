import test from 'node:test'
import assert from 'node:assert/strict'
import { pagesForStage, nextStagePage } from './setupDialogue.js'

test('pagination uses source stage and cannot advance the wizard or jump pages', () => {
  const pages = [
    { stage: 'FantasySetup', progress: 1, sourceProgress: 1 },
    { stage: 'FantasySetup', progress: 2, sourceProgress: 1 },
    { stage: 'FantasySetup', progress: 3, sourceProgress: 2 },
    { stage: 'Unrelated', progress: 4, sourceProgress: 1 }
  ]
  assert.deepEqual(pagesForStage(pages, 1).map(p => p.progress), [1, 2])
  assert.equal(nextStagePage(pages, 1, 1, pages[1]), 2)
  assert.equal(nextStagePage(pages, 1, 1, pages[2]), 1)
  assert.equal(nextStagePage(pages, 1, 2, pages[2]), 2)
  assert.equal(nextStagePage(pages, 1, 2, undefined), 2)
})

test('unpaginated packs retain original progress identifiers', () => {
  const pages = [{ stage: 'FantasySetup', progress: 5 }]
  assert.equal(pagesForStage(pages, 5)[0], pages[0])
  assert.deepEqual(pagesForStage(pages, 4), [])
})
