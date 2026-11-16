/**
 * dsh-qisland-bridge
 * DSH 要你人工确认时，先在 Qisland 灵动岛上弹一张审批卡（允许 / 拒绝 / 回 DSH）。
 *
 * 做法与 CodeIsland 的 DSH 桥一致（先认领、不行再交回）：
 * - 岛上点「允许」→ 返回 'allowed-once'；点「拒绝」→ 返回 'rejected'，DSH 自己的弹窗不会出现。
 * - 岛没开、超时、点「回 DSH」或出错 → 调 next()，交回 DSH 自己的审批界面，绝不默认同意。
 * - 只连 127.0.0.1；DSH 取消这一轮时同步放弃等待。
 * 注册时不抢在 dsh-approval-gate 之前（不 prepend），它自动放行的请求不会打扰你。
 */
import http from 'node:http'

const NAME = 'dsh-qisland-bridge'
const PORT = Number(process.env.QISLAND_PORT) || 9840
const WAIT_MS = 110_000 // 岛上 ApprovalBroker 等 100 秒，多留一点余量

const oneLine = (s, n) => String(s || '').replace(/\s+/g, ' ').trim().slice(0, n)

function ask(body, signal) {
  return new Promise((resolve) => {
    let done = false
    const finish = (v) => { if (!done) { done = true; signal?.removeEventListener?.('abort', onAbort); resolve(v) } }
    let req
    const onAbort = () => { try { req?.destroy() } catch {} ; finish('') }
    try {
      const data = Buffer.from(JSON.stringify(body), 'utf8')
      req = http.request({
        host: '127.0.0.1', port: PORT, path: '/v1/approval/deepseek-harness', method: 'POST',
        headers: { 'content-type': 'application/json', 'content-length': data.length },
        timeout: WAIT_MS,
      }, (res) => {
        let buf = ''
        res.setEncoding('utf8')
        res.on('data', (c) => { buf += c })
        res.on('end', () => {
          try { finish(String(JSON.parse(buf).decision || '')) } catch { finish('') }
        })
        res.on('error', () => finish(''))
      })
      req.on('error', () => finish(''))
      req.on('timeout', () => { req.destroy(); finish('') })
      signal?.addEventListener?.('abort', onAbort, { once: true })
      req.end(data)
    } catch { finish('') }
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
