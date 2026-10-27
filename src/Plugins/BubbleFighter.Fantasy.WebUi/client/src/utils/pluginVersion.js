/** Compare semantic plugin versions, ignoring build metadata; return null for unknown formats. */
export function comparePluginVersions(left, right) {
  const parse = value => /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([\da-zA-Z.-]+))?(?:\+[\da-zA-Z.-]+)?$/.exec(value || '')
  const a = parse(left), b = parse(right)
  if (!a || !b) return null
  for (let index = 1; index <= 3; index++) {
    if (BigInt(a[index]) !== BigInt(b[index])) return BigInt(a[index]) > BigInt(b[index]) ? 1 : -1
  }
  if (!a[4] || !b[4]) return a[4] === b[4] ? 0 : a[4] ? -1 : 1
  const x = a[4].split('.'), y = b[4].split('.')
  for (let index = 0; index < Math.max(x.length, y.length); index++) {
    if (x[index] === y[index]) continue
    if (x[index] === undefined || y[index] === undefined) return x[index] === undefined ? -1 : 1
    const nx = /^\d+$/.test(x[index]), ny = /^\d+$/.test(y[index])
    if (nx && ny) {
      if (BigInt(x[index]) !== BigInt(y[index])) return BigInt(x[index]) > BigInt(y[index]) ? 1 : -1
    } else if (nx !== ny) return nx ? -1 : 1
    else return x[index] > y[index] ? 1 : -1
  }
  return 0
}
