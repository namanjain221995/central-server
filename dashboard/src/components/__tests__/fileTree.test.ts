import { describe, expect, it } from 'vitest'
import type { FileEntry } from '../../pages/fileCollectionTransfer'
import { buildTree, coveredByAncestor, selectionStats } from '../../pages/fileTree'

const entries: FileEntry[] = [
  { path: 'Desktop', isDirectory: true, sizeBytes: 0 },
  { path: 'Desktop/a.txt', isDirectory: false, sizeBytes: 100 },
  { path: 'Desktop/sub', isDirectory: true, sizeBytes: 0 },
  { path: 'Desktop/sub/b.txt', isDirectory: false, sizeBytes: 50 },
  { path: 'Downloads', isDirectory: true, sizeBytes: 0 },
  { path: 'Downloads/c.txt', isDirectory: false, sizeBytes: 200 },
]

describe('buildTree', () => {
  it('builds the forest, sizes folders, and sorts folders before files', () => {
    const roots = buildTree(entries)
    expect(roots.map((r) => r.path)).toEqual(['Desktop', 'Downloads'])

    const desktop = roots[0]
    expect(desktop.totalBytes).toBe(150) // a.txt (100) + sub/b.txt (50)
    expect(desktop.children.map((c) => c.name)).toEqual(['sub', 'a.txt']) // folder first
    expect(desktop.children[0].totalBytes).toBe(50)
    expect(roots[1].totalBytes).toBe(200)
  })

  it('creates a missing intermediate folder from a deep file path', () => {
    const roots = buildTree([{ path: 'Desktop/x/y/z.txt', isDirectory: false, sizeBytes: 7 }])
    const desktop = roots.find((r) => r.path === 'Desktop')!
    expect(desktop.isDirectory).toBe(true)
    expect(desktop.totalBytes).toBe(7)
    expect(desktop.children[0].path).toBe('Desktop/x')
  })
})

describe('coveredByAncestor', () => {
  it('is true only when a ticked ancestor covers the path', () => {
    expect(coveredByAncestor('Desktop/sub/b.txt', new Set(['Desktop']))).toBe(true)
    expect(coveredByAncestor('Desktop/sub/b.txt', new Set(['Desktop/sub']))).toBe(true)
    expect(coveredByAncestor('Desktop/a.txt', new Set(['Downloads']))).toBe(false)
    expect(coveredByAncestor('Desktop', new Set(['Desktop']))).toBe(false) // itself, not an ancestor
  })
})

describe('selectionStats', () => {
  const roots = buildTree(entries)

  it('counts every file under a selected folder', () => {
    expect(selectionStats(roots, new Set(['Desktop']))).toEqual({ files: 2, bytes: 150 })
  })

  it('counts a single selected file', () => {
    expect(selectionStats(roots, new Set(['Desktop/a.txt']))).toEqual({ files: 1, bytes: 100 })
  })

  it('is empty when nothing is selected', () => {
    expect(selectionStats(roots, new Set())).toEqual({ files: 0, bytes: 0 })
  })

  it('does not double-count a file whose folder is also selected', () => {
    expect(selectionStats(roots, new Set(['Desktop', 'Desktop/a.txt']))).toEqual({ files: 2, bytes: 150 })
  })
})
