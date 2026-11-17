import type { Code, Localized, ReplyBlock } from '../types'

// Streams an agent reply from the step clock: text arrives a few characters per frame, tool
// calls reveal their lines over their duration, and the final Markdown keeps its formatting
// while it grows. Every scene renders these parts in its own product's style.

export type Inline = { kind: 'text' | 'bold' | 'code' | 'link'; text: string }
export type Block = { kind: 'h' | 'li' | 'p' | 'pre'; parts: Inline[] }

export function parse(md: string): Block[] {
  const blocks: Block[] = []
  const lines = md.split('\n')
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]
    if (line.startsWith('```')) {
      const code: string[] = []
      while (++i < lines.length && !lines[i].startsWith('```')) code.push(lines[i])
      blocks.push({ kind: 'pre', parts: [{ kind: 'text', text: code.join('\n') }] })
    } else if (line.trim()) {
      const kind = line.startsWith('### ') ? 'h' : line.startsWith('- ') ? 'li' : 'p'
      const body = kind === 'h' ? line.slice(4) : kind === 'li' ? line.slice(2) : line
      const parts: Inline[] = body.split(/(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\))/).filter(Boolean).map(token =>
        token.startsWith('**') ? { kind: 'bold', text: token.slice(2, -2) }
          : token.startsWith('`') ? { kind: 'code', text: token.slice(1, -1) }
          : token.startsWith('[') ? { kind: 'link', text: token.slice(1, token.indexOf(']')) }
          : { kind: 'text', text: token })
      blocks.push({ kind, parts })
    }
  }
  return blocks
}

/** Shows only the first `budget` characters, keeping each token's formatting while it streams. */
export function reveal(blocks: Block[], budget: number) {
  const out: Block[] = []
  for (const block of blocks) {
    if (budget <= 0) break
    const parts: Inline[] = []
    for (const part of block.parts) {
      if (budget <= 0) break
      const chars = Array.from(part.text)
      parts.push({ ...part, text: chars.slice(0, budget).join('') })
      budget -= chars.length
    }
    out.push({ kind: block.kind, parts })
  }
  return out
}

export type ReplyPart =
  | { n: number; kind: 'text' | 'md'; blocks: Block[]; streaming: boolean }
  | { n: number; kind: 'tool'; title: string; summary: string; cmd: string; lines: { cmd?: string; out?: string }[]; running: boolean; open: boolean }

export function streamReply(blocks: ReplyBlock[], t: number, lang: Code): ReplyPart[] {
  const l = (value: Localized) => value[lang]
  return blocks.filter(block => t >= block.at).map((block, n): ReplyPart => {
    const since = t - block.at
    if (block.kind === 'text') {
      const text = l(block.text)
      return { n, kind: 'text', blocks: reveal(parse(text), Math.round(since * 0.11)), streaming: since * 0.11 < Array.from(text).length }
    }
    if (block.kind === 'md') {
      const parsed = parse(l(block.md))
      const total = parsed.reduce((sum, b) => sum + b.parts.reduce((s, part) => s + Array.from(part.text).length, 0), 0)
      return { n, kind: 'md', blocks: reveal(parsed, Math.round(total * Math.min(1, since / block.dur))), streaming: since < block.dur }
    }
    const shown = block.lines.filter((_, i) => since >= 350 + (i * (block.dur - 500)) / Math.max(1, block.lines.length))
    return { n, kind: 'tool', title: block.title, summary: l(block.summary), cmd: block.lines.find(line => line.cmd)?.cmd ?? '', lines: shown, running: since < block.dur, open: since >= 220 }
  })
}
