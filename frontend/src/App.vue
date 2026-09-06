<script setup lang="ts">
import PlanetScene from './scene/PlanetScene.vue'
import PlanetOverview from './scene/PlanetOverview.vue'
import PlanetEvolution from './scene/PlanetEvolution.vue'
import EvolutionControls from './ui/EvolutionControls.vue'
import CoordinateHud from './ui/CoordinateHud.vue'
import ModeSwitcher from './ui/ModeSwitcher.vue'
import Compass from './ui/Compass.vue'
import GlobePositionIndicator from './ui/GlobePositionIndicator.vue'
import ViewModeToggle from './ui/ViewModeToggle.vue'
import PlanetLoadingGate from './ui/PlanetLoadingGate.vue'
import AdminPanel from './ui/AdminPanel.vue'
import { useGameStateStore } from './stores/gameState'

const gameState = useGameStateStore()
</script>

<template>
  <PlanetLoadingGate>
    <PlanetScene v-if="gameState.viewMode === 'walk'" />
    <PlanetEvolution v-else-if="gameState.viewMode === 'evolution'" />
    <PlanetOverview v-else />

    <template v-if="gameState.viewMode === 'walk'">
      <CoordinateHud />
      <ModeSwitcher />
      <Compass />
      <GlobePositionIndicator />
    </template>

    <EvolutionControls v-if="gameState.viewMode === 'evolution'" />

    <ViewModeToggle />
    <AdminPanel />
  </PlanetLoadingGate>
</template>

<style>
html,
body,
#app {
  margin: 0;
  padding: 0;
  width: 100%;
  height: 100%;
  overflow: hidden;
}
</style>
