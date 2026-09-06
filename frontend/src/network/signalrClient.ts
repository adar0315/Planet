import * as signalR from '@microsoft/signalr'
import type { TileDto } from '@/types/tile'

const HUB_URL = import.meta.env.VITE_HUB_URL ?? 'http://localhost:5202/hubs/movement'

let connection: signalR.HubConnection | null = null
let startPromise: Promise<void> | null = null
let stopPromise: Promise<void> | null = null

export function getMovementConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder().withUrl(HUB_URL).withAutomaticReconnect().build()
  }
  return connection
}

/** 確保連線已啟動（重複呼叫安全；若前一個連線正在關閉中，會先等它關閉再重新啟動）。 */
export async function ensureMovementConnectionStarted(): Promise<void> {
  if (stopPromise) await stopPromise

  const conn = getMovementConnection()
  if (conn.state === signalR.HubConnectionState.Connected) return
  if (!startPromise) startPromise = conn.start().finally(() => (startPromise = null))
  await startPromise
}

/** 請後端驗證 fromTileId→toTileId 是否為合法鄰居移動，合法才會回傳目標Tile資料，否則回傳null。 */
export async function requestMoveTo(fromTileId: number, toTileId: number): Promise<TileDto | null> {
  await ensureMovementConnectionStarted()
  return getMovementConnection().invoke<TileDto | null>('MoveTo', fromTileId, toTileId)
}

export function stopMovementConnection(): Promise<void> {
  if (!connection) return Promise.resolve()
  if (!stopPromise) stopPromise = connection.stop().finally(() => (stopPromise = null))
  return stopPromise
}
