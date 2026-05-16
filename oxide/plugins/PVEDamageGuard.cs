using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PVE Damage Guard", "Gabriel Dungan (DunganSoft Technologies)", "1.1.0")]
    [Description("Future-proof NPC classifier, per-attacker and per-victim damage scaling, building-grade modifiers, time-of-day modifiers, and reflect-as-a-service for PVE Rust servers. Designed as a TruePVE companion.")]
    public class PVEDamageGuard : CovalencePlugin
    {
        [PluginReference] private Plugin TruePVE;
        [PluginReference] private Plugin PVEMode;
        [PluginReference] private Plugin NextGenPVE;
        [PluginReference] private Plugin DamageControl;

        private const string PermBypass = "pvedamageguard.bypass";
        private const string PermAdmin  = "pvedamageguard.admin";

        private static readonly DamageType[] _allDamageTypes = (DamageType[])Enum.GetValues(typeof(DamageType));

        // Time-of-day categories (string keys must match TimeOfDayMultipliers config dict)
        private const string TodGlobal         = "Global";
        private const string TodPvp            = "PvP";
        private const string TodNpcToPlayer    = "NpcToPlayer";
        private const string TodNpcToStructure = "NpcToStructure";

        private Configuration _config;
        private readonly HashSet<DamageType> _envDamageTypes = new HashSet<DamageType>();
        private readonly HashSet<ulong> _reflectInFlight = new HashSet<ulong>();
        private bool _yieldToTruePve;

        // Hot-path flags - computed once at config load, skip expensive lookups when feature is off
        private bool _todEnabled;
        private bool _victimScalingEnabled;
        private bool _buildingGradeEnabled;
        private bool _perAttackerStructureEnabled;

        #region NPC category taxonomy (public)

        public enum NpcCategory
        {
            None,            // not an NPC (real player, environment, structure, etc.)
            RealPlayer,      // human player
            HumanNpc,        // any BasePlayer with IsNpc=true (scientists, vendor guards, HumanNPCNew variants, future)
            AnimalNpc,       // BaseNpc (bears, wolves, boars, zombies, scarecrows)
            VehicleNpc,      // BaseHelicopter, BradleyAPC and their projectiles
            OwnedTrap,       // player-owned trap (auto-turret, shotgun trap, flame turret)
            Building,        // BuildingBlock / Door
            Deployable,      // player-owned DecayEntity (box, TC, etc.)
            Environment,     // fall, bleed, cold, etc. (no real initiator)
            Other            // anything we cannot confidently classify
        }

        #endregion

        #region Config

        public enum LogLevel
        {
            None     = 0,
            Reflects = 1,
            Scaled   = 2,
            All      = 3,
            Trace    = 4
        }

        private class Configuration
        {
            // ===================== PvP =====================
            [JsonProperty("PvP - Reflect damage to shooter (master switch)")]
            public bool ReflectPvpEnabled = true;

            [JsonProperty("PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)")]
            public float ReflectMultiplier = 1.0f;

            [JsonProperty("PvP - If reflect is disabled, block PvP damage outright instead of letting it through")]
            public bool BlockPvpIfNotReflecting = true;

            [JsonProperty("PvP - Allow teammates (Rust team system) to damage each other")]
            public bool AllowTeammateDamage = false;

            // ===================== NPC -> Player =====================
            [JsonProperty("NPC -> Player - Per-damage-type scaling. Missing types use 'Default'. Set to 0 to make players immune to that type.")]
            public Dictionary<string, float> NpcToPlayerScaling = new Dictionary<string, float>
            {
                ["Default"]   = 0.5f,
                ["Bullet"]    = 0.25f,
                ["Slash"]     = 0.5f,
                ["Stab"]      = 0.5f,
                ["Bite"]      = 0.5f,
                ["Blunt"]     = 0.5f,
                ["Explosion"] = 0.5f,
                ["Arrow"]     = 0.5f,
                ["Generic"]   = 1.0f
            };

            // ===================== NPC -> Structure =====================
            [JsonProperty("NPC -> Structure - Default uniform scaling for heli/Bradley/scientist damage to player-built structures (0 = invulnerable). Used when an attacker subtype is not listed in PerAttackerStructureScaling.")]
            public float NpcToStructureScaling = 0.5f;

            [JsonProperty("NPC -> Structure - Per-attacker overrides. Keyed by attacker subtype: PatrolHelicopter, BradleyAPC, HumanNpc, AnimalNpc. Replaces the old DamageControl 'Heli_bypass' flag: set 'PatrolHelicopter' to 1.0 to let helis raid at full damage even when other NPCs are scaled down. Empty dict = use Default everywhere.")]
            public Dictionary<string, float> PerAttackerStructureScaling = new Dictionary<string, float>();

            [JsonProperty("Building grade multipliers - Stacks on top of attacker-based structure scaling. Applies only to BuildingBlock entities (foundations, walls, doors built via the building plan). Set to 1.0 (default) for no effect.")]
            public Dictionary<string, float> BuildingGradeMultipliers = new Dictionary<string, float>
            {
                ["Twigs"]   = 1.0f,
                ["Wood"]    = 1.0f,
                ["Stone"]   = 1.0f,
                ["Metal"]   = 1.0f,
                ["TopTier"] = 1.0f
            };

            // ===================== Per-victim subtype scaling =====================
            [JsonProperty("Per-victim subtype scaling - Apply per-damage-type multipliers when the VICTIM matches a specific entity subtype. Stacks on top of attacker-based rules. Use for 'bears are tougher', 'minicopters tank explosions', 'barrels die in one hit'. Empty inner dict or 'Default'=1.0 = no effect. Recognized subtypes: Bear, Wolf, Boar, Chicken, Stag, Horse, RidableHorse, Minicopter, ScrapHelicopter, HotAirBalloon, BradleyAPC, PatrolHelicopter, SamSite, Barrel, Zombie, Scientist.")]
            public Dictionary<string, Dictionary<string, float>> PerVictimSubtypeScaling = new Dictionary<string, Dictionary<string, float>>
            {
                ["Bear"]            = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Wolf"]            = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Boar"]            = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Chicken"]         = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Stag"]            = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Horse"]           = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["RidableHorse"]    = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Minicopter"]      = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["ScrapHelicopter"] = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["HotAirBalloon"]   = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["BradleyAPC"]      = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["PatrolHelicopter"]= new Dictionary<string, float> { ["Default"] = 1.0f },
                ["SamSite"]         = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Barrel"]          = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Zombie"]          = new Dictionary<string, float> { ["Default"] = 1.0f },
                ["Scientist"]       = new Dictionary<string, float> { ["Default"] = 1.0f }
            };

            // ===================== Time of day =====================
            [JsonProperty("Time of day - source for hour lookup. 'Game' uses Rust's in-game day/night cycle (TOD_Sky 0-24). 'Real' uses server wall-clock hour (DateTime.Now 0-23).")]
            public string TimeOfDaySource = "Game";

            [JsonProperty("Time of day multipliers - 24-element arrays indexed by hour. Applied multiplicatively to the relevant damage path. All-ones (default) disables the feature for that category. Categories: Global (applied to every modified hit), PvP, NpcToPlayer, NpcToStructure. Use to make NPCs less lethal at night, restrict PvP to certain hours, etc.")]
            public Dictionary<string, float[]> TimeOfDayMultipliers = new Dictionary<string, float[]>
            {
                [TodGlobal]         = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodPvp]            = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToPlayer]    = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToStructure] = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1}
            };

            // ===================== Misc =====================
            [JsonProperty("Treat traps owned by a player (auto-turret, shotgun trap, flame turret) as PvP from that owner")]
            public bool TreatPlayerTrapsAsPvp = true;

            [JsonProperty("Damage types to NEVER touch (always vanilla). Fall, bleed, cold, etc.")]
            public List<string> EnvironmentalDamageTypes = new List<string>
            {
                "Hunger", "Thirst", "Cold", "Heat", "Drowned",
                "Bleeding", "Poison", "Suicide", "Fall",
                "Radiation", "RadiationExposure", "ColdExposure", "Decay"
            };

            [JsonProperty("Yield allow/block decisions to TruePVE if it is loaded (we only scale and classify)")]
            public bool YieldToTruePVE = true;

            [JsonProperty("Log verbosity: None | Reflects | Scaled | All | Trace")]
            [JsonConverter(typeof(StringEnumConverter))]
            public LogLevel Logging = LogLevel.None;

            [JsonProperty("Also write log entries to oxide/logs/PVEDamageGuard/ files for audit")]
            public bool LogToFile = false;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) LoadDefaultConfig();
            }
            catch
            {
                PrintWarning("Config file is corrupt, regenerating default. Old file kept as .jsonError");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void RebuildCaches()
        {
            _envDamageTypes.Clear();
            foreach (var s in _config.EnvironmentalDamageTypes)
            {
                if (Enum.TryParse<DamageType>(s, true, out var dt))
                    _envDamageTypes.Add(dt);
                else
                    PrintWarning($"Unknown DamageType '{s}' in EnvironmentalDamageTypes, ignored.");
            }

            // Determine which optional features are actually active so the hot path can skip them
            _perAttackerStructureEnabled = _config.PerAttackerStructureScaling != null
                                           && _config.PerAttackerStructureScaling.Count > 0;

            _buildingGradeEnabled = false;
            if (_config.BuildingGradeMultipliers != null)
            {
                foreach (var kv in _config.BuildingGradeMultipliers)
                    if (Math.Abs(kv.Value - 1.0f) > 0.0001f) { _buildingGradeEnabled = true; break; }
            }

            _victimScalingEnabled = false;
            if (_config.PerVictimSubtypeScaling != null)
            {
                foreach (var entry in _config.PerVictimSubtypeScaling)
                {
                    foreach (var kv in entry.Value)
                        if (Math.Abs(kv.Value - 1.0f) > 0.0001f) { _victimScalingEnabled = true; break; }
                    if (_victimScalingEnabled) break;
                }
            }

            _todEnabled = false;
            if (_config.TimeOfDayMultipliers != null)
            {
                foreach (var entry in _config.TimeOfDayMultipliers)
                {
                    if (entry.Value == null || entry.Value.Length != 24) continue;
                    for (int i = 0; i < 24; i++)
                        if (Math.Abs(entry.Value[i] - 1.0f) > 0.0001f) { _todEnabled = true; break; }
                    if (_todEnabled) break;
                }
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermBypass, this);
            permission.RegisterPermission(PermAdmin,  this);
        }

        private void OnServerInitialized()
        {
            RebuildCaches();
            DetectCompanions();
            Puts($"PVE Damage Guard v{Version} loaded. Reflect={_config.ReflectPvpEnabled}, NPC->Structure default={_config.NpcToStructureScaling:F2}x, Features: TOD={_todEnabled}, VictimSub={_victimScalingEnabled}, BuildingGrade={_buildingGradeEnabled}, PerAttackerStruct={_perAttackerStructureEnabled}, Logging={_config.Logging}, YieldToTruePVE={_yieldToTruePve}");
        }

        private void DetectCompanions()
        {
            _yieldToTruePve = _config.YieldToTruePVE && (TruePVE != null);
            if (TruePVE != null)
                Puts(_yieldToTruePve
                    ? "TruePVE detected. Yielding allow/block to TruePVE; PVEDamageGuard will only classify, scale, and reflect-on-request."
                    : "TruePVE detected but YieldToTruePVE=false in config. Both plugins will hook OnEntityTakeDamage - verify your intent.");
            if (DamageControl != null)
                PrintError("Damage Control (legacy) is loaded alongside PVEDamageGuard. PVEDamageGuard is the replacement for Damage Control - both hook OnEntityTakeDamage and will fight over every hit. Unload Damage Control: oxide.unload DamageControl");
            if (PVEMode != null)    PrintWarning("PVEMode also loaded. Test carefully for conflicts.");
            if (NextGenPVE != null) PrintWarning("NextGenPVE also loaded. Test carefully for conflicts.");
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            var name = plugin?.Name;
            if (name == "TruePVE" || name == "PVEMode" || name == "NextGenPVE" || name == "DamageControl")
                DetectCompanions();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            var name = plugin?.Name;
            if (name == "TruePVE" || name == "PVEMode" || name == "NextGenPVE" || name == "DamageControl")
                DetectCompanions();
        }

        #endregion

        #region Public API (callable by other plugins)

        [HookMethod("API_Classify")]
        public string API_Classify(BaseEntity entity)
        {
            return ClassifyEntity(entity).ToString();
        }

        [HookMethod("API_ClassifySubtype")]
        public string API_ClassifySubtype(BaseEntity entity)
        {
            return ClassifySubtype(entity);
        }

        [HookMethod("API_IsNpcAttacker")]
        public bool API_IsNpcAttacker(HitInfo info)
        {
            if (info == null) return false;
            var cat = ClassifyEntity(ResolveRootAttacker(info));
            return cat == NpcCategory.HumanNpc || cat == NpcCategory.AnimalNpc || cat == NpcCategory.VehicleNpc;
        }

        [HookMethod("API_ReflectDamage")]
        public bool API_ReflectDamage(BasePlayer attacker, BasePlayer victim, HitInfo info, float multiplier)
        {
            if (attacker == null || victim == null || info == null) return false;
            DoReflect(attacker, victim, info, multiplier);
            return true;
        }

        [HookMethod("API_GetNpcScaling")]
        public float API_GetNpcScaling(string damageType)
        {
            if (_config.NpcToPlayerScaling.TryGetValue(damageType, out var m)) return m;
            return _config.NpcToPlayerScaling.TryGetValue("Default", out var d) ? d : 1f;
        }

        [HookMethod("API_GetCurrentHour")]
        public int API_GetCurrentHour() => GetCurrentHour();

        #endregion

        #region Hook

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null) return null;

            // Reflect-in-progress passthrough
            if (entity is BasePlayer beingHit && _reflectInFlight.Contains(beingHit.userID))
                return null;

            var rootAttacker = ResolveRootAttacker(info);
            var attackerCat  = ClassifyEntity(rootAttacker);
            var victimCat    = ClassifyEntity(entity);
            var victimPlayer = entity as BasePlayer;

            // Bypass perm
            if (victimCat == NpcCategory.RealPlayer && permission.UserHasPermission(victimPlayer.UserIDString, PermBypass))
            {
                LogHit(LogLevel.All, "bypass-perm", info, entity, attackerCat, victimCat);
                return null;
            }

            // Environmental - never touch
            if (info.Initiator == null || rootAttacker == null || IsEnvironmental(info))
            {
                LogHit(LogLevel.All, "env-passthrough", info, entity, attackerCat, victimCat);
                return null;
            }

            // Hour lookup (computed once per hit)
            int hour = _todEnabled ? GetCurrentHour() : 0;
            float todGlobal = _todEnabled ? GetTodMult(TodGlobal, hour) : 1f;

            // Per-victim subtype scaling applies on top of everything else (e.g. "bears tougher than wolves")
            string victimSubtype = _victimScalingEnabled ? ClassifySubtype(entity) : null;

            // ===== CASE B: Player vs Player =====
            if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer
                && rootAttacker != entity)
            {
                if (_config.AllowTeammateDamage && AreTeammates(rootAttacker as BasePlayer, victimPlayer))
                {
                    LogHit(LogLevel.All, "teammate-allow", info, entity, attackerCat, victimCat);
                    return null;
                }
                if (_yieldToTruePve)
                {
                    LogHit(LogLevel.All, "pvp-yielded-to-truepve", info, entity, attackerCat, victimCat);
                    return null;
                }

                float pvpTod = _todEnabled ? GetTodMult(TodPvp, hour) : 1f;
                float pvpScalar = todGlobal * pvpTod;

                if (_config.ReflectPvpEnabled)
                {
                    DoReflect(rootAttacker as BasePlayer, victimPlayer, info, _config.ReflectMultiplier * pvpScalar);
                    return true;
                }
                if (_config.BlockPvpIfNotReflecting)
                {
                    LogHit(LogLevel.Scaled, "pvp-blocked", info, entity, attackerCat, victimCat);
                    return true;
                }
                // Else: let through, optionally scaled by TOD
                if (Math.Abs(pvpScalar - 1.0f) > 0.0001f)
                    info.damageTypes.ScaleAll(pvpScalar);
                return null;
            }

            // ===== CASE C: Player vs NPC - vanilla, but per-victim subtype scaling can apply =====
            if (attackerCat == NpcCategory.RealPlayer
                && (entity is BaseNpc || (victimPlayer != null && victimPlayer.IsNpc)
                    || entity is BaseHelicopter || entity is BradleyAPC))
            {
                ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
                LogHit(LogLevel.All, "player->npc", info, entity, attackerCat, victimCat);
                return null;
            }

            // ===== CASE C2: Player vs vehicle / Loot / Misc - per-victim subtype scaling only =====
            if (attackerCat == NpcCategory.RealPlayer && victimSubtype != null)
            {
                ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
                LogHit(LogLevel.All, "player->subtype", info, entity, attackerCat, victimCat);
                return null;
            }

            bool attackerIsAnyNpc = attackerCat == NpcCategory.HumanNpc
                                  || attackerCat == NpcCategory.AnimalNpc
                                  || attackerCat == NpcCategory.VehicleNpc;

            // ===== CASE D: NPC -> Player =====
            if (attackerIsAnyNpc && victimCat == NpcCategory.RealPlayer)
            {
                float npcToPlayerTod = _todEnabled ? GetTodMult(TodNpcToPlayer, hour) : 1f;
                ApplyNpcToPlayerScaling(info, todGlobal * npcToPlayerTod);
                ApplyPerVictimSubtypeScaling(info, victimSubtype, 1f); // already applied todGlobal once
                LogHit(LogLevel.Scaled, "npc->player-scaled", info, entity, attackerCat, victimCat);
                return null;
            }

            // ===== CASE E: NPC -> Structure / Deployable =====
            if (attackerIsAnyNpc && IsStructure(entity))
            {
                float structureMult = GetAttackerStructureMult(rootAttacker, attackerCat);
                float npcToStructureTod = _todEnabled ? GetTodMult(TodNpcToStructure, hour) : 1f;
                structureMult *= todGlobal * npcToStructureTod;

                if (_buildingGradeEnabled && entity is BuildingBlock bb)
                    structureMult *= GetBuildingGradeMult(bb.grade);

                if (structureMult <= 0f)
                {
                    LogHit(LogLevel.Scaled, "npc->structure-blocked", info, entity, attackerCat, victimCat);
                    return true;
                }
                info.damageTypes.ScaleAll(structureMult);
                ApplyPerVictimSubtypeScaling(info, victimSubtype, 1f);
                LogHit(LogLevel.Scaled, $"npc->structure-{structureMult:F2}x", info, entity, attackerCat, victimCat);
                return null;
            }

            // ===== CASE F: Everything else - vanilla, but per-victim subtype scaling can still apply =====
            ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
            LogHit(LogLevel.All, "other-passthrough", info, entity, attackerCat, victimCat);
            return null;
        }

        #endregion

        #region Classification

        public NpcCategory ClassifyEntity(BaseEntity entity)
        {
            if (entity == null) return NpcCategory.Environment;

            if (entity is BasePlayer bp)
                return bp.IsNpc ? NpcCategory.HumanNpc : NpcCategory.RealPlayer;
            if (entity is NPCPlayer)      return NpcCategory.HumanNpc;
            if (entity is BaseNpc)        return NpcCategory.AnimalNpc;
            if (entity is BaseHelicopter) return NpcCategory.VehicleNpc;
            if (entity is BradleyAPC)     return NpcCategory.VehicleNpc;
            if (entity is BuildingBlock)  return NpcCategory.Building;
            if (entity is Door)           return NpcCategory.Building;
            if (entity is DecayEntity && entity.OwnerID != 0UL) return NpcCategory.Deployable;

            if (entity is BaseCombatEntity bce && bce.OwnerID != 0UL && bce.OwnerID.IsSteamId())
                return NpcCategory.OwnedTrap;

            return NpcCategory.Other;
        }

        // Returns a stable subtype string for entities admins want to tune individually.
        // Type checks where the class has been stable for years; falls back to ShortPrefabName
        // for specific animals/vehicles whose prefab names have not changed in the entire
        // history of Rust. This is NOT used for NPC humanoid detection (the part that breaks
        // every wipe) - that stays type-based in ClassifyEntity.
        public string ClassifySubtype(BaseEntity entity)
        {
            if (entity == null) return null;
            string prefab = entity.ShortPrefabName ?? "";

            // Vehicle NPCs (type-based, stable)
            if (entity is BaseHelicopter) return "PatrolHelicopter";
            if (entity is BradleyAPC)     return "BradleyAPC";

            // Animals and zombies (BaseNpc-derived; prefab names stable since 2015)
            if (entity is BaseNpc)
            {
                if (prefab.Contains("bear"))      return "Bear";
                if (prefab.Contains("wolf"))      return "Wolf";
                if (prefab.Contains("boar"))      return "Boar";
                if (prefab.Contains("chicken"))   return "Chicken";
                if (prefab.Contains("stag"))      return "Stag";
                if (prefab.Contains("zombie")
                    || prefab.Contains("scarecrow")
                    || prefab.Contains("murderer")) return "Zombie";
            }

            // Horses
            if (prefab.Contains("ridablehorse")) return "RidableHorse";
            if (prefab.Contains("horse"))        return "Horse";

            // Player-driven vehicles
            if (prefab.Contains("minicopter"))               return "Minicopter";
            if (prefab.Contains("scraptransporthelicopter")) return "ScrapHelicopter";
            if (prefab.Contains("hotairballoon"))            return "HotAirBalloon";

            // SAM site
            if (prefab.Contains("sam_site") || prefab.Contains("sam_static"))
                return "SamSite";

            // Barrels (LootContainer with "barrel" in prefab name)
            if (entity is LootContainer && prefab.Contains("barrel"))
                return "Barrel";

            // Scientists / HumanNPC (catch-all for scaling toughness of NPC humanoids)
            if (entity is NPCPlayer
                || (entity is BasePlayer bp2 && bp2.IsNpc))
                return "Scientist";

            return null;
        }

        // Walks projectile/explosive/trap wrappers to the source entity
        public BaseEntity ResolveRootAttacker(HitInfo info)
        {
            var init = info?.Initiator;
            if (init == null) return null;

            if (init is BasePlayer)     return init;
            if (init is NPCPlayer)      return init;
            if (init is BaseNpc)        return init;
            if (init is BaseHelicopter) return init;
            if (init is BradleyAPC)     return init;

            if (init.OwnerID != 0UL && init.OwnerID.IsSteamId() && _config.TreatPlayerTrapsAsPvp)
            {
                var owner = BasePlayer.FindByID(init.OwnerID);
                if (owner != null) return owner;
            }

            var creator = init.creatorEntity;
            if (creator != null)
            {
                if (creator is BasePlayer || creator is NPCPlayer || creator is BaseNpc
                    || creator is BaseHelicopter || creator is BradleyAPC)
                    return creator;
            }

            return init;
        }

        #endregion

        #region Modifier helpers

        private int GetCurrentHour()
        {
            if (string.Equals(_config.TimeOfDaySource, "Real", StringComparison.OrdinalIgnoreCase))
                return DateTime.Now.Hour;
            try
            {
                if (TOD_Sky.Instance != null && TOD_Sky.Instance.Cycle != null)
                {
                    int h = (int)Math.Floor(TOD_Sky.Instance.Cycle.Hour);
                    if (h < 0) h = 0;
                    if (h > 23) h = h % 24;
                    return h;
                }
            }
            catch { }
            return 0;
        }

        private float GetTodMult(string category, int hour)
        {
            if (_config.TimeOfDayMultipliers == null) return 1f;
            if (!_config.TimeOfDayMultipliers.TryGetValue(category, out var arr)) return 1f;
            if (arr == null || arr.Length != 24) return 1f;
            if (hour < 0) hour = 0;
            if (hour > 23) hour = 23;
            return arr[hour];
        }

        private float GetAttackerStructureMult(BaseEntity attacker, NpcCategory cat)
        {
            float baseMult = _config.NpcToStructureScaling;
            if (!_perAttackerStructureEnabled) return baseMult;

            // Try specific subtype first (PatrolHelicopter, BradleyAPC, etc.)
            var subtype = ClassifySubtype(attacker);
            if (subtype != null && _config.PerAttackerStructureScaling.TryGetValue(subtype, out var sub))
                return sub;

            // Fall back to broad category (HumanNpc / AnimalNpc / VehicleNpc)
            if (cat != NpcCategory.None && _config.PerAttackerStructureScaling.TryGetValue(cat.ToString(), out var c))
                return c;

            // Fall back to the per-attacker dict's own "Default" entry, then NpcToStructureScaling
            if (_config.PerAttackerStructureScaling.TryGetValue("Default", out var d))
                return d;
            return baseMult;
        }

        private float GetBuildingGradeMult(BuildingGrade.Enum grade)
        {
            if (!_buildingGradeEnabled) return 1f;
            string key = grade.ToString();
            if (_config.BuildingGradeMultipliers.TryGetValue(key, out var m)) return m;
            return 1f;
        }

        private void ApplyPerVictimSubtypeScaling(HitInfo info, string victimSubtype, float crossMult)
        {
            if (!_victimScalingEnabled || victimSubtype == null) {
                if (Math.Abs(crossMult - 1.0f) > 0.0001f && _todEnabled)
                    info.damageTypes.ScaleAll(crossMult);
                return;
            }
            if (!_config.PerVictimSubtypeScaling.TryGetValue(victimSubtype, out var map) || map == null)
            {
                if (Math.Abs(crossMult - 1.0f) > 0.0001f && _todEnabled)
                    info.damageTypes.ScaleAll(crossMult);
                return;
            }
            float defaultMult = 1f;
            map.TryGetValue("Default", out defaultMult);

            for (int i = 0; i < _allDamageTypes.Length; i++)
            {
                var dt = _allDamageTypes[i];
                if (dt == DamageType.LAST) continue;
                if (_envDamageTypes.Contains(dt)) continue;

                float mult = defaultMult;
                if (map.TryGetValue(dt.ToString(), out var configured)) mult = configured;
                info.damageTypes.Scale(dt, mult * crossMult);
            }
        }

        #endregion

        #region Helpers (existing)

        private bool IsEnvironmental(HitInfo info)
        {
            var major = info.damageTypes.GetMajorityDamageType();
            return _envDamageTypes.Contains(major);
        }

        private bool IsStructure(BaseCombatEntity entity)
        {
            if (entity is BuildingBlock) return true;
            if (entity is Door) return true;
            if (entity is DecayEntity && entity.OwnerID != 0UL) return true;
            return false;
        }

        private bool AreTeammates(BasePlayer a, BasePlayer b)
        {
            if (a == null || b == null) return false;
            if (a.currentTeam == 0UL || b.currentTeam == 0UL) return false;
            return a.currentTeam == b.currentTeam;
        }

        private void ApplyNpcToPlayerScaling(HitInfo info, float crossMult)
        {
            var map = _config.NpcToPlayerScaling;
            float defaultMult = 1.0f;
            map.TryGetValue("Default", out defaultMult);

            for (int i = 0; i < _allDamageTypes.Length; i++)
            {
                var dt = _allDamageTypes[i];
                if (dt == DamageType.LAST) continue;
                if (_envDamageTypes.Contains(dt)) continue;

                float mult = defaultMult;
                if (map.TryGetValue(dt.ToString(), out var configured)) mult = configured;
                info.damageTypes.Scale(dt, mult * crossMult);
            }
        }

        private void DoReflect(BasePlayer attacker, BasePlayer victim, HitInfo info, float multiplier)
        {
            if (attacker == null || victim == null || info?.damageTypes == null) return;

            float total = info.damageTypes.Total() * multiplier;
            if (total <= 0f) return;

            var major = info.damageTypes.GetMajorityDamageType();

            if (!_reflectInFlight.Add(attacker.userID)) return;
            try
            {
                attacker.Hurt(total, major, victim, true);
            }
            finally
            {
                _reflectInFlight.Remove(attacker.userID);
            }

            LogReflect(victim, attacker, total, major);
        }

        #endregion

        #region Logging

        private bool LogAt(LogLevel min) => _config.Logging >= min;

        private void Log(LogLevel level, string msg)
        {
            if (!LogAt(level)) return;
            Puts(msg);
            if (_config.LogToFile)
                LogToFile("damage", $"[{DateTime.Now:HH:mm:ss}] {msg}", this);
        }

        private void LogHit(LogLevel level, string tag, HitInfo info, BaseCombatEntity entity,
                            NpcCategory attackerCat, NpcCategory victimCat)
        {
            if (!LogAt(level)) return;
            var attackerName = info.Initiator?.ShortPrefabName ?? "<none>";
            var victimName   = entity.ShortPrefabName ?? "<none>";
            var dmg          = info.damageTypes?.Total() ?? 0f;
            var major        = info.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic;
            var msg = $"[{tag}] {attackerCat}({attackerName}) -> {victimCat}({victimName}) | {major} {dmg:F1}";

            if (LogAt(LogLevel.Trace))
                msg += $" | Initiator={info.Initiator?.GetType().Name} Weapon={info.Weapon?.GetType().Name} HitBone={info.HitBone}";

            Log(level, msg);
        }

        private void LogReflect(BasePlayer victim, BasePlayer attacker, float total, DamageType major)
        {
            if (!LogAt(LogLevel.Reflects)) return;
            Log(LogLevel.Reflects, $"[reflect] {victim.displayName} -> {attacker.displayName} | {major} {total:F1}");
        }

        #endregion

        #region Admin command

        [Command("pdg")]
        private void CmdPdg(IPlayer player, string command, string[] args)
        {
            if (!player.IsServer && !player.IsAdmin && !player.HasPermission(PermAdmin))
            {
                player.Reply(Lang("NoPermission", player.Id));
                return;
            }

            if (args.Length == 0)
            {
                ShowStatus(player);
                return;
            }

            switch (args[0].ToLower())
            {
                case "reload":  CmdReload(player); break;
                case "log":     CmdLog(player, args); break;
                case "logfile": CmdLogFile(player, args); break;
                case "test":    CmdTest(player); break;
                case "scale":   CmdScale(player, args); break;
                case "hour":    CmdHour(player); break;
                default:
                    player.Reply(Lang("UsageRoot", player.Id));
                    break;
            }
        }

        private void ShowStatus(IPlayer player)
        {
            float defaultNpc = 1f;
            _config.NpcToPlayerScaling.TryGetValue("Default", out defaultNpc);
            player.Reply(string.Format(Lang("StatusBlock", player.Id),
                Version,
                _config.ReflectPvpEnabled, _config.ReflectMultiplier,
                _config.BlockPvpIfNotReflecting,
                _config.AllowTeammateDamage,
                defaultNpc,
                _config.NpcToStructureScaling,
                _config.TreatPlayerTrapsAsPvp,
                _config.Logging, _config.LogToFile,
                _yieldToTruePve,
                _todEnabled, _victimScalingEnabled, _buildingGradeEnabled, _perAttackerStructureEnabled,
                GetCurrentHour(), _config.TimeOfDaySource));
        }

        private void CmdReload(IPlayer player)
        {
            LoadConfig();
            RebuildCaches();
            DetectCompanions();
            player.Reply(Lang("ConfigReloaded", player.Id));
        }

        private void CmdLog(IPlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.Reply(string.Format(Lang("LogCurrent", player.Id), _config.Logging));
                return;
            }
            if (!Enum.TryParse<LogLevel>(args[1], true, out var lvl))
            {
                player.Reply(string.Format(Lang("LogUnknown", player.Id), args[1]));
                return;
            }
            _config.Logging = lvl;
            SaveConfig();
            player.Reply(string.Format(Lang("LogSet", player.Id), lvl));
        }

        private void CmdLogFile(IPlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.Reply(string.Format(Lang("LogFileCurrent", player.Id), _config.LogToFile));
                return;
            }
            var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase)
                  || args[1] == "true" || args[1] == "1";
            _config.LogToFile = on;
            SaveConfig();
            player.Reply(string.Format(Lang("LogFileSet", player.Id), on));
        }

        private void CmdScale(IPlayer player, string[] args)
        {
            if (args.Length < 3)
            {
                player.Reply(Lang("ScaleUsage", player.Id));
                return;
            }
            if (!float.TryParse(args[2], out var mult) || mult < 0f || mult > 100f)
            {
                player.Reply(Lang("ScaleBadNumber", player.Id));
                return;
            }
            _config.NpcToPlayerScaling[args[1]] = mult;
            SaveConfig();
            player.Reply(string.Format(Lang("ScaleSet", player.Id), args[1], mult));
        }

        private void CmdHour(IPlayer player)
        {
            int h = GetCurrentHour();
            player.Reply(string.Format(Lang("HourReport", player.Id),
                h, _config.TimeOfDaySource,
                GetTodMult(TodGlobal, h), GetTodMult(TodPvp, h),
                GetTodMult(TodNpcToPlayer, h), GetTodMult(TodNpcToStructure, h)));
        }

        private void CmdTest(IPlayer player)
        {
            var bp = player.Object as BasePlayer;
            if (bp == null)
            {
                player.Reply(Lang("TestOnlyInGame", player.Id));
                return;
            }

            RaycastHit hit;
            if (!Physics.Raycast(bp.eyes.HeadRay(), out hit, 250f))
            {
                player.Reply(Lang("TestNoHit", player.Id));
                return;
            }

            var ent = hit.GetEntity();
            if (ent == null)
            {
                player.Reply(Lang("TestNoEntity", player.Id));
                return;
            }

            var cat = ClassifyEntity(ent);
            var sub = ClassifySubtype(ent) ?? "<none>";
            int hour = GetCurrentHour();
            float todGlobal = GetTodMult(TodGlobal, hour);

            var lines = new List<string>();
            lines.Add(string.Format(Lang("TestTarget", player.Id), ent.ShortPrefabName, ent.GetType().Name, cat, sub));
            lines.Add(string.Format(Lang("TestDistance", player.Id), hit.distance));
            lines.Add(string.Format(Lang("TestHour", player.Id), hour, _config.TimeOfDaySource, todGlobal));

            // Rule preview based on category
            if (cat == NpcCategory.HumanNpc || cat == NpcCategory.AnimalNpc || cat == NpcCategory.VehicleNpc)
            {
                float dmm = 1f;
                _config.NpcToPlayerScaling.TryGetValue("Default", out dmm);
                float npcToPlayerTod = GetTodMult(TodNpcToPlayer, hour);
                float structMult = GetAttackerStructureMult(ent, cat);
                float npcToStructureTod = GetTodMult(TodNpcToStructure, hour);
                lines.Add(string.Format(Lang("TestRuleNpcAttacker", player.Id),
                    dmm, npcToPlayerTod, todGlobal,
                    structMult, npcToStructureTod));
            }
            else if (cat == NpcCategory.RealPlayer)
            {
                lines.Add(string.Format(Lang("TestRulePvp", player.Id),
                    _config.ReflectPvpEnabled, _config.ReflectMultiplier,
                    _config.BlockPvpIfNotReflecting, _yieldToTruePve,
                    GetTodMult(TodPvp, hour)));
            }
            else if (cat == NpcCategory.Building)
            {
                float bgMult = 1f;
                if (ent is BuildingBlock bb) bgMult = GetBuildingGradeMult(bb.grade);
                lines.Add(string.Format(Lang("TestRuleBuilding", player.Id),
                    _config.NpcToStructureScaling, bgMult,
                    ent is BuildingBlock b2 ? b2.grade.ToString() : "n/a"));
            }
            else if (cat == NpcCategory.Deployable)
            {
                lines.Add(string.Format(Lang("TestRuleStructure", player.Id), _config.NpcToStructureScaling));
            }
            else if (cat == NpcCategory.OwnedTrap)
            {
                lines.Add(Lang("TestRuleTrap", player.Id));
            }
            else
            {
                lines.Add(Lang("TestRuleOther", player.Id));
            }

            // Show per-victim subtype scaling if any applies
            if (_victimScalingEnabled && sub != "<none>"
                && _config.PerVictimSubtypeScaling.TryGetValue(sub, out var subMap))
            {
                float subDefault = 1f;
                subMap.TryGetValue("Default", out subDefault);
                lines.Add(string.Format(Lang("TestRuleVictimSubtype", player.Id), sub, subDefault));
            }

            player.Reply(string.Join("\n", lines));
        }

        #endregion

        #region Lang

        private string Lang(string key, string userId = null, params object[] args)
        {
            return lang.GetMessage(key, this, userId);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"]    = "You do not have permission to use this command.",
                ["ConfigReloaded"]  = "PVEDamageGuard config reloaded.",
                ["UsageRoot"]       = "Usage: /pdg [reload | log <level> | logfile <on|off> | scale <damageType> <mult> | hour | test]",
                ["LogCurrent"]      = "Current log level: {0}. Usage: /pdg log <None|Reflects|Scaled|All|Trace>",
                ["LogUnknown"]      = "Unknown log level '{0}'. Valid: None, Reflects, Scaled, All, Trace.",
                ["LogSet"]          = "Log level set to {0}.",
                ["LogFileCurrent"]  = "Current file logging: {0}. Usage: /pdg logfile <on|off>",
                ["LogFileSet"]      = "File logging set to {0}. Writes to oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt",
                ["ScaleUsage"]      = "Usage: /pdg scale <DamageType> <multiplier 0-100>",
                ["ScaleBadNumber"]  = "Multiplier must be a number between 0 and 100.",
                ["ScaleSet"]        = "NPC->Player scaling for {0} set to {1:F2}x.",
                ["HourReport"]      = "Hour {0} ({1}). TOD multipliers: Global={2:F2}, PvP={3:F2}, NpcToPlayer={4:F2}, NpcToStructure={5:F2}.",
                ["TestOnlyInGame"]  = "/pdg test must be run by an in-game player.",
                ["TestNoHit"]       = "/pdg test: raycast hit nothing within 250m.",
                ["TestNoEntity"]    = "/pdg test: raycast hit a surface but no game entity.",
                ["TestTarget"]      = "Target: {0} (type={1}) classified as {2}, subtype={3}",
                ["TestDistance"]    = "Distance: {0:F1}m",
                ["TestHour"]        = "Hour: {0} ({1}). Global TOD multiplier: {2:F2}x",
                ["TestRuleNpcAttacker"] = "If this entity damages a player: NPC->Player scaling (Default {0:F2}x) * NpcToPlayer TOD ({1:F2}x) * Global TOD ({2:F2}x). If it damages a structure: attacker struct scaling {3:F2}x * NpcToStructure TOD ({4:F2}x).",
                ["TestRulePvp"]     = "If you damage this player: Reflect={0} ({1:F2}x), BlockIfNotReflecting={2}, YieldToTruePVE={3}, PvP TOD={4:F2}x",
                ["TestRuleBuilding"]= "If an NPC damages this building: NpcToStructure default {0:F2}x, Building grade multiplier {1:F2}x (grade={2}).",
                ["TestRuleStructure"]= "If an NPC damages this deployable: NpcToStructure scaling {0:F2}x (0 = blocked).",
                ["TestRuleTrap"]    = "Trap is player-owned. Damage from this trap to other players is treated as PvP from the owner.",
                ["TestRuleOther"]   = "No PVEDamageGuard rule applies to this entity. Vanilla damage behavior.",
                ["TestRuleVictimSubtype"] = "Per-victim subtype scaling: '{0}' Default {1:F2}x (stacks on top of attacker rules).",
                ["StatusBlock"]     =
                    "PVE Damage Guard v{0}\n" +
                    "  Reflect: {1} ({2:F2}x), BlockPvpIfNotReflecting: {3}, Teammates: {4}\n" +
                    "  NPC->Player default: {5:F2}, NPC->Structure default: {6:F2}, Traps as PvP: {7}\n" +
                    "  Logging: {8} (file={9}), Yield to TruePVE: {10}\n" +
                    "  Features: TOD={11}, VictimSubtype={12}, BuildingGrade={13}, PerAttackerStruct={14}\n" +
                    "  Current hour: {15} ({16})\n" +
                    "  Commands: /pdg reload | log <lvl> | logfile <on|off> | scale <type> <mult> | hour | test"
            }, this);
        }

        #endregion
    }
}
