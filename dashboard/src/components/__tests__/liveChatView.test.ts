import { describe, expect, it } from 'vitest'
import { advanceCursor, bubbleAlignment } from '../../pages/liveChatView'

describe('bubbleAlignment', () => {
  it('puts the operator on the right, the employee on the left, system notes centred', () => {
    expect(bubbleAlignment('Admin')).toBe('right')
    expect(bubbleAlignment('Employee')).toBe('left')
    expect(bubbleAlignment('System')).toBe('center')
  })
})

describe('advanceCursor', () => {
  it('returns the highest seq in the page', () => {
    expect(advanceCursor([{ seq: 3 }, { seq: 7 }, { seq: 5 }], 0)).toBe(7)
  })

  it('keeps the current cursor when the page is empty', () => {
    expect(advanceCursor([], 12)).toBe(12)
  })

  it('never moves the cursor backwards', () => {
    // A late-arriving lower seq (or a re-read) must not rewind the cursor.
    expect(advanceCursor([{ seq: 2 }, { seq: 4 }], 9)).toBe(9)
  })

  it('advances past the current cursor when newer messages arrive', () => {
    expect(advanceCursor([{ seq: 10 }, { seq: 11 }], 9)).toBe(11)
  })
})
