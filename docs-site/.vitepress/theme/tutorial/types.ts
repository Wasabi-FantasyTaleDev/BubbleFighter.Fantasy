/** Page languages a tutorial must cover; the keys match the site's locale codes. */
export type Code = 'en' | 'zh-CN' | 'zh-TW' | 'ko'

/** Systems a tutorial can show; the first is the default. */
export type Platform = 'linux' | 'windows'

/** One text in every page language. */
export type Localized = Record<Code, string>

/**
 * A pointer waypoint. The pointer arrives at `at` (milliseconds into the step) after travelling
 * for `move` milliseconds. `target` names an element of the scene by its `data-tp` attribute, so
 * the path follows the drawing instead of hard-coded coordinates; `x`/`y` are stage pixels.
 */
export interface CursorPoint {
  at: number
  target?: string
  x?: number
  y?: number
  dx?: number
  dy?: number
  move?: number
  click?: boolean
  double?: boolean
  hide?: boolean
}

/**
 * A camera move that starts at `at` and settles after `dur`. `z` is the zoom on wide players and
 * `mz` on narrow ones, where the stage would otherwise be too small to read.
 */
export interface CameraPoint {
  at: number
  target?: string
  x?: number
  y?: number
  z?: number
  mz?: number
  dur?: number
}

/** A subtitle shown from `at` until the next caption. */
export interface Caption {
  at: number
  text: Localized
}

/** One chapter of a tutorial: a scene, its named cue times and the narration over it. */
export interface Step {
  id: string
  title: Localized
  scene: string
  duration: number
  cues: Record<string, number>
  cursor: CursorPoint[]
  camera: CameraPoint[]
  captions: Caption[]
  props?: Record<string, unknown>
}

/** A playable tutorial. `prompt` is optional text the reader copies, shown under the player. */
export interface Tutorial {
  steps: Step[]
  prompt?: { step: number; agent: string; title: Localized; note: Localized; text: Localized }
  /** A notice readers in the listed languages must confirm before the player appears. */
  notice?: Partial<Record<Code, { title: string; body: string[]; ok: string }>>
  /** Shows the install commands (Windows and Linux tabs) under the player instead of a prompt. */
  commands?: boolean
  /** The same tutorial per system; the switch beside the page title picks one, Linux first. */
  platforms?: Record<Platform, Step[]>
  /** A short credit line under the player per system, such as the author of a drawn wallpaper. */
  credits?: Partial<Record<Platform, Localized>>
}

/** Props every scene component receives from the player. */
export interface SceneProps {
  t: number
  step: Step
  lang: Code
}

/** Agent reply parts in the order they stream; `at` is milliseconds into the step. */
export type ReplyBlock =
  | { at: number; kind: 'text'; text: Localized }
  | { at: number; kind: 'tool'; title: string; summary: Localized; lines: { cmd?: string; out?: string }[]; dur: number }
  | { at: number; kind: 'md'; md: Localized; dur: number }

/** Logical stage size; scenes draw in these pixels and the player scales them. */
export const STAGE = { width: 1280, height: 800 } as const
