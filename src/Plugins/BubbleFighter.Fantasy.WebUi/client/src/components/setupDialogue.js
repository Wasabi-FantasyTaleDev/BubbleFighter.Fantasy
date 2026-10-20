/** Generated page numbers are separate from the five form stages. */
export function pagesForStage(pages, stage) {
  return pages.filter(page => page.stage === 'FantasySetup' && (page.sourceProgress ?? page.progress) === stage)
}

/** A dialogue click may reveal another page of this stage, never advance installation. */
export function nextStagePage(pages, stage, current, requested) {
  const allowed = pagesForStage(pages, stage)
  const index = allowed.findIndex(page => page.progress === current)
  const next = allowed[index + 1]
  return index >= 0 && requested && next?.progress === requested.progress ? next.progress : current
}
