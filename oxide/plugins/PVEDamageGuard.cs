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
    [Info("PVE Damage Guard", "Gabriel Dungan (DunganSoft Technologies)", "1.0.0")]
    [Description("Future-proof NPC classifier, per-attacker damage scaling, and reflect-as-a-service for PVE Rust servers. Designed as a TruePVE companion.")]
    public class PVEDamageGuard : CovalencePlugin
    {
        [PluginReference] private Plugin TruePVE;
        [PluginReference] private Plugin PVEMode;
        [PluginReference] private Plugin NextGenPVE;

        private const string PermBypass = "pvedamageguard.bypass";
        private const string PermAdmin  = "pvedamageguard.admin";

        private static readonly DamageType[] _allDamageTypes = (DamageType[])Enum.GetValues(typeof(DamageType));

        private Configuration _config;
        private readonly HashSet<DamageType> _envDamageTypes = new HashSet<DamageType>();
        private readonly HashSet<ulong> _reflectInFlight = new HashSet<ulong>();
        private bool _yieldToTruePve;

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
            [JsonProperty("PvP - Reflect damage to shooter (master switch)")]
            public bool ReflectPvpEnabled = true;

            [JsonProperty("PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)")]
            public float ReflectMultiplier = 1.0f;

            [JsonProperty("PvP - If reflect is disabled, block PvP damage outright instead of letting it through")]
            public bool BlockPvpIfNotReflecting = true;

            [JsonProperty("PvP - Allow teammates (Rust team system) to damage each other")]
            public bool AllowTeammateDamage = false;

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

            [JsonProperty("NPC -> Structure - Uniform scaling for heli/Bradley/scientist damage to player-built structures (0 = invulnerable)")]
            public float NpcToStructureScaling = 0.5f;

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
            Puts($"PVE Damage Guard v{Version} loaded. Reflect={_config.ReflectPvpEnabled}, NPC->Structure={_config.NpcToStructureScaling:F2}x, Logging={_config.Logging}, YieldToTruePVE={_yieldToTruePve}");
        }

        private void DetectCompanions()
        {
            _yieldToTruePve = _config.YieldToTruePVE && (TruePVE != null);
            if (TruePVE != null)
                Puts(_yieldToTruePve
                    ? "TruePVE detected. Yielding allow/block to TruePVE; PVEDamageGuard will only classify, scale, and reflect-on-request."
                    : "TruePVE detected but YieldToTruePVE=false in config. Both plugins will hook OnEntityTakeDamage - verify your intent.");
            if (PVEMode != null)    PrintWarning("PVEMode also loaded. Test carefully for conflicts.");
            if (NextGenPVE != null) PrintWarning("NextGenPVE also loaded. Test carefully for conflicts.");
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin?.Name == "TruePVE" || plugin?.Name == "PVEMode" || plugin?.Name == "NextGenPVE")
                DetectCompanions();
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin?.Name == "TruePVE" || plugin?.Name == "PVEMode" || plugin?.Name == "NextGenPVE")
                DetectCompanions();
        }

        #endregion

        #region Public API (callable by other plugins)

        // OTHER_PLUGIN: var cat = (string)PVEDamageGuard?.Call("API_Classify", entity);
        [HookMethod("API_Classify")]
        public string API_Classify(BaseEntity entity)
        {
            return ClassifyEntity(entity).ToString();
        }

        // OTHER_PLUGIN: var isNpc = (bool)PVEDamageGuard?.Call("API_IsNpcAttacker", hitInfo);
        [HookMethod("API_IsNpcAttacker")]
        public bool API_IsNpcAttacker(HitInfo info)
        {
            if (info == null) return false;
            var cat = ClassifyEntity(ResolveRootAttacker(info));
            return cat == NpcCategory.HumanNpc || cat == NpcCategory.AnimalNpc || cat == NpcCategory.VehicleNpc;
        }

        // OTHER_PLUGIN: PVEDamageGuard?.Call("API_ReflectDamage", attacker, victim, hitInfo, 1.0f);
        [HookMethod("API_ReflectDamage")]
        public bool API_ReflectDamage(BasePlayer attacker, BasePlayer victim, HitInfo info, float multiplier)
        {
            if (attacker == null || victim == null || info == null) return false;
            DoReflect(attacker, victim, info, multiplier);
            return true;
        }

        // OTHER_PLUGIN: var mult = (float)PVEDamageGuard?.Call("API_GetNpcScaling", "Bullet");
        [HookMethod("API_GetNpcScaling")]
        public float API_GetNpcScaling(string damageType)
        {
            if (_config.NpcToPlayerScaling.TryGetValue(damageType, out var m)) return m;
            return _config.NpcToPlayerScaling.TryGetValue("Default", out var d) ? d : 1f;
        }

        #endregion

        #region Hook

        // Return null = let vanilla apply (possibly scaled) damage.
        // Return true = cancel damage entirely.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null) return null;

            // Reflect-in-progress passthrough.
            if (entity is BasePlayer beingHit && _reflectInFlight.Contains(beingHit.userID))
                return null;

            var rootAttacker = ResolveRootAttacker(info);
            var attackerCat  = ClassifyEntity(rootAttacker);
            var victimCat    = ClassifyEntity(entity);
            var victimPlayer = entity as BasePlayer;

            // Bypass permission for testing
            if (victimCat == NpcCategory.RealPlayer && permission.UserHasPermission(victimPlayer.UserIDString, PermBypass))
            {
                LogHit(LogLevel.All, "bypass-perm", info, entity, attackerCat, victimCat);
                return null;
            }

            // Environmental damage - never touch
            if (info.Initiator == null || rootAttacker == null || IsEnvironmental(info))
            {
                LogHit(LogLevel.All, "env-passthrough", info, entity, attackerCat, victimCat);
                return null;
            }

            // ---- PvP ----
            if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer
                && rootAttacker != entity)
            {
                if (_config.AllowTeammateDamage && AreTeammates(rootAttacker as BasePlayer, victimPlayer))
                {
                    LogHit(LogLevel.All, "teammate-allow", info, entity, attackerCat, victimCat);
                    return null;
                }
                // Yield allow/block to TruePVE if present; only do reflect if explicitly enabled
                if (_yieldToTruePve)
                {
                    LogHit(LogLevel.All, "pvp-yielded-to-truepve", info, entity, attackerCat, victimCat);
                    return null;
                }
                if (_config.ReflectPvpEnabled)
                {
                    DoReflect(rootAttacker as BasePlayer, victimPlayer, info, _config.ReflectMultiplier);
                    return true;
                }
                if (_config.BlockPvpIfNotReflecting)
                {
                    LogHit(LogLevel.Scaled, "pvp-blocked", info, entity, attackerCat, victimCat);
                    return true;
                }
                return null;
            }

            // ---- Player damaging NPC - vanilla ----
            if (attackerCat == NpcCategory.RealPlayer
                && (victimCat == NpcCategory.HumanNpc || victimCat == NpcCategory.AnimalNpc || victimCat == NpcCategory.VehicleNpc))
            {
                LogHit(LogLevel.All, "player->npc", info, entity, attackerCat, victimCat);
                return null;
            }

            // ---- NPC -> Player - scale per damage type ----
            if ((attackerCat == NpcCategory.HumanNpc || attackerCat == NpcCategory.AnimalNpc || attackerCat == NpcCategory.VehicleNpc)
                && victimCat == NpcCategory.RealPlayer)
            {
                ApplyDamageTypeScaling(info, _config.NpcToPlayerScaling);
                LogHit(LogLevel.Scaled, "npc->player-scaled", info, entity, attackerCat, victimCat);
                return null;
            }

            // ---- NPC -> Structure - uniform scale ----
            if ((attackerCat == NpcCategory.HumanNpc || attackerCat == NpcCategory.AnimalNpc || attackerCat == NpcCategory.VehicleNpc)
                && (victimCat == NpcCategory.Building || victimCat == NpcCategory.Deployable))
            {
                if (_config.NpcToStructureScaling <= 0f)
                {
                    LogHit(LogLevel.Scaled, "npc->structure-blocked", info, entity, attackerCat, victimCat);
                    return true;
                }
                info.damageTypes.ScaleAll(_config.NpcToStructureScaling);
                LogHit(LogLevel.Scaled, $"npc->structure-{_config.NpcToStructureScaling:F2}x", info, entity, attackerCat, victimCat);
                return null;
            }

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

            // Player-owned combat entity (turret, trap)
            if (entity is BaseCombatEntity bce && bce.OwnerID != 0UL && bce.OwnerID.IsSteamId())
                return NpcCategory.OwnedTrap;

            return NpcCategory.Other;
        }

        // Walks projectile/explosive/trap wrappers to the source entity.
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

        #region Helpers

        private bool IsEnvironmental(HitInfo info)
        {
            var major = info.damageTypes.GetMajorityDamageType();
            return _envDamageTypes.Contains(major);
        }

        private bool AreTeammates(BasePlayer a, BasePlayer b)
        {
            if (a == null || b == null) return false;
            if (a.currentTeam == 0UL || b.currentTeam == 0UL) return false;
            return a.currentTeam == b.currentTeam;
        }

        private void ApplyDamageTypeScaling(HitInfo info, Dictionary<string, float> map)
        {
            float defaultMult = 1.0f;
            map.TryGetValue("Default", out defaultMult);

            for (int i = 0; i < _allDamageTypes.Length; i++)
            {
                var dt = _allDamageTypes[i];
                if (dt == DamageType.LAST) continue;
                if (_envDamageTypes.Contains(dt)) continue;

                float mult = defaultMult;
                if (map.TryGetValue(dt.ToString(), out var configured))
                    mult = configured;

                info.damageTypes.Scale(dt, mult);
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
                _yieldToTruePve));
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
            // /pdg scale <damageType> <multiplier>
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

        // /pdg test - raycast from the admin's crosshair and report classification
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
            var line1 = string.Format(Lang("TestTarget", player.Id),
                ent.ShortPrefabName, ent.GetType().Name, cat);

            var line2 = string.Format(Lang("TestDistance", player.Id), hit.distance);

            string line3;
            if (cat == NpcCategory.HumanNpc || cat == NpcCategory.AnimalNpc || cat == NpcCategory.VehicleNpc)
            {
                float defaultMult = 1f;
                _config.NpcToPlayerScaling.TryGetValue("Default", out defaultMult);
                line3 = string.Format(Lang("TestRuleNpcAttacker", player.Id),
                    defaultMult, _config.NpcToStructureScaling);
            }
            else if (cat == NpcCategory.RealPlayer)
            {
                line3 = string.Format(Lang("TestRulePvp", player.Id),
                    _config.ReflectPvpEnabled, _config.ReflectMultiplier,
                    _config.BlockPvpIfNotReflecting, _yieldToTruePve);
            }
            else if (cat == NpcCategory.Building || cat == NpcCategory.Deployable)
            {
                line3 = string.Format(Lang("TestRuleStructure", player.Id), _config.NpcToStructureScaling);
            }
            else if (cat == NpcCategory.OwnedTrap)
            {
                line3 = Lang("TestRuleTrap", player.Id);
            }
            else
            {
                line3 = Lang("TestRuleOther", player.Id);
            }

            player.Reply($"{line1}\n{line2}\n{line3}");
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
                ["UsageRoot"]       = "Usage: /pdg [reload | log <level> | logfile <on|off> | scale <damageType> <mult> | test]",
                ["LogCurrent"]      = "Current log level: {0}. Usage: /pdg log <None|Reflects|Scaled|All|Trace>",
                ["LogUnknown"]      = "Unknown log level '{0}'. Valid: None, Reflects, Scaled, All, Trace.",
                ["LogSet"]          = "Log level set to {0}.",
                ["LogFileCurrent"]  = "Current file logging: {0}. Usage: /pdg logfile <on|off>",
                ["LogFileSet"]      = "File logging set to {0}. Writes to oxide/logs/PVEDamageGuard/damage-YYYY-MM-DD.txt",
                ["ScaleUsage"]      = "Usage: /pdg scale <DamageType> <multiplier 0-100>",
                ["ScaleBadNumber"]  = "Multiplier must be a number between 0 and 100.",
                ["ScaleSet"]        = "NPC->Player scaling for {0} set to {1:F2}x.",
                ["TestOnlyInGame"]  = "/pdg test must be run by an in-game player.",
                ["TestNoHit"]       = "/pdg test: raycast hit nothing within 250m.",
                ["TestNoEntity"]    = "/pdg test: raycast hit a surface but no game entity.",
                ["TestTarget"]      = "Target: {0} (type={1}) classified as {2}",
                ["TestDistance"]    = "Distance: {0:F1}m",
                ["TestRuleNpcAttacker"]   = "If this entity damages a player: NPC->Player scaling applied (Default {0:F2}x). If it damages a structure: NPC->Structure scaling {1:F2}x.",
                ["TestRulePvp"]     = "If you damage this player: Reflect={0} ({1:F2}x), BlockIfNotReflecting={2}, YieldToTruePVE={3}",
                ["TestRuleStructure"]    = "If an NPC damages this structure: scaling {0:F2}x (0 = blocked).",
                ["TestRuleTrap"]    = "Trap is player-owned. Damage from this trap to other players is treated as PvP from the owner.",
                ["TestRuleOther"]   = "No PVEDamageGuard rule applies to this entity. Vanilla damage behavior.",
                ["StatusBlock"]     =
                    "PVE Damage Guard v{0}\n" +
                    "  Reflect: {1} ({2:F2}x)\n" +
                    "  Block-PvP-if-not-reflecting: {3}\n" +
                    "  Teammate damage allowed: {4}\n" +
                    "  NPC->Player default mult: {5:F2}\n" +
                    "  NPC->Structure mult: {6:F2}\n" +
                    "  Traps treated as PvP: {7}\n" +
                    "  Logging: {8} (file={9})\n" +
                    "  Yield to TruePVE: {10}\n" +
                    "  Commands: /pdg reload | log <lvl> | logfile <on|off> | scale <type> <mult> | test"
            }, this);
        }

        #endregion
    }
}
