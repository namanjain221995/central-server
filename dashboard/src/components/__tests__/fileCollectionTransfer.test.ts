import { describe, expect, it } from 'vitest'
import { FrameReader } from '../../pages/fileCollectionTransfer'

/**
 * The length-prefixed frame parser the download relies on. It must reassemble
 * frames that the network splits across arbitrary chunk boundaries, and treat a
 * zero-length frame (or the stream simply ending) as the end.
 */

function frame(payload: number[]): number[] {
  const len = payload.length
  return [(len >>> 24) & 0xff, (len >>> 16) & 0xff, (len >>> 8) & 0xff, len & 0xff, ...payload]
}

/** A stream that yields the given bytes in the given chunk sizes. */
function streamOf(bytes: number[], chunkSizes: number[]): ReadableStreamDefaultReader<Uint8Array> {
  let offset = 0
  let i = 0
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (offset >= bytes.length) {
        controller.close()
        return
      }
      const size = chunkSizes[i++] ?? bytes.length - offset
      controller.enqueue(new Uint8Array(bytes.slice(offset, offset + size)))
      offset += size
    },
  }).getReader()
}

describe('FrameReader', () => {
  it('reassembles frames split across chunk boundaries', async () => {
    const bytes = [...frame([1, 2, 3]), ...frame([9, 9]), ...frame([])] // two frames then a terminator
    // Deliberately awkward chunking: a length prefix split, a payload split.
    const reader = new FrameReader(streamOf(bytes, [2, 3, 1, 4, 1]))

    expect([...(await reader.next())!]).toEqual([1, 2, 3])
    expect([...(await reader.next())!]).toEqual([9, 9])
    expect(await reader.next()).toBeNull() // zero-length terminator
  })

  it('ends when the stream closes without a terminator', async () => {
    const reader = new FrameReader(streamOf(frame([7]), [10]))
    expect([...(await reader.next())!]).toEqual([7])
    expect(await reader.next()).toBeNull()
  })
})
