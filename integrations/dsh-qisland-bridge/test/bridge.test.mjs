import test from 'node:test'
import assert from 'node:assert/strict'
import http from 'node:http'
import crypto from 'node:crypto'
import fs from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'

const PROTOCOL = 'qisland-approval-v1'
const KEY = '4a8e6d9271c35f0b5e2a9d8347c6b1f0a5d3e8c2964b7f1d0a6c9e2b5f8d3a71'

const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), 'qisland-bridge-test-'))
const keyPath = path.join(tempDir, 'approval.key')
await fs.writeFile(keyPath, KEY, 'utf8')

let reply = (_req, res) => respond(res, 500, '{}')
let requests = 0
const server = http.createServer(async (req, res) => {
  requests++
  try {
    await reply(req, res)
  } catch (err) {
    respond(res, 500, JSON.stringify({ error: String(err?.message || err) }))
  }
})
await new Promise((resolve, reject) => {
  server.once('error', reject)
  server.listen(0, '127.0.0.1', resolve)
})
const port = server.address().port

process.env.QISLAND_PORT = String(port)
process.env.QISLAND_APPROVAL_KEY_FILE = keyPath
process.env.QISLAND_APPROVAL_TIMEOUT_MS = '300'

const plugin = (await import(`../src/index.mjs?test=${Date.now()}`)).default

async function invoke(signal = new AbortController().signal) {
  let nextCalls = 0
  const handlers = new Map()
  plugin.apply({
    on(name, handler) { handlers.set(name, handler) },
    logger: { info() {}, warn() {} },
  })
  const handler = handlers.get('approval/request')
  assert.equal(typeof handler, 'function')
  const result = await handler({
    agent: { session: { id: 'session-1', cwd: 'C:\\work\\demo' } },
    toolName: 'Bash',
    reason: 'echo allow',
    signal,
  }, () => {
    nextCalls++
    return 'fallback'
  })
  return { result, nextCalls }
}

function respond(res, status, body) {
  const data = Buffer.from(body, 'utf8')
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': data.length,
  })
  res.end(data)
}

function hmac(text) {
  return crypto.createHmac('sha256', Buffer.from(KEY, 'hex')).update(text, 'utf8').digest('hex')
}

function requestSignature(pathname, requestId, nonce, body) {
  return hmac(`${PROTOCOL}\nPOST\n${pathname}\n${requestId}\n${nonce}\n${body}`)
}

function responseSignature(requestId, nonce, decision) {
  return hmac(`${PROTOCOL}\n${requestId}\n${nonce}\n${decision}`)
}

async function bodyOf(req) {
  const chunks = []
  for await (const chunk of req) chunks.push(chunk)
  return Buffer.concat(chunks).toString('utf8')
}

function header(req, name) {
  const value = req.headers[name.toLowerCase()]
  return Array.isArray(value) ? value[0] : String(value || '')
}

async function signedReply(decision = 'allow', overrides = {}) {
  return async (req, res) => {
    const body = await bodyOf(req)
    const requestId = header(req, 'x-qisland-request-id')
    const nonce = header(req, 'x-qisland-nonce')
    const version = header(req, 'x-qisland-approval-version')
    const actual = header(req, 'x-qisland-signature')
    const expected = requestSignature(req.url, requestId, nonce, body)
    if (version !== '1' || requestId.length !== 32 || nonce.length !== 32 || actual !== expected) {
      respond(res, 401, '{"ok":false}')
      return
    }
    const responseNonce = overrides.nonce ?? nonce
    const responseId = overrides.requestId ?? requestId
    const responseDecision = overrides.decision ?? decision
    const signature = responseSignature(responseId, responseNonce, responseDecision)
    respond(res, 200, JSON.stringify({
      v: 1,
      request_id: responseId,
      nonce: responseNonce,
      decision: responseDecision,
      signature,
    }))
  }
}

test.after(async () => {
  await new Promise((resolve) => server.close(resolve))
  await fs.rm(tempDir, { recursive: true, force: true })
})

test('protocol vectors match the C# client', () => {
  const key = '0011223344556677' + '8899aabbccddeeff' + '0011223344556677' + '8899aabbccddeeff'
  const requestId = '00112233445566778899aabbccddeeff'
  const nonce = 'ffeeddccbbaa99887766554433221100'
  const body = '{"hook_event_name":"PermissionRequest","tool_name":"Bash"}'
  const sign = (text) => crypto.createHmac('sha256', Buffer.from(key, 'hex')).update(text, 'utf8').digest('hex')
  assert.equal(
    sign(`qisland-approval-v1\nPOST\n/v1/approval/codex\n${requestId}\n${nonce}\n${body}`),
    '2a978ed54981481927470995533d1c65719e57f432d600433b6e4e355c720eb5')
  assert.equal(
    sign(`qisland-approval-v1\n${requestId}\n${nonce}\nallow`),
    '5c25c3b2756fab6b3205b1be44a4922b5ab8cee1e2cd69644486b5b6e8e6082a')
})

test('unsigned allow falls back to DSH', async () => {
  reply = async (_req, res) => respond(res, 200, '{"decision":"allow"}')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('allow word outside decision falls back to DSH', async () => {
  reply = async (_req, res) => respond(res, 200, '{"note":"allow"}')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('nonce mismatch falls back to DSH', async () => {
  reply = async (req, res) => {
    const body = await bodyOf(req)
    const requestId = header(req, 'x-qisland-request-id') || 'a'.repeat(32)
    const nonce = '0'.repeat(32)
    const decision = 'allow'
    respond(res, 200, JSON.stringify({
      v: 1,
      request_id: requestId,
      nonce,
      decision,
      signature: responseSignature(requestId, nonce, decision),
    }))
  }
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('request id mismatch falls back to DSH', async () => {
  reply = async (req, res) => {
    const requestId = 'f'.repeat(32)
    const nonce = header(req, 'x-qisland-nonce') || 'b'.repeat(32)
    const decision = 'allow'
    respond(res, 200, JSON.stringify({
      v: 1,
      request_id: requestId,
      nonce,
      decision,
      signature: responseSignature(requestId, nonce, decision),
    }))
  }
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('non-200 response falls back to DSH', async () => {
  reply = async (_req, res) => respond(res, 500, '{"decision":"allow"}')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('slow response body falls back at total timeout', async () => {
  const abort = new AbortController()
  reply = async (_req, res) => {
    res.writeHead(200, { 'content-type': 'application/json; charset=utf-8' })
    res.write('{')
    const keepAlive = setInterval(() => {
      try { res.write(' ') } catch {}
    }, 50)
    res.on('close', () => clearInterval(keepAlive))
  }

  const task = invoke(abort.signal)
  let raceTimer
  const outcome = await Promise.race([
    task.then(value => ({ value })),
    new Promise(resolve => {
      raceTimer = setTimeout(() => resolve({ timedOut: true }), 1200)
    }),
  ])
  clearTimeout(raceTimer)
  if (outcome.timedOut) {
    abort.abort()
    await task.catch(() => {})
  }

  assert.equal(outcome.timedOut, undefined)
  assert.equal(outcome.value.result, 'fallback')
  assert.equal(outcome.value.nextCalls, 1)
})

test('malformed JSON falls back to DSH', async () => {
  reply = async (_req, res) => respond(res, 200, '{"decision":"allow"')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('invalid signature falls back to DSH', async () => {
  reply = async (req, res) => {
    const requestId = header(req, 'x-qisland-request-id')
    const nonce = header(req, 'x-qisland-nonce')
    respond(res, 200, JSON.stringify({
      v: 1,
      request_id: requestId,
      nonce,
      decision: 'allow',
      signature: '0'.repeat(64),
    }))
  }
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
})

test('correctly signed allow is accepted', async () => {
  reply = await signedReply('allow')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'allowed-once')
  assert.equal(nextCalls, 0)
})

test('correctly signed deny is accepted', async () => {
  reply = await signedReply('deny')
  const { result, nextCalls } = await invoke()
  assert.equal(result, 'rejected')
  assert.equal(nextCalls, 0)
})

test('missing key falls back without contacting the island', async () => {
  process.env.QISLAND_APPROVAL_KEY_FILE = path.join(tempDir, 'missing.key')
  const before = requests
  reply = await signedReply('allow')
  const { result, nextCalls } = await invoke()
  process.env.QISLAND_APPROVAL_KEY_FILE = keyPath
  assert.equal(result, 'fallback')
  assert.equal(nextCalls, 1)
  assert.equal(requests, before)
})
