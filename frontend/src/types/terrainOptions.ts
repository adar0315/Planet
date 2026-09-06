// 對應後端 Planet.Generation.TerrainGenerationOptions

export interface TerrainGenerationOptionsDto {
  seed: number
  continentALatitudeStretch: number
  continentALongitudeStretch: number
  continentBLatitudeStretch: number
  continentBLongitudeStretch: number
  islandCount: number
  islandClusterRatio: number
  islandVolcanicProbability: number
}
