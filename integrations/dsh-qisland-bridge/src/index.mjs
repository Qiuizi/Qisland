/**
 * dsh-qisland-bridge
 * DSH 要你人工确认时，先在 Qisland 灵动岛上弹一张审批卡（允许 / 拒绝 / 回 DSH）。
 *
 * - 请求带随机 request id / nonce 和 HMAC；响应必须来自同一个 Qisland 密钥、带回同一组 id / nonce。
 * - 只有 200、合法 JSON、decision 恰好为 allow / deny、签名与 nonce 全部匹配才采用。
 * - 岛没开、密钥读不到、超时、点「回 DSH」或任何校验失败 → 调 next()，绝不默认同意。
 * - 只连 127.0.0.1；DSH 取消这一轮时同步放弃等待。
 * - 注册时不抢在 dsh-approval-gate 之前（不 prepend），它自动放行的请求不会打扰你。
 */
import http from 'node:http'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'

const NAME = 'dsh-qisland-bridge'
const PORT = Number(process.env.QISLAND_PORT) || 9840
const configuredWaitMs = Number(process.env.QISLAND_APPROVAL_TIMEOUT_MS)
const WAIT_MS = Number.isFinite(configuredWaitMs) && configuredWaitMs > 0 ? configuredWaitMs : 110_000
const PROTOCOL = 'qisland-approval-v1'
const MAX_RESPONSE = 64 * 1024

const oneLine = (s, n) => String(s || '').replace(/\s+/g, ' ').trim().slice(0, n)

function keyPath() {
  const override = process.env.QISLAND_APPROVAL_KEY_FILE
  if (override && override.trim()) return path.resolve(override)
  if (!process.env.APPDATA) return ''
  return path.join(process.env.APPDATA, 'Qisland', 'approval.key')
}

function readSecret() {
  try {
    const file = keyPath()
    if (!file) return null
    const text = fs.readFileSync(file, 'utf8').trim()
    return /^[0-9a-f]{64}$/i.test(text) ? Buffer.from(text, 'hex') : null
  } catch {
    return null
  }
}

function token() {
  return crypto.randomBytes(16).toString('hex')
}

function hmac(key, text) {
  return crypto.createHmac('sha256', key).update(text, 'utf8').digest('hex')
}

function requestSignature(key, requestPath, requestId, nonce, body) {
  return hmac(key, `${PROTOCOL}\nPOST\n${requestPath}\n${requestId}\n${nonce}\n${body}`)
}

function responseSignature(key, requestId, nonce, decision) {
  return hmac(key, `${PROTOCOL}\n${requestId}\n${nonce}\n${decision}`)
}

function validateResponse(body, key, requestId, nonce) {
  try {
    const doc = JSON.parse(body)
    if (!doc || typeof doc !== 'object' || Array.isArray(doc) || Object.keys(doc).length !== 5) return ''
    if (doc.v !== 1 || doc.request_id !== requestId || doc.nonce !== nonce) return ''
    const decision = doc.decision
    if (decision !== 'allow' && decision !== 'deny') return ''
    const signature = doc.signature
    if (typeof signature !== 'string' || !/^[0-9a-f]{64}$/i.test(signature)) return ''
    const actual = Buffer.from(signature, 'hex')
    const expected = Buffer.from(responseSignature(key, requestId, nonce, decision), 'hex')
    return actual.length === expected.length && crypto.timingSafeEqual(actual, expected) ? decision : ''
  } catch {
    return ''
  }
}

function ask(body, signal) {
  return new Promise((resolve) => {
    let done = false
    let deadline
    const finish = (v) => {
      if (done) return
      done = true
      clearTimeout(deadline)
      signal?.removeEventListener?.('abort', onAbort)
      resolve(v)
    }
    let req
    const onAbort = () => {
      try { req?.destroy() } catch {}
      finish('')
    }

    try {
      const key = readSecret()
      if (!key) return finish('')
      const requestPath = '/v1/approval/deepseek-harness'
      const requestId = token()
      const nonce = token()
      const data = Buffer.from(JSON.stringify(body), 'utf8')
      const signature = requestSignature(key, requestPath, requestId, nonce, data.toString('utf8'))
      deadline = setTimeout(() => {
        try { req?.destroy() } catch {}
        finish('')
      }, WAIT_MS)
      req = http.request({
        host: '127.0.0.1',
        port: PORT,
        path: requestPath,
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          'content-length': data.length,
          'x-qisland-approval-version': '1',
          'x-qisland-request-id': requestId,
          'x-qisland-nonce': nonce,
          'x-qisland-signature': signature,
        },
        timeout: WAIT_MS,
      }, (res) => {
        let buf = ''
        res.setEncoding('utf8')
        res.on('data', (chunk) => {
          if (buf.length + chunk.length > MAX_RESPONSE) {
            res.destroy()
            finish('')
            return
          }
          buf += chunk
        })
        res.on('end', () => {
          if (res.statusCode !== 200) return finish('')
          finish(validateResponse(buf, key, requestId, nonce))
        })
        res.on('aborted', () => finish(''))
        res.on('error', () => finish(''))
      })
      req.on('error', () => finish(''))
      req.on('timeout', () => {
        req.destroy()
        finish('')
      })
      signal?.addEventListener?.('abort', onAbort, { once: true })
      req.end(data)
    } catch {
      finish('')
    }
  })
}

export default {
  name: NAME,
  inject: ['approval'],
  apply(ctx) {
    ctx.on('approval/request', async (req, next) => {
      try {
        if (req?.signal?.aborted) return next()
        const session = req?.agent?.session
        const cwd = session && typeof session.cwd === 'string' ? session.cwd : ''
        const tool = oneLine(req?.toolName, 60) || '工具'
        const reason = oneLine(req?.reason, 400)
        const decision = await ask({
          source: 'deepseek-harness',
          hook_event_name: 'PermissionRequest',
          session_id: session && typeof session.id === 'string' ? session.id : '',
          cwd,
          tool_name: tool,
          tool_input: { command: reason || tool },
        }, req?.signal)
        if (req?.signal?.aborted) return 'cancelled'
        if (decision === 'allow') return 'allowed-once'
        if (decision === 'deny') return 'rejected'
      } catch (err) {
        ctx.logger?.warn?.(`[${NAME}] ${err?.message || err}`)
      }
      return next()
    })
    ctx.logger?.info?.(`[${NAME}] 已启用：审批会先弹到 Qisland（127.0.0.1:${PORT}）`)
  },
}
