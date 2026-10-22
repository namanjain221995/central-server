import type { ChatSender } from '../api/client'

/**
 * Presentation logic for Live Chat, kept out of the component so the two rules
 * that matter can be asserted directly.
 *
 * The transcript places each speaker on its own side — the operator's own lines
 * to the right, the employee's to the left, and a system note centred — and the
 * short poll advances a single cursor so it only ever asks for what is new.
 * Both are pure functions of their inputs, with no clock and no component state.
 */

/** Where a message sits in the transcript: the operator right, the employee left, a system note centred. */
export function bubbleAlignment(sender: ChatSender): 'left' | 'right' | 'center' {
  switch (sender) {
    case 'Admin':
      return 'right'
    case 'Employee':
      return 'left'
    default:
      return 'center'
  }
}

/**
 * The cursor for the next poll: the highest seq seen so far.
 *
 * Returns `current` unchanged when the page is empty or every message is at or
 * below it, so an idle poll never moves the cursor backwards and a message is
 * never fetched — or shown — twice.
 */
export function advanceCursor(messages: readonly { seq: number }[], current: number): number {
  return messages.reduce((max, m) => (m.seq > max ? m.seq : max), current)
}
