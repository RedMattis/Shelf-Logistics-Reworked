using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Noise;
using static RimWorld.FleshTypeDef;

namespace Shelf
{
    [DefOf]
    public static class SDefsOf
    {
        public static ThingDef SL_LogisticOutput;
    }

    public class LogisticsMapComponent : MapComponent
    {
        protected int tick = 0;
        private bool queueRefresh = false;
        public HashSet<LogisticsOutputComp> PickupPoints { get; } = [];
        public HashSet<IntVec3> DepotTiles { get; } = [];
        public LogisticsMapComponent(Map map) : base(map){}
        public ThingWithComps GetClosestPickup(IntVec3 pos)
        {
            float bestDist = float.MaxValue;
            ThingWithComps closest = null;
            foreach(var pp in PickupPoints)
            {
                var dist = pp.parent.Position.DistanceToSquared(pos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    closest = pp.parent;
                }
            }
            return closest;
        }
        public override void MapComponentTick()
        {
            base.MapComponentTick();
            tick++;

            if (tick < 2000 && !queueRefresh)
            {
                return;
            }
            tick = 0;
            queueRefresh = false;

            RefreshDepotTilesNow();
        }

        public void RefreshDepotTilesNow()
        {
            DepotTiles.Clear();
            foreach (var item in map.listerBuildings.AllBuildingsColonistOfClass<LogisticsDepot>())
            {
                DepotTiles.UnionWith(item.GetLogisticCellsAroundThis());
            }
        }

        public void RefreshDepotTilesSoon()
        {
            queueRefresh = true;
        }
    }

    public class CompProperties_LogisticsOutput : CompProperties { }
    public class LogisticsOutputComp : ThingComp
    {
        protected LogisticsMapComponent logisticsMapComp = null;
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (parent.MapHeld is Map map && map.GetComponent<LogisticsMapComponent>() is var lmc)
            {
                lmc.PickupPoints.Add(this);
                logisticsMapComp = lmc;
            }
        }
        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (logisticsMapComp != null)
            {
                logisticsMapComp.PickupPoints.Remove(this);
            }
        }
    }

    public class LogisticsDepot : Building
    {
        private const float logisticRadius = 7.9f;
        public readonly HashSet<IntVec3> logisticCells = [];
        public static IntVec3 LastCheckedPos = IntVec3.Invalid;
        private static readonly List<IntVec3> logisticCellsTemp = [];
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (map.GetComponent<LogisticsMapComponent>() is var lmc)
            {
                lmc.RefreshDepotTilesSoon();
            }
        }
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            if (Map?.GetComponent<LogisticsMapComponent>() is var lmc)
            {
                lmc.RefreshDepotTilesSoon();
            }
            base.DeSpawn(mode);
            
        }
        public HashSet<IntVec3> GetLogisticCellsAroundThis()
        {
            logisticCells.Clear();
            var pos = Position;
            var region = this.GetRegion();
            if (region == null) return [];
            RegionTraverser.BreadthFirstTraverse(region, (Region from, Region r) => r.door == null, delegate (Region r)
            {
                foreach (IntVec3 cell in r.Cells)
                {
                    if (cell.InHorDistOf(pos, logisticRadius))
                    {
                        logisticCells.Add(cell);
                    }
                }
                return false;
            }, 16);
            return logisticCells;
        }
        public static List<IntVec3> LogisticCellsAround(IntVec3 pos, Map map)
        {
            if (LastCheckedPos == pos) return logisticCellsTemp;
            LastCheckedPos = pos;
            logisticCellsTemp.Clear();
            if (!pos.InBounds(map))
            {
                return logisticCellsTemp;
            }
            Region region = pos.GetRegion(map);
            if (region == null)
            {
                return logisticCellsTemp;
            }
            RegionTraverser.BreadthFirstTraverse(region, (Region from, Region r) => r.door == null, delegate (Region r)
            {
                foreach (IntVec3 cell in r.Cells)
                {
                    if (cell.InHorDistOf(pos, logisticRadius))
                    {
                        logisticCellsTemp.Add(cell);
                    }
                }
                return false;
            }, 16);
            return logisticCellsTemp;
        }
        public IEnumerable<IntVec3> CellsInDepotRange => LogisticCellsAround(Position, Map);
    }

    public class PlaceWorker_LogisticsDepot : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map currentMap = Find.CurrentMap;
            GenDraw.DrawFieldEdges(LogisticsDepot.LogisticCellsAround(center, currentMap));
        }
    }


    [StaticConstructorOnStartup]
    public partial class Main : Mod
    {

        public Main(ModContentPack content) : base(content)
        {
            Harmony harmony = new Harmony("RedMattis.ShelfLogistics");
            harmony.PatchAll();
        }

    }

    [HarmonyPatch]
    public static class HarmonyPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(JobDriver_DoBill), "JumpIfTargetInsideBillGiver")]
        public static void JumpIfTargetInisdeBillGiver(JobDriver_DoBill __instance, Toil __result, Toil jumpToil, TargetIndex ingredient, TargetIndex billGiver)
        {
            var prevInitAction = __result.initAction;
            __result.initAction = InitActionWithLogisticsHook;

            void InitActionWithLogisticsHook()
            {
                var actor = __result.actor;
                Thing thing = actor.CurJob.GetTarget(billGiver).Thing;
                if (thing != null && thing.Spawned)
                {
                    Thing ingThing = actor.jobs.curJob.GetTarget(ingredient).Thing;
                    if (ingThing != null && ingThing.IsInAnyStorage())
                    {
                        var actorMap = actor.MapHeld;
                        var ingredientMap = ingThing.MapHeld ?? actorMap;
                        ThingOwner thingOwner = thing.TryGetInnerInteractableThingOwner();
                        bool alreadyIn = thingOwner != null && thingOwner.Contains(ingThing);
                        if (!alreadyIn && ingredientMap != null && ingredientMap.GetComponent<LogisticsMapComponent>() is var lmc)
                        {
                            bool differentMaps = ingredientMap != actorMap;
                            var closest = lmc.GetClosestPickup(thing.Position);
                            if (closest?.Position is IntVec3 closestPos
                                && (differentMaps || closestPos.DistanceToSquared(thing.Position) < ingThing.Position.DistanceToSquared(actor.Position))
                                && lmc.DepotTiles.Contains(ingThing.Position))
                            {
                                int teleportCount = Mathf.Min(ingThing.stackCount, actor.jobs.curJob.count);
                                var splitThing = ingThing.SplitOff(teleportCount);
                                var t = GenSpawn.Spawn(splitThing, closestPos, actorMap);
                                actor.Reserve(t, actor.CurJob, stackCount: teleportCount, errorOnFailed:false);
                                actor.CurJob.SetTarget(ingredient, t);
                            }
                        }
                    }
                }
                prevInitAction();
            }
        }
    }
}
