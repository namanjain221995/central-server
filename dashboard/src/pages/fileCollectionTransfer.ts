import { ApiError } from '../api/client'

/**
 * The browser half of a file collection: generate the one-time key the agent
 * encrypts to, then stream the encrypted archive down, decrypt it frame by frame,
 * and write it straight to a file the operator chooses — all client-side, so the
 * server only ever relays ciphertext it cannot read.
 *
 * The crypto mirrors the agent exactly: ECDH on P-256, HKDF-SHA256 (salt + the
 * label "epp-file-collection") to the AES-256 key, and AES-GCM per frame with the
 * frame index as additional data. The wire frame is nonce(12) || ciphertext || tag(16).
 */

const HKDF_INFO = new TextEncoder().encode('epp-file-collection')

/** The agent's key material, carried as the stream's first frame. */
interface CollectionHeader {
  ephemeralPublicKey: string
  salt: string
}

/** The agent's own verdict, read after the stream ends. */
export interface CollectionResult {
  succeeded: boolean
  message: string | null
  totalBytes: number
  frameCount: number
  truncated: boolean
}

export interface CollectionOutcome {
  /** Decrypted (plaintext archive) bytes written to disk. */
  bytesWritten: number
  /** True when the viewer's browser streamed to a file rather than buffering in memory. */
  streamedToDisk: boolean
  /** The agent's result, if the status route returned one. */
  result: CollectionResult | null
}

export interface RecipientKeyPair {
  publicKeyBase64: string
  privateKey: CryptoKey
}

function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (const b of bytes) binary += String.fromCharCode(b)
  return btoa(binary)
}

function fromBase64(value: string): Uint8Array {
  const binary = atob(value)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes
}

/** The browser's one-time ECDH P-256 keypair; the public point goes to the agent. */
export async function generateRecipientKeyPair(): Promise<RecipientKeyPair> {
  const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits'])
  const raw = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey))
  return { publicKeyBase64: toBase64(raw), privateKey: pair.privateKey }
}

// crypto.subtle wants BufferSource; a Uint8Array view is one at runtime, but TS's
// typed-array generics distinguish ArrayBuffer from ArrayBufferLike, so views are
// cast here. The runtime value is always a correct, non-shared view.
const asBuf = (view: Uint8Array): BufferSource => view as unknown as BufferSource

async function deriveAesKey(privateKey: CryptoKey, header: CollectionHeader): Promise<CryptoKey> {
  const agentPublic = await crypto.subtle.importKey(
    'raw', asBuf(fromBase64(header.ephemeralPublicKey)), { name: 'ECDH', namedCurve: 'P-256' }, false, [])
  const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: agentPublic }, privateKey, 256)
  const hkdf = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveBits'])
  const aesBits = await crypto.subtle.deriveBits(
    { name: 'HKDF', hash: 'SHA-256', salt: asBuf(fromBase64(header.salt)), info: asBuf(HKDF_INFO) }, hkdf, 256)
  return crypto.subtle.importKey('raw', aesBits, { name: 'AES-GCM' }, false, ['decrypt'])
}

async function decryptFrame(key: CryptoKey, frameIndex: number, frame: Uint8Array): Promise<Uint8Array> {
  const iv = frame.subarray(0, 12)
  const data = frame.subarray(12) // ciphertext || tag
  const aad = new Uint8Array(8)
  new DataView(aad.buffer).setBigInt64(0, BigInt(frameIndex), false) // big-endian, matches the agent
  const plaintext = await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: asBuf(iv), additionalData: asBuf(aad) }, key, asBuf(data))
  return new Uint8Array(plaintext)
}

/**
 * Reads the length-prefixed framing off a byte stream: a 4-byte big-endian length,
 * then that many bytes. A zero-length frame, or the stream closing, ends it (next
 * returns null).
 */
export class FrameReader {
  private readonly reader: ReadableStreamDefaultReader<Uint8Array>
  private buffer = new Uint8Array(0)
  private done = false

  constructor(reader: ReadableStreamDefaultReader<Uint8Array>) {
    this.reader = reader
  }

  async next(): Promise<Uint8Array | null> {
    const header = await this.read(4)
    if (header === null) return null
    const length = new DataView(header.buffer, header.byteOffset, 4).getUint32(0, false)
    if (length === 0) return null // terminator
    const payload = await this.read(length)
    if (payload === null) throw new ApiError(0, 'The download ended mid-frame.', null)
    return payload
  }

  private async read(count: number): Promise<Uint8Array | null> {
    while (this.buffer.length < count) {
      if (this.done) return null
      const { value, done } = await this.reader.read()
      if (done) {
        this.done = true
        if (this.buffer.length < count) return null
        break
      }
      if (value && value.length > 0) {
        const merged = new Uint8Array(this.buffer.length + value.length)
        merged.set(this.buffer)
        merged.set(value, this.buffer.length)
        this.buffer = merged
      }
    }
    const out = this.buffer.subarray(0, count)
    this.buffer = this.buffer.subarray(count)
    return out
  }
}

export interface DownloadSink {
  write(chunk: Uint8Array): Promise<void>
  close(): Promise<void>
  readonly streamedToDisk: boolean
}

/** True when this browser can stream a download straight to a file the viewer picks. */
export function canStreamToDisk(): boolean {
  return typeof (window as unknown as { showSaveFilePicker?: unknown }).showSaveFilePicker === 'function'
}

/**
 * Opens the download destination. Call this inside the click handler, BEFORE any
 * network await: the Save-File picker needs the user gesture, which a prior await
 * would consume. The in-memory fallback has no such constraint.
 */
export async function openDownloadSink(suggestedName: string): Promise<DownloadSink> {
  const picker = (window as unknown as {
    showSaveFilePicker?: (o: { suggestedName?: string }) => Promise<FileSystemFileHandle>
  }).showSaveFilePicker

  if (typeof picker === 'function') {
    const handle = await picker({ suggestedName })
    const writable = await handle.createWritable()
    return {
      streamedToDisk: true,
      write: (chunk) => writable.write(chunk as unknown as FileSystemWriteChunkType),
      close: () => writable.close(),
    }
  }

  // Fallback: buffer in memory and trigger a normal download. Bounded by RAM, so the
  // panel warns before using it for a large collection.
  const parts: Uint8Array[] = []
  return {
    streamedToDisk: false,
    write: (chunk) => {
      parts.push(chunk.slice())
      return Promise.resolve()
    },
    close: () => {
      const blob = new Blob(parts as BlobPart[], { type: 'application/zip' })
      const url = URL.createObjectURL(blob)
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = suggestedName
      anchor.click()
      setTimeout(() => URL.revokeObjectURL(url), 10_000)
      return Promise.resolve()
    },
  }
}

/**
 * Streams the collection for <paramref /> transferId, decrypts it with the one-time
 * private key, and writes the archive to a file. Returns once the archive is written
 * and the agent's result has been read.
 */
export async function downloadCollection(
  deviceId: string,
  transferId: string,
  privateKey: CryptoKey,
  sink: DownloadSink,
  onProgress?: (bytesWritten: number) => void,
): Promise<CollectionOutcome> {
  let bytesWritten = 0
  try {
    await streamDecrypted(deviceId, transferId, privateKey, async (chunk) => {
      await sink.write(chunk)
      bytesWritten += chunk.length
      onProgress?.(bytesWritten)
    })
    await sink.close()
  } catch (error) {
    try {
      await sink.close()
    } catch {
      // The primary error is the one that matters.
    }
    throw error
  }

  return { bytesWritten, streamedToDisk: sink.streamedToDisk, result: await fetchStatus(deviceId, transferId) }
}

/** One entry in a browse listing, as the agent reports it. */
export interface FileEntry {
  /** Relative path including the root key, e.g. "Desktop/report.docx". */
  path: string
  isDirectory: boolean
  sizeBytes: number
}

export interface FileListing {
  truncated: boolean
  entries: FileEntry[]
}

/**
 * Starts nothing itself: given a browse transfer the caller already started, streams
 * the encrypted listing, decrypts it in memory and returns the file tree. The listing
 * is small, so it is buffered rather than streamed to disk.
 */
export async function browseListing(
  deviceId: string,
  transferId: string,
  privateKey: CryptoKey,
): Promise<FileListing> {
  const chunks: Uint8Array[] = []
  await streamDecrypted(deviceId, transferId, privateKey, async (chunk) => {
    chunks.push(chunk)
  })

  const total = chunks.reduce((n, c) => n + c.length, 0)
  const buffer = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    buffer.set(chunk, offset)
    offset += chunk.length
  }

  return JSON.parse(new TextDecoder().decode(buffer)) as FileListing
}

function transferBase(deviceId: string, transferId: string): string {
  return `/api/admin/v1/devices/${encodeURIComponent(deviceId)}/file-collections/${encodeURIComponent(transferId)}`
}

/**
 * The shared core: open the transfer stream, read the header, derive the key and hand
 * each decrypted frame to <paramref />onPlaintext. The collection writes it to disk;
 * the browse accumulates it into the listing.
 */
async function streamDecrypted(
  deviceId: string,
  transferId: string,
  privateKey: CryptoKey,
  onPlaintext: (chunk: Uint8Array) => Promise<void>,
): Promise<void> {
  const response = await fetch(`${transferBase(deviceId, transferId)}/stream`, {
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
  if (!response.ok || !response.body) {
    const problem = await response.json().catch(() => null)
    throw new ApiError(
      response.status,
      problem?.title ?? problem?.detail ?? `The device did not start the transfer (HTTP ${response.status}).`,
      response.headers.get('X-Correlation-Id'),
    )
  }

  const reader = new FrameReader(response.body.getReader())
  const headerBytes = await reader.next()
  if (headerBytes === null) {
    throw new ApiError(0, 'The transfer ended before any data arrived.', null)
  }
  const header = JSON.parse(new TextDecoder().decode(headerBytes)) as CollectionHeader
  const key = await deriveAesKey(privateKey, header)

  let frameIndex = 0
  while (true) {
    const frame = await reader.next()
    if (frame === null) break
    await onPlaintext(await decryptFrame(key, frameIndex, frame))
    frameIndex += 1
  }
}

async function fetchStatus(deviceId: string, transferId: string): Promise<CollectionResult | null> {
  try {
    const status = await fetch(`${transferBase(deviceId, transferId)}/status`, {
      headers: { 'X-Requested-With': 'XMLHttpRequest' },
    })
    if (status.ok && status.status !== 204) {
      return (await status.json()) as CollectionResult
    }
  } catch {
    // The archive is already on disk; a missing status only costs the success note.
  }
  return null
}
