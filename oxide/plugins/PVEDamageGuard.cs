using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PVE Damage Guard", "Gabriel Dungan (DunganSoft Technologies)", "1.3.0")]
    [Description("Future-proof NPC classifier with optional declarative rule matrix, per-attacker/per-victim damage scaling, building grade and time-of-day modifiers, ZoneManager and event-aware context switching, reflect-as-a-service, Damage Control config import, preset configurations, config validation, and admin diagnostics for PVE Rust servers.")]
    public class PVEDamageGuard : CovalencePlugin
    {
        [PluginReference] private Plugin TruePVE;
        [PluginReference] private Plugin PVEMode;
        [PluginReference] private Plugin NextGenPVE;
        [PluginReference] private Plugin DamageControl;
        [PluginReference] private Plugin ZoneManager;

        private const string PermBypass = "pvedamageguard.bypass";
        private const string PermAdmin  = "pvedamageguard.admin";

        private static readonly DamageType[] _allDamageTypes = (DamageType[])Enum.GetValues(typeof(DamageType));

        // Time-of-day category keys
        private const string TodGlobal         = "Global";
        private const string TodPvp            = "PvP";
        private const string TodNpcToPlayer    = "NpcToPlayer";
        private const string TodNpcToStructure = "NpcToStructure";

        private Configuration _config;
        private readonly HashSet<DamageType> _envDamageTypes = new HashSet<DamageType>();
        private readonly HashSet<ulong> _reflectInFlight = new HashSet<ulong>();
        private bool _yieldToTruePve;

        // Feature active flags (computed at config load)
        private bool _todEnabled;
        private bool _victimScalingEnabled;
        private bool _buildingGradeEnabled;
        private bool _perAttackerStructureEnabled;
        private bool _ruleMatrixEnabled;

        // Event tracker state
        private readonly Dictionary<ulong, TrackedEvent> _activeEvents = new Dictionary<ulong, TrackedEvent>();

        // History ring buffer
        private readonly Queue<HistoryEntry> _history = new Queue<HistoryEntry>();
        private const int HistoryCapacity = 100;

        #region Public types

        public enum NpcCategory
        {
            None,
            RealPlayer,
            HumanNpc,
            AnimalNpc,
            VehicleNpc,
            OwnedTrap,
            Building,
            Deployable,
            Environment,
            Other
        }

        public enum LogLevel
        {
            None     = 0,
            Reflects = 1,
            Scaled   = 2,
            All      = 3,
            Trace    = 4
        }

        private class TrackedEvent
        {
            public ulong NetId;
            public string EventType;
            public Vector3 Position;
            public DateTime SeenAt;
        }

        private class HistoryEntry
        {
            public DateTime At;
            public string Tag;
            public string AttackerCat;
            public string AttackerName;
            public string VictimCat;
            public string VictimName;
            public string Context;
            public float Damage;
            public string MajorType;
            public string Action;
        }

        #endregion

        #region Rule action types

        private abstract class RuleAction
        {
            public abstract string Encode();
        }

        private sealed class AllowAction : RuleAction
        {
            public override string Encode() => "allow";
        }

        private sealed class BlockAction : RuleAction
        {
            public override string Encode() => "block";
        }

        private sealed class ReflectAction : RuleAction
        {
            public float Multiplier;
            public override string Encode() => $"reflect:{Multiplier:F2}";
        }

        private sealed class ScaleAction : RuleAction
        {
            public float? Uniform;                       // null when per-type
            public Dictionary<string, float> PerType;    // null when uniform
            public override string Encode()
            {
                if (Uniform.HasValue) return $"scale:{Uniform.Value:F2}";
                if (PerType == null || PerType.Count == 0) return "scale:1.0";
                var inner = string.Join(",", PerType.Select(kv => $"{kv.Key}:{kv.Value:F2}"));
                return "scale:{" + inner + "}";
            }
        }

        private static RuleAction ParseRuleAction(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            var lower = s.ToLowerInvariant();
            if (lower == "allow") return new AllowAction();
            if (lower == "block") return new BlockAction();
            if (lower.StartsWith("reflect:"))
            {
                if (float.TryParse(s.Substring(8).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m))
                    return new ReflectAction { Multiplier = m };
            }
            if (lower.StartsWith("scale:"))
            {
                var body = s.Substring(6).Trim();
                if (body.StartsWith("{"))
                {
                    var inner = body.Trim('{', '}', ' ');
                    var parts = inner.Split(',');
                    var dict = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in parts)
                    {
                        var kv = p.Split(':');
                        if (kv.Length != 2) continue;
                        if (!float.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mult))
                            continue;
                        dict[kv[0].Trim()] = mult;
                    }
                    return new ScaleAction { PerType = dict };
                }
                if (float.TryParse(body, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var u))
                    return new ScaleAction { Uniform = u };
            }
            return null;
        }

        #endregion

        #region Config

        private class Configuration
        {
            [JsonProperty("PvP - Reflect damage to shooter (master switch)")]
            public bool ReflectPvpEnabled = true;

            [JsonProperty("PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)")]
            public float ReflectMultiplier = 1.0f;

            [JsonProperty("PvP - If reflect is disabled, block PvP damage outright instead of letting it through")]
            public bool BlockPvpIfNotReflecting = true;

            [JsonProperty("PvP - Allow teammates (Rust team system) to damage each other")]
            public bool AllowTeammateDamage = false;

            [JsonProperty("NPC -> Player - Per-damage-type scaling. Missing types use 'Default'. Set to 0 to make players immune.")]
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

            [JsonProperty("NPC -> Structure - Default uniform scaling (used when PerAttackerStructureScaling has no match).")]
            public float NpcToStructureScaling = 0.5f;

            [JsonProperty("NPC -> Structure - Per-attacker overrides. Replaces Damage Control's Heli_bypass; set 'PatrolHelicopter':1.0 to let helis raid at full power.")]
            public Dictionary<string, float> PerAttackerStructureScaling = new Dictionary<string, float>();

            [JsonProperty("Building grade multipliers - Stacks on top of structure scaling. Applies to BuildingBlock only.")]
            public Dictionary<string, float> BuildingGradeMultipliers = new Dictionary<string, float>
            {
                ["Twigs"]   = 1.0f,
                ["Wood"]    = 1.0f,
                ["Stone"]   = 1.0f,
                ["Metal"]   = 1.0f,
                ["TopTier"] = 1.0f
            };

            [JsonProperty("Per-victim subtype scaling - Per-damage-type multipliers applied when the VICTIM matches a known subtype. Stacks on top of attacker rules.")]
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

            [JsonProperty("Time of day source - 'Game' (TOD_Sky cycle) or 'Real' (server wall clock).")]
            public string TimeOfDaySource = "Game";

            [JsonProperty("Time of day multipliers - 24-element arrays per category. All-ones disables. Categories: Global, PvP, NpcToPlayer, NpcToStructure.")]
            public Dictionary<string, float[]> TimeOfDayMultipliers = new Dictionary<string, float[]>
            {
                [TodGlobal]         = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodPvp]            = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToPlayer]    = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToStructure] = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1}
            };

            [JsonProperty("Treat traps owned by a player as PvP from that owner")]
            public bool TreatPlayerTrapsAsPvp = true;

            [JsonProperty("Damage types to NEVER touch (always vanilla)")]
            public List<string> EnvironmentalDamageTypes = new List<string>
            {
                "Hunger", "Thirst", "Cold", "Heat", "Drowned",
                "Bleeding", "Poison", "Suicide", "Fall",
                "Radiation", "RadiationExposure", "ColdExposure", "Decay"
            };

            [JsonProperty("Yield allow/block decisions to TruePVE if it is loaded")]
            public bool YieldToTruePVE = true;

            [JsonProperty("Log verbosity: None | Reflects | Scaled | All | Trace")]
            [JsonConverter(typeof(StringEnumConverter))]
            public LogLevel Logging = LogLevel.None;

            [JsonProperty("Also write log entries to oxide/logs/PVEDamageGuard/ files for audit")]
            public bool LogToFile = false;

            [JsonProperty("Rule matrix configuration (v1.2). Declarative AttackerCategory x VictimCategory -> Action rules with contexts and inheritance. When Enabled=true this REPLACES the case-based scaling for allow/block/reflect decisions; scale actions still compose with TOD, victim subtype, and building grade modifiers.")]
            public RuleMatrixConfig RuleMatrix = new RuleMatrixConfig();
        }

        private class RuleMatrixConfig
        {
            [JsonProperty("Enabled - master switch. When false the v1.1 case-based scaling logic is used unchanged.")]
            public bool Enabled = false;

            [JsonProperty("Default context name (used when no provider returns a match)")]
            public string DefaultContext = "Default";

            [JsonProperty("Contexts - named rule sets, optionally inheriting from another context")]
            public Dictionary<string, ContextConfig> Contexts = new Dictionary<string, ContextConfig>
            {
                ["Default"] = new ContextConfig
                {
                    Description = "Normal server state, no events or zones overriding.",
                    Inherits = null,
                    Rules = new Dictionary<string, string>
                    {
                        ["RealPlayer -> RealPlayer"]   = "reflect:1.0",
                        ["RealPlayer -> HumanNpc"]    = "allow",
                        ["RealPlayer -> AnimalNpc"]   = "allow",
                        ["RealPlayer -> VehicleNpc"]  = "allow",
                        ["RealPlayer -> Building"]    = "allow",
                        ["RealPlayer -> Deployable"]  = "allow",
                        ["HumanNpc -> RealPlayer"]    = "scale:{Bullet:0.25,Default:0.5}",
                        ["AnimalNpc -> RealPlayer"]   = "scale:{Bite:0.5,Default:0.5}",
                        ["VehicleNpc -> RealPlayer"]  = "scale:{Bullet:0.25,Explosion:0.5}",
                        ["HumanNpc -> Building"]      = "scale:0.5",
                        ["AnimalNpc -> Building"]     = "block",
                        ["VehicleNpc -> Building"]    = "scale:0.5",
                        ["VehicleNpc -> Deployable"]  = "scale:0.5",
                        ["OwnedTrap -> RealPlayer"]   = "reflect:1.0",
                        ["Environment -> *"]          = "allow"
                    }
                },
                ["AtPvpEvent"] = new ContextConfig
                {
                    Description = "Active Bradley/Heli/Cargo event nearby; PvP enabled.",
                    Inherits = "Default",
                    Rules = new Dictionary<string, string>
                    {
                        ["RealPlayer -> RealPlayer"] = "allow"
                    }
                },
                ["InRaidableBaseDome"] = new ContextConfig
                {
                    Description = "Inside a RaidableBases dome; full PvP and full building damage to the raid base.",
                    Inherits = "Default",
                    Rules = new Dictionary<string, string>
                    {
                        ["RealPlayer -> RealPlayer"]   = "allow",
                        ["RealPlayer -> Building"]     = "allow",
                        ["RealPlayer -> Deployable"]   = "allow"
                    }
                }
            };

            [JsonProperty("Context providers - ZoneManager and EventTracker. Order: ZoneManager checked first, then EventTracker proximity, else DefaultContext.")]
            public ContextProvidersConfig ContextProviders = new ContextProvidersConfig();
        }

        private class ContextConfig
        {
            public string Description;
            public string Inherits;
            public Dictionary<string, string> Rules = new Dictionary<string, string>();
        }

        private class ContextProvidersConfig
        {
            public ZoneManagerProviderConfig ZoneManager = new ZoneManagerProviderConfig();
            public EventTrackerProviderConfig EventTracker = new EventTrackerProviderConfig();
        }

        private class ZoneManagerProviderConfig
        {
            public bool Enabled = true;

            [JsonProperty("Map ZoneManager zone flags to context names")]
            public Dictionary<string, string> ZoneFlagToContext = new Dictionary<string, string>
            {
                ["pvp"] = "AtPvpEvent"
            };
        }

        private class EventTrackerProviderConfig
        {
            public bool Enabled = true;

            [JsonProperty("Which context to switch to when any tracked event is active within RadiusMeters of the victim")]
            public string TriggerContext = "AtPvpEvent";

            [JsonProperty("Which event entity types trigger this context")]
            public List<string> Events = new List<string> { "BradleyAPC", "BaseHelicopter", "CargoShip" };

            [JsonProperty("Radius from the event entity that activates the context")]
            public float RadiusMeters = 200f;
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

            _ruleMatrixEnabled = _config.RuleMatrix != null && _config.RuleMatrix.Enabled
                              && _config.RuleMatrix.Contexts != null
                              && _config.RuleMatrix.Contexts.Count > 0;
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
            ValidateConfig();
            DetectCompanions();
            // Seed event tracker with currently-spawned event entities
            if (_ruleMatrixEnabled && _config.RuleMatrix.ContextProviders.EventTracker.Enabled)
            {
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    var be = entity as BaseEntity;
                    if (be == null) continue;
                    TryTrackEvent(be);
                }
            }
            Puts($"PVE Damage Guard v{Version} loaded. Reflect={_config.ReflectPvpEnabled}, NPC->Structure default={_config.NpcToStructureScaling:F2}x, Features: TOD={_todEnabled}, VictimSub={_victimScalingEnabled}, BuildingGrade={_buildingGradeEnabled}, PerAttackerStruct={_perAttackerStructureEnabled}, RuleMatrix={_ruleMatrixEnabled}, Logging={_config.Logging}, YieldToTruePVE={_yieldToTruePve}");
            if (_ruleMatrixEnabled) Puts($"Rule matrix: {_config.RuleMatrix.Contexts.Count} contexts, default='{_config.RuleMatrix.DefaultContext}'. Tracked events: {_activeEvents.Count}.");
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
            if (ZoneManager != null && _ruleMatrixEnabled && _config.RuleMatrix.ContextProviders.ZoneManager.Enabled)
                Puts("ZoneManager detected. Per-zone context switching is active.");
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            var name = plugin?.Name;
            if (name == "TruePVE" || name == "PVEMode" || name == "NextGenPVE"
                || name == "DamageControl" || name == "ZoneManager")
                DetectCompanions();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            var name = plugin?.Name;
            if (name == "TruePVE" || name == "PVEMode" || name == "NextGenPVE"
                || name == "DamageControl" || name == "ZoneManager")
                DetectCompanions();
        }

        #endregion

        #region Event tracker

        private void OnEntitySpawned(BaseEntity entity)
        {
            if (!_ruleMatrixEnabled) return;
            TryTrackEvent(entity);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net == null) return;
            _activeEvents.Remove(entity.net.ID.Value);
        }

        private void TryTrackEvent(BaseEntity entity)
        {
            if (entity == null || entity.net == null) return;
            if (_config.RuleMatrix == null || _config.RuleMatrix.ContextProviders == null) return;
            var et = _config.RuleMatrix.ContextProviders.EventTracker;
            if (et == null || !et.Enabled || et.Events == null) return;

            string type = null;
            if (entity is BradleyAPC && et.Events.Contains("BradleyAPC")) type = "BradleyAPC";
            else if (entity is BaseHelicopter && et.Events.Contains("BaseHelicopter")) type = "BaseHelicopter";
            else if (entity is CargoShip && et.Events.Contains("CargoShip")) type = "CargoShip";

            if (type == null) return;

            _activeEvents[entity.net.ID.Value] = new TrackedEvent
            {
                NetId = entity.net.ID.Value,
                EventType = type,
                Position = entity.transform.position,
                SeenAt = DateTime.UtcNow,
            };
        }

        #endregion

        #region Public API

        [HookMethod("API_Classify")]
        public string API_Classify(BaseEntity entity) => ClassifyEntity(entity).ToString();

        [HookMethod("API_ClassifySubtype")]
        public string API_ClassifySubtype(BaseEntity entity) => ClassifySubtype(entity);

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

        [HookMethod("API_GetActiveContext")]
        public string API_GetActiveContext(Vector3 pos)
        {
            if (!_ruleMatrixEnabled) return null;
            return ResolveContext(pos, null);
        }

        [HookMethod("API_IsPvpAt")]
        public bool API_IsPvpAt(Vector3 pos)
        {
            if (!_ruleMatrixEnabled) return !_config.ReflectPvpEnabled && !_config.BlockPvpIfNotReflecting;
            var ctx = ResolveContext(pos, null);
            var action = ResolveRule(ctx, NpcCategory.RealPlayer, NpcCategory.RealPlayer, null, null);
            return action is AllowAction;
        }

        [HookMethod("API_IsAllowed")]
        public bool API_IsAllowed(BaseEntity attacker, BaseEntity victim)
        {
            if (!_ruleMatrixEnabled) return true;
            var aCat = ClassifyEntity(attacker);
            var vCat = ClassifyEntity(victim);
            var aSub = ClassifySubtype(attacker);
            var vSub = ClassifySubtype(victim);
            var ctx = ResolveContext(victim?.transform?.position ?? Vector3.zero, victim as BasePlayer);
            var action = ResolveRule(ctx, aCat, vCat, aSub, vSub);
            return !(action is BlockAction);
        }

        #endregion

        #region Hook

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null) return null;

            if (entity is BasePlayer beingHit && _reflectInFlight.Contains(beingHit.userID))
                return null;

            var rootAttacker = ResolveRootAttacker(info);
            var attackerCat  = ClassifyEntity(rootAttacker);
            var victimCat    = ClassifyEntity(entity);
            var victimPlayer = entity as BasePlayer;

            if (victimCat == NpcCategory.RealPlayer && permission.UserHasPermission(victimPlayer.UserIDString, PermBypass))
            {
                LogHit(LogLevel.All, "bypass-perm", info, entity, attackerCat, victimCat, null, "passthrough");
                return null;
            }

            if (info.Initiator == null || rootAttacker == null || IsEnvironmental(info))
            {
                LogHit(LogLevel.All, "env-passthrough", info, entity, attackerCat, victimCat, null, "passthrough");
                return null;
            }

            // ===== Rule matrix path (v1.2 opt-in) =====
            if (_ruleMatrixEnabled)
                return HandleViaRuleMatrix(entity, info, rootAttacker, attackerCat, victimCat, victimPlayer);

            // ===== Legacy v1.1 scaling path (unchanged) =====
            return HandleViaScaling(entity, info, rootAttacker, attackerCat, victimCat, victimPlayer);
        }

        #endregion

        #region Rule matrix path

        private object HandleViaRuleMatrix(BaseCombatEntity entity, HitInfo info,
                                           BaseEntity rootAttacker,
                                           NpcCategory attackerCat, NpcCategory victimCat,
                                           BasePlayer victimPlayer)
        {
            var attackerSub = ClassifySubtype(rootAttacker);
            var victimSub   = _victimScalingEnabled ? ClassifySubtype(entity) : null;
            var vPos = entity.transform != null ? entity.transform.position : Vector3.zero;
            var context = ResolveContext(vPos, victimPlayer);

            // Teammate allow (special-case before rule matrix - teammates always allowed if configured)
            if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer
                && _config.AllowTeammateDamage && AreTeammates(rootAttacker as BasePlayer, victimPlayer))
            {
                LogHit(LogLevel.All, "teammate-allow", info, entity, attackerCat, victimCat, context, "allow");
                return null;
            }

            // Yield PvP allow/block to TruePVE if present and enabled
            if (_yieldToTruePve && attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer)
            {
                LogHit(LogLevel.All, "pvp-yielded-to-truepve", info, entity, attackerCat, victimCat, context, "yield");
                return null;
            }

            var action = ResolveRule(context, attackerCat, victimCat, attackerSub, victimSub);

            int hour = _todEnabled ? GetCurrentHour() : 0;
            float todGlobal = _todEnabled ? GetTodMult(TodGlobal, hour) : 1f;

            if (action is BlockAction)
            {
                LogHit(LogLevel.Scaled, $"rule[{context}]-block", info, entity, attackerCat, victimCat, context, "block");
                return true;
            }
            if (action is AllowAction)
            {
                LogHit(LogLevel.All, $"rule[{context}]-allow", info, entity, attackerCat, victimCat, context, "allow");
                return null;
            }
            if (action is ReflectAction ra)
            {
                if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer)
                {
                    float pvpTod = _todEnabled ? GetTodMult(TodPvp, hour) : 1f;
                    DoReflect(rootAttacker as BasePlayer, victimPlayer, info, ra.Multiplier * todGlobal * pvpTod);
                    LogHit(LogLevel.Reflects, $"rule[{context}]-reflect", info, entity, attackerCat, victimCat, context, ra.Encode());
                    return true;
                }
                // Reflect requested for non-PvP - just block since reflect only makes sense for player attackers
                LogHit(LogLevel.Scaled, $"rule[{context}]-reflect-fallback-block", info, entity, attackerCat, victimCat, context, "block");
                return true;
            }
            if (action is ScaleAction sa)
            {
                ApplyScaleAction(info, sa);

                // Compose v1.1 modifiers on top of the scale
                bool attackerIsAnyNpc = attackerCat == NpcCategory.HumanNpc
                                      || attackerCat == NpcCategory.AnimalNpc
                                      || attackerCat == NpcCategory.VehicleNpc;
                bool victimIsStruct = victimCat == NpcCategory.Building || victimCat == NpcCategory.Deployable;

                float compose = todGlobal;
                if (_todEnabled)
                {
                    if (attackerIsAnyNpc && victimCat == NpcCategory.RealPlayer)
                        compose *= GetTodMult(TodNpcToPlayer, hour);
                    else if (attackerIsAnyNpc && victimIsStruct)
                        compose *= GetTodMult(TodNpcToStructure, hour);
                }
                if (_buildingGradeEnabled && entity is BuildingBlock bb)
                    compose *= GetBuildingGradeMult(bb.grade);
                if (Math.Abs(compose - 1.0f) > 0.0001f)
                    info.damageTypes.ScaleAll(compose);

                ApplyPerVictimSubtypeScaling(info, victimSub, 1f);
                LogHit(LogLevel.Scaled, $"rule[{context}]-{sa.Encode()}", info, entity, attackerCat, victimCat, context, sa.Encode());
                return null;
            }

            // Should not reach here
            LogHit(LogLevel.All, $"rule[{context}]-unknown", info, entity, attackerCat, victimCat, context, "passthrough");
            return null;
        }

        // Rule lookup: walk current context's rules, then Inherits chain.
        // Precedence (most specific first):
        //   AttackerSubtype -> VictimSubtype
        //   AttackerSubtype -> VictimCategory
        //   AttackerCategory -> VictimSubtype
        //   AttackerCategory -> VictimCategory
        //   AttackerSubtype  -> *
        //   AttackerCategory -> *
        //   *                -> VictimSubtype
        //   *                -> VictimCategory
        //   *                -> *
        private RuleAction ResolveRule(string context, NpcCategory attackerCat, NpcCategory victimCat,
                                       string attackerSubtype, string victimSubtype)
        {
            if (_config.RuleMatrix == null || _config.RuleMatrix.Contexts == null)
                return new AllowAction();

            var aCat = attackerCat.ToString();
            var vCat = victimCat.ToString();
            var aSub = attackerSubtype;
            var vSub = victimSubtype;

            var candidates = new List<string>(9);
            if (aSub != null && vSub != null) candidates.Add(MakeKey(aSub, vSub));
            if (aSub != null)                 candidates.Add(MakeKey(aSub, vCat));
            if (vSub != null)                 candidates.Add(MakeKey(aCat, vSub));
            candidates.Add(MakeKey(aCat, vCat));
            if (aSub != null) candidates.Add(MakeKey(aSub, "*"));
            candidates.Add(MakeKey(aCat, "*"));
            if (vSub != null) candidates.Add(MakeKey("*", vSub));
            candidates.Add(MakeKey("*", vCat));
            candidates.Add(MakeKey("*", "*"));

            var visited = new HashSet<string>();
            string name = context;
            while (name != null && visited.Add(name))
            {
                if (!_config.RuleMatrix.Contexts.TryGetValue(name, out var ctx) || ctx == null) break;
                if (ctx.Rules != null)
                {
                    foreach (var cand in candidates)
                    {
                        if (TryMatchRule(ctx.Rules, cand, out var actionStr))
                        {
                            var parsed = ParseRuleAction(actionStr);
                            if (parsed != null) return parsed;
                        }
                    }
                }
                name = ctx.Inherits;
            }

            return new AllowAction();
        }

        private static string MakeKey(string a, string v) => $"{a} -> {v}";

        // Match a candidate against the rule dict, tolerating various whitespace/case forms.
        private static bool TryMatchRule(Dictionary<string, string> rules, string candidate, out string actionStr)
        {
            actionStr = null;
            if (rules == null) return false;
            if (rules.TryGetValue(candidate, out actionStr)) return true;
            // Tolerant lookup
            foreach (var kv in rules)
            {
                if (NormalizeKey(kv.Key) == NormalizeKey(candidate))
                {
                    actionStr = kv.Value;
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            // Strip whitespace around the arrow, lowercase
            return string.Concat(key.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                         .ToLowerInvariant();
        }

        private void ApplyScaleAction(HitInfo info, ScaleAction sa)
        {
            if (sa.Uniform.HasValue)
            {
                info.damageTypes.ScaleAll(sa.Uniform.Value);
                return;
            }
            if (sa.PerType == null) return;
            float defaultMult = 1f;
            sa.PerType.TryGetValue("Default", out defaultMult);
            for (int i = 0; i < _allDamageTypes.Length; i++)
            {
                var dt = _allDamageTypes[i];
                if (dt == DamageType.LAST) continue;
                if (_envDamageTypes.Contains(dt)) continue;
                float mult = defaultMult;
                if (sa.PerType.TryGetValue(dt.ToString(), out var configured)) mult = configured;
                info.damageTypes.Scale(dt, mult);
            }
        }

        private string ResolveContext(Vector3 pos, BasePlayer victimForZoneCheck)
        {
            if (!_ruleMatrixEnabled) return _config.RuleMatrix?.DefaultContext ?? "Default";
            var providers = _config.RuleMatrix.ContextProviders;

            // 1. ZoneManager
            if (providers?.ZoneManager != null && providers.ZoneManager.Enabled
                && ZoneManager != null && providers.ZoneManager.ZoneFlagToContext != null)
            {
                foreach (var entry in providers.ZoneManager.ZoneFlagToContext)
                {
                    try
                    {
                        if (victimForZoneCheck != null)
                        {
                            var has = (bool)(ZoneManager.Call("EntityHasFlag", (BaseEntity)victimForZoneCheck, entry.Key) ?? false);
                            if (has) return entry.Value;
                        }
                        else
                        {
                            // Position-based check via ZoneManager API if available
                            var zones = ZoneManager.Call("GetZoneIDs") as string[];
                            if (zones != null)
                            {
                                foreach (var zid in zones)
                                {
                                    var zonePos = ZoneManager.Call("GetZoneLocation", zid) as Vector3?;
                                    var zoneRad = ZoneManager.Call("GetZoneRadius", zid);
                                    if (zonePos.HasValue && zoneRad is float rf && Vector3.Distance(pos, zonePos.Value) <= rf)
                                    {
                                        var hasFlag = (bool)(ZoneManager.Call("HasFlag", zid, entry.Key) ?? false);
                                        if (hasFlag) return entry.Value;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* tolerate ZoneManager API drift */ }
                }
            }

            // 2. Event tracker proximity
            if (providers?.EventTracker != null && providers.EventTracker.Enabled
                && _activeEvents.Count > 0 && !string.IsNullOrEmpty(providers.EventTracker.TriggerContext))
            {
                float r = providers.EventTracker.RadiusMeters;
                float r2 = r * r;
                foreach (var ev in _activeEvents.Values)
                {
                    if ((ev.Position - pos).sqrMagnitude <= r2)
                        return providers.EventTracker.TriggerContext;
                }
            }

            return _config.RuleMatrix.DefaultContext ?? "Default";
        }

        #endregion

        #region Legacy v1.1 scaling path

        private object HandleViaScaling(BaseCombatEntity entity, HitInfo info,
                                        BaseEntity rootAttacker,
                                        NpcCategory attackerCat, NpcCategory victimCat,
                                        BasePlayer victimPlayer)
        {
            int hour = _todEnabled ? GetCurrentHour() : 0;
            float todGlobal = _todEnabled ? GetTodMult(TodGlobal, hour) : 1f;
            string victimSubtype = _victimScalingEnabled ? ClassifySubtype(entity) : null;

            // PvP
            if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer
                && rootAttacker != entity)
            {
                if (_config.AllowTeammateDamage && AreTeammates(rootAttacker as BasePlayer, victimPlayer))
                {
                    LogHit(LogLevel.All, "teammate-allow", info, entity, attackerCat, victimCat, null, "allow");
                    return null;
                }
                if (_yieldToTruePve)
                {
                    LogHit(LogLevel.All, "pvp-yielded-to-truepve", info, entity, attackerCat, victimCat, null, "yield");
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
                    LogHit(LogLevel.Scaled, "pvp-blocked", info, entity, attackerCat, victimCat, null, "block");
                    return true;
                }
                if (Math.Abs(pvpScalar - 1.0f) > 0.0001f) info.damageTypes.ScaleAll(pvpScalar);
                return null;
            }

            // Player -> NPC (vanilla, but per-victim subtype scaling applies)
            if (attackerCat == NpcCategory.RealPlayer
                && (entity is BaseNpc || (victimPlayer != null && victimPlayer.IsNpc)
                    || entity is BaseHelicopter || entity is BradleyAPC))
            {
                ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
                LogHit(LogLevel.All, "player->npc", info, entity, attackerCat, victimCat, null, "scaled");
                return null;
            }

            // Player -> any-other-subtype
            if (attackerCat == NpcCategory.RealPlayer && victimSubtype != null)
            {
                ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
                LogHit(LogLevel.All, "player->subtype", info, entity, attackerCat, victimCat, null, "scaled");
                return null;
            }

            bool attackerIsAnyNpc = attackerCat == NpcCategory.HumanNpc
                                  || attackerCat == NpcCategory.AnimalNpc
                                  || attackerCat == NpcCategory.VehicleNpc;

            // NPC -> Player
            if (attackerIsAnyNpc && victimCat == NpcCategory.RealPlayer)
            {
                float npcToPlayerTod = _todEnabled ? GetTodMult(TodNpcToPlayer, hour) : 1f;
                ApplyNpcToPlayerScaling(info, todGlobal * npcToPlayerTod);
                ApplyPerVictimSubtypeScaling(info, victimSubtype, 1f);
                LogHit(LogLevel.Scaled, "npc->player-scaled", info, entity, attackerCat, victimCat, null, "scaled");
                return null;
            }

            // NPC -> Structure
            if (attackerIsAnyNpc && IsStructure(entity))
            {
                float structureMult = GetAttackerStructureMult(rootAttacker, attackerCat);
                float npcToStructureTod = _todEnabled ? GetTodMult(TodNpcToStructure, hour) : 1f;
                structureMult *= todGlobal * npcToStructureTod;
                if (_buildingGradeEnabled && entity is BuildingBlock bb)
                    structureMult *= GetBuildingGradeMult(bb.grade);
                if (structureMult <= 0f)
                {
                    LogHit(LogLevel.Scaled, "npc->structure-blocked", info, entity, attackerCat, victimCat, null, "block");
                    return true;
                }
                info.damageTypes.ScaleAll(structureMult);
                ApplyPerVictimSubtypeScaling(info, victimSubtype, 1f);
                LogHit(LogLevel.Scaled, $"npc->structure-{structureMult:F2}x", info, entity, attackerCat, victimCat, null, $"scale:{structureMult:F2}");
                return null;
            }

            ApplyPerVictimSubtypeScaling(info, victimSubtype, todGlobal);
            LogHit(LogLevel.All, "other-passthrough", info, entity, attackerCat, victimCat, null, "passthrough");
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

        public string ClassifySubtype(BaseEntity entity)
        {
            if (entity == null) return null;
            string prefab = entity.ShortPrefabName ?? "";

            if (entity is BaseHelicopter) return "PatrolHelicopter";
            if (entity is BradleyAPC)     return "BradleyAPC";

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

            if (prefab.Contains("ridablehorse")) return "RidableHorse";
            if (prefab.Contains("horse"))        return "Horse";
            if (prefab.Contains("minicopter"))               return "Minicopter";
            if (prefab.Contains("scraptransporthelicopter")) return "ScrapHelicopter";
            if (prefab.Contains("hotairballoon"))            return "HotAirBalloon";
            if (prefab.Contains("sam_site") || prefab.Contains("sam_static")) return "SamSite";
            if (entity is LootContainer && prefab.Contains("barrel")) return "Barrel";

            if (entity is NPCPlayer || (entity is BasePlayer bp2 && bp2.IsNpc))
                return "Scientist";

            return null;
        }

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
            if (creator != null
                && (creator is BasePlayer || creator is NPCPlayer || creator is BaseNpc
                    || creator is BaseHelicopter || creator is BradleyAPC))
                return creator;
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
            var subtype = ClassifySubtype(attacker);
            if (subtype != null && _config.PerAttackerStructureScaling.TryGetValue(subtype, out var sub)) return sub;
            if (cat != NpcCategory.None && _config.PerAttackerStructureScaling.TryGetValue(cat.ToString(), out var c)) return c;
            if (_config.PerAttackerStructureScaling.TryGetValue("Default", out var d)) return d;
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
            if (!_victimScalingEnabled || victimSubtype == null)
            {
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

        #region Onboarding (v1.3)

        // Validation - run after RebuildCaches; results surface as console warnings and in /pdg status.
        private readonly List<string> _configIssues = new List<string>();

        private void ValidateConfig()
        {
            _configIssues.Clear();

            // Environmental damage types
            foreach (var s in _config.EnvironmentalDamageTypes)
                if (!Enum.TryParse<DamageType>(s, true, out _))
                    _configIssues.Add($"EnvironmentalDamageTypes: '{s}' is not a valid DamageType");

            // Scaling values in [0, 100]
            ValidateMultBounds(_configIssues, "NpcToPlayerScaling", _config.NpcToPlayerScaling);
            ValidateMultBounds(_configIssues, "PerAttackerStructureScaling", _config.PerAttackerStructureScaling);
            ValidateMultBounds(_configIssues, "BuildingGradeMultipliers", _config.BuildingGradeMultipliers);
            if (_config.NpcToStructureScaling < 0f || _config.NpcToStructureScaling > 100f)
                _configIssues.Add($"NpcToStructureScaling: {_config.NpcToStructureScaling} out of [0, 100]");
            if (_config.ReflectMultiplier < 0f || _config.ReflectMultiplier > 100f)
                _configIssues.Add($"ReflectMultiplier: {_config.ReflectMultiplier} out of [0, 100]");

            // Per-victim subtype scaling (nested)
            if (_config.PerVictimSubtypeScaling != null)
                foreach (var sub in _config.PerVictimSubtypeScaling)
                    ValidateMultBounds(_configIssues, $"PerVictimSubtypeScaling[{sub.Key}]", sub.Value);

            // TOD arrays
            if (_config.TimeOfDayMultipliers != null)
                foreach (var entry in _config.TimeOfDayMultipliers)
                    if (entry.Value == null || entry.Value.Length != 24)
                        _configIssues.Add($"TimeOfDayMultipliers[{entry.Key}]: must have 24 elements (got {entry.Value?.Length ?? 0})");

            // TimeOfDaySource
            if (!string.Equals(_config.TimeOfDaySource, "Game", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(_config.TimeOfDaySource, "Real", StringComparison.OrdinalIgnoreCase))
                _configIssues.Add($"TimeOfDaySource: must be 'Game' or 'Real' (got '{_config.TimeOfDaySource}')");

            // Rule matrix
            if (_config.RuleMatrix != null && _config.RuleMatrix.Enabled)
            {
                var contexts = _config.RuleMatrix.Contexts ?? new Dictionary<string, ContextConfig>();

                if (!string.IsNullOrEmpty(_config.RuleMatrix.DefaultContext)
                    && !contexts.ContainsKey(_config.RuleMatrix.DefaultContext))
                    _configIssues.Add($"RuleMatrix.DefaultContext '{_config.RuleMatrix.DefaultContext}' does not exist in Contexts");

                foreach (var ctx in contexts)
                {
                    // Inherits chain - detect cycles and dangling references
                    var visited = new HashSet<string>();
                    var name = ctx.Key;
                    while (name != null)
                    {
                        if (!visited.Add(name))
                        {
                            _configIssues.Add($"RuleMatrix.Contexts[{ctx.Key}]: Inherits cycle through '{name}'");
                            break;
                        }
                        if (!contexts.TryGetValue(name, out var c)) break;
                        if (c.Inherits != null && !contexts.ContainsKey(c.Inherits))
                        {
                            _configIssues.Add($"RuleMatrix.Contexts[{ctx.Key}]: Inherits target '{c.Inherits}' does not exist");
                            break;
                        }
                        name = c.Inherits;
                    }

                    // Rule action parsing
                    if (ctx.Value.Rules != null)
                        foreach (var rule in ctx.Value.Rules)
                            if (ParseRuleAction(rule.Value) == null)
                                _configIssues.Add($"RuleMatrix.Contexts[{ctx.Key}].Rules['{rule.Key}']: invalid action '{rule.Value}'");
                }

                // Provider target context names
                var providers = _config.RuleMatrix.ContextProviders;
                if (providers?.ZoneManager?.ZoneFlagToContext != null)
                    foreach (var entry in providers.ZoneManager.ZoneFlagToContext)
                        if (!contexts.ContainsKey(entry.Value))
                            _configIssues.Add($"RuleMatrix.ContextProviders.ZoneManager: flag '{entry.Key}' targets non-existent context '{entry.Value}'");
                if (providers?.EventTracker?.Enabled == true
                    && !string.IsNullOrEmpty(providers.EventTracker.TriggerContext)
                    && !contexts.ContainsKey(providers.EventTracker.TriggerContext))
                    _configIssues.Add($"RuleMatrix.ContextProviders.EventTracker.TriggerContext '{providers.EventTracker.TriggerContext}' does not exist");
            }

            if (_configIssues.Count > 0)
            {
                PrintWarning($"Config validation found {_configIssues.Count} issue(s):");
                foreach (var issue in _configIssues) PrintWarning($"  - {issue}");
            }
        }

        private static void ValidateMultBounds(List<string> issues, string path, Dictionary<string, float> dict)
        {
            if (dict == null) return;
            foreach (var kv in dict)
                if (kv.Value < 0f || kv.Value > 100f)
                    issues.Add($"{path}['{kv.Key}']: {kv.Value} out of [0, 100]");
        }

        // Presets - admin applies a known-good baseline config.
        private void ApplyPreset(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "pvepure":
                    _config.ReflectPvpEnabled = false;
                    _config.BlockPvpIfNotReflecting = true;
                    _config.AllowTeammateDamage = false;
                    _config.NpcToPlayerScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0.25f, ["Bullet"] = 0.1f, ["Slash"] = 0.25f, ["Stab"] = 0.25f,
                        ["Bite"] = 0.25f, ["Blunt"] = 0.25f, ["Explosion"] = 0.25f, ["Arrow"] = 0.25f,
                        ["Generic"] = 1.0f
                    };
                    _config.NpcToStructureScaling = 0f;
                    _config.PerAttackerStructureScaling = new Dictionary<string, float>();
                    if (_config.RuleMatrix != null) _config.RuleMatrix.Enabled = false;
                    break;

                case "pvereflect":
                    _config.ReflectPvpEnabled = true;
                    _config.ReflectMultiplier = 1.0f;
                    _config.BlockPvpIfNotReflecting = true;
                    _config.AllowTeammateDamage = false;
                    _config.NpcToPlayerScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0.5f, ["Bullet"] = 0.25f, ["Slash"] = 0.5f, ["Stab"] = 0.5f,
                        ["Bite"] = 0.5f, ["Blunt"] = 0.5f, ["Explosion"] = 0.5f, ["Arrow"] = 0.5f,
                        ["Generic"] = 1.0f
                    };
                    _config.NpcToStructureScaling = 0.5f;
                    _config.PerAttackerStructureScaling = new Dictionary<string, float>();
                    if (_config.RuleMatrix != null) _config.RuleMatrix.Enabled = false;
                    break;

                case "pvevehicleraids":
                    _config.ReflectPvpEnabled = true;
                    _config.ReflectMultiplier = 1.0f;
                    _config.BlockPvpIfNotReflecting = true;
                    _config.NpcToPlayerScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0.5f, ["Bullet"] = 0.25f, ["Explosion"] = 0.5f
                    };
                    _config.NpcToStructureScaling = 0f; // block by default
                    _config.PerAttackerStructureScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0f,
                        ["PatrolHelicopter"] = 1.0f,
                        ["BradleyAPC"] = 1.0f
                    };
                    if (_config.RuleMatrix != null) _config.RuleMatrix.Enabled = false;
                    break;

                case "pvphoursevents":
                    _config.ReflectPvpEnabled = false;
                    _config.BlockPvpIfNotReflecting = true;
                    _config.AllowTeammateDamage = false;
                    _config.NpcToPlayerScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0.5f, ["Bullet"] = 0.25f
                    };
                    _config.NpcToStructureScaling = 0.5f;
                    if (_config.RuleMatrix == null) _config.RuleMatrix = new RuleMatrixConfig();
                    _config.RuleMatrix.Enabled = true;
                    _config.RuleMatrix.DefaultContext = "Default";
                    if (!_config.RuleMatrix.Contexts.ContainsKey("Default"))
                        _config.RuleMatrix.Contexts["Default"] = new ContextConfig
                        {
                            Description = "Default PVE - no PvP",
                            Rules = new Dictionary<string, string>()
                        };
                    _config.RuleMatrix.Contexts["Default"].Rules["RealPlayer -> RealPlayer"] = "block";
                    if (!_config.RuleMatrix.Contexts.ContainsKey("AtPvpEvent"))
                        _config.RuleMatrix.Contexts["AtPvpEvent"] = new ContextConfig
                        {
                            Description = "Event nearby - PvP enabled",
                            Inherits = "Default",
                            Rules = new Dictionary<string, string>()
                        };
                    _config.RuleMatrix.Contexts["AtPvpEvent"].Rules["RealPlayer -> RealPlayer"] = "allow";
                    if (_config.RuleMatrix.ContextProviders != null
                        && _config.RuleMatrix.ContextProviders.EventTracker != null)
                    {
                        _config.RuleMatrix.ContextProviders.EventTracker.Enabled = true;
                        _config.RuleMatrix.ContextProviders.EventTracker.TriggerContext = "AtPvpEvent";
                    }
                    break;

                default:
                    throw new ArgumentException("unknown preset: " + name);
            }
        }

        private static readonly string[] _presetNames = { "pvepure", "pvereflect", "pvevehicleraids", "pvphoursevents" };

        // Import from Damage Control - reads oxide/config/DamageControl.json, maps fields, applies.
        private (int imported, int skipped, List<string> report) ImportFromDamageControl()
        {
            var report = new List<string>();
            int imported = 0, skipped = 0;
            var dcPath = Path.Combine(Interface.Oxide.ConfigDirectory, "DamageControl.json");
            if (!File.Exists(dcPath))
            {
                report.Add($"DamageControl.json not found at {dcPath} - nothing to import.");
                return (imported, skipped, report);
            }

            // Backup our current config first
            var pdgPath = Path.Combine(Interface.Oxide.ConfigDirectory, "PVEDamageGuard.json");
            var backupPath = Path.Combine(Interface.Oxide.ConfigDirectory, $"PVEDamageGuard.backup.{DateTime.Now:yyyyMMddHHmmss}.json");
            if (File.Exists(pdgPath))
            {
                try { File.Copy(pdgPath, backupPath, true); report.Add($"Backed up current config to {backupPath}"); }
                catch (Exception e) { report.Add($"Backup failed: {e.Message}"); }
            }

            JObject dc;
            try { dc = JObject.Parse(File.ReadAllText(dcPath)); }
            catch (Exception e) { report.Add($"Failed to parse DamageControl.json: {e.Message}"); return (imported, skipped, report); }

            // Per-victim damage scaling (DC stores a flat dict per victim)
            var victimMap = new Dictionary<string, string>
            {
                ["APC_Multipliers"]         = "BradleyAPC",
                ["Bear_Multipliers"]        = "Bear",
                ["Boar_Multipliers"]        = "Boar",
                ["Chicken_Multipliers"]     = "Chicken",
                ["Heli_Multipliers"]        = "PatrolHelicopter",
                ["Horse_Multipliers"]       = "Horse",
                ["RidableHorse_Multipliers"]= "RidableHorse",
                ["SAMSite_Multipliers"]     = "SamSite",
                ["Minicopter_Multipliers"]  = "Minicopter",
                ["Scrapcopter_Multipliers"] = "ScrapHelicopter",
                ["Scientist_Multipliers"]   = "Scientist",
                ["Stag_Multipliers"]        = "Stag",
                ["Wolf_Multipliers"]        = "Wolf",
                ["Zombie_Multipliers"]      = "Zombie",
                ["Balloon_Multipliers"]     = "HotAirBalloon"
            };
            foreach (var entry in victimMap)
            {
                if (dc[entry.Key] is JObject dmgs)
                {
                    var dict = new Dictionary<string, float>();
                    foreach (var prop in dmgs.Properties())
                    {
                        // DC uses lowercase damage type names; PDG uses PascalCase
                        var pdgType = CapitalizeDamageType(prop.Name);
                        if (float.TryParse(prop.Value.ToString(), System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var v))
                            dict[pdgType] = v;
                    }
                    if (dict.Count > 0)
                    {
                        if (_config.PerVictimSubtypeScaling == null)
                            _config.PerVictimSubtypeScaling = new Dictionary<string, Dictionary<string, float>>();
                        _config.PerVictimSubtypeScaling[entry.Value] = dict;
                        imported++;
                        report.Add($"Mapped {entry.Key} -> PerVictimSubtypeScaling[\"{entry.Value}\"] ({dict.Count} damage types)");
                    }
                }
            }

            // Player_Multipliers - this maps closest to NpcToPlayerScaling but the semantics differ
            // (DC: applied when player TAKES damage; PDG: applied when NPC ATTACKS player).
            if (dc["Player_Multipliers"] is JObject playerDmgs)
            {
                var dict = new Dictionary<string, float>();
                foreach (var prop in playerDmgs.Properties())
                {
                    var pdgType = CapitalizeDamageType(prop.Name);
                    if (float.TryParse(prop.Value.ToString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var v))
                        dict[pdgType] = v;
                }
                if (dict.Count > 0)
                {
                    _config.NpcToPlayerScaling = dict;
                    imported++;
                    report.Add("Mapped Player_Multipliers -> NpcToPlayerScaling (semantics: DC was victim-side, PDG is attacker-side; same numeric values, different meaning).");
                }
            }

            // BuildingBlock_Multipliers - no direct equivalent. Take the average as a single NpcToStructureScaling.
            if (dc["BuildingBlock_Multipliers"] is JObject bbDmgs)
            {
                float sum = 0f; int n = 0;
                foreach (var prop in bbDmgs.Properties())
                    if (float.TryParse(prop.Value.ToString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var v))
                    { sum += v; n++; }
                if (n > 0)
                {
                    _config.NpcToStructureScaling = sum / n;
                    imported++;
                    report.Add($"Averaged BuildingBlock_Multipliers ({n} entries) -> NpcToStructureScaling = {_config.NpcToStructureScaling:F2}");
                }
                else
                {
                    skipped++;
                    report.Add("SKIPPED BuildingBlock_Multipliers: no parseable values.");
                }
            }

            // Building grade multipliers
            if (dc["Building_Grade_Multipliers"] is JObject grades)
            {
                if (_config.BuildingGradeMultipliers == null)
                    _config.BuildingGradeMultipliers = new Dictionary<string, float>();
                foreach (var prop in grades.Properties())
                    if (float.TryParse(prop.Value.ToString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var v))
                        _config.BuildingGradeMultipliers[prop.Name] = v;
                imported++;
                report.Add("Mapped Building_Grade_Multipliers -> BuildingGradeMultipliers");
            }

            // Heli bypass
            if (dc["Bypasses"] is JObject bypasses)
            {
                var hb = bypasses["Heli_bypass"];
                if (hb != null && hb.Type == JTokenType.Boolean && hb.Value<bool>())
                {
                    if (_config.PerAttackerStructureScaling == null)
                        _config.PerAttackerStructureScaling = new Dictionary<string, float>();
                    _config.PerAttackerStructureScaling["PatrolHelicopter"] = 1.0f;
                    imported++;
                    report.Add("Mapped Bypasses.Heli_bypass=true -> PerAttackerStructureScaling[PatrolHelicopter] = 1.0");
                }
            }

            // Time source
            if (dc["Time"] is JObject timeBlock)
            {
                var tt = timeBlock.Value<string>("Time_Type");
                if (!string.IsNullOrEmpty(tt))
                {
                    _config.TimeOfDaySource = tt.Equals("real", StringComparison.OrdinalIgnoreCase) ? "Real" : "Game";
                    imported++;
                    report.Add($"Mapped Time.Time_Type='{tt}' -> TimeOfDaySource={_config.TimeOfDaySource}");
                }
            }

            // Time multipliers - DC has 8 categories, PDG has 4. Map the close ones.
            var timeMap = new Dictionary<string, string>
            {
                ["Global_Time_Multipliers"]   = TodGlobal,
                ["Player_Time_Multipliers"]   = TodPvp,
                ["NPC_Time_Multipliers"]      = TodNpcToPlayer,
                ["Building_Time_Multipliers"] = TodNpcToStructure
            };
            foreach (var entry in timeMap)
            {
                if (dc[entry.Key] is JObject hours)
                {
                    var arr = new float[24];
                    for (int i = 0; i < 24; i++) arr[i] = 1f;
                    int set = 0;
                    foreach (var prop in hours.Properties())
                    {
                        if (!int.TryParse(prop.Name.Trim(), out var h)) continue;
                        if (h < 0 || h > 23) continue;
                        if (float.TryParse(prop.Value.ToString(), System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out var v))
                        { arr[h] = v; set++; }
                    }
                    if (set > 0)
                    {
                        if (_config.TimeOfDayMultipliers == null)
                            _config.TimeOfDayMultipliers = new Dictionary<string, float[]>();
                        _config.TimeOfDayMultipliers[entry.Value] = arr;
                        imported++;
                        report.Add($"Mapped {entry.Key} ({set} hours) -> TimeOfDayMultipliers[\"{entry.Value}\"]");
                    }
                }
            }

            // Unmapped DC fields
            var unmapped = new[]
            {
                "Animal_Time_Multipliers", "Heli_Time_Multipliers", "Bradley_Time_Multipliers", "Other_Time_Multipliers",
                "Building"
            };
            foreach (var u in unmapped)
            {
                if (dc[u] != null)
                {
                    skipped++;
                    report.Add($"SKIPPED {u}: no direct PVEDamageGuard equivalent. See docs/configuration.md for the migration mapping table.");
                }
            }

            return (imported, skipped, report);
        }

        private static string CapitalizeDamageType(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // Special cases: RadiationExposure, ColdExposure, ElectricShock have multi-word forms
            var lower = name.ToLowerInvariant();
            switch (lower)
            {
                case "radiationexposure": return "RadiationExposure";
                case "coldexposure":      return "ColdExposure";
                case "electricshock":     return "ElectricShock";
                case "antivehicle":       return "AntiVehicle";
                case "fun_water":         return "Fun_Water";
                default:
                    return char.ToUpperInvariant(name[0]) + name.Substring(1).ToLowerInvariant();
            }
        }

        // Help system - per-subcommand help text.
        private string GetHelpFor(string subcommand)
        {
            switch (subcommand?.ToLowerInvariant())
            {
                case null:
                case "":
                    return Lang("HelpRoot");
                case "reload":   return Lang("HelpReload");
                case "log":      return Lang("HelpLog");
                case "logfile":  return Lang("HelpLogFile");
                case "scale":    return Lang("HelpScale");
                case "hour":     return Lang("HelpHour");
                case "context":  return Lang("HelpContext");
                case "history":  return Lang("HelpHistory");
                case "test":     return Lang("HelpTest");
                case "import":   return Lang("HelpImport");
                case "preset":   return string.Format(Lang("HelpPreset"), string.Join(", ", _presetNames));
                case "help":     return Lang("HelpHelp");
                default:         return string.Format(Lang("HelpUnknown"), subcommand);
            }
        }

        #endregion

        #region Existing helpers

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
            try { attacker.Hurt(total, major, victim, true); }
            finally { _reflectInFlight.Remove(attacker.userID); }
            LogReflect(victim, attacker, total, major);
        }

        #endregion

        #region Logging + history

        private bool LogAt(LogLevel min) => _config.Logging >= min;

        private void Log(LogLevel level, string msg)
        {
            if (!LogAt(level)) return;
            Puts(msg);
            if (_config.LogToFile)
                LogToFile("damage", $"[{DateTime.Now:HH:mm:ss}] {msg}", this);
        }

        private void LogHit(LogLevel level, string tag, HitInfo info, BaseCombatEntity entity,
                            NpcCategory attackerCat, NpcCategory victimCat, string context, string action)
        {
            // Always push to history (regardless of console log level) - it's a separate diagnostic
            var attackerName = info.Initiator?.ShortPrefabName ?? "<none>";
            var victimName   = entity.ShortPrefabName ?? "<none>";
            var dmg          = info.damageTypes?.Total() ?? 0f;
            var major        = info.damageTypes?.GetMajorityDamageType() ?? DamageType.Generic;

            _history.Enqueue(new HistoryEntry
            {
                At = DateTime.Now,
                Tag = tag,
                AttackerCat = attackerCat.ToString(),
                AttackerName = attackerName,
                VictimCat = victimCat.ToString(),
                VictimName = victimName,
                Context = context,
                Damage = dmg,
                MajorType = major.ToString(),
                Action = action ?? string.Empty,
            });
            while (_history.Count > HistoryCapacity) _history.Dequeue();

            if (!LogAt(level)) return;
            var msg = $"[{tag}] {attackerCat}({attackerName}) -> {victimCat}({victimName}) | {major} {dmg:F1}";
            if (context != null) msg += $" | ctx={context}";
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

            if (args.Length == 0) { ShowStatus(player); return; }

            switch (args[0].ToLower())
            {
                case "reload":  CmdReload(player); break;
                case "log":     CmdLog(player, args); break;
                case "logfile": CmdLogFile(player, args); break;
                case "scale":   CmdScale(player, args); break;
                case "hour":    CmdHour(player); break;
                case "test":    CmdTest(player, args); break;
                case "history": CmdHistory(player, args); break;
                case "context": CmdContext(player); break;
                case "preset":  CmdPreset(player, args); break;
                case "import":  CmdImport(player, args); break;
                case "help":    CmdHelp(player, args); break;
                case "validate": CmdValidate(player); break;
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
                _config.BlockPvpIfNotReflecting, _config.AllowTeammateDamage,
                defaultNpc, _config.NpcToStructureScaling, _config.TreatPlayerTrapsAsPvp,
                _config.Logging, _config.LogToFile, _yieldToTruePve,
                _todEnabled, _victimScalingEnabled, _buildingGradeEnabled, _perAttackerStructureEnabled, _ruleMatrixEnabled,
                GetCurrentHour(), _config.TimeOfDaySource,
                _activeEvents.Count, _configIssues.Count));
        }

        private void CmdReload(IPlayer player)
        {
            LoadConfig();
            RebuildCaches();
            ValidateConfig();
            DetectCompanions();
            if (_configIssues.Count > 0)
                player.Reply(string.Format(Lang("ConfigReloadedWithIssues", player.Id), _configIssues.Count));
            else
                player.Reply(Lang("ConfigReloaded", player.Id));
        }

        private void CmdPreset(IPlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                player.Reply(string.Format(Lang("PresetUsage", player.Id), string.Join(", ", _presetNames)));
                return;
            }
            var name = args[1].ToLowerInvariant();
            if (Array.IndexOf(_presetNames, name) < 0)
            {
                player.Reply(string.Format(Lang("PresetUnknown", player.Id), name, string.Join(", ", _presetNames)));
                return;
            }
            try
            {
                ApplyPreset(name);
                SaveConfig();
                RebuildCaches();
                ValidateConfig();
                player.Reply(string.Format(Lang("PresetApplied", player.Id), name));
            }
            catch (Exception e)
            {
                player.Reply($"Failed to apply preset '{name}': {e.Message}");
            }
        }

        private void CmdImport(IPlayer player, string[] args)
        {
            if (args.Length < 2 || !args[1].Equals("damagecontrol", StringComparison.OrdinalIgnoreCase))
            {
                player.Reply(Lang("ImportUsage", player.Id));
                return;
            }
            var result = ImportFromDamageControl();
            SaveConfig();
            RebuildCaches();
            ValidateConfig();
            var header = string.Format(Lang("ImportDone", player.Id), result.imported, result.skipped);
            player.Reply(header + "\n" + string.Join("\n", result.report));
        }

        private void CmdHelp(IPlayer player, string[] args)
        {
            string sub = args.Length >= 2 ? args[1] : null;
            player.Reply(GetHelpFor(sub));
        }

        private void CmdValidate(IPlayer player)
        {
            ValidateConfig();
            if (_configIssues.Count == 0)
                player.Reply(Lang("ValidateClean", player.Id));
            else
                player.Reply(string.Format(Lang("ValidateIssues", player.Id), _configIssues.Count)
                             + "\n  - " + string.Join("\n  - ", _configIssues));
        }

        private void CmdLog(IPlayer player, string[] args)
        {
            if (args.Length < 2) { player.Reply(string.Format(Lang("LogCurrent", player.Id), _config.Logging)); return; }
            if (!Enum.TryParse<LogLevel>(args[1], true, out var lvl))
            { player.Reply(string.Format(Lang("LogUnknown", player.Id), args[1])); return; }
            _config.Logging = lvl;
            SaveConfig();
            player.Reply(string.Format(Lang("LogSet", player.Id), lvl));
        }

        private void CmdLogFile(IPlayer player, string[] args)
        {
            if (args.Length < 2) { player.Reply(string.Format(Lang("LogFileCurrent", player.Id), _config.LogToFile)); return; }
            var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase) || args[1] == "true" || args[1] == "1";
            _config.LogToFile = on;
            SaveConfig();
            player.Reply(string.Format(Lang("LogFileSet", player.Id), on));
        }

        private void CmdScale(IPlayer player, string[] args)
        {
            if (args.Length < 3) { player.Reply(Lang("ScaleUsage", player.Id)); return; }
            if (!float.TryParse(args[2], out var mult) || mult < 0f || mult > 100f)
            { player.Reply(Lang("ScaleBadNumber", player.Id)); return; }
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

        private void CmdContext(IPlayer player)
        {
            var bp = player.Object as BasePlayer;
            if (bp == null) { player.Reply(Lang("ContextOnlyInGame", player.Id)); return; }
            if (!_ruleMatrixEnabled) { player.Reply(Lang("ContextRuleMatrixDisabled", player.Id)); return; }
            var ctx = ResolveContext(bp.transform.position, bp);
            player.Reply(string.Format(Lang("ContextReport", player.Id),
                ctx, _config.RuleMatrix.DefaultContext, _activeEvents.Count));
        }

        private void CmdHistory(IPlayer player, string[] args)
        {
            int n = 10;
            if (args.Length >= 2 && int.TryParse(args[1], out var parsed)) n = Math.Max(1, Math.Min(parsed, HistoryCapacity));
            var entries = _history.ToArray();
            int start = Math.Max(0, entries.Length - n);
            var lines = new List<string>();
            lines.Add(string.Format(Lang("HistoryHeader", player.Id), entries.Length - start, entries.Length, HistoryCapacity));
            for (int i = start; i < entries.Length; i++)
            {
                var h = entries[i];
                var ctxPart = h.Context != null ? $" ctx={h.Context}" : string.Empty;
                lines.Add($"  [{h.At:HH:mm:ss}] {h.Tag} {h.AttackerCat}({h.AttackerName}) -> {h.VictimCat}({h.VictimName}) | {h.MajorType} {h.Damage:F1} -> {h.Action}{ctxPart}");
            }
            player.Reply(string.Join("\n", lines));
        }

        private void CmdTest(IPlayer player, string[] args)
        {
            var bp = player.Object as BasePlayer;
            if (bp == null) { player.Reply(Lang("TestOnlyInGame", player.Id)); return; }

            RaycastHit hit;
            if (!Physics.Raycast(bp.eyes.HeadRay(), out hit, 250f))
            { player.Reply(Lang("TestNoHit", player.Id)); return; }
            var ent = hit.GetEntity();
            if (ent == null) { player.Reply(Lang("TestNoEntity", player.Id)); return; }

            // /pdg test fire <type> <amount> - simulate hit
            if (args.Length >= 4 && string.Equals(args[1], "fire", StringComparison.OrdinalIgnoreCase))
            {
                CmdTestFire(player, ent, args[2], args[3]);
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

            // Active context, if rule matrix is on
            if (_ruleMatrixEnabled)
            {
                var ctx = ResolveContext(ent.transform != null ? ent.transform.position : Vector3.zero, bp);
                lines.Add(string.Format(Lang("TestContext", player.Id), ctx));

                // Preview what rule fires against this entity as a victim from a real player attacker
                var actionVsPlayer = ResolveRule(ctx, NpcCategory.RealPlayer, cat, null, sub != "<none>" ? sub : null);
                var actionFromNpc  = ResolveRule(ctx, NpcCategory.HumanNpc, cat, null, sub != "<none>" ? sub : null);
                lines.Add(string.Format(Lang("TestRuleAsVictim", player.Id), actionVsPlayer.Encode(), actionFromNpc.Encode()));
            }
            else if (cat == NpcCategory.HumanNpc || cat == NpcCategory.AnimalNpc || cat == NpcCategory.VehicleNpc)
            {
                float dmm = 1f; _config.NpcToPlayerScaling.TryGetValue("Default", out dmm);
                float npcToPlayerTod = GetTodMult(TodNpcToPlayer, hour);
                float structMult = GetAttackerStructureMult(ent, cat);
                float npcToStructureTod = GetTodMult(TodNpcToStructure, hour);
                lines.Add(string.Format(Lang("TestRuleNpcAttacker", player.Id), dmm, npcToPlayerTod, todGlobal, structMult, npcToStructureTod));
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

            if (_victimScalingEnabled && sub != "<none>"
                && _config.PerVictimSubtypeScaling.TryGetValue(sub, out var subMap))
            {
                float subDefault = 1f; subMap.TryGetValue("Default", out subDefault);
                lines.Add(string.Format(Lang("TestRuleVictimSubtype", player.Id), sub, subDefault));
            }

            player.Reply(string.Join("\n", lines));
        }

        // /pdg test fire <damageType> <amount> - dry-run a synthetic hit through the modifier stack
        private void CmdTestFire(IPlayer player, BaseEntity target, string typeArg, string amountArg)
        {
            if (!Enum.TryParse<DamageType>(typeArg, true, out var dt))
            { player.Reply(string.Format(Lang("TestFireBadType", player.Id), typeArg)); return; }
            if (!float.TryParse(amountArg, out var amt) || amt <= 0f)
            { player.Reply(Lang("TestFireBadAmount", player.Id)); return; }

            var bp = player.Object as BasePlayer;
            var targetCombat = target as BaseCombatEntity;
            if (targetCombat == null) { player.Reply(Lang("TestFireNotCombatEntity", player.Id)); return; }

            // Build synthetic HitInfo: real player (bp) attacks target with the configured damage type
            var info = new HitInfo();
            info.Initiator = bp;
            info.HitEntity = targetCombat;
            info.damageTypes = new DamageTypeList();
            info.damageTypes.Add(dt, amt);

            float incoming = amt;

            // We can't safely run the FULL OnEntityTakeDamage (it would actually hurt the target).
            // Instead, simulate what the hook would do by computing the same multipliers.
            var attackerCat = ClassifyEntity(bp);
            var victimCat   = ClassifyEntity(targetCombat);
            var attackerSub = ClassifySubtype(bp);
            var victimSub   = ClassifySubtype(targetCombat);
            int hour = GetCurrentHour();
            float todGlobal = _todEnabled ? GetTodMult(TodGlobal, hour) : 1f;

            string outcome;
            if (_ruleMatrixEnabled)
            {
                var ctx = ResolveContext(targetCombat.transform.position, bp);
                var action = ResolveRule(ctx, attackerCat, victimCat, attackerSub, victimSub);
                outcome = string.Format(Lang("TestFireRuleMatrixOutcome", player.Id),
                    ctx, action.Encode(), SimulateFinalDamage(info, action, victimCat, victimSub, hour));
            }
            else
            {
                float finalDmg = SimulateLegacyDamage(info, attackerCat, victimCat, targetCombat, victimSub, hour, todGlobal);
                outcome = string.Format(Lang("TestFireLegacyOutcome", player.Id), finalDmg);
            }

            player.Reply(string.Format(Lang("TestFireHeader", player.Id),
                dt, amt, target.ShortPrefabName, victimCat, victimSub ?? "<none>") + "\n" + outcome);
        }

        private float SimulateFinalDamage(HitInfo info, RuleAction action, NpcCategory victimCat, string victimSub, int hour)
        {
            if (action is BlockAction) return 0f;
            if (action is ReflectAction ra)
            {
                float pvpTod = _todEnabled ? GetTodMult(TodPvp, hour) : 1f;
                float todGlobalLocal = _todEnabled ? GetTodMult(TodGlobal, hour) : 1f;
                return info.damageTypes.Total() * ra.Multiplier * todGlobalLocal * pvpTod;
            }
            if (action is AllowAction) return info.damageTypes.Total();
            if (action is ScaleAction sa)
            {
                ApplyScaleAction(info, sa);
                float compose = 1f;
                if (_todEnabled)
                {
                    compose *= GetTodMult(TodGlobal, hour);
                    if (victimCat == NpcCategory.RealPlayer) compose *= GetTodMult(TodNpcToPlayer, hour);
                    else if (victimCat == NpcCategory.Building || victimCat == NpcCategory.Deployable)
                        compose *= GetTodMult(TodNpcToStructure, hour);
                }
                if (_buildingGradeEnabled && info.HitEntity is BuildingBlock bb)
                    compose *= GetBuildingGradeMult(bb.grade);
                if (Math.Abs(compose - 1f) > 0.0001f) info.damageTypes.ScaleAll(compose);
                ApplyPerVictimSubtypeScaling(info, victimSub, 1f);
                return info.damageTypes.Total();
            }
            return info.damageTypes.Total();
        }

        private float SimulateLegacyDamage(HitInfo info, NpcCategory aCat, NpcCategory vCat, BaseCombatEntity victim,
                                           string victimSub, int hour, float todGlobal)
        {
            // Player -> anything is mostly vanilla with subtype scaling
            ApplyPerVictimSubtypeScaling(info, victimSub, todGlobal);
            return info.damageTypes.Total();
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
                ["ConfigReloadedWithIssues"] = "PVEDamageGuard config reloaded with {0} validation issue(s). Run /pdg validate for details.",
                ["UsageRoot"]       = "Usage: /pdg [reload | log <level> | logfile <on|off> | scale <type> <mult> | hour | context | history [N] | test [fire <type> <amount>] | preset <name> | import damagecontrol | validate | help [subcommand]]",
                ["PresetUsage"]     = "Usage: /pdg preset <name>. Available presets: {0}.",
                ["PresetUnknown"]   = "Unknown preset '{0}'. Available: {1}.",
                ["PresetApplied"]   = "Applied preset '{0}'. Config saved and reloaded. Review with /pdg status.",
                ["ImportUsage"]     = "Usage: /pdg import damagecontrol. Reads oxide/config/DamageControl.json and maps compatible settings into PVEDamageGuard.",
                ["ImportDone"]      = "Damage Control import complete. Imported: {0}, skipped: {1}.",
                ["ValidateClean"]   = "Config validation passed. No issues found.",
                ["ValidateIssues"]  = "Config validation found {0} issue(s):",
                ["HelpRoot"]        = "PVEDamageGuard subcommands. Use /pdg help <subcommand> for details on each:\n  reload   - reload config from disk\n  log      - set console log verbosity\n  logfile  - toggle file logging\n  scale    - tune NPC->Player damage multiplier\n  hour     - show current hour and TOD multipliers\n  context  - show active rule-matrix context\n  history  - show recent classified hits\n  test     - aim and classify, or 'test fire <type> <amount>' to dry-run\n  preset   - apply a known-good preset config\n  import   - import from Damage Control config\n  validate - check config for issues\n  help     - this help",
                ["HelpReload"]      = "/pdg reload - reload config and lang from disk, re-run validation. Equivalent to oxide.reload PVEDamageGuard.",
                ["HelpLog"]         = "/pdg log <level> - set log verbosity. Levels: None (silent), Reflects (only PvP reflects), Scaled (every modified hit), All (also passthroughs), Trace (full HitInfo dump). Example: /pdg log Reflects",
                ["HelpLogFile"]     = "/pdg logfile <on|off> - toggle whether log lines are also written to oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt. Example: /pdg logfile on",
                ["HelpScale"]       = "/pdg scale <DamageType> <multiplier 0-100> - tune the NPC->Player scaling for one damage type. Persists to config. Example: /pdg scale Bullet 0.1",
                ["HelpHour"]        = "/pdg hour - report current hour from TimeOfDaySource and all four TOD multipliers (Global, PvP, NpcToPlayer, NpcToStructure).",
                ["HelpContext"]     = "/pdg context - show the rule-matrix context active at your current position. Only meaningful when RuleMatrix.Enabled=true.",
                ["HelpHistory"]     = "/pdg history [N] - show the last N classified hits (default 10, max 100). Filled regardless of console log level.",
                ["HelpTest"]        = "/pdg test - aim at an entity, see classification + active rule. /pdg test fire <DamageType> <amount> - dry-run a synthetic hit and report final damage without applying it. Example: /pdg test fire Bullet 100",
                ["HelpImport"]      = "/pdg import damagecontrol - read oxide/config/DamageControl.json and map compatible settings into PVEDamageGuard's config. Backs up the current config first.",
                ["HelpPreset"]      = "/pdg preset <name> - apply a known-good config. Available presets: {0}. Each preset is a complete config snapshot; existing fine-tuning is overwritten.",
                ["HelpHelp"]        = "/pdg help [subcommand] - show this help, or detail on one subcommand.",
                ["HelpUnknown"]     = "Unknown subcommand '{0}'. Run /pdg help for the list.",
                ["LogCurrent"]      = "Current log level: {0}. Usage: /pdg log <None|Reflects|Scaled|All|Trace>",
                ["LogUnknown"]      = "Unknown log level '{0}'. Valid: None, Reflects, Scaled, All, Trace.",
                ["LogSet"]          = "Log level set to {0}.",
                ["LogFileCurrent"]  = "Current file logging: {0}. Usage: /pdg logfile <on|off>",
                ["LogFileSet"]      = "File logging set to {0}. Writes to oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt",
                ["ScaleUsage"]      = "Usage: /pdg scale <DamageType> <multiplier 0-100>",
                ["ScaleBadNumber"]  = "Multiplier must be a number between 0 and 100.",
                ["ScaleSet"]        = "NPC->Player scaling for {0} set to {1:F2}x.",
                ["HourReport"]      = "Hour {0} ({1}). TOD multipliers: Global={2:F2}, PvP={3:F2}, NpcToPlayer={4:F2}, NpcToStructure={5:F2}.",
                ["ContextOnlyInGame"] = "/pdg context must be run by an in-game player (position-based lookup).",
                ["ContextRuleMatrixDisabled"] = "Rule matrix is disabled in config (RuleMatrix.Enabled=false). No context is currently active.",
                ["ContextReport"]   = "Active context at your position: '{0}' (default fallback: '{1}'). Active tracked events: {2}.",
                ["HistoryHeader"]   = "Showing last {0} of {1} hits (buffer capacity {2}):",
                ["TestOnlyInGame"]  = "/pdg test must be run by an in-game player.",
                ["TestNoHit"]       = "/pdg test: raycast hit nothing within 250m.",
                ["TestNoEntity"]    = "/pdg test: raycast hit a surface but no game entity.",
                ["TestTarget"]      = "Target: {0} (type={1}) classified as {2}, subtype={3}",
                ["TestDistance"]    = "Distance: {0:F1}m",
                ["TestHour"]        = "Hour: {0} ({1}). Global TOD multiplier: {2:F2}x",
                ["TestContext"]     = "Active context: '{0}'",
                ["TestRuleAsVictim"] = "Rules at this context: RealPlayer -> {0}_victim = '{0}', HumanNpc -> {0}_victim = '{1}'",
                ["TestRuleNpcAttacker"] = "If this entity damages a player: NPC->Player scaling (Default {0:F2}x) * NpcToPlayer TOD ({1:F2}x) * Global TOD ({2:F2}x). If it damages a structure: attacker struct scaling {3:F2}x * NpcToStructure TOD ({4:F2}x).",
                ["TestRulePvp"]     = "If you damage this player: Reflect={0} ({1:F2}x), BlockIfNotReflecting={2}, YieldToTruePVE={3}, PvP TOD={4:F2}x",
                ["TestRuleBuilding"]= "If an NPC damages this building: NpcToStructure default {0:F2}x, Building grade multiplier {1:F2}x (grade={2}).",
                ["TestRuleStructure"]= "If an NPC damages this deployable: NpcToStructure scaling {0:F2}x (0 = blocked).",
                ["TestRuleTrap"]    = "Trap is player-owned. Damage from this trap to other players is treated as PvP from the owner.",
                ["TestRuleOther"]   = "No PVEDamageGuard rule applies to this entity. Vanilla damage behavior.",
                ["TestRuleVictimSubtype"] = "Per-victim subtype scaling: '{0}' Default {1:F2}x (stacks on top of attacker rules).",
                ["TestFireBadType"] = "/pdg test fire: unknown damage type '{0}'.",
                ["TestFireBadAmount"] = "/pdg test fire: amount must be a positive number.",
                ["TestFireNotCombatEntity"] = "/pdg test fire: target is not a damageable entity.",
                ["TestFireHeader"]  = "Dry-run: {0} {1:F1} damage to {2} ({3} / subtype={4}).",
                ["TestFireRuleMatrixOutcome"] = "Rule matrix: context='{0}', action={1}. Final damage if applied: {2:F1}.",
                ["TestFireLegacyOutcome"] = "Legacy scaling path. Final damage if applied: {0:F1}.",
                ["StatusBlock"]     =
                    "PVE Damage Guard v{0}\n" +
                    "  Reflect: {1} ({2:F2}x), BlockPvpIfNotReflecting: {3}, Teammates: {4}\n" +
                    "  NPC->Player default: {5:F2}, NPC->Structure default: {6:F2}, Traps as PvP: {7}\n" +
                    "  Logging: {8} (file={9}), Yield to TruePVE: {10}\n" +
                    "  Features: TOD={11}, VictimSubtype={12}, BuildingGrade={13}, PerAttackerStruct={14}, RuleMatrix={15}\n" +
                    "  Current hour: {16} ({17}), Tracked events: {18}, Config issues: {19}\n" +
                    "  Commands: /pdg reload | log | logfile | scale | hour | context | history | test | preset | import | validate | help"
            }, this);
        }

        #endregion
    }
}
