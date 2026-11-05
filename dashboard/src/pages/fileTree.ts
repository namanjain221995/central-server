import type { FileEntry } from './fileCollectionTransfer'

/**
 * Pure logic for the browse tree: turn the agent's flat path list into a tree, add up
 * folder sizes, and resolve what a selection actually collects. No React here, so the
 * tricky parts — folder sizing and "is this covered by a ticked ancestor?" — are tested
 * directly.
 */

export interface TreeNode {
  /** Full relative path including the root key, e.g. "Desktop/sub". */
  path: string
  /** The last segment, for display. */
  name: string
  isDirectory: boolean
  /** A file's own size; 0 for a folder. */
  sizeBytes: number
  /** Total bytes under this node (its own size, or the sum of descendant files). */
  totalBytes: number
  children: TreeNode[]
}

function parentPath(path: string): string | null {
  const i = path.lastIndexOf('/')
  return i < 0 ? null : path.slice(0, i)
}

function lastSegment(path: string): string {
  const i = path.lastIndexOf('/')
  return i < 0 ? path : path.slice(i + 1)
}

/** Builds the forest of root nodes from the flat listing, sized and sorted. */
export function buildTree(entries: FileEntry[]): TreeNode[] {
  const nodes = new Map<string, TreeNode>()

  // Create a node and, on first creation, its whole ancestor chain, attaching it to
  // its parent exactly once. The agent lists every folder, but this also copes if an
  // intermediate folder is ever missing.
  const ensure = (path: string, isDirectory: boolean, sizeBytes: number): TreeNode => {
    const existing = nodes.get(path)
    if (existing) {
      if (isDirectory) existing.isDirectory = true
      return existing
    }

    const node: TreeNode = {
      path, name: lastSegment(path), isDirectory, sizeBytes, totalBytes: 0, children: [],
    }
    nodes.set(path, node)

    const parent = parentPath(path)
    if (parent !== null) {
      ensure(parent, true, 0).children.push(node)
    }
    return node
  }

  for (const entry of entries) {
    ensure(entry.path, entry.isDirectory, entry.isDirectory ? 0 : entry.sizeBytes)
  }

  const roots = [...nodes.values()].filter((n) => parentPath(n.path) === null)

  const sizeOf = (node: TreeNode): number => {
    node.totalBytes = node.isDirectory
      ? node.children.reduce((sum, c) => sum + sizeOf(c), 0)
      : node.sizeBytes
    node.children.sort(compareNodes)
    return node.totalBytes
  }
  roots.forEach(sizeOf)
  roots.sort(compareNodes)
  return roots
}

function compareNodes(a: TreeNode, b: TreeNode): number {
  if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1
  return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
}

/** True when a ticked ancestor already covers this path (so it cannot be toggled on its own). */
export function coveredByAncestor(path: string, selected: ReadonlySet<string>): boolean {
  let parent = parentPath(path)
  while (parent !== null) {
    if (selected.has(parent)) return true
    parent = parentPath(parent)
  }
  return false
}

/** True when some descendant of this folder is ticked (for the indeterminate state). */
export function hasSelectedDescendant(node: TreeNode, selected: ReadonlySet<string>): boolean {
  for (const child of node.children) {
    if (selected.has(child.path) || hasSelectedDescendant(child, selected)) return true
  }
  return false
}

export interface SelectionStats {
  files: number
  bytes: number
}

/** How many files, and how many bytes, the current selection would collect. */
export function selectionStats(roots: TreeNode[], selected: ReadonlySet<string>): SelectionStats {
  let files = 0
  let bytes = 0
  const walk = (node: TreeNode, covered: boolean) => {
    const here = covered || selected.has(node.path)
    if (!node.isDirectory && here) {
      files += 1
      bytes += node.sizeBytes
    }
    for (const child of node.children) walk(child, here)
  }
  roots.forEach((r) => walk(r, false))
  return { files, bytes }
}
