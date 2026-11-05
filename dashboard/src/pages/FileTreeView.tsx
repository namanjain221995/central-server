import { useEffect, useRef } from 'react'
import { Icon } from '../components/Icon'
import { coveredByAncestor, hasSelectedDescendant, type TreeNode } from './fileTree'

/**
 * The browse tree: expand folders, tick files or whole folders, see sizes. Ticking a
 * folder covers everything under it, so its descendants show as checked and disabled —
 * to exclude one, untick the folder and tick the specific children instead.
 */
export function FileTree({
  roots,
  selected,
  expanded,
  onToggleSelect,
  onToggleExpand,
  formatBytes,
}: {
  roots: TreeNode[]
  selected: ReadonlySet<string>
  expanded: ReadonlySet<string>
  onToggleSelect: (path: string) => void
  onToggleExpand: (path: string) => void
  formatBytes: (bytes: number) => string
}) {
  return (
    <div role="tree" style={{ maxHeight: 360, overflow: 'auto', border: '1px solid var(--color-border)', borderRadius: 'var(--radius)', padding: '6px 4px' }}>
      {roots.map((node) => (
        <TreeRow
          key={node.path}
          node={node}
          depth={0}
          selected={selected}
          expanded={expanded}
          onToggleSelect={onToggleSelect}
          onToggleExpand={onToggleExpand}
          formatBytes={formatBytes}
        />
      ))}
    </div>
  )
}

function TreeRow({
  node,
  depth,
  selected,
  expanded,
  onToggleSelect,
  onToggleExpand,
  formatBytes,
}: {
  node: TreeNode
  depth: number
  selected: ReadonlySet<string>
  expanded: ReadonlySet<string>
  onToggleSelect: (path: string) => void
  onToggleExpand: (path: string) => void
  formatBytes: (bytes: number) => string
}) {
  const coveredByParent = coveredByAncestor(node.path, selected)
  const checked = selected.has(node.path) || coveredByParent
  const indeterminate = node.isDirectory && !checked && hasSelectedDescendant(node, selected)
  const isOpen = expanded.has(node.path)

  return (
    <div role="treeitem" aria-expanded={node.isDirectory ? isOpen : undefined}>
      <div
        style={{
          display: 'flex', alignItems: 'center', gap: 6,
          padding: '2px 4px', paddingLeft: 4 + depth * 18, fontSize: 13, borderRadius: 4,
        }}
      >
        {node.isDirectory ? (
          <button
            type="button"
            aria-label={isOpen ? 'Collapse' : 'Expand'}
            onClick={() => onToggleExpand(node.path)}
            style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 0, lineHeight: 0, color: 'var(--color-text-secondary)' }}
          >
            <Icon name={isOpen ? 'chevron-down' : 'chevron-right'} size={14} />
          </button>
        ) : (
          <span style={{ width: 14, display: 'inline-block' }} />
        )}

        <TriStateCheckbox
          checked={checked}
          indeterminate={indeterminate}
          disabled={coveredByParent}
          onChange={() => onToggleSelect(node.path)}
        />

        <span style={{ flex: 1, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {node.isDirectory ? <strong>{node.name}</strong> : node.name}
        </span>
        <span className="muted" style={{ fontSize: 12 }}>{formatBytes(node.totalBytes)}</span>
      </div>

      {node.isDirectory && isOpen &&
        node.children.map((child) => (
          <TreeRow
            key={child.path}
            node={child}
            depth={depth + 1}
            selected={selected}
            expanded={expanded}
            onToggleSelect={onToggleSelect}
            onToggleExpand={onToggleExpand}
            formatBytes={formatBytes}
          />
        ))}
    </div>
  )
}

function TriStateCheckbox({
  checked,
  indeterminate,
  disabled,
  onChange,
}: {
  checked: boolean
  indeterminate: boolean
  disabled: boolean
  onChange: () => void
}) {
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate
  }, [indeterminate])
  return <input ref={ref} type="checkbox" checked={checked} disabled={disabled} onChange={onChange} />
}
