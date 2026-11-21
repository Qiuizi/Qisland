/**
 * 可选草稿。依据公开的 Cordis 插件教程和 agent 生命周期文档：
 * https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/cordis-tutorial/01-first-plugin.zh.md
 * https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/agent-lifecycle.zh.md
 *
 * 只观察，不拦截。tools/pre-execute 必须继续调用 next()，否则会卡住工具。
 * 没有在 DeepSeek Harness 0.2.2-rc.1 桌面版里运行过。
 */
import type { Context } from '@deepseek-ai/cordis'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

export const name = 'qisland'

const endpoint = () => process.env.QISLAND_URL ?? 'http://127.0.0.1:9840/v1/qisland/ingest'

// Token：环境变量 QISLAND_TOKEN 优先，其次是 %APPDATA%\Qisland\api-token.txt（没有就不带）
function token(): string {
  if (process.env.QISLAND_TOKEN) return process.env.QISLAND_TOKEN.trim()
  try {
    const file = process.env.QISLAND_API_TOKEN_FILE ?? join(process.env.APPDATA ?? '', 'Qisland', 'api-token.txt')
    return readFileSync(file, 'utf8').trim()
  } catch {
    return ''
  }
}

async function post(body: Record<string, unknown>): Promise<void> {
  try {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' }
    const t = token()
    if (t) headers['X-Qisland-Token'] = t
    await fetch(endpoint(), { method: 'POST', headers, body: JSON.stringify(body) })
  } catch {
    // 岛没开就跳过，不影响 Harness。
  }
}

function textOf(value: unknown): string {
  if (typeof value === 'string') return value
  try { return JSON.stringify(value) } catch { return '' }
}

export function apply(ctx: Context) {
  ctx.on('agent/status', (status: unknown) => {
    const text = textOf(status).toLowerCase()
    const running = text.includes('running')
    void post({
      source: 'deepseek-harness',
      session_id: 'desktop',
      status: running ? 'running' : 'done',
      title: running ? 'DeepSeek Harness 进行中' : 'DeepSeek Harness 空闲',
      detail: textOf(status).slice(0, 180),
    })
  })

  ctx.on('agent/turn-stopping', () => {
    void post({
      source: 'deepseek-harness',
      session_id: 'desktop',
      status: 'done',
      title: 'DeepSeek Harness 已完成',
    })
  })

  // auto-review 使用的是同一个事件名。这里只报「即将调用工具」，然后把决定权交回去。
  ctx.on('tools/pre-execute', async (exec: unknown, next: () => Promise<unknown>) => {
    void post({
      source: 'deepseek-harness',
      session_id: 'desktop',
      status: 'running',
      title: 'DeepSeek Harness 进行中',
      detail: textOf(exec).slice(0, 180),
    })
    return next()
  })
}
