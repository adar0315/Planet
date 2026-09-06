/**
 * 太陽直射點（subsolar point）的簡化計算：忽略均時差（equation of time，最多約±16分鐘的修正），
 * 對遊戲用途來說精度足夠——重點是晝夜週期跟緯度季節性正確，不是天文台等級的精確度。
 */
export function computeSubsolarPoint(date: Date): { latitude: number; longitude: number } {
  const startOfYearUtc = Date.UTC(date.getUTCFullYear(), 0, 1)
  const dayOfYear = Math.floor((date.getTime() - startOfYearUtc) / 86_400_000) + 1

  // 太陽赤緯：地軸傾角23.44°造成的季節性南北擺動，近似公式（誤差約在1°內）。
  const declination = -23.44 * Math.cos(((2 * Math.PI) / 365) * (dayOfYear + 10))

  // 太陽直射的經度：UTC 12:00時直射本初子午線（0°），每小時往西移15°（地球自轉）。
  const utcHours = date.getUTCHours() + date.getUTCMinutes() / 60 + date.getUTCSeconds() / 3600
  const rawLongitude = -(utcHours - 12) * 15
  const longitude = ((((rawLongitude + 180) % 360) + 360) % 360) - 180

  return { latitude: declination, longitude }
}
