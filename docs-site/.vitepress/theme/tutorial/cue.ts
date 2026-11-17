import type { Code, Localized, SceneProps } from './types'

export const clamp = (value: number, min = 0, max = 1) => Math.min(max, Math.max(min, value))
export const easeInOut = (p: number) => (p < 0.5 ? 4 * p * p * p : 1 - Math.pow(-2 * p + 2, 3) / 2)
/** The site's own entrance curve, cubic-bezier(.16,1,.3,1), approximated as an exponential ease-out. */
export const easeOut = (p: number) => (p >= 1 ? 1 : 1 - Math.pow(2, -10 * p))

/**
 * Scene helpers bound to the current clock. Scenes derive every state from `t`, so pausing,
 * replaying and reduced motion (where `t` jumps to the end) need no special cases in them.
 */
export function cues(props: SceneProps) {
  const time = (name: string) => props.step.cues[name] ?? Number.POSITIVE_INFINITY
  return {
    time,
    /** True once the named cue has been reached. */
    on: (name: string) => props.t >= time(name),
    /** True between two cues. */
    between: (from: string, to: string) => props.t >= time(from) && props.t < time(to),
    /** 0..1 progress of a span that starts at a cue. */
    progress: (name: string, duration: number) => clamp((props.t - time(name)) / duration),
    /** Characters of `text` typed so far between two cues; code points, so CJK and Hangul type whole. */
    typed: (text: string, from: string, to: string) => {
      const chars = Array.from(text)
      const p = clamp((props.t - time(from)) / Math.max(1, time(to) - time(from)))
      return chars.slice(0, Math.round(chars.length * p)).join('')
    },
    /** Picks the page-language text. */
    l: (value: Localized | string) => (typeof value === 'string' ? value : value[props.lang as Code]),
  }
}
