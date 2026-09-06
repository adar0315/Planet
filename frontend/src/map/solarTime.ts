const DEG2RAD = Math.PI / 180
const RAD2DEG = 180 / Math.PI

export type DayPeriod = 'dawn' | 'day' | 'dusk' | 'night'

interface SolarTimes {
  /** 當地（該經緯度）日出的UTC時刻（小時，0~24），永晝/永夜時是null。 */
  sunriseUtcHours: number | null
  /** 當地日落的UTC時刻（小時，0~24），永晝/永夜時是null。 */
  sunsetUtcHours: number | null
  /** 永晝（該緯度＋當天太陽赤緯下，太陽整天不落下）。 */
  isPolarDay: boolean
  /** 永夜（太陽整天不升起）。 */
  isPolarNight: boolean
}

function normalizeHours(hours: number): number {
  return ((hours % 24) + 24) % 24
}

/**
 * 算出指定經緯度在指定日期的日出／日落UTC時刻。用跟 sunPosition.ts 同一套簡化模型
 * （忽略均時差，只用地軸傾角23.44°的季節性太陽赤緯），公式是標準的日出時角(hour angle)方程式：
 * cos(H) = -tan(緯度)*tan(赤緯)，H算出來的是「日出/日落相對當地正午的時角」，換算成小時、
 * 再依經度反推出當地正午對應的UTC時刻，就能得到日出/日落的UTC時刻。
 */
function computeSolarTimes(latitudeDegrees: number, longitudeDegrees: number, date: Date): SolarTimes {
  const startOfYearUtc = Date.UTC(date.getUTCFullYear(), 0, 1)
  const dayOfYear = Math.floor((date.getTime() - startOfYearUtc) / 86_400_000) + 1
  const declinationDeg = -23.44 * Math.cos(((2 * Math.PI) / 365) * (dayOfYear + 10))

  const latRad = latitudeDegrees * DEG2RAD
  const declRad = declinationDeg * DEG2RAD
  const cosHourAngle = -Math.tan(latRad) * Math.tan(declRad)

  if (cosHourAngle > 1) {
    return { sunriseUtcHours: null, sunsetUtcHours: null, isPolarDay: false, isPolarNight: true }
  }
  if (cosHourAngle < -1) {
    return { sunriseUtcHours: null, sunsetUtcHours: null, isPolarDay: true, isPolarNight: false }
  }

  const halfDayHours = (Math.acos(cosHourAngle) * RAD2DEG) / 15
  // 太陽直射經度＝-(utcHours-12)*15（見sunPosition.ts），反過來就是「這個經度的當地正午對應到幾點UTC」。
  const solarNoonUtc = 12 - longitudeDegrees / 15

  return {
    sunriseUtcHours: normalizeHours(solarNoonUtc - halfDayHours),
    sunsetUtcHours: normalizeHours(solarNoonUtc + halfDayHours),
    isPolarDay: false,
    isPolarNight: false,
  }
}

/**
 * 判斷指定經緯度在指定時刻屬於清晨／白天／黃昏／夜晚哪個時段。
 * 清晨＝日出後第一小時，黃昏＝日落前第一小時，其餘日出~日落之間算白天，否則算夜晚。
 * 用「距離日出/日落經過幾小時」而非直接比較時刻區間，這樣不用另外處理日出/日落時刻跨過UTC午夜的情況。
 */
export function determineDayPeriod(latitudeDegrees: number, longitudeDegrees: number, date: Date): DayPeriod {
  const times = computeSolarTimes(latitudeDegrees, longitudeDegrees, date)
  if (times.isPolarNight) return 'night'
  if (times.isPolarDay) return 'day'

  const nowUtcHours = date.getUTCHours() + date.getUTCMinutes() / 60 + date.getUTCSeconds() / 3600
  const sunrise = times.sunriseUtcHours!
  const sunset = times.sunsetUtcHours!

  const hoursSinceSunrise = normalizeHours(nowUtcHours - sunrise)
  const hoursUntilSunset = normalizeHours(sunset - nowUtcHours)
  const dayLengthHours = normalizeHours(sunset - sunrise)

  if (hoursSinceSunrise >= dayLengthHours) return 'night' // 不在日出~日落之間
  if (hoursSinceSunrise <= 1) return 'dawn'
  if (hoursUntilSunset <= 1) return 'dusk'
  return 'day'
}
