// Typed client for the host's JSON bridge (see src/Barbaric.App/Bridge/WebBridge.cs).
// UI -> host: { id, method, params }. Host -> UI: { id, ok, result | error } or { event, data }.

type Reply = { id: number; ok: true; result: unknown } | { id: number; ok: false; error: string }
type EventMessage = { event: string; data: unknown }

interface HostWebView {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void
}

const webview: HostWebView | undefined = (window as any).chrome?.webview

/** False when the UI runs in a plain browser (e.g. `npm run dev` opened directly). */
export const hasHost = webview !== undefined

const pending = new Map<number, { resolve: (value: unknown) => void; reject: (error: Error) => void }>()
const listeners = new Map<string, Set<(data: any) => void>>()
let nextId = 1

webview?.addEventListener('message', (e) => {
  const message = e.data as Reply | EventMessage
  if ('event' in message) {
    listeners.get(message.event)?.forEach((listener) => listener(message.data))
    return
  }

  const request = pending.get(message.id)
  if (!request) return
  pending.delete(message.id)
  if (message.ok) request.resolve(message.result)
  else request.reject(new Error(message.error))
})

/** Calls a host method and resolves with its result. */
export function call<T = void>(method: string, params?: Record<string, unknown>): Promise<T> {
  if (!webview) return Promise.reject(new Error(`No host available for '${method}'.`))
  const id = nextId++
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
    webview.postMessage({ id, method, params })
  })
}

/** Subscribes to a host event. Returns an unsubscribe function. */
export function on<T>(event: string, listener: (data: T) => void): () => void {
  let set = listeners.get(event)
  if (!set) listeners.set(event, (set = new Set()))
  set.add(listener)
  return () => set.delete(listener)
}
