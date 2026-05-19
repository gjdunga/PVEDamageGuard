using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PVE Damage Guard", "Gabriel Dungan (DunganSoft Technologies)", "2.0.4")]
    [Description("Future-proof NPC classifier, declarative rule matrix, per-attacker/per-victim damage scaling, time-of-day modifiers, ZoneManager / RaidableBases / Convoy / Armored Train context switching, reflect-as-a-service, Discord webhook output, Damage Control config import, preset configurations, config validation, classification cache, hook timing telemetry, Carbon framework support, in-game CUI admin panel with live-streaming Logging and paginated History tabs, per-player damage statistics, and per-event context overrides for PVE Rust servers.")]
    public class PVEDamageGuard : CovalencePlugin
    {
        [PluginReference] private Plugin TruePVE;
        [PluginReference] private Plugin PVEMode;
        [PluginReference] private Plugin NextGenPVE;
        [PluginReference] private Plugin DamageControl;
        [PluginReference] private Plugin ZoneManager;
        [PluginReference] private Plugin RaidableBases;
        [PluginReference] private Plugin Convoy;
        [PluginReference] private Plugin ArmoredTrain;
        [PluginReference] private Plugin Backpacks;
        [PluginReference] private Plugin Backpacks4;

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

        // RaidableBases dome positions (v1.4): keyed by net.ID of the dome marker, value is center+radius.
        private readonly Dictionary<ulong, TrackedDome> _activeDomes = new Dictionary<ulong, TrackedDome>();

        // Global event flags (v1.4): server-wide "an event is active right now" booleans for Convoy/ArmoredTrain
        // that don't have a single positional marker entity.
        private readonly HashSet<string> _activeGlobalEvents = new HashSet<string>();

        // Discord webhook rate limit (v1.4): sliding 1-minute window of send timestamps.
        private readonly Queue<DateTime> _webhookSendTimes = new Queue<DateTime>();

        // Per-entity classification cache (v1.5): avoid repeated type checks on the same entity.
        // Keyed by net.ID.Value; entries cleared on OnEntityKill.
        private readonly Dictionary<ulong, CachedClassification> _classifyCache = new Dictionary<ulong, CachedClassification>();
        private const int CacheMaxEntries = 10000;

        // Hook timing telemetry (v1.5): rolling buffer of last N OnEntityTakeDamage timings in microseconds.
        private const int TimingBufferSize = 1000;
        private readonly long[] _hookTimingsUs = new long[TimingBufferSize];
        private int _hookTimingIdx;
        private long _hookTimingCount;
        private bool _hookTimingEnabled;

        // Self-test results (v1.5)
        private bool _selfTestPassed = true;
        private string _selfTestSummary = "Self-test not run yet.";

        // Recent log lines for the CUI Logging tab (v1.7)
        private readonly Queue<LogLine> _recentLogLines = new Queue<LogLine>();
        private const int LogLineCapacity = 100;

        // Per-player stats (v1.7), keyed by Steam ID
        private Dictionary<ulong, PlayerStats> _playerStats = new Dictionary<ulong, PlayerStats>();

        // Per-player log-level filter for the Logging CUI tab (v1.7). Default Reflects.
        private readonly Dictionary<ulong, LogLevel> _uiLogFilter = new Dictionary<ulong, LogLevel>();
        // Per-player History tab pagination state (v1.7), zero-based page index.
        private readonly Dictionary<ulong, int> _uiHistoryPage = new Dictionary<ulong, int>();
        private const int HistoryRowsPerPage = 12;

        // Per-player Rules tab selected context (v1.8).
        private readonly Dictionary<ulong, string> _uiRulesContext = new Dictionary<ulong, string>();

        // Custom NPC categories registered by other plugins (v1.8).
        // Matchers run before the built-in ClassifySubtype checks; first match wins.
        private readonly Dictionary<string, Func<BaseEntity, bool>> _registeredCategories = new Dictionary<string, Func<BaseEntity, bool>>();

        // Per-player Rules tab edit-mode flag (v1.9).
        private readonly HashSet<ulong> _uiRulesEditMode = new HashSet<ulong>();

        // Track which players just died from a PVEDamageGuard-induced cause (reflect or
        // block) so external plugins (Backpacks etc.) can query via API_IsPveDeath
        // before deciding whether to drop the corpse's backpack. (v1.9)
        private readonly Dictionary<ulong, DateTime> _recentPveDeaths = new Dictionary<ulong, DateTime>();
        private static readonly TimeSpan PveDeathStickyWindow = TimeSpan.FromSeconds(5);

        // CUI state (v1.6): track which players currently have the panel open and on which tab.
        private readonly Dictionary<ulong, string> _openPanels = new Dictionary<ulong, string>();
        private const string CuiMainPanel = "PvedgPanel";
        private const string CuiContentPanel = "PvedgContent";
        // Theme - PVEDamageGuard brand colors
        private const string CuiBgColor      = "0.10 0.10 0.12 0.95";
        private const string CuiTabBarColor  = "0.15 0.15 0.18 1.0";
        private const string CuiContentColor = "0.13 0.13 0.16 1.0";
        private const string CuiAccentColor  = "0.93 0.49 0.35 1.0";   // coral accent
        private const string CuiAccentDim    = "0.93 0.49 0.35 0.4";
        private const string CuiButtonColor  = "0.20 0.20 0.24 1.0";
        private const string CuiTextColor    = "0.95 0.95 0.95 1.0";
        private const string CuiMutedTextColor = "0.65 0.65 0.65 1.0";

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

        // v1.4 - RaidableBases dome
        private class TrackedDome
        {
            public ulong Id;
            public Vector3 Center;
            public float Radius;
            public int Mode;          // RaidableBases mode/difficulty 0-5
            public DateTime SeenAt;
        }

        // v1.5 - cached classification result for an entity instance.
        private struct CachedClassification
        {
            public NpcCategory Category;
            public string Subtype;
            public bool HasSubtype; // distinguishes "not yet computed" from "computed to null"
        }

        // v1.7 - one ring-buffer entry for the CUI Logging tab
        private struct LogLine
        {
            public DateTime At;
            public LogLevel Level;
            public string Message;
        }

        // v1.7 - per-player stats (also serialized to oxide/data/PVEDamageGuard/stats.json)
        public class PlayerStats
        {
            public string Name;
            public double DamageDealtToPlayers;
            public double DamageReflectedBack;     // damage that was reflected back to me when I attacked someone
            public double DamageTakenFromNpcs;
            public double DamageTakenFromPlayers;
            public long   NpcsKilled;
            public long   PvpKillsAgainstMe;       // I died to another player
            public long   ReflectsAgainstMe;       // how many times I got reflected (regardless of total damage)
            public DateTime FirstSeen;
            public DateTime LastSeen;
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
            public bool ReflectPvpEnabled = true;

            public float ReflectMultiplier = 1.0f;

            public bool BlockPvpIfNotReflecting = true;

            public bool AllowTeammateDamage = false;

            public bool ReflectPlayerDamageToForeignStructures = true;

            // v2.0.4 - when true, damage events where info.Initiator and info.Weapon are both
            // null - so the plugin can't identify a hostile attacker - get blocked outright
            // for RealPlayer victims. Catches helicopter / Bradley crash explosions, /home
            // teleport-into-geometry damage, and plugin-induced Hurt() calls that don't
            // attribute to an attacker. Environmental damage (fall / cold / bleed / etc.)
            // is still allowed because it's matched by EnvironmentalDamageTypes first.
            // Default false for backward compatibility; set true on strict-PVE servers.
            public bool BlockUnattributedDamageToPlayers = false;

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

            public float NpcToStructureScaling = 0.5f;

            public Dictionary<string, float> PerAttackerStructureScaling = new Dictionary<string, float>();

            public Dictionary<string, float> BuildingGradeMultipliers = new Dictionary<string, float>
            {
                ["Twigs"]   = 1.0f,
                ["Wood"]    = 1.0f,
                ["Stone"]   = 1.0f,
                ["Metal"]   = 1.0f,
                ["TopTier"] = 1.0f
            };

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

            public string TimeOfDaySource = "Game";

            public Dictionary<string, float[]> TimeOfDayMultipliers = new Dictionary<string, float[]>
            {
                [TodGlobal]         = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodPvp]            = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToPlayer]    = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1},
                [TodNpcToStructure] = new float[] {1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1}
            };

            public bool TreatPlayerTrapsAsPvp = true;

            // v2.0.1 - ObjectCreationHandling.Replace prevents Newtonsoft from APPENDING
            // the JSON contents to the default list on load. Without this, each LoadConfig
            // call doubled the list (defaults + previous load's contents), accumulating
            // duplicates over many reloads. Same fix applied to the Events lists below.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> EnvironmentalDamageTypes = new List<string>
            {
                "Hunger", "Thirst", "Cold", "Heat", "Drowned",
                "Bleeding", "Poison", "Suicide", "Fall",
                "Radiation", "RadiationExposure", "ColdExposure", "Decay"
            };

            public bool YieldToTruePVE = true;

            [JsonConverter(typeof(StringEnumConverter))]
            public LogLevel Logging = LogLevel.None;

            public bool LogToFile = false;

            public DiscordWebhookConfig DiscordWebhook = new DiscordWebhookConfig();

            public RuleMatrixConfig RuleMatrix = new RuleMatrixConfig();
        }

        private class DiscordWebhookConfig
        {
            public bool Enabled = false;

            public string Url = "";

            [JsonConverter(typeof(StringEnumConverter))]
            public LogLevel MinLevel = LogLevel.Reflects;

            public int RateLimitPerMinute = 20;

            public string MessagePrefix = "";

            public string Username = "PVEDamageGuard";

            public string AvatarUrl = "";
        }

        private class RuleMatrixConfig
        {
            public bool Enabled = false;

            public string DefaultContext = "Default";

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

            public GlobalEventTriggersConfig GlobalEventTriggers = new GlobalEventTriggersConfig();

            public RaidableBasesProviderConfig RaidableBases = new RaidableBasesProviderConfig();
        }

        private class ZoneManagerProviderConfig
        {
            public bool Enabled = true;

            public Dictionary<string, string> ZoneFlagToContext = new Dictionary<string, string>
            {
                ["pvp"] = "AtPvpEvent"
            };
        }

        private class EventTrackerProviderConfig
        {
            public bool Enabled = true;

            public string TriggerContext = "AtPvpEvent";

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Events = new List<string> { "BradleyAPC", "BaseHelicopter", "CargoShip" };

            public float RadiusMeters = 200f;

            public Dictionary<string, string> PerEventContext = new Dictionary<string, string>();
        }

        private class GlobalEventTriggersConfig
        {
            public bool Enabled = true;

            public string TriggerContext = "AtPvpEvent";

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Events = new List<string> { "Convoy", "ArmoredTrain" };

            public Dictionary<string, string> PerEventContext = new Dictionary<string, string>();
        }

        private class RaidableBasesProviderConfig
        {
            public bool Enabled = true;

            public string TriggerContext = "InRaidableBaseDome";

            public float RadiusOverrideMeters = 0f;
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        // v1.7.3 - migrate v1.0 through v1.7.2 long-name config field keys to short names.
        // Old configs used descriptive JsonProperty names like "PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)"
        // which were fragile (text editors mangling smart quotes, encoding swaps, line wraps, embedded commas).
        // v1.7.3 removed all JsonProperty attributes; Newtonsoft now uses C# property names directly.
        // This dictionary maps every old long name to its new short name so existing configs auto-upgrade.
        private static readonly Dictionary<string, string> _legacyFieldRenames = new Dictionary<string, string>
        {
            ["PvP - Reflect damage to shooter (master switch)"] = "ReflectPvpEnabled",
            ["PvP - Reflect multiplier (1.0 = full reflect, 0.5 = half)"] = "ReflectMultiplier",
            ["PvP - If reflect is disabled, block PvP damage outright instead of letting it through"] = "BlockPvpIfNotReflecting",
            ["PvP - Allow teammates (Rust team system) to damage each other"] = "AllowTeammateDamage",
            ["PvP - Reflect damage when a player damages ANOTHER player's structure (BuildingBlock / Door / Deployable). Defaults true. Authorized damage (owner, teammates, TC-authorized players) is never reflected."] = "ReflectPlayerDamageToForeignStructures",
            ["NPC -> Player - Per-damage-type scaling. Missing types use 'Default'. Set to 0 to make players immune."] = "NpcToPlayerScaling",
            ["NPC -> Structure - Default uniform scaling (used when PerAttackerStructureScaling has no match)."] = "NpcToStructureScaling",
            ["NPC -> Structure - Per-attacker overrides. Replaces Damage Control's Heli_bypass; set 'PatrolHelicopter':1.0 to let helis raid at full power."] = "PerAttackerStructureScaling",
            ["Building grade multipliers - Stacks on top of structure scaling. Applies to BuildingBlock only."] = "BuildingGradeMultipliers",
            ["Per-victim subtype scaling - Per-damage-type multipliers applied when the VICTIM matches a known subtype. Stacks on top of attacker rules."] = "PerVictimSubtypeScaling",
            ["Time of day source - 'Game' (TOD_Sky cycle) or 'Real' (server wall clock)."] = "TimeOfDaySource",
            ["Time of day multipliers - 24-element arrays per category. All-ones disables. Categories: Global, PvP, NpcToPlayer, NpcToStructure."] = "TimeOfDayMultipliers",
            ["Treat traps owned by a player as PvP from that owner"] = "TreatPlayerTrapsAsPvp",
            ["Damage types to NEVER touch (always vanilla)"] = "EnvironmentalDamageTypes",
            ["Yield allow/block decisions to TruePVE if it is loaded"] = "YieldToTruePVE",
            ["Log verbosity: None | Reflects | Scaled | All | Trace"] = "Logging",
            ["Also write log entries to oxide/logs/PVEDamageGuard/ files for audit"] = "LogToFile",
            ["Discord webhook output for reflect/block events (v1.4). Disabled by default; admin sets URL to opt in."] = "DiscordWebhook",
            ["Rule matrix configuration (v1.2). Declarative AttackerCategory x VictimCategory -> Action rules with contexts and inheritance. When Enabled=true this REPLACES the case-based scaling for allow/block/reflect decisions; scale actions still compose with TOD, victim subtype, and building grade modifiers."] = "RuleMatrix",
            ["Enabled - master switch. When false the webhook code path is skipped."] = "Enabled",
            ["Discord webhook URL. Get one from Server Settings -> Integrations -> Webhooks in your Discord server."] = "Url",
            ["Minimum log level to forward to Discord (None | Reflects | Scaled | All | Trace). Recommended: Reflects."] = "MinLevel",
            ["Maximum webhook messages per minute (Discord caps at 30; default 20 to leave headroom)."] = "RateLimitPerMinute",
            ["Prefix prepended to every webhook message. Use to identify which server is reporting."] = "MessagePrefix",
            ["Username override that the webhook posts as. Empty = use the webhook's default name."] = "Username",
            ["Avatar URL override. Empty = use the webhook's default avatar."] = "AvatarUrl",
            ["Enabled - master switch. When false the v1.1 case-based scaling logic is used unchanged."] = "Enabled",
            ["Default context name (used when no provider returns a match)"] = "DefaultContext",
            ["Contexts - named rule sets, optionally inheriting from another context"] = "Contexts",
            ["Context providers - ZoneManager and EventTracker. Order: ZoneManager checked first, then EventTracker proximity, else DefaultContext."] = "ContextProviders",
            ["Global event triggers (v1.4): server-wide context flip when Convoy / ArmoredTrain / etc. is active anywhere on the map. Use for events that do not have a single positional marker entity."] = "GlobalEventTriggers",
            ["RaidableBases provider (v1.4): tracks raid base domes via OnRaidableBaseStarted/Ended hooks and proximity-checks the victim position."] = "RaidableBases",
            ["Map ZoneManager zone flags to context names"] = "ZoneFlagToContext",
            ["Which context to switch to when any tracked event is active within RadiusMeters of the victim (fallback if PerEventContext has no match)"] = "TriggerContext",
            ["Which event entity types trigger this context"] = "Events",
            ["Radius from the event entity that activates the context"] = "RadiusMeters",
            ["Per-event context override (v1.6). Map event name to a specific context. Falls back to TriggerContext if event not listed. Example: { \"BradleyAPC\": \"AtBradleyEvent\", \"BaseHelicopter\": \"AtHeliEvent\" }"] = "PerEventContext",
            ["Context to flip to server-wide while any listed global event is active (fallback if PerEventContext has no match)"] = "TriggerContext",
            ["Recognized global event names: Convoy, ArmoredTrain. Listening hooks: OnConvoyStart/Stop, OnTrainEventStart/Stop, OnArmoredTrainEventStart/Stop."] = "Events",
            ["Per-event context override (v1.6). Map event name to a specific context. Falls back to TriggerContext if event not listed. Example: { \"Convoy\": \"AtConvoyEvent\", \"ArmoredTrain\": \"AtTrainEvent\" }"] = "PerEventContext",
            ["Context applied when victim is inside a tracked raid base dome"] = "TriggerContext",
            ["Override the dome radius the RaidableBases plugin reports. Set to 0 to use the value RaidableBases supplies."] = "RadiusOverrideMeters",
        };

        private bool TryMigrateLegacyConfig()
        {
            var configPath = Path.Combine(Interface.Oxide.ConfigDirectory, "PVEDamageGuard.json");
            if (!File.Exists(configPath)) return false;
            string raw;
            try { raw = File.ReadAllText(configPath); }
            catch { return false; }

            bool changed = false;
            foreach (var entry in _legacyFieldRenames)
            {
                var oldKey = "\"" + entry.Key + "\":";
                var newKey = "\"" + entry.Value + "\":";
                if (raw.IndexOf(oldKey, StringComparison.Ordinal) >= 0)
                {
                    raw = raw.Replace(oldKey, newKey);
                    changed = true;
                }
            }
            if (changed)
            {
                try
                {
                    File.WriteAllText(configPath, raw);
                    Puts("Migrated legacy long-name config field keys to v1.7.3 short names.");
                    return true;
                }
                catch (Exception e) { PrintWarning($"Config migration write failed: {e.Message}"); }
            }
            return false;
        }

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
                // First-load failure - try the legacy-name migration once, then retry
                if (TryMigrateLegacyConfig())
                {
                    try
                    {
                        base.LoadConfig();
                        _config = Config.ReadObject<Configuration>();
                        if (_config != null) { SaveConfig(); return; }
                    }
                    catch { /* fall through to default */ }
                }
                PrintWarning("Config file is corrupt, regenerating default. Old file kept as .jsonError");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void RebuildCaches()
        {
            // v2.0.1 - clean up accumulated duplicates from the v1.7.3-v2.0.0
            // ObjectCreationHandling.Auto append bug. Operates in-place; preserves order.
            bool anyDuplicatesRemoved = false;
            if (_config.EnvironmentalDamageTypes != null)
            {
                var deduped = new List<string>(_config.EnvironmentalDamageTypes.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in _config.EnvironmentalDamageTypes)
                    if (seen.Add(s)) deduped.Add(s);
                if (deduped.Count != _config.EnvironmentalDamageTypes.Count)
                {
                    Puts($"Deduped EnvironmentalDamageTypes: {_config.EnvironmentalDamageTypes.Count} -> {deduped.Count} entries (accumulated dupes from older versions).");
                    _config.EnvironmentalDamageTypes = deduped;
                    anyDuplicatesRemoved = true;
                }
            }
            if (_config.RuleMatrix?.ContextProviders?.EventTracker?.Events != null)
            {
                var lst = _config.RuleMatrix.ContextProviders.EventTracker.Events;
                var deduped = new List<string>(lst.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in lst) if (seen.Add(s)) deduped.Add(s);
                if (deduped.Count != lst.Count)
                {
                    Puts($"Deduped EventTracker.Events: {lst.Count} -> {deduped.Count} entries.");
                    _config.RuleMatrix.ContextProviders.EventTracker.Events = deduped;
                    anyDuplicatesRemoved = true;
                }
            }
            if (_config.RuleMatrix?.ContextProviders?.GlobalEventTriggers?.Events != null)
            {
                var lst = _config.RuleMatrix.ContextProviders.GlobalEventTriggers.Events;
                var deduped = new List<string>(lst.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in lst) if (seen.Add(s)) deduped.Add(s);
                if (deduped.Count != lst.Count)
                {
                    Puts($"Deduped GlobalEventTriggers.Events: {lst.Count} -> {deduped.Count} entries.");
                    _config.RuleMatrix.ContextProviders.GlobalEventTriggers.Events = deduped;
                    anyDuplicatesRemoved = true;
                }
            }
            if (anyDuplicatesRemoved)
            {
                SaveConfig();
            }

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
            RunSelfTest();
            DetectCompanions();
            LoadPlayerStats();
            timer.Every(60f, SavePlayerStats);
            timer.Every(2f, RefreshOpenCuiPanels);
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
            var id = entity.net.ID.Value;
            _activeEvents.Remove(id);
            _classifyCache.Remove(id); // v1.5 cache invalidation
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

        // ===== RaidableBases hooks (v1.4) =====
        // nivex's RaidableBases plugin fires these on dome lifecycle. Signatures have varied
        // across versions; we accept the common forms via object params and dig out Vector3+radius.

        private void OnRaidableBaseStarted(Vector3 pos, int mode)
        {
            if (!_ruleMatrixEnabled) return;
            var p = _config.RuleMatrix?.ContextProviders?.RaidableBases;
            if (p == null || !p.Enabled) return;
            float radius = p.RadiusOverrideMeters > 0f ? p.RadiusOverrideMeters : 75f; // typical RB dome radius
            ulong id = (ulong)pos.GetHashCode() ^ (ulong)mode;
            _activeDomes[id] = new TrackedDome
            {
                Id = id, Center = pos, Radius = radius, Mode = mode, SeenAt = DateTime.UtcNow,
            };
            LogReflectsLevel($"RaidableBases dome started: pos={pos}, mode={mode}, radius={radius:F0}m. Active domes: {_activeDomes.Count}");
        }

        private void OnRaidableBaseEnded(Vector3 pos, int mode)
        {
            if (!_ruleMatrixEnabled) return;
            ulong id = (ulong)pos.GetHashCode() ^ (ulong)mode;
            if (_activeDomes.Remove(id))
                LogReflectsLevel($"RaidableBases dome ended: pos={pos}, mode={mode}. Active domes: {_activeDomes.Count}");
        }

        // Some RaidableBases versions emit only OnRaidableBaseStarted with one Vector3 arg.
        private void OnRaidableBaseStarted(Vector3 pos)
        {
            OnRaidableBaseStarted(pos, 0);
        }

        private void OnRaidableBaseEnded(Vector3 pos)
        {
            OnRaidableBaseEnded(pos, 0);
        }

        // ===== Convoy hooks (v1.4) =====

        private void OnConvoyStart()
        {
            ActivateGlobalEvent("Convoy");
        }

        private void OnConvoyStop()
        {
            DeactivateGlobalEvent("Convoy");
        }

        // ===== Armored Train hooks (v1.4) =====
        // Adem's plugin has used various hook names across versions; handle the common ones.

        private void OnTrainEventStart()
        {
            ActivateGlobalEvent("ArmoredTrain");
        }

        private void OnTrainEventStop()
        {
            DeactivateGlobalEvent("ArmoredTrain");
        }

        private void OnArmoredTrainEventStart()
        {
            ActivateGlobalEvent("ArmoredTrain");
        }

        private void OnArmoredTrainEventStop()
        {
            DeactivateGlobalEvent("ArmoredTrain");
        }

        private void ActivateGlobalEvent(string name)
        {
            if (!_ruleMatrixEnabled) return;
            var p = _config.RuleMatrix?.ContextProviders?.GlobalEventTriggers;
            if (p == null || !p.Enabled || p.Events == null || !p.Events.Contains(name)) return;
            if (_activeGlobalEvents.Add(name))
                LogReflectsLevel($"Global event activated: {name}. Active global events: [{string.Join(", ", _activeGlobalEvents)}]");
        }

        private void DeactivateGlobalEvent(string name)
        {
            if (_activeGlobalEvents.Remove(name))
                LogReflectsLevel($"Global event deactivated: {name}. Active global events: [{string.Join(", ", _activeGlobalEvents)}]");
        }

        // Always log lifecycle events at Reflects-or-higher visibility so admins see them; respects file logging.
        private void LogReflectsLevel(string msg)
        {
            if (LogAt(LogLevel.Reflects)) Log(LogLevel.Reflects, msg);
            else Puts(msg); // ensure visibility even at None
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

        // v1.8 - register a custom NPC category from another plugin.
        // The matcher runs on every ClassifySubtype call until unregistered.
        // First-match-wins among registered categories; registered matchers run BEFORE built-in
        // type checks, so plugin authors can override our default classification (e.g. a Frontier
        // mod can classify a scarecrow-prefab BaseNpc as "FrontierBandit" instead of "Zombie").
        [HookMethod("API_RegisterCategory")]
        public bool API_RegisterCategory(string name, Func<BaseEntity, bool> matcher)
        {
            if (string.IsNullOrEmpty(name) || matcher == null) return false;
            _registeredCategories[name] = matcher;
            _classifyCache.Clear(); // existing cache entries may now classify differently
            Puts($"Custom NPC category registered: '{name}'");
            return true;
        }

        [HookMethod("API_UnregisterCategory")]
        public bool API_UnregisterCategory(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool removed = _registeredCategories.Remove(name);
            if (removed)
            {
                _classifyCache.Clear();
                Puts($"Custom NPC category unregistered: '{name}'");
            }
            return removed;
        }

        [HookMethod("API_ListCustomCategories")]
        public string[] API_ListCustomCategories()
        {
            return _registeredCategories.Keys.ToArray();
        }

        // v1.9 - Backpacks-friendly query. Returns true if this player died from a
        // PVEDamageGuard-enforced cause (reflect or block) within the sticky window.
        // Backpacks-on-death plugins (Backpacks by WhiteThunder, Backpacks 4 by Whispers88)
        // can query this to decide whether to drop the corpse's backpack.
        // See docs/integrations/backpacks.md for the integration recipe.
        [HookMethod("API_IsPveDeath")]
        public bool API_IsPveDeath(BasePlayer victim)
        {
            if (victim == null) return false;
            if (!_recentPveDeaths.TryGetValue(victim.userID, out var when)) return false;
            return DateTime.UtcNow - when <= PveDeathStickyWindow;
        }

        [HookMethod("API_GetPlayerStats")]
        public Dictionary<string, object> API_GetPlayerStats(BasePlayer player)
        {
            if (player == null) return null;
            if (!_playerStats.TryGetValue(player.userID, out var s)) return null;
            return new Dictionary<string, object>
            {
                ["Name"] = s.Name,
                ["DamageDealtToPlayers"] = s.DamageDealtToPlayers,
                ["DamageReflectedBack"]  = s.DamageReflectedBack,
                ["DamageTakenFromNpcs"]  = s.DamageTakenFromNpcs,
                ["DamageTakenFromPlayers"] = s.DamageTakenFromPlayers,
                ["NpcsKilled"]            = s.NpcsKilled,
                ["PvpKillsAgainstMe"]     = s.PvpKillsAgainstMe,
                ["ReflectsAgainstMe"]     = s.ReflectsAgainstMe,
                ["FirstSeen"]             = s.FirstSeen.ToString("o"),
                ["LastSeen"]              = s.LastSeen.ToString("o"),
            };
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
            // v1.5 - wrap with optional Stopwatch timing
            if (!_hookTimingEnabled) return OnEntityTakeDamageInner(entity, info);
            var sw = Stopwatch.StartNew();
            try { return OnEntityTakeDamageInner(entity, info); }
            finally { sw.Stop(); RecordHookTiming(sw.ElapsedTicks); }
        }

        private object OnEntityTakeDamageInner(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null) return null;

            if (entity is BasePlayer beingHit && _reflectInFlight.Contains(beingHit.userID))
                return null;

            var rootAttacker = ResolveRootAttacker(info);
            var attackerCat  = ClassifyEntity(rootAttacker);
            var victimCat    = ClassifyEntity(entity);
            var victimPlayer = entity as BasePlayer;

            // v2.0.1 - WeaponPrefab fallback. If creator-chain attribution didn't reach a
            // vehicle NPC but the weapon prefab is a heli rocket or Bradley shell, force
            // VehicleNpc classification. Without this, heli rocket splash damage that
            // arrives via a generic TimedExplosive (with no creator chain back to the heli)
            // would be classified as Other and leak through as vanilla damage.
            if (attackerCat != NpcCategory.VehicleNpc && LooksLikeVehicleNpcWeapon(info))
                attackerCat = NpcCategory.VehicleNpc;

            if (victimCat == NpcCategory.RealPlayer && permission.UserHasPermission(victimPlayer.UserIDString, PermBypass))
            {
                LogHit(LogLevel.All, "bypass-perm", info, entity, attackerCat, victimCat, null, "passthrough");
                return null;
            }

            // True environmental damage (fall / cold / radiation / bleed / etc.) is in
            // EnvironmentalDamageTypes and always passes through.
            if (IsEnvironmental(info))
            {
                LogHit(LogLevel.All, "env-passthrough", info, entity, attackerCat, victimCat, null, "passthrough");
                return null;
            }

            // v2.0.4 - null-Initiator non-environmental damage. Heli/Bradley crash explosions,
            // /teleport into geometry, plugin-induced damage (admin.kill, anti-cheat slap, etc.)
            // all arrive with Initiator=null Weapon=null and a non-environmental damage type
            // (Generic, Explosion, Bullet). The plugin previously treated these as passthrough
            // and applied them at full vanilla damage. On a strict-PVE server the player
            // expectation is "if PVEDamageGuard can't identify a hostile attacker, don't hurt me".
            // BlockUnattributedDamageToPlayers (default false for backward compat) opts into that.
            if (info.Initiator == null || rootAttacker == null)
            {
                if (victimCat == NpcCategory.RealPlayer && _config.BlockUnattributedDamageToPlayers)
                {
                    LogHit(LogLevel.Scaled, "unattributed-player-damage-blocked", info, entity, attackerCat, victimCat, null, "block");
                    return true;
                }
                LogHit(LogLevel.All, "unattributed-passthrough", info, entity, attackerCat, victimCat, null, "passthrough");
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

            // v2.0.3 - self-damage exclusion. The rule-matrix path (unlike the legacy
            // scaling path at HandleViaScaling line 1397) was treating self-inflicted
            // damage - shotgun pellets ricocheting off your own wall, rocket splash on
            // yourself, F1 grenade in hand - as PvP and applying the
            // `RealPlayer -> RealPlayer` reflect rule on top. With reflect:2.0 the
            // player ate 3x their own damage. Mirror the legacy guard here so self
            // hits never resolve via the PvP rule.
            if (attackerCat == NpcCategory.RealPlayer && victimCat == NpcCategory.RealPlayer
                && rootAttacker == entity)
            {
                LogHit(LogLevel.All, "self-damage-passthrough", info, entity, attackerCat, victimCat, context, "passthrough");
                return null;
            }

            var action = ResolveRule(context, attackerCat, victimCat, attackerSub, victimSub);

            // v1.7.1 - foreign-structure reflect override.
            // If the rule resolved to "allow" for Player -> Building/Deployable, and the attacker
            // is not authorized on that structure, override to reflect (or block if reflect is off).
            // Skipped when YieldToTruePVE is on; TruePVE owns the decision in that mode.
            if (_config.ReflectPlayerDamageToForeignStructures
                && !_yieldToTruePve
                && action is AllowAction
                && attackerCat == NpcCategory.RealPlayer
                && (victimCat == NpcCategory.Building || victimCat == NpcCategory.Deployable))
            {
                var attackerPlayer3 = rootAttacker as BasePlayer;
                if (!IsAttackerAuthorizedOnStructure(attackerPlayer3, entity))
                {
                    if (_config.ReflectPvpEnabled)
                        action = new ReflectAction { Multiplier = _config.ReflectMultiplier };
                    else if (_config.BlockPvpIfNotReflecting)
                        action = new BlockAction();
                    // else: leave action as AllowAction (admin explicitly disabled both)
                }
            }

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
                // v1.7.1 - Player damaging a non-player victim with a reflect action.
                // Common case: Player -> Building/Deployable on a PVE server. Self-reflect.
                if (attackerCat == NpcCategory.RealPlayer)
                {
                    var attackerSelf = rootAttacker as BasePlayer;
                    DoReflect(attackerSelf, attackerSelf, info, ra.Multiplier * todGlobal);
                    LogHit(LogLevel.Reflects, $"rule[{context}]-reflect-self", info, entity, attackerCat, victimCat, context, ra.Encode());
                    return true;
                }
                // Reflect requested for non-player attacker - block (reflect doesn't make sense for NPCs)
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
                if (attackerIsAnyNpc && victimCat == NpcCategory.RealPlayer)
                    StatsRecordNpcDamageToPlayer(victimPlayer, info.damageTypes?.Total() ?? 0f);
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

            // 2. RaidableBases dome proximity (v1.4)
            if (providers?.RaidableBases != null && providers.RaidableBases.Enabled
                && _activeDomes.Count > 0 && !string.IsNullOrEmpty(providers.RaidableBases.TriggerContext))
            {
                foreach (var dome in _activeDomes.Values)
                {
                    float r = providers.RaidableBases.RadiusOverrideMeters > 0f
                        ? providers.RaidableBases.RadiusOverrideMeters
                        : dome.Radius;
                    if ((dome.Center - pos).sqrMagnitude <= r * r)
                        return providers.RaidableBases.TriggerContext;
                }
            }

            // 3. Event tracker proximity (Bradley / Heli / Cargo entities)
            // v1.6: PerEventContext lookup takes precedence over TriggerContext
            if (providers?.EventTracker != null && providers.EventTracker.Enabled
                && _activeEvents.Count > 0)
            {
                float r = providers.EventTracker.RadiusMeters;
                float r2 = r * r;
                foreach (var ev in _activeEvents.Values)
                {
                    if ((ev.Position - pos).sqrMagnitude > r2) continue;
                    // Per-event override first
                    if (providers.EventTracker.PerEventContext != null
                        && providers.EventTracker.PerEventContext.TryGetValue(ev.EventType, out var perEvent)
                        && !string.IsNullOrEmpty(perEvent))
                        return perEvent;
                    if (!string.IsNullOrEmpty(providers.EventTracker.TriggerContext))
                        return providers.EventTracker.TriggerContext;
                }
            }

            // 4. Global event triggers (v1.4) - Convoy / ArmoredTrain server-wide flag
            // v1.6: PerEventContext lookup takes precedence over TriggerContext
            if (providers?.GlobalEventTriggers != null && providers.GlobalEventTriggers.Enabled
                && _activeGlobalEvents.Count > 0)
            {
                if (providers.GlobalEventTriggers.PerEventContext != null)
                {
                    foreach (var name in _activeGlobalEvents)
                    {
                        if (providers.GlobalEventTriggers.PerEventContext.TryGetValue(name, out var perEvent)
                            && !string.IsNullOrEmpty(perEvent))
                            return perEvent;
                    }
                }
                if (!string.IsNullOrEmpty(providers.GlobalEventTriggers.TriggerContext))
                    return providers.GlobalEventTriggers.TriggerContext;
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

            // v1.7.1 - Player -> Player-owned Structure (Building or Deployable)
            // Reflects when attacker is NOT authorized on the structure (not owner,
            // not on owner's team, not TC-authorized). Does NOT trigger for NPCs,
            // monuments (OwnerID==0), or barrels (Other category).
            if (attackerCat == NpcCategory.RealPlayer
                && (victimCat == NpcCategory.Building || victimCat == NpcCategory.Deployable)
                && _config.ReflectPlayerDamageToForeignStructures)
            {
                var attackerPlayer2 = rootAttacker as BasePlayer;
                if (IsAttackerAuthorizedOnStructure(attackerPlayer2, entity))
                {
                    LogHit(LogLevel.All, "player->own-structure", info, entity, attackerCat, victimCat, null, "allow");
                    return null;
                }
                // Unauthorized - mirror the PvP reflect/block behavior
                if (_yieldToTruePve)
                {
                    LogHit(LogLevel.All, "player->foreign-structure-yielded-truepve", info, entity, attackerCat, victimCat, null, "yield");
                    return null;
                }
                if (_config.ReflectPvpEnabled)
                {
                    DoReflect(attackerPlayer2, attackerPlayer2, info, _config.ReflectMultiplier);
                    LogHit(LogLevel.Reflects, "player->foreign-structure-reflected", info, entity, attackerCat, victimCat, null, $"reflect:{_config.ReflectMultiplier:F2}");
                    return true;
                }
                if (_config.BlockPvpIfNotReflecting)
                {
                    LogHit(LogLevel.Scaled, "player->foreign-structure-blocked", info, entity, attackerCat, victimCat, null, "block");
                    return true;
                }
                // Reflect and block both disabled - let it through (rare config)
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
                StatsRecordNpcDamageToPlayer(victimPlayer, info.damageTypes?.Total() ?? 0f);
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

            // v1.5 - cached lookup
            ulong cacheKey = 0UL;
            bool useCache = false;
            if (entity.net != null && entity.net.ID.IsValid)
            {
                cacheKey = entity.net.ID.Value;
                useCache = true;
                if (_classifyCache.TryGetValue(cacheKey, out var cached))
                    return cached.Category;
            }

            NpcCategory result;
            if (entity is BasePlayer bp)
                result = bp.IsNpc ? NpcCategory.HumanNpc : NpcCategory.RealPlayer;
            else if (entity is NPCPlayer)      result = NpcCategory.HumanNpc;
            else if (entity is BaseNpc)        result = NpcCategory.AnimalNpc;
            else if (entity is BaseHelicopter) result = NpcCategory.VehicleNpc;
            else if (entity is BradleyAPC)     result = NpcCategory.VehicleNpc;
            else if (entity is BuildingBlock)  result = NpcCategory.Building;
            else if (entity is Door)           result = NpcCategory.Building;
            else if (entity is DecayEntity && entity.OwnerID != 0UL) result = NpcCategory.Deployable;
            else if (entity is BaseCombatEntity bce && bce.OwnerID != 0UL && bce.OwnerID.IsSteamId())
                result = NpcCategory.OwnedTrap;
            else result = NpcCategory.Other;

            if (useCache) CachePut(cacheKey, result, null, hasSubtype: false);
            return result;
        }

        // v1.5 - cache helpers
        private void CachePut(ulong key, NpcCategory cat, string subtype, bool hasSubtype)
        {
            if (_classifyCache.Count >= CacheMaxEntries) _classifyCache.Clear();
            if (_classifyCache.TryGetValue(key, out var existing))
            {
                // Preserve subtype if not being updated
                if (!hasSubtype) { subtype = existing.Subtype; hasSubtype = existing.HasSubtype; }
            }
            _classifyCache[key] = new CachedClassification { Category = cat, Subtype = subtype, HasSubtype = hasSubtype };
        }

        private void CacheInvalidate(ulong key) => _classifyCache.Remove(key);

        public string ClassifySubtype(BaseEntity entity)
        {
            if (entity == null) return null;

            // v1.5 - cached lookup (only valid if we've already computed the subtype, marked by HasSubtype)
            ulong cacheKey = 0UL;
            bool useCache = false;
            if (entity.net != null && entity.net.ID.IsValid)
            {
                cacheKey = entity.net.ID.Value;
                useCache = true;
                if (_classifyCache.TryGetValue(cacheKey, out var cached) && cached.HasSubtype)
                    return cached.Subtype;
            }

            string result = ClassifySubtypeImpl(entity);
            if (useCache)
            {
                // Preserve existing category if present, else compute it
                NpcCategory cat = NpcCategory.Other;
                if (_classifyCache.TryGetValue(cacheKey, out var existing)) cat = existing.Category;
                else cat = ClassifyEntity(entity); // will populate category cache as side effect
                CachePut(cacheKey, cat, result, hasSubtype: true);
            }
            return result;
        }

        private string ClassifySubtypeImpl(BaseEntity entity)
        {
            // v1.8 - custom NPC categories registered by other plugins.
            // Run first so plugin authors can override built-in classifications.
            if (_registeredCategories.Count > 0)
            {
                foreach (var entry in _registeredCategories)
                {
                    try
                    {
                        if (entry.Value(entity)) return entry.Key;
                    }
                    catch (Exception e)
                    {
                        PrintWarning($"Custom category matcher '{entry.Key}' threw: {e.Message}");
                    }
                }
            }

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
            if (info == null) return null;
            var init = info.Initiator;
            if (init == null) return null;

            // Direct match — already a top-level attacker
            if (init is BasePlayer)     return init;
            if (init is NPCPlayer)      return init;
            if (init is BaseNpc)        return init;
            if (init is BaseHelicopter) return init;
            if (init is BradleyAPC)     return init;

            // Player-owned trap attribution (v1.4)
            if (init.OwnerID != 0UL && init.OwnerID.IsSteamId() && _config.TreatPlayerTrapsAsPvp)
            {
                var owner = BasePlayer.FindByID(init.OwnerID);
                if (owner != null) return owner;
            }

            // v2.0.1 - walk creatorEntity chain up to 4 levels deep so heli rockets
            // attribute to the heli even through projectile->explosion intermediates.
            // Previous code walked only 1 level, which missed multi-step chains.
            var cur = init;
            for (int depth = 0; depth < 4; depth++)
            {
                BaseEntity creator;
                try { creator = cur.creatorEntity; }
                catch { creator = null; }
                if (creator == null) break;
                if (creator is BasePlayer || creator is NPCPlayer || creator is BaseNpc
                    || creator is BaseHelicopter || creator is BradleyAPC)
                    return creator;
                cur = creator;
            }

            return init;
        }

        // v2.0.1 - Returns true if the HitInfo carries a known patrol-heli or Bradley
        // munition prefab. Used in the damage hook as a fallback to force VehicleNpc
        // classification when ResolveRootAttacker couldn't find the parent vehicle.
        private bool LooksLikeVehicleNpcWeapon(HitInfo info)
        {
            if (info?.WeaponPrefab == null) return false;
            var prefab = info.WeaponPrefab.ShortPrefabName ?? "";
            if (string.IsNullOrEmpty(prefab)) return false;
            // Known patrol-heli munitions: rocket_heli, rocket_heli_napalm
            // Known Bradley munitions: maincannonshell
            return prefab.Contains("rocket_heli")
                || prefab.Contains("napalm")
                || prefab.Contains("maincannonshell");
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

        #region Per-player stats (v1.7)

        private const string StatsDataFileName = "PVEDamageGuard/stats";

        private void LoadPlayerStats()
        {
            try
            {
                _playerStats = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerStats>>(StatsDataFileName)
                               ?? new Dictionary<ulong, PlayerStats>();
            }
            catch
            {
                PrintWarning("stats.json corrupt or missing, starting fresh.");
                _playerStats = new Dictionary<ulong, PlayerStats>();
            }
        }

        private void SavePlayerStats()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(StatsDataFileName, _playerStats); }
            catch (Exception e) { PrintWarning($"Failed to save stats: {e.Message}"); }
        }

        private PlayerStats GetOrCreateStats(BasePlayer player)
        {
            if (player == null) return null;
            if (!_playerStats.TryGetValue(player.userID, out var s))
            {
                s = new PlayerStats { Name = player.displayName, FirstSeen = DateTime.UtcNow };
                _playerStats[player.userID] = s;
            }
            s.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(player.displayName)) s.Name = player.displayName;
            return s;
        }

        // Track damage taken from NPC (called from HandleViaScaling and HandleViaRuleMatrix scale paths)
        private void StatsRecordNpcDamageToPlayer(BasePlayer victim, float total)
        {
            if (victim == null || total <= 0f) return;
            var s = GetOrCreateStats(victim);
            if (s != null) s.DamageTakenFromNpcs += total;
        }

        // Track PvP damage taken (only relevant when PvP is NOT reflected, e.g. AllowTeammateDamage)
        private void StatsRecordPvpDamageTaken(BasePlayer victim, BasePlayer attacker, float total)
        {
            if (victim == null || attacker == null || total <= 0f) return;
            var sv = GetOrCreateStats(victim);
            var sa = GetOrCreateStats(attacker);
            if (sv != null) sv.DamageTakenFromPlayers += total;
            if (sa != null) sa.DamageDealtToPlayers += total;
        }

        // Track PvP reflect: damage that bounced back to the attacker
        private void StatsRecordReflect(BasePlayer attacker, BasePlayer victim, float total)
        {
            if (attacker == null || victim == null || total <= 0f) return;
            var sa = GetOrCreateStats(attacker);
            if (sa != null) { sa.DamageReflectedBack += total; sa.ReflectsAgainstMe += 1; }
            // The victim gets credit for damage "dealt" via reflect
            var sv = GetOrCreateStats(victim);
            if (sv != null) sv.DamageDealtToPlayers += total;
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var rootAttacker = ResolveRootAttacker(info) as BasePlayer;
            if (rootAttacker == null || rootAttacker.IsNpc) return;
            var sa = GetOrCreateStats(rootAttacker);
            if (sa == null) return;

            // Player killed an NPC
            if (entity is BaseNpc || (entity is BasePlayer eb && eb.IsNpc) || entity is NPCPlayer
                || entity is BaseHelicopter || entity is BradleyAPC)
            {
                sa.NpcsKilled += 1;
                return;
            }
            // Player killed another real player (PvP kill)
            if (entity is BasePlayer victim && !victim.IsNpc && victim != rootAttacker)
            {
                var sv = GetOrCreateStats(victim);
                if (sv != null) sv.PvpKillsAgainstMe += 1;
            }
        }

        #endregion

        #region Performance and diagnostics (v1.5)

        // Self-test verifies that the Rust types our classifier depends on resolve correctly at runtime.
        // If a Facepunch update removes or renames a type, the plugin would throw on the first hit
        // involving that branch; the self-test surfaces it at load time as a clear error instead.
        private void RunSelfTest()
        {
            var checks = new List<(string name, bool ok, string detail)>();

            // Type existence checks
            checks.Add(("typeof(BasePlayer)",     typeof(BasePlayer)     != null, "real players + humanoid NPCs (IsNpc)"));
            checks.Add(("typeof(BaseNpc)",        typeof(BaseNpc)        != null, "animals, zombies, scarecrows"));
            checks.Add(("typeof(NPCPlayer)",      typeof(NPCPlayer)      != null, "legacy NPC humanoids"));
            checks.Add(("typeof(BaseHelicopter)", typeof(BaseHelicopter) != null, "patrol helicopter"));
            checks.Add(("typeof(BradleyAPC)",     typeof(BradleyAPC)     != null, "Bradley APC"));
            checks.Add(("typeof(BuildingBlock)",  typeof(BuildingBlock)  != null, "building grade victims"));
            checks.Add(("typeof(Door)",           typeof(Door)           != null, "doors"));
            checks.Add(("typeof(DecayEntity)",    typeof(DecayEntity)    != null, "deployables"));
            checks.Add(("typeof(LootContainer)",  typeof(LootContainer)  != null, "barrels"));

            // DamageType enum sanity
            checks.Add(("DamageType.LAST present",
                Enum.IsDefined(typeof(DamageType), DamageType.LAST), "enum sentinel intact"));
            checks.Add(("_allDamageTypes populated",
                _allDamageTypes != null && _allDamageTypes.Length > 1, $"cached {_allDamageTypes?.Length ?? 0} types"));

            // TOD source - non-fatal if missing (TOD_Sky may not be ready yet at load time)
            bool tod = false;
            try { tod = TOD_Sky.Instance != null; } catch { tod = false; }
            checks.Add(("TOD_Sky.Instance",       tod, tod ? "ready" : "not ready (will be checked again on demand)"));

            var failures = checks.Where(c => !c.ok).ToList();
            _selfTestPassed = failures.Count == 0
                              || (failures.Count == 1 && failures[0].name == "TOD_Sky.Instance"); // only TOD failed = soft fail

            if (_selfTestPassed)
            {
                _selfTestSummary = $"Self-test: {checks.Count - failures.Count}/{checks.Count} checks passed.";
                Puts(_selfTestSummary);
            }
            else
            {
                _selfTestSummary = $"Self-test FAILED: {failures.Count}/{checks.Count} checks did not pass.";
                PrintError(_selfTestSummary);
                foreach (var f in failures)
                    PrintError($"  - {f.name} (used for: {f.detail})");
            }
        }

        // Hook timing - records elapsed ticks of OnEntityTakeDamage into a rolling buffer when enabled.
        private void RecordHookTiming(long elapsedTicks)
        {
            long us = elapsedTicks * 1000000L / Stopwatch.Frequency;
            _hookTimingsUs[_hookTimingIdx] = us;
            _hookTimingIdx = (_hookTimingIdx + 1) % TimingBufferSize;
            _hookTimingCount++;
        }

        private (long mean, long p95, long max, long sampleCount) ComputeTimingStats()
        {
            long count = Math.Min(_hookTimingCount, TimingBufferSize);
            if (count == 0) return (0, 0, 0, 0);

            var snapshot = new long[count];
            Array.Copy(_hookTimingsUs, snapshot, count);
            Array.Sort(snapshot);

            long sum = 0;
            for (int i = 0; i < count; i++) sum += snapshot[i];
            long mean = sum / count;
            long p95 = snapshot[(int)Math.Min(count - 1, (long)(count * 0.95))];
            long max = snapshot[count - 1];
            return (mean, p95, max, count);
        }

        #endregion

        #region CUI panel (v1.6)

        private static readonly string[] _tabs = { "status", "logging", "history", "rules", "scaling" };

        private void ShowPanel(BasePlayer player, string tab = "status")
        {
            if (player == null || !player.IsConnected) return;
            if (string.IsNullOrEmpty(tab) || Array.IndexOf(_tabs, tab.ToLowerInvariant()) < 0) tab = "status";

            // Destroy any existing panel for this player first to keep it idempotent
            CuiHelper.DestroyUi(player, CuiMainPanel);

            var container = new CuiElementContainer();

            // Main backdrop
            container.Add(new CuiPanel
            {
                Image = { Color = CuiBgColor },
                RectTransform = { AnchorMin = "0.18 0.18", AnchorMax = "0.82 0.85" },
                CursorEnabled = true,
            }, "Overlay", CuiMainPanel);

            // Title bar
            container.Add(new CuiPanel
            {
                Image = { Color = CuiTabBarColor },
                RectTransform = { AnchorMin = "0 0.93", AnchorMax = "1 1" },
            }, CuiMainPanel, CuiMainPanel + ".title");

            container.Add(new CuiLabel
            {
                Text = { Text = $"PVE Damage Guard v{Version}",
                         FontSize = 16, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.7 1" },
            }, CuiMainPanel + ".title");

            // Close button
            container.Add(new CuiButton
            {
                Button = { Color = CuiButtonColor, Command = "pdgui.close" },
                RectTransform = { AnchorMin = "0.94 0.15", AnchorMax = "0.99 0.85" },
                Text = { Text = "X", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiMainPanel + ".title");

            // Tab strip (left column)
            container.Add(new CuiPanel
            {
                Image = { Color = CuiTabBarColor },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0.18 0.93" },
            }, CuiMainPanel, CuiMainPanel + ".tabs");

            BuildTabButton(container, "status",  "Status",  0.92f, 1.00f, tab);
            BuildTabButton(container, "logging", "Logging", 0.83f, 0.91f, tab);
            BuildTabButton(container, "history", "History", 0.74f, 0.82f, tab);
            BuildTabButton(container, "rules",   "Rules",   0.65f, 0.73f, tab);
            BuildTabButton(container, "scaling", "Scaling", 0.56f, 0.64f, tab);

            // Footer in tab strip with version + soft hint
            container.Add(new CuiLabel
            {
                Text = { Text = "/pdg help for CLI", FontSize = 10,
                         Align = TextAnchor.LowerCenter, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = "0 0.01", AnchorMax = "1 0.06" },
            }, CuiMainPanel + ".tabs");

            // Content area
            container.Add(new CuiPanel
            {
                Image = { Color = CuiContentColor },
                RectTransform = { AnchorMin = "0.18 0", AnchorMax = "1 0.93" },
            }, CuiMainPanel, CuiContentPanel);

            // Render the active tab's content
            switch (tab.ToLowerInvariant())
            {
                case "status":  BuildStatusTabContent(container); break;
                case "logging": BuildLoggingTabContent(container, player); break;
                case "history": BuildHistoryTabContent(container, player); break;
                case "rules":   BuildRulesTabContent(container, player); break;
                case "scaling": BuildScalingTabContent(container, player); break;
            }

            CuiHelper.AddUi(player, container);
            _openPanels[player.userID] = tab;
        }

        private void BuildTabButton(CuiElementContainer container, string id, string label,
                                    float yMin, float yMax, string activeTab)
        {
            bool active = string.Equals(id, activeTab, StringComparison.OrdinalIgnoreCase);
            container.Add(new CuiButton
            {
                Button = { Color = active ? CuiAccentDim : CuiButtonColor,
                           Command = $"pdgui.tab {id}" },
                RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.95 {yMax}" },
                Text = { Text = label, FontSize = 13,
                         Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
            }, CuiMainPanel + ".tabs");
        }

        private void BuildStatusTabContent(CuiElementContainer container)
        {
            float defaultNpc = 1f;
            _config.NpcToPlayerScaling.TryGetValue("Default", out defaultNpc);

            var lines = new List<string>
            {
                $"<color=#ee7d5a>Reflect</color>: {_config.ReflectPvpEnabled} ({_config.ReflectMultiplier:F2}x)   <color=#ee7d5a>Block PvP w/o reflect</color>: {_config.BlockPvpIfNotReflecting}   <color=#ee7d5a>Teammates</color>: {_config.AllowTeammateDamage}",
                $"<color=#ee7d5a>NPC -> Player default</color>: {defaultNpc:F2}x   <color=#ee7d5a>NPC -> Structure default</color>: {_config.NpcToStructureScaling:F2}x   <color=#ee7d5a>Traps as PvP</color>: {_config.TreatPlayerTrapsAsPvp}",
                $"<color=#ee7d5a>Logging</color>: {_config.Logging} (file={_config.LogToFile})   <color=#ee7d5a>Yield to TruePVE</color>: {_yieldToTruePve}   <color=#ee7d5a>Discord webhook</color>: {_config.DiscordWebhook.Enabled}",
                "",
                $"<color=#a0a0a0>Features:</color> TOD={_todEnabled}   VictimSubtype={_victimScalingEnabled}   BuildingGrade={_buildingGradeEnabled}   PerAttackerStruct={_perAttackerStructureEnabled}   RuleMatrix={_ruleMatrixEnabled}",
                $"<color=#a0a0a0>Current hour:</color> {GetCurrentHour()} ({_config.TimeOfDaySource})   <color=#a0a0a0>Events tracked:</color> {_activeEvents.Count} entity / {_activeDomes.Count} dome / {_activeGlobalEvents.Count} global   <color=#a0a0a0>Config issues:</color> {_configIssues.Count}",
                "",
                "<color=#888888>Tip: this tab is read-only. Use /pdg commands or edit oxide/config/PVEDamageGuard.json then /pdg reload.</color>",
            };

            float lineHeight = 1f / 14f;
            float y = 0.92f;
            for (int i = 0; i < lines.Count; i++)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = lines[i], FontSize = 13,
                             Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                    RectTransform = { AnchorMin = $"0.02 {y - lineHeight}", AnchorMax = $"0.98 {y}" },
                }, CuiContentPanel);
                y -= lineHeight;
            }
        }

        private void BuildPlaceholderTabContent(CuiElementContainer container, string title, string message)
        {
            container.Add(new CuiLabel
            {
                Text = { Text = title, FontSize = 20, Align = TextAnchor.MiddleCenter, Color = CuiAccentColor },
                RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 0.85" },
            }, CuiContentPanel);

            container.Add(new CuiLabel
            {
                Text = { Text = message, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = "0.05 0.5", AnchorMax = "0.95 0.65" },
            }, CuiContentPanel);
        }

        // v1.7 - Logging tab: color-coded recent log lines with level filter row
        private void BuildLoggingTabContent(CuiElementContainer container, BasePlayer player)
        {
            LogLevel filter = LogLevel.Reflects;
            if (_uiLogFilter.TryGetValue(player.userID, out var saved)) filter = saved;

            // Header: title + level filter row
            container.Add(new CuiLabel
            {
                Text = { Text = $"Live Log (filter: {filter}+) - last {_recentLogLines.Count}", FontSize = 14,
                         Align = TextAnchor.MiddleLeft, Color = CuiAccentColor },
                RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.7 0.99" },
            }, CuiContentPanel);

            // Level filter buttons (right side of header)
            BuildLogFilterButton(container, "None",     LogLevel.None,     filter, 0.70f, 0.755f);
            BuildLogFilterButton(container, "Reflects", LogLevel.Reflects, filter, 0.76f, 0.815f);
            BuildLogFilterButton(container, "Scaled",   LogLevel.Scaled,   filter, 0.82f, 0.875f);
            BuildLogFilterButton(container, "All",      LogLevel.All,      filter, 0.88f, 0.935f);
            BuildLogFilterButton(container, "Trace",    LogLevel.Trace,    filter, 0.94f, 0.995f);

            // Log lines
            var snapshot = _recentLogLines.ToArray();
            // Filter
            var visible = new List<LogLine>();
            for (int i = snapshot.Length - 1; i >= 0; i--)
                if (snapshot[i].Level >= filter) visible.Add(snapshot[i]);

            int maxRows = 20;
            int rows = Math.Min(maxRows, visible.Count);
            float topY = 0.88f;
            float rowH = (topY - 0.04f) / maxRows;
            for (int i = 0; i < rows; i++)
            {
                var line = visible[i];
                var color = LogLevelColor(line.Level);
                container.Add(new CuiLabel
                {
                    Text = { Text = $"<color={color}>[{line.At:HH:mm:ss}] [{line.Level}]</color> {Escape(line.Message)}",
                             FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                    RectTransform = {
                        AnchorMin = $"0.02 {topY - rowH * (i + 1)}",
                        AnchorMax = $"0.98 {topY - rowH * i}"
                    },
                }, CuiContentPanel);
            }

            if (visible.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = $"<color=#888888>No log lines at or above {filter}. Lower the filter or trigger some damage events.</color>",
                             FontSize = 12, Align = TextAnchor.MiddleCenter, Color = CuiMutedTextColor },
                    RectTransform = { AnchorMin = "0.02 0.4", AnchorMax = "0.98 0.6" },
                }, CuiContentPanel);
            }
        }

        private void BuildLogFilterButton(CuiElementContainer container, string label, LogLevel level,
                                          LogLevel currentFilter, float xMin, float xMax)
        {
            bool active = level == currentFilter;
            container.Add(new CuiButton
            {
                Button = { Color = active ? CuiAccentDim : CuiButtonColor,
                           Command = $"pdgui.logfilter {level}" },
                RectTransform = { AnchorMin = $"{xMin} 0.92", AnchorMax = $"{xMax} 0.99" },
                Text = { Text = label, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);
        }

        private string LogLevelColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Reflects: return "#ee7d5a"; // coral
                case LogLevel.Scaled:   return "#6bb5ee"; // cyan
                case LogLevel.All:      return "#ffffff"; // white
                case LogLevel.Trace:    return "#888888"; // gray
                default:                return "#aaaaaa";
            }
        }

        // CUI text can have angle-bracket-like content; escape what could break our tag wrapping.
        private static string Escape(string s) => s == null ? "" : s.Replace("<", "&lt;").Replace(">", "&gt;");

        // v1.7 - History tab: paginated rows + prev/next buttons
        private void BuildHistoryTabContent(CuiElementContainer container, BasePlayer player)
        {
            var entries = _history.ToArray();
            int page = 0;
            _uiHistoryPage.TryGetValue(player.userID, out page);
            int totalPages = Math.Max(1, (entries.Length + HistoryRowsPerPage - 1) / HistoryRowsPerPage);
            if (page >= totalPages) page = totalPages - 1;
            if (page < 0) page = 0;

            // Header
            container.Add(new CuiLabel
            {
                Text = { Text = $"History - page {page + 1} of {totalPages} ({entries.Length} hits in ring buffer)",
                         FontSize = 14, Align = TextAnchor.MiddleLeft, Color = CuiAccentColor },
                RectTransform = { AnchorMin = "0.02 0.92", AnchorMax = "0.7 0.99" },
            }, CuiContentPanel);

            // Prev / Next buttons
            container.Add(new CuiButton
            {
                Button = { Color = page > 0 ? CuiButtonColor : "0.15 0.15 0.18 0.5",
                           Command = page > 0 ? "pdgui.histpage prev" : "" },
                RectTransform = { AnchorMin = "0.78 0.92", AnchorMax = "0.86 0.99" },
                Text = { Text = "< Prev", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);
            container.Add(new CuiButton
            {
                Button = { Color = page < totalPages - 1 ? CuiButtonColor : "0.15 0.15 0.18 0.5",
                           Command = page < totalPages - 1 ? "pdgui.histpage next" : "" },
                RectTransform = { AnchorMin = "0.87 0.92", AnchorMax = "0.95 0.99" },
                Text = { Text = "Next >", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);

            // Column header
            container.Add(new CuiLabel
            {
                Text = { Text = "<color=#888888>Time     Tag                       Attacker -> Victim                       Dmg    Action          Ctx</color>",
                         FontSize = 10, Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = "0.02 0.86", AnchorMax = "0.98 0.91" },
            }, CuiContentPanel);

            // Rows (newest first, paginated)
            // Reverse so newest is at top of the buffer
            int start = entries.Length - 1 - page * HistoryRowsPerPage;
            int end = Math.Max(-1, start - HistoryRowsPerPage);
            float topY = 0.84f;
            float rowH = (topY - 0.04f) / HistoryRowsPerPage;
            int rowIdx = 0;
            for (int i = start; i > end && i >= 0; i--, rowIdx++)
            {
                var h = entries[i];
                var ctxPart = h.Context != null ? h.Context : "-";
                var line = string.Format("{0:HH:mm:ss} {1,-25} {2,-14}({3,-10}) -> {4,-12}({5,-10}) {6,5:F1}  {7,-15} {8}",
                    h.At, Truncate(h.Tag, 25),
                    Truncate(h.AttackerCat, 14), Truncate(h.AttackerName, 10),
                    Truncate(h.VictimCat, 12), Truncate(h.VictimName, 10),
                    h.Damage, Truncate(h.Action, 15), Truncate(ctxPart, 12));
                container.Add(new CuiLabel
                {
                    Text = { Text = Escape(line), FontSize = 10,
                             Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                    RectTransform = {
                        AnchorMin = $"0.02 {topY - rowH * (rowIdx + 1)}",
                        AnchorMax = $"0.98 {topY - rowH * rowIdx}"
                    },
                }, CuiContentPanel);
            }

            if (entries.Length == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "<color=#888888>No history yet. Trigger some damage events to populate the ring buffer.</color>",
                             FontSize = 12, Align = TextAnchor.MiddleCenter, Color = CuiMutedTextColor },
                    RectTransform = { AnchorMin = "0.02 0.4", AnchorMax = "0.98 0.6" },
                }, CuiContentPanel);
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + ".";
        }

        // v1.8 - Rules tab: read-only browser for the rule matrix
        private void BuildRulesTabContent(CuiElementContainer container, BasePlayer player)
        {
            if (!_ruleMatrixEnabled || _config.RuleMatrix?.Contexts == null || _config.RuleMatrix.Contexts.Count == 0)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Rules Browser", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = CuiAccentColor },
                    RectTransform = { AnchorMin = "0 0.7", AnchorMax = "1 0.85" },
                }, CuiContentPanel);
                container.Add(new CuiLabel
                {
                    Text = { Text = "<color=#888888>Rule matrix is disabled. Set RuleMatrix.Enabled=true in config and reload to use this tab.</color>",
                             FontSize = 13, Align = TextAnchor.MiddleCenter, Color = CuiMutedTextColor },
                    RectTransform = { AnchorMin = "0.1 0.45", AnchorMax = "0.9 0.65" },
                }, CuiContentPanel);
                return;
            }

            // Resolve active context (where the admin is standing) and currently-selected viewing context
            string activeContext;
            try { activeContext = ResolveContext(player.transform != null ? player.transform.position : Vector3.zero, player); }
            catch { activeContext = _config.RuleMatrix.DefaultContext; }

            string selected;
            if (!_uiRulesContext.TryGetValue(player.userID, out selected)
                || string.IsNullOrEmpty(selected)
                || !_config.RuleMatrix.Contexts.ContainsKey(selected))
            {
                selected = activeContext ?? _config.RuleMatrix.DefaultContext;
                if (selected == null || !_config.RuleMatrix.Contexts.ContainsKey(selected))
                    selected = _config.RuleMatrix.Contexts.Keys.First();
                _uiRulesContext[player.userID] = selected;
            }

            bool editMode = _uiRulesEditMode.Contains(player.userID);

            // Header line 1: active context at player position + edit-mode toggle button
            container.Add(new CuiLabel
            {
                Text = { Text = $"<color=#ee7d5a>Active at your position:</color> {activeContext ?? "<none>"}",
                         FontSize = 13, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = "0.02 0.95", AnchorMax = "0.78 0.99" },
            }, CuiContentPanel);
            container.Add(new CuiButton
            {
                Button = { Color = editMode ? CuiAccentColor : CuiButtonColor, Command = "pdgui.rulesedit" },
                RectTransform = { AnchorMin = "0.80 0.95", AnchorMax = "0.98 0.99" },
                Text = { Text = editMode ? "Edit mode: ON" : "Edit mode: OFF", FontSize = 11,
                         Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);

            // Header line 2: inheritance chain for selected context
            var chain = ComputeInheritsChain(selected);
            container.Add(new CuiLabel
            {
                Text = { Text = $"<color=#a0a0a0>Viewing:</color> {string.Join(" → ", chain)}",
                         FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = "0.02 0.91", AnchorMax = "0.98 0.94" },
            }, CuiContentPanel);

            // Left column: clickable list of contexts
            float ctxYTop = 0.88f;
            float ctxRowH = 0.045f;
            int ctxIdx = 0;
            foreach (var ctxName in _config.RuleMatrix.Contexts.Keys)
            {
                bool isSelected = string.Equals(ctxName, selected, StringComparison.Ordinal);
                bool isActive = string.Equals(ctxName, activeContext, StringComparison.Ordinal);
                var label = isActive ? $"★ {ctxName}" : ctxName;
                container.Add(new CuiButton
                {
                    Button = { Color = isSelected ? CuiAccentDim : CuiButtonColor,
                               Command = $"pdgui.rulesctx {ctxName}" },
                    RectTransform = { AnchorMin = $"0.02 {ctxYTop - ctxRowH * (ctxIdx + 1)}",
                                      AnchorMax = $"0.24 {ctxYTop - ctxRowH * ctxIdx}" },
                    Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                }, CuiContentPanel);
                ctxIdx++;
            }

            // Right column: rules in selected context, with inherited rules below
            if (_config.RuleMatrix.Contexts.TryGetValue(selected, out var ctxCfg) && ctxCfg != null)
            {
                var directRules = ctxCfg.Rules ?? new Dictionary<string, string>();
                var inheritedRules = CollectInheritedRules(selected);
                // Remove overridden inherited rules
                var inheritedOnly = new Dictionary<string, string>();
                foreach (var kv in inheritedRules)
                    if (!directRules.ContainsKey(kv.Key))
                        inheritedOnly[kv.Key] = kv.Value;

                float ruleYTop = 0.88f;
                float ruleRowH = 0.035f;
                int ri = 0;
                if (directRules.Count > 0)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = $"<color=#ee7d5a>Direct rules in '{selected}':</color>", FontSize = 12,
                                 Align = TextAnchor.MiddleLeft, Color = CuiAccentColor },
                        RectTransform = { AnchorMin = $"0.26 {ruleYTop - ruleRowH * (ri + 1)}",
                                          AnchorMax = $"0.98 {ruleYTop - ruleRowH * ri}" },
                    }, CuiContentPanel);
                    ri++;
                    foreach (var kv in directRules)
                    {
                        AddRuleLineToCui(container, kv.Key, kv.Value, ruleYTop, ruleRowH, ri++, false, editMode, selected);
                    }
                }

                if (inheritedOnly.Count > 0)
                {
                    ri++; // gap
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "<color=#888888>Inherited rules:</color>", FontSize = 12,
                                 Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                        RectTransform = { AnchorMin = $"0.26 {ruleYTop - ruleRowH * (ri + 1)}",
                                          AnchorMax = $"0.98 {ruleYTop - ruleRowH * ri}" },
                    }, CuiContentPanel);
                    ri++;
                    foreach (var kv in inheritedOnly)
                    {
                        AddRuleLineToCui(container, kv.Key, kv.Value, ruleYTop, ruleRowH, ri++, true, false, selected);
                    }
                }

                if (directRules.Count == 0 && inheritedOnly.Count == 0)
                {
                    container.Add(new CuiLabel
                    {
                        Text = { Text = "<color=#888888>This context has no rules.</color>", FontSize = 12,
                                 Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                        RectTransform = { AnchorMin = $"0.26 0.84", AnchorMax = $"0.98 0.88" },
                    }, CuiContentPanel);
                }
            }
        }

        private void AddRuleLineToCui(CuiElementContainer container, string ruleKey, string actionStr,
                                       float topY, float rowH, int idx, bool inherited, bool editMode, string contextName)
        {
            var color = GetActionColor(actionStr);
            var keyText = inherited ? $"<color=#888888>{Escape(ruleKey)}</color>" : Escape(ruleKey);
            var line = $"{keyText}   <color={color}>{Escape(actionStr)}</color>";
            // Right edge for label depends on whether we're showing edit buttons
            float labelRight = editMode ? 0.80f : 0.98f;
            container.Add(new CuiLabel
            {
                Text = { Text = line, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = $"0.26 {topY - rowH * (idx + 1)}",
                                  AnchorMax = $"{labelRight} {topY - rowH * idx}" },
            }, CuiContentPanel);
            if (editMode)
            {
                // Cycle action button
                container.Add(new CuiButton
                {
                    Button = { Color = CuiButtonColor, Command = $"pdgui.ruleaction {contextName} {ruleKey}" },
                    RectTransform = { AnchorMin = $"0.81 {topY - rowH * (idx + 1)}",
                                      AnchorMax = $"0.89 {topY - rowH * idx}" },
                    Text = { Text = "cycle", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
                }, CuiContentPanel);
                // Delete button
                container.Add(new CuiButton
                {
                    Button = { Color = "0.5 0.2 0.2 1.0", Command = $"pdgui.ruledel {contextName} {ruleKey}" },
                    RectTransform = { AnchorMin = $"0.90 {topY - rowH * (idx + 1)}",
                                      AnchorMax = $"0.98 {topY - rowH * idx}" },
                    Text = { Text = "del", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
                }, CuiContentPanel);
            }
        }

        private string GetActionColor(string actionStr)
        {
            if (string.IsNullOrWhiteSpace(actionStr)) return "#aaaaaa";
            var lower = actionStr.Trim().ToLowerInvariant();
            if (lower == "allow") return "#5acd5a";          // green
            if (lower == "block") return "#cd5a5a";          // red
            if (lower.StartsWith("reflect")) return "#ee7d5a"; // coral
            if (lower.StartsWith("scale")) return "#6bb5ee";   // cyan
            return "#aaaaaa";
        }

        private List<string> ComputeInheritsChain(string contextName)
        {
            var chain = new List<string>();
            if (_config.RuleMatrix?.Contexts == null) return chain;
            var visited = new HashSet<string>();
            var name = contextName;
            while (!string.IsNullOrEmpty(name) && visited.Add(name))
            {
                chain.Add(name);
                if (!_config.RuleMatrix.Contexts.TryGetValue(name, out var ctx) || ctx == null) break;
                name = ctx.Inherits;
            }
            return chain;
        }

        private Dictionary<string, string> CollectInheritedRules(string contextName)
        {
            var result = new Dictionary<string, string>();
            if (_config.RuleMatrix?.Contexts == null) return result;
            var visited = new HashSet<string>();
            var name = contextName;
            bool first = true;
            while (!string.IsNullOrEmpty(name) && visited.Add(name))
            {
                if (!_config.RuleMatrix.Contexts.TryGetValue(name, out var ctx) || ctx == null) break;
                if (!first && ctx.Rules != null)
                {
                    foreach (var kv in ctx.Rules)
                        if (!result.ContainsKey(kv.Key)) result[kv.Key] = kv.Value;
                }
                first = false;
                name = ctx.Inherits;
            }
            return result;
        }

        // v1.9 - Scaling tab: live-edit controls for the most-commonly-tuned values.
        // Per-damage-type and per-grade fine-tuning stays in JSON config + /pdg scale command;
        // this tab covers the booleans, the headline multipliers, the log level, and TOD source.
        private void BuildScalingTabContent(CuiElementContainer container, BasePlayer player)
        {
            float defaultNpc = 1f;
            _config.NpcToPlayerScaling.TryGetValue("Default", out defaultNpc);

            // Header
            container.Add(new CuiLabel
            {
                Text = { Text = "<color=#ee7d5a>Live scaling editor</color> - changes save and reload immediately",
                         FontSize = 13, Align = TextAnchor.MiddleLeft, Color = CuiAccentColor },
                RectTransform = { AnchorMin = "0.02 0.94", AnchorMax = "0.98 0.99" },
            }, CuiContentPanel);

            // Multiplier rows
            float y = 0.88f;
            float rowH = 0.06f;
            BuildScaleRow(container, "NPC->Player default", "NpcToPlayer.Default", defaultNpc, y);
            BuildScaleRow(container, "NPC->Structure default", "NpcToStructure", _config.NpcToStructureScaling, y - rowH);
            BuildScaleRow(container, "PvP reflect multiplier", "ReflectMultiplier", _config.ReflectMultiplier, y - rowH * 2);

            // Toggles
            float ty = y - rowH * 3 - 0.02f;
            container.Add(new CuiLabel
            {
                Text = { Text = "<color=#a0a0a0>Toggles:</color>", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = $"0.02 {ty}", AnchorMax = $"0.98 {ty + 0.04f}" },
            }, CuiContentPanel);
            ty -= 0.05f;
            BuildToggleRow(container, "Reflect PvP",                _config.ReflectPvpEnabled,                  "ReflectPvpEnabled",                  ty);
            ty -= 0.045f;
            BuildToggleRow(container, "Block PvP w/o reflect",       _config.BlockPvpIfNotReflecting,            "BlockPvpIfNotReflecting",            ty);
            ty -= 0.045f;
            BuildToggleRow(container, "Allow teammate damage",       _config.AllowTeammateDamage,                "AllowTeammateDamage",                ty);
            ty -= 0.045f;
            BuildToggleRow(container, "Traps as PvP",                _config.TreatPlayerTrapsAsPvp,              "TreatPlayerTrapsAsPvp",              ty);
            ty -= 0.045f;
            BuildToggleRow(container, "Reflect on foreign structures", _config.ReflectPlayerDamageToForeignStructures, "ReflectPlayerDamageToForeignStructures", ty);
            ty -= 0.045f;
            BuildToggleRow(container, "Yield to TruePVE",            _config.YieldToTruePVE,                     "YieldToTruePVE",                     ty);

            // Log level dropdown
            ty -= 0.06f;
            BuildEnumRow(container, "Log level", _config.Logging.ToString(),
                new[] { "None", "Reflects", "Scaled", "All", "Trace" }, "Logging", ty);

            // TOD source dropdown
            ty -= 0.05f;
            BuildEnumRow(container, "TOD source", _config.TimeOfDaySource,
                new[] { "Game", "Real" }, "TimeOfDaySource", ty);

            // Footer hint
            container.Add(new CuiLabel
            {
                Text = { Text = "<color=#666666>Per-damage-type tuning: /pdg scale <Type> <mult>. Building grade and TOD arrays: edit JSON + /pdg reload.</color>",
                         FontSize = 10, Align = TextAnchor.MiddleLeft, Color = CuiMutedTextColor },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.98 0.06" },
            }, CuiContentPanel);
        }

        private void BuildScaleRow(CuiElementContainer container, string label, string field, float value, float y)
        {
            float h = 0.05f;
            container.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = $"0.02 {y - h}", AnchorMax = $"0.40 {y}" },
            }, CuiContentPanel);
            // [-0.10] [-0.01] [value] [+0.01] [+0.10]
            BuildScaleBtn(container, "-0.10", $"pdgui.scalemod {field} -0.10", 0.42f, y, h);
            BuildScaleBtn(container, "-0.01", $"pdgui.scalemod {field} -0.01", 0.50f, y, h);
            container.Add(new CuiLabel
            {
                Text = { Text = $"<color=#ee7d5a>{value:F2}</color>", FontSize = 13,
                         Align = TextAnchor.MiddleCenter, Color = CuiAccentColor },
                RectTransform = { AnchorMin = $"0.58 {y - h}", AnchorMax = $"0.72 {y}" },
            }, CuiContentPanel);
            BuildScaleBtn(container, "+0.01", $"pdgui.scalemod {field} 0.01", 0.74f, y, h);
            BuildScaleBtn(container, "+0.10", $"pdgui.scalemod {field} 0.10", 0.82f, y, h);
        }

        private void BuildScaleBtn(CuiElementContainer container, string label, string command, float x, float y, float h)
        {
            container.Add(new CuiButton
            {
                Button = { Color = CuiButtonColor, Command = command },
                RectTransform = { AnchorMin = $"{x} {y - h}", AnchorMax = $"{x + 0.07f} {y}" },
                Text = { Text = label, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);
        }

        private void BuildToggleRow(CuiElementContainer container, string label, bool value, string field, float y)
        {
            float h = 0.04f;
            container.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = $"0.02 {y - h}", AnchorMax = $"0.55 {y}" },
            }, CuiContentPanel);
            container.Add(new CuiButton
            {
                Button = { Color = value ? CuiAccentDim : CuiButtonColor, Command = $"pdgui.toggle {field}" },
                RectTransform = { AnchorMin = $"0.58 {y - h}", AnchorMax = $"0.78 {y}" },
                Text = { Text = value ? "ON" : "OFF", FontSize = 11,
                         Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
            }, CuiContentPanel);
        }

        private void BuildEnumRow(CuiElementContainer container, string label, string current,
                                   string[] options, string field, float y)
        {
            float h = 0.04f;
            container.Add(new CuiLabel
            {
                Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = CuiTextColor },
                RectTransform = { AnchorMin = $"0.02 {y - h}", AnchorMax = $"0.30 {y}" },
            }, CuiContentPanel);
            // Layout each option as a small button
            float x = 0.32f;
            float btnW = 0.13f;
            for (int i = 0; i < options.Length; i++)
            {
                bool active = options[i] == current;
                container.Add(new CuiButton
                {
                    Button = { Color = active ? CuiAccentDim : CuiButtonColor,
                               Command = $"pdgui.dropdown {field} {options[i]}" },
                    RectTransform = { AnchorMin = $"{x} {y - h}", AnchorMax = $"{x + btnW - 0.005f} {y}" },
                    Text = { Text = options[i], FontSize = 10,
                             Align = TextAnchor.MiddleCenter, Color = CuiTextColor },
                }, CuiContentPanel);
                x += btnW;
            }
        }

        private void HidePanel(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, CuiMainPanel);
            _openPanels.Remove(player.userID);
        }

        private void HideAllPanels()
        {
            foreach (var pair in _openPanels.ToList())
            {
                var p = BasePlayer.FindByID(pair.Key);
                if (p != null) CuiHelper.DestroyUi(p, CuiMainPanel);
            }
            _openPanels.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _openPanels.Remove(player.userID);
        }

        private void Unload()
        {
            HideAllPanels();
            SavePlayerStats();
            // Timers auto-destroyed by Oxide on plugin unload.
        }

        private void RefreshOpenCuiPanels()
        {
            if (_openPanels.Count == 0) return;
            foreach (var kv in _openPanels.ToList())
            {
                // Only Logging and History tabs need live updates; static tabs don't churn the UI.
                if (kv.Value != "logging" && kv.Value != "history") continue;
                var p = BasePlayer.FindByID(kv.Key);
                if (p == null || !p.IsConnected) { _openPanels.Remove(kv.Key); continue; }
                ShowPanel(p, kv.Value); // re-render
            }
        }

        [ConsoleCommand("pdgui.tab")]
        private void CcmdPdguiTab(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            var tab = arg.Args != null && arg.Args.Length > 0 ? arg.Args[0] : "status";
            ShowPanel(p, tab);
        }

        [ConsoleCommand("pdgui.close")]
        private void CcmdPdguiClose(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            HidePanel(p);
        }

        // v1.7 - log filter button handler from Logging tab
        [ConsoleCommand("pdgui.logfilter")]
        private void CcmdPdguiLogFilter(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length == 0) return;
            if (!Enum.TryParse<LogLevel>(arg.Args[0], true, out var level)) return;
            _uiLogFilter[p.userID] = level;
            ShowPanel(p, "logging");
        }

        // v1.9 - modify a multiplier from the Scaling tab
        [ConsoleCommand("pdgui.scalemod")]
        private void CcmdPdguiScaleMod(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var field = arg.Args[0];
            if (!float.TryParse(arg.Args[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var delta)) return;

            // Apply the delta to the named field; clamp to [0, 100]
            switch (field)
            {
                case "ReflectMultiplier":
                    _config.ReflectMultiplier = ClampMult(_config.ReflectMultiplier + delta); break;
                case "NpcToStructure":
                    _config.NpcToStructureScaling = ClampMult(_config.NpcToStructureScaling + delta); break;
                case "NpcToPlayer.Default":
                    if (!_config.NpcToPlayerScaling.TryGetValue("Default", out var cur)) cur = 1f;
                    _config.NpcToPlayerScaling["Default"] = ClampMult(cur + delta); break;
                default:
                    PrintWarning($"pdgui.scalemod: unknown field '{field}'"); return;
            }
            SaveConfig();
            RebuildCaches();
            ShowPanel(p, "scaling");
        }

        private static float ClampMult(float v)
        {
            if (v < 0f) return 0f;
            if (v > 100f) return 100f;
            return (float)Math.Round(v, 2);
        }

        // v1.9 - toggle a boolean from the Scaling tab
        [ConsoleCommand("pdgui.toggle")]
        private void CcmdPdguiToggle(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            switch (arg.Args[0])
            {
                case "ReflectPvpEnabled":                       _config.ReflectPvpEnabled = !_config.ReflectPvpEnabled; break;
                case "BlockPvpIfNotReflecting":                 _config.BlockPvpIfNotReflecting = !_config.BlockPvpIfNotReflecting; break;
                case "AllowTeammateDamage":                     _config.AllowTeammateDamage = !_config.AllowTeammateDamage; break;
                case "TreatPlayerTrapsAsPvp":                   _config.TreatPlayerTrapsAsPvp = !_config.TreatPlayerTrapsAsPvp; break;
                case "ReflectPlayerDamageToForeignStructures":  _config.ReflectPlayerDamageToForeignStructures = !_config.ReflectPlayerDamageToForeignStructures; break;
                case "YieldToTruePVE":
                    _config.YieldToTruePVE = !_config.YieldToTruePVE;
                    DetectCompanions(); // recompute _yieldToTruePve
                    break;
                default: PrintWarning($"pdgui.toggle: unknown field '{arg.Args[0]}'"); return;
            }
            SaveConfig();
            RebuildCaches();
            ShowPanel(p, "scaling");
        }

        // v1.9 - set an enum/string value from the Scaling tab
        [ConsoleCommand("pdgui.dropdown")]
        private void CcmdPdguiDropdown(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var field = arg.Args[0];
            var value = arg.Args[1];
            switch (field)
            {
                case "Logging":
                    if (Enum.TryParse<LogLevel>(value, true, out var lvl)) _config.Logging = lvl;
                    else return;
                    break;
                case "TimeOfDaySource":
                    if (value == "Game" || value == "Real") _config.TimeOfDaySource = value;
                    else return;
                    break;
                default: PrintWarning($"pdgui.dropdown: unknown field '{field}'"); return;
            }
            SaveConfig();
            RebuildCaches();
            ShowPanel(p, "scaling");
        }

        // v1.9 - toggle Rules tab edit mode
        [ConsoleCommand("pdgui.rulesedit")]
        private void CcmdPdguiRulesEdit(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (_uiRulesEditMode.Contains(p.userID)) _uiRulesEditMode.Remove(p.userID);
            else _uiRulesEditMode.Add(p.userID);
            ShowPanel(p, "rules");
        }

        // v1.9 - cycle the action on a single rule (allow -> block -> reflect:1.0 -> scale:0.5 -> allow)
        [ConsoleCommand("pdgui.ruleaction")]
        private void CcmdPdguiRuleAction(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var contextName = arg.Args[0];
            // The rule key may contain spaces; we passed it URL-encoded as args[1..]
            var ruleKey = string.Join(" ", arg.Args, 1, arg.Args.Length - 1);
            if (_config.RuleMatrix == null || !_config.RuleMatrix.Contexts.TryGetValue(contextName, out var ctx) || ctx.Rules == null) return;
            if (!ctx.Rules.TryGetValue(ruleKey, out var current)) return;
            // Cycle
            string next;
            var cur = current.ToLowerInvariant();
            if (cur == "allow") next = "block";
            else if (cur == "block") next = "reflect:1.0";
            else if (cur.StartsWith("reflect")) next = "scale:0.5";
            else if (cur.StartsWith("scale")) next = "allow";
            else next = "allow";
            ctx.Rules[ruleKey] = next;
            SaveConfig();
            RebuildCaches();
            ShowPanel(p, "rules");
        }

        // v1.9 - delete a rule from a context
        [ConsoleCommand("pdgui.ruledel")]
        private void CcmdPdguiRuleDel(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            var contextName = arg.Args[0];
            var ruleKey = string.Join(" ", arg.Args, 1, arg.Args.Length - 1);
            if (_config.RuleMatrix == null || !_config.RuleMatrix.Contexts.TryGetValue(contextName, out var ctx) || ctx.Rules == null) return;
            if (ctx.Rules.Remove(ruleKey))
            {
                SaveConfig();
                RebuildCaches();
            }
            ShowPanel(p, "rules");
        }

        // v1.8 - select a context in the Rules tab
        [ConsoleCommand("pdgui.rulesctx")]
        private void CcmdPdguiRulesCtx(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length == 0) return;
            var name = arg.Args[0];
            if (_config.RuleMatrix?.Contexts != null && _config.RuleMatrix.Contexts.ContainsKey(name))
                _uiRulesContext[p.userID] = name;
            ShowPanel(p, "rules");
        }

        // v1.7 - history page navigation from History tab
        [ConsoleCommand("pdgui.histpage")]
        private void CcmdPdguiHistPage(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return;
            if (!HasUiPerm(p)) return;
            if (arg.Args == null || arg.Args.Length == 0) return;
            int page;
            _uiHistoryPage.TryGetValue(p.userID, out page);
            switch (arg.Args[0].ToLowerInvariant())
            {
                case "next": page += 1; break;
                case "prev": page -= 1; break;
                case "first": page = 0; break;
            }
            if (page < 0) page = 0;
            _uiHistoryPage[p.userID] = page;
            ShowPanel(p, "history");
        }

        private bool HasUiPerm(BasePlayer p)
        {
            return p != null && (p.IsAdmin || permission.UserHasPermission(p.UserIDString, PermAdmin));
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

                case "pvelockdown":
                    // v2.0.2 - strict PVE: zero damage from any NPC/heli/Bradley to structures,
                    // own-structure damage allowed, foreign-structure damage reflected back.
                    // Use this when the v2.0 defaults (scale:0.5 on VehicleNpc -> Building)
                    // still let helis chip your walls.
                    _config.ReflectPvpEnabled = true;
                    _config.ReflectMultiplier = 1.0f;
                    _config.BlockPvpIfNotReflecting = true;
                    _config.AllowTeammateDamage = false;
                    _config.ReflectPlayerDamageToForeignStructures = true;
                    _config.BlockUnattributedDamageToPlayers = true; // v2.0.4 - block heli/Bradley crash, teleport-into-geometry, plugin-induced null-Initiator damage
                    _config.NpcToPlayerScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0.25f, ["Bullet"] = 0.1f, ["Slash"] = 0.25f, ["Stab"] = 0.25f,
                        ["Bite"] = 0.25f, ["Blunt"] = 0.25f, ["Explosion"] = 0.25f, ["Arrow"] = 0.25f,
                        ["Generic"] = 1.0f
                    };
                    _config.NpcToStructureScaling = 0f;
                    _config.PerAttackerStructureScaling = new Dictionary<string, float>
                    {
                        ["Default"] = 0f,
                        ["PatrolHelicopter"] = 0f,
                        ["BradleyAPC"] = 0f
                    };
                    if (_config.RuleMatrix == null) _config.RuleMatrix = new RuleMatrixConfig();
                    _config.RuleMatrix.Enabled = true;
                    _config.RuleMatrix.DefaultContext = "Default";
                    if (!_config.RuleMatrix.Contexts.ContainsKey("Default"))
                        _config.RuleMatrix.Contexts["Default"] = new ContextConfig
                        {
                            Description = "Lockdown PVE - NPCs/helis cannot damage structures",
                            Rules = new Dictionary<string, string>()
                        };
                    var dr = _config.RuleMatrix.Contexts["Default"].Rules;
                    dr["RealPlayer -> RealPlayer"]   = "block";
                    dr["RealPlayer -> Building"]     = "allow"; // foreign-structure reflect handles non-owners
                    dr["RealPlayer -> Deployable"]   = "allow";
                    dr["HumanNpc -> RealPlayer"]     = "scale:{Bullet:0.25,Default:0.5}";
                    dr["AnimalNpc -> RealPlayer"]    = "scale:{Bite:0.5,Default:0.5}";
                    dr["VehicleNpc -> RealPlayer"]   = "scale:{Bullet:0.25,Explosion:0.5}";
                    dr["HumanNpc -> Building"]       = "block";
                    dr["HumanNpc -> Deployable"]     = "block";
                    dr["AnimalNpc -> Building"]      = "block";
                    dr["AnimalNpc -> Deployable"]    = "block";
                    dr["VehicleNpc -> Building"]     = "block";
                    dr["VehicleNpc -> Deployable"]   = "block";
                    dr["OwnedTrap -> RealPlayer"]    = "reflect:1.0";
                    dr["Environment -> *"]           = "allow";
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

        private static readonly string[] _presetNames = { "pvepure", "pvereflect", "pvevehicleraids", "pvphoursevents", "pvelockdown" };

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
                case "events":   return Lang("HelpEvents");
                case "webhook":  return Lang("HelpWebhook");
                case "timing":   return Lang("HelpTiming");
                case "selftest": return Lang("HelpSelfTest");
                case "cache":    return Lang("HelpCache");
                case "ui":       return Lang("HelpUi");
                case "close":    return Lang("HelpClose");
                case "stats":    return Lang("HelpStats");
                case "rules":    return Lang("HelpRules");
                case "categories": return Lang("HelpCategories");
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

        // v1.7.1 - true if the attacker is allowed to damage this structure
        // (owns it, is on the owner's team, or is TC-authorized on the building).
        // Used to decide whether to reflect Player->Building/Deployable damage.
        //
        // v2.0.2 - rewritten to emit Trace-level diagnostics so admins can see
        // exactly which path matched (owner / team / TC / fallback), and to walk
        // the BuildingBlock's full Building aggregate when the immediate
        // GetBuildingPrivilege returns nothing. Also handles the modern
        // PlayerNameID-shaped authorizedPlayers list directly (no longer relying
        // on a try/catch to detect the shape - some Rust builds let the wrong-type
        // comparison silently fail-to-match instead of throwing).
        private bool IsAttackerAuthorizedOnStructure(BasePlayer attacker, BaseCombatEntity victim)
        {
            if (attacker == null || victim == null)
            {
                TraceAuth("attacker or victim null", attacker, victim, false);
                return false;
            }

            // Unowned structures (monuments, world spawns) are never reflected on damage.
            // OwnerID == 0 means no player owns this entity; treat as authorized so we
            // don't trip the reflect on monument crates / barrels / etc.
            ulong owner = victim.OwnerID;
            if (owner == 0UL) { TraceAuth("victim.OwnerID == 0 (unowned)", attacker, victim, true); return true; }

            // Direct ownership
            if (owner == attacker.userID)
            {
                TraceAuth($"owner == attacker.userID ({owner})", attacker, victim, true);
                return true;
            }

            // Team ownership
            if (attacker.currentTeam != 0UL)
            {
                try
                {
                    var team = RelationshipManager.ServerInstance?.FindTeam(attacker.currentTeam);
                    if (team != null && team.members != null && team.members.Contains(owner))
                    {
                        TraceAuth($"team {attacker.currentTeam} contains owner {owner}", attacker, victim, true);
                        return true;
                    }
                }
                catch { /* tolerate API drift */ }
            }

            // TC authorization. v2.0.2: handle BuildingBlock by walking the building's
            // full TC set, not just the single privilege returned by GetBuildingPrivilege.
            // GetBuildingPrivilege can return null when the block sits at the edge of a
            // building or between TCs; the player may still be authorized on a connected
            // TC. Iterating buildingPrivileges of the parent Building covers that.
            if (victim is BuildingBlock bb)
            {
                if (IsAttackerOnAnyBuildingPrivilege(attacker, bb))
                {
                    TraceAuth("TC auth matched (one of building's TCs)", attacker, victim, true);
                    return true;
                }
            }

            // Last-resort: Deployable victims sometimes share OwnerID with a teammate
            // not in RelationshipManager (e.g., owner left team but kept perms). Skip.
            TraceAuth($"no auth path matched (owner={owner}, attacker.userID={attacker.userID}, team={attacker.currentTeam})",
                attacker, victim, false);
            return false;
        }

        // v2.0.2 - log the auth decision when LogLevel >= Trace. Cheap when off.
        private void TraceAuth(string reason, BasePlayer attacker, BaseCombatEntity victim, bool authorized)
        {
            if (_config == null || _config.Logging < LogLevel.Trace) return;
            string aId = attacker?.userID.ToString() ?? "<null>";
            string vId = victim?.OwnerID.ToString() ?? "<null>";
            string vName = victim?.ShortPrefabName ?? "<null>";
            Puts($"[Trace] auth-check: attacker={aId} victim={vName} ownerID={vId} -> {(authorized ? "AUTHORIZED" : "DENIED")} ({reason})");
        }

        // v2.0.2 - walk every BuildingPrivlidge attached to the BuildingBlock's parent
        // Building. Returns true if any TC's authorizedPlayers list contains the
        // attacker's userID. Handles both List<ulong> and List<PlayerNameID> shapes
        // (current and prior Rust builds) via explicit type-check, not exception-driven.
        private bool IsAttackerOnAnyBuildingPrivilege(BasePlayer attacker, BuildingBlock bb)
        {
            if (attacker == null || bb == null) return false;

            // 1) The block's directly-resolved privilege (cheapest path)
            if (IsAttackerOnPrivilege(attacker, bb.GetBuildingPrivilege())) return true;

            // 2) Walk the parent Building's full TC set, if reachable. The reflection
            //    isolates us from Building-API renames; we want any field/property
            //    on the Building that yields an IEnumerable of BuildingPrivlidge.
            //    v2.0.4 - reflection lookups are cached per-Type. Without this, every
            //    Player -> Building hit spent ~10-50ms on reflection; on a busy server
            //    the OnEntityTakeDamage hook was timing out at 456ms.
            try
            {
                var building = bb.GetBuilding();
                if (building == null) return false;

                var list = GetBuildingPrivilegeList(building);
                if (list == null) return false;

                foreach (var entry in list)
                {
                    if (entry is BuildingPrivlidge bp && IsAttackerOnPrivilege(attacker, bp)) return true;
                }
            }
            catch { /* Building API drift - fall through to false */ }

            return false;
        }

        // v2.0.4 - per-Type cache of the Building.buildingPrivileges (or .privileges)
        // accessor. Lookup happens once per Building Type instead of once per hit.
        // Stores a Func that returns the IEnumerable or null. Marker for "no match".
        private static readonly Dictionary<Type, Func<object, System.Collections.IEnumerable>> _buildingPrivAccessor
            = new Dictionary<Type, Func<object, System.Collections.IEnumerable>>();
        private static readonly Func<object, System.Collections.IEnumerable> _noBuildingPrivAccessor = _ => null;

        private System.Collections.IEnumerable GetBuildingPrivilegeList(object building)
        {
            if (building == null) return null;
            var bt = building.GetType();
            if (!_buildingPrivAccessor.TryGetValue(bt, out var accessor))
            {
                accessor = _noBuildingPrivAccessor;
                foreach (var memberName in new[] { "buildingPrivileges", "privileges" })
                {
                    var f = bt.GetField(memberName);
                    if (f != null) { accessor = obj => f.GetValue(obj) as System.Collections.IEnumerable; break; }
                    var p = bt.GetProperty(memberName);
                    if (p != null) { accessor = obj => p.GetValue(obj) as System.Collections.IEnumerable; break; }
                }
                _buildingPrivAccessor[bt] = accessor;
            }
            return accessor(building);
        }

        // v2.0.2 - explicit auth-list scan that handles both List<ulong> and
        // List<PlayerNameID> via type-check on each element. Quiet-fails to false
        // on unknown element shapes instead of letting a silent miscompare claim
        // the player isn't authorized.
        // v2.0.4 - reflection lookups for the authorizedPlayers field/property and
        // for each element's userid field/property are cached per-Type.
        private static readonly Dictionary<Type, Func<object, System.Collections.IEnumerable>> _authListAccessor
            = new Dictionary<Type, Func<object, System.Collections.IEnumerable>>();
        private static readonly Dictionary<Type, Func<object, ulong>> _authElementUserId
            = new Dictionary<Type, Func<object, ulong>>();
        private static readonly Func<object, System.Collections.IEnumerable> _noAuthListAccessor = _ => null;
        private static readonly Func<object, ulong> _noAuthElementUserId = _ => 0UL;

        private static System.Collections.IEnumerable GetAuthorizedPlayersList(BuildingPrivlidge priv)
        {
            if (priv == null) return null;
            var t = priv.GetType();
            if (!_authListAccessor.TryGetValue(t, out var accessor))
            {
                accessor = _noAuthListAccessor;
                var f = t.GetField("authorizedPlayers");
                if (f != null) accessor = obj => f.GetValue(obj) as System.Collections.IEnumerable;
                else
                {
                    var p = t.GetProperty("authorizedPlayers");
                    if (p != null) accessor = obj => p.GetValue(obj) as System.Collections.IEnumerable;
                }
                _authListAccessor[t] = accessor;
            }
            return accessor(priv);
        }

        private static ulong GetAuthElementUserId(object item)
        {
            if (item == null) return 0UL;
            if (item is ulong ul) return ul;
            var t = item.GetType();
            if (!_authElementUserId.TryGetValue(t, out var accessor))
            {
                accessor = _noAuthElementUserId;
                var f = t.GetField("userid") ?? t.GetField("userId") ?? t.GetField("UserId") ?? t.GetField("UserID");
                if (f != null) accessor = obj => f.GetValue(obj) is ulong uid ? uid : 0UL;
                else
                {
                    var p = t.GetProperty("userid") ?? t.GetProperty("userId") ?? t.GetProperty("UserId") ?? t.GetProperty("UserID");
                    if (p != null) accessor = obj => p.GetValue(obj) is ulong uid ? uid : 0UL;
                }
                _authElementUserId[t] = accessor;
            }
            return accessor(item);
        }

        private bool IsAttackerOnPrivilege(BasePlayer attacker, BuildingPrivlidge priv)
        {
            if (priv == null || attacker == null) return false;
            ulong needle = attacker.userID;
            try
            {
                var seq = GetAuthorizedPlayersList(priv);
                if (seq == null) return false;
                foreach (var item in seq)
                {
                    var id = GetAuthElementUserId(item);
                    if (id != 0UL && id == needle) return true;
                }
            }
            catch { /* unsupported shape - return false */ }
            return false;
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
            // v1.9 - if the reflect would kill the attacker, note it for Backpacks integration.
            // Sticky for PveDeathStickyWindow so the death hook (and any external query) sees it.
            if (attacker.health - total <= 0f)
                _recentPveDeaths[attacker.userID] = DateTime.UtcNow;
            if (!_reflectInFlight.Add(attacker.userID)) return;
            try { attacker.Hurt(total, major, victim, true); }
            finally { _reflectInFlight.Remove(attacker.userID); }
            LogReflect(victim, attacker, total, major);
            StatsRecordReflect(attacker, victim, total);
        }

        #endregion

        #region Logging + history

        private bool LogAt(LogLevel min) => _config.Logging >= min;

        private void Log(LogLevel level, string msg)
        {
            // v1.7 - always push into the CUI ring buffer (regardless of console log level)
            // so the Logging tab has live data even when the console is silent.
            _recentLogLines.Enqueue(new LogLine { At = DateTime.Now, Level = level, Message = msg });
            while (_recentLogLines.Count > LogLineCapacity) _recentLogLines.Dequeue();

            if (!LogAt(level)) return;
            Puts(msg);
            if (_config.LogToFile)
                LogToFile("damage", $"[{DateTime.Now:HH:mm:ss}] {msg}", this);
            SendDiscordWebhook(level, msg);
        }

        // v1.4 - Discord webhook with token-bucket rate limiting.
        private void SendDiscordWebhook(LogLevel level, string msg)
        {
            var dw = _config.DiscordWebhook;
            if (dw == null || !dw.Enabled) return;
            if (string.IsNullOrWhiteSpace(dw.Url)) return;
            if (level < dw.MinLevel) return;
            if (string.IsNullOrEmpty(msg)) return;

            // Sliding 1-minute window rate limit
            var now = DateTime.UtcNow;
            while (_webhookSendTimes.Count > 0 && (now - _webhookSendTimes.Peek()).TotalSeconds >= 60.0)
                _webhookSendTimes.Dequeue();
            if (_webhookSendTimes.Count >= dw.RateLimitPerMinute) return;
            _webhookSendTimes.Enqueue(now);

            var content = (dw.MessagePrefix ?? string.Empty) + msg;
            if (content.Length > 1900) content = content.Substring(0, 1900) + "...";

            var payload = new Dictionary<string, object> { ["content"] = content };
            if (!string.IsNullOrWhiteSpace(dw.Username))  payload["username"] = dw.Username;
            if (!string.IsNullOrWhiteSpace(dw.AvatarUrl)) payload["avatar_url"] = dw.AvatarUrl;

            string body;
            try { body = JsonConvert.SerializeObject(payload); }
            catch { return; }

            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            try
            {
                webrequest.Enqueue(dw.Url, body, (code, response) =>
                {
                    if (code >= 400)
                        PrintWarning($"Discord webhook returned HTTP {code}: {response}");
                }, this, Oxide.Core.Libraries.RequestMethod.POST, headers);
            }
            catch (Exception e)
            {
                PrintWarning($"Discord webhook send failed: {e.Message}");
            }
        }

        private void LogHit(LogLevel level, string tag, HitInfo info, BaseCombatEntity entity,
                            NpcCategory attackerCat, NpcCategory victimCat, string context, string action)
        {
            // Always push to history (regardless of console log level) - it's a separate diagnostic
            var attackerName = info.Initiator?.ShortPrefabName ?? "<none>";
            var victimName   = entity.ShortPrefabName ?? "<none>";
            // v2.0.4 - if the victim is a real player, include their displayName so admins
            // can correlate damage events to specific players without having to cross-
            // reference Steam IDs.
            if (entity is BasePlayer vbp && !vbp.IsNpc)
                victimName = $"{victimName}/{vbp.displayName}";
            // Same for the attacker side.
            if (info.Initiator is BasePlayer abp && !abp.IsNpc)
                attackerName = $"{attackerName}/{abp.displayName}";
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
                case "events":  CmdEvents(player); break;
                case "webhook": CmdWebhook(player, args); break;
                case "timing":  CmdTiming(player, args); break;
                case "selftest": CmdSelfTest(player); break;
                case "cache":   CmdCache(player, args); break;
                case "ui":      CmdUi(player); break;
                case "close":   CmdUiClose(player); break;
                case "stats":   CmdStats(player, args); break;
                case "rules":   CmdRules(player, args); break;
                case "categories": CmdCategories(player); break;
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
                _activeEvents.Count, _configIssues.Count,
                _activeDomes.Count, _activeGlobalEvents.Count,
                _config.DiscordWebhook.Enabled));
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

        private void CmdEvents(IPlayer player)
        {
            var lines = new List<string>();
            lines.Add(string.Format(Lang("EventsHeader", player.Id),
                _activeEvents.Count, _activeDomes.Count, _activeGlobalEvents.Count));

            if (_activeEvents.Count > 0)
            {
                lines.Add(Lang("EventsEntitiesHeader", player.Id));
                foreach (var ev in _activeEvents.Values)
                    lines.Add($"  - {ev.EventType} at ({ev.Position.x:F0}, {ev.Position.z:F0}), seen {(int)(DateTime.UtcNow - ev.SeenAt).TotalSeconds}s ago");
            }
            if (_activeDomes.Count > 0)
            {
                lines.Add(Lang("EventsDomesHeader", player.Id));
                foreach (var dome in _activeDomes.Values)
                    lines.Add($"  - mode={dome.Mode} at ({dome.Center.x:F0}, {dome.Center.z:F0}), radius={dome.Radius:F0}m, started {(int)(DateTime.UtcNow - dome.SeenAt).TotalSeconds}s ago");
            }
            if (_activeGlobalEvents.Count > 0)
            {
                lines.Add(Lang("EventsGlobalHeader", player.Id));
                foreach (var name in _activeGlobalEvents)
                    lines.Add($"  - {name}");
            }
            if (_activeEvents.Count == 0 && _activeDomes.Count == 0 && _activeGlobalEvents.Count == 0)
                lines.Add(Lang("EventsNone", player.Id));

            player.Reply(string.Join("\n", lines));
        }

        private void CmdTiming(IPlayer player, string[] args)
        {
            if (args.Length < 2)
            {
                var s = ComputeTimingStats();
                player.Reply(string.Format(Lang("TimingStatus", player.Id),
                    _hookTimingEnabled, s.sampleCount, s.mean, s.p95, s.max));
                return;
            }
            switch (args[1].ToLowerInvariant())
            {
                case "on":
                    _hookTimingEnabled = true;
                    player.Reply(Lang("TimingEnabled", player.Id));
                    break;
                case "off":
                    _hookTimingEnabled = false;
                    player.Reply(Lang("TimingDisabled", player.Id));
                    break;
                case "clear":
                    Array.Clear(_hookTimingsUs, 0, _hookTimingsUs.Length);
                    _hookTimingIdx = 0;
                    _hookTimingCount = 0;
                    player.Reply(Lang("TimingCleared", player.Id));
                    break;
                default:
                    player.Reply(Lang("TimingUsage", player.Id));
                    break;
            }
        }

        private void CmdSelfTest(IPlayer player)
        {
            RunSelfTest();
            player.Reply(_selfTestSummary + (_selfTestPassed ? " (passed)" : " (FAILED - see console)"));
        }

        private void CmdUi(IPlayer player)
        {
            var bp = player.Object as BasePlayer;
            if (bp == null) { player.Reply(Lang("UiOnlyInGame", player.Id)); return; }
            ShowPanel(bp, "status");
        }

        private void CmdUiClose(IPlayer player)
        {
            var bp = player.Object as BasePlayer;
            if (bp == null) return;
            HidePanel(bp);
        }

        // v1.8 - list rules from a context (or all contexts) in chat
        private void CmdRules(IPlayer player, string[] args)
        {
            if (!_ruleMatrixEnabled || _config.RuleMatrix?.Contexts == null)
            {
                player.Reply(Lang("RulesMatrixDisabled", player.Id));
                return;
            }
            string ctxName = args.Length >= 2 ? args[1] : null;
            if (string.IsNullOrEmpty(ctxName))
            {
                // List all contexts
                var names = _config.RuleMatrix.Contexts.Keys.ToArray();
                player.Reply(string.Format(Lang("RulesContextList", player.Id),
                    string.Join(", ", names),
                    _config.RuleMatrix.DefaultContext));
                return;
            }
            if (!_config.RuleMatrix.Contexts.TryGetValue(ctxName, out var ctx))
            {
                player.Reply(string.Format(Lang("RulesContextNotFound", player.Id), ctxName));
                return;
            }
            var lines = new List<string>();
            lines.Add(string.Format(Lang("RulesContextHeader", player.Id),
                ctxName, string.Join(" -> ", ComputeInheritsChain(ctxName))));
            if (ctx.Rules != null && ctx.Rules.Count > 0)
            {
                lines.Add(Lang("RulesDirectHeader", player.Id));
                foreach (var kv in ctx.Rules)
                    lines.Add($"  {kv.Key}  ->  {kv.Value}");
            }
            var inherited = CollectInheritedRules(ctxName);
            if (ctx.Rules != null)
                foreach (var key in ctx.Rules.Keys) inherited.Remove(key);
            if (inherited.Count > 0)
            {
                lines.Add(Lang("RulesInheritedHeader", player.Id));
                foreach (var kv in inherited)
                    lines.Add($"  {kv.Key}  ->  {kv.Value}");
            }
            player.Reply(string.Join("\n", lines));
        }

        // v1.8 - list custom NPC categories registered by other plugins
        private void CmdCategories(IPlayer player)
        {
            if (_registeredCategories.Count == 0)
            {
                player.Reply(Lang("CategoriesNone", player.Id));
                return;
            }
            player.Reply(string.Format(Lang("CategoriesList", player.Id),
                _registeredCategories.Count,
                string.Join(", ", _registeredCategories.Keys)));
        }

        private void CmdStats(IPlayer player, string[] args)
        {
            // /pdg stats              -> show requesting player's stats
            // /pdg stats <SteamId>    -> show that player's stats (admin only)
            BasePlayer target = player.Object as BasePlayer;
            if (args.Length >= 2)
            {
                if (!player.IsAdmin && !player.HasPermission(PermAdmin))
                { player.Reply(Lang("NoPermission", player.Id)); return; }
                if (ulong.TryParse(args[1], out var sid))
                    target = BasePlayer.FindByID(sid) ?? BasePlayer.FindSleeping(sid);
                else
                    target = BasePlayer.Find(args[1]);
                if (target == null) { player.Reply(string.Format(Lang("StatsNotFound", player.Id), args[1])); return; }
            }
            if (target == null) { player.Reply(Lang("StatsOnlyInGame", player.Id)); return; }
            if (!_playerStats.TryGetValue(target.userID, out var s))
            { player.Reply(string.Format(Lang("StatsNone", player.Id), target.displayName)); return; }
            player.Reply(string.Format(Lang("StatsReport", player.Id),
                s.Name ?? target.displayName,
                s.DamageDealtToPlayers, s.DamageTakenFromPlayers, s.DamageTakenFromNpcs,
                s.DamageReflectedBack, s.ReflectsAgainstMe,
                s.NpcsKilled, s.PvpKillsAgainstMe));
        }

        private void CmdCache(IPlayer player, string[] args)
        {
            if (args.Length >= 2 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                int before = _classifyCache.Count;
                _classifyCache.Clear();
                player.Reply(string.Format(Lang("CacheCleared", player.Id), before));
                return;
            }
            player.Reply(string.Format(Lang("CacheStatus", player.Id),
                _classifyCache.Count, CacheMaxEntries));
        }

        private void CmdWebhook(IPlayer player, string[] args)
        {
            // /pdg webhook [test|on|off|status]
            if (args.Length < 2)
            {
                player.Reply(string.Format(Lang("WebhookStatus", player.Id),
                    _config.DiscordWebhook.Enabled,
                    string.IsNullOrEmpty(_config.DiscordWebhook.Url) ? "<unset>" : "<set>",
                    _config.DiscordWebhook.MinLevel,
                    _config.DiscordWebhook.RateLimitPerMinute,
                    _webhookSendTimes.Count));
                return;
            }
            switch (args[1].ToLowerInvariant())
            {
                case "test":
                    if (!_config.DiscordWebhook.Enabled || string.IsNullOrWhiteSpace(_config.DiscordWebhook.Url))
                    {
                        player.Reply(Lang("WebhookNotConfigured", player.Id));
                        return;
                    }
                    SendDiscordWebhook(LogLevel.Reflects, "[test] PVEDamageGuard webhook test from " + player.Name);
                    player.Reply(Lang("WebhookTestSent", player.Id));
                    break;
                case "on":
                    _config.DiscordWebhook.Enabled = true;
                    SaveConfig();
                    player.Reply(Lang("WebhookEnabled", player.Id));
                    break;
                case "off":
                    _config.DiscordWebhook.Enabled = false;
                    SaveConfig();
                    player.Reply(Lang("WebhookDisabled", player.Id));
                    break;
                default:
                    player.Reply(Lang("WebhookUsage", player.Id));
                    break;
            }
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
                // v1.7.1 - show foreign-structure-reflect status
                if (_config.ReflectPlayerDamageToForeignStructures && ent is BaseCombatEntity bce1)
                {
                    bool authed = IsAttackerAuthorizedOnStructure(bp, bce1);
                    lines.Add(string.Format(Lang("TestStructureAuth", player.Id),
                        authed ? "authorized (vanilla damage)" : "NOT authorized (reflects damage back to you)",
                        bce1.OwnerID == 0UL ? "<unowned>" : bce1.OwnerID.ToString()));
                }
            }
            else if (cat == NpcCategory.Deployable)
            {
                lines.Add(string.Format(Lang("TestRuleStructure", player.Id), _config.NpcToStructureScaling));
                if (_config.ReflectPlayerDamageToForeignStructures && ent is BaseCombatEntity bce2)
                {
                    bool authed = IsAttackerAuthorizedOnStructure(bp, bce2);
                    lines.Add(string.Format(Lang("TestStructureAuth", player.Id),
                        authed ? "authorized (vanilla damage)" : "NOT authorized (reflects damage back to you)",
                        bce2.OwnerID == 0UL ? "<unowned>" : bce2.OwnerID.ToString()));
                }
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
                ["UsageRoot"]       = "Usage: /pdg [reload | log | logfile | scale | hour | context | history | test | preset | import | validate | events | webhook | timing | selftest | cache | ui | close | help]",
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
                ["EventsHeader"]    = "Active context-affecting events: {0} entity, {1} dome, {2} global.",
                ["EventsEntitiesHeader"] = "Tracked entity events (Bradley/Heli/Cargo):",
                ["EventsDomesHeader"]    = "Tracked RaidableBases domes:",
                ["EventsGlobalHeader"]   = "Active global events (Convoy/ArmoredTrain):",
                ["EventsNone"]      = "No context-affecting events are currently active. Default context applies everywhere.",
                ["WebhookStatus"]   = "Discord webhook: Enabled={0}, URL={1}, MinLevel={2}, RateLimit={3}/min, Sent-in-last-minute={4}. Usage: /pdg webhook [test|on|off]",
                ["WebhookNotConfigured"] = "Discord webhook is not configured. Set DiscordWebhook.Url in config and DiscordWebhook.Enabled=true, then /pdg reload.",
                ["WebhookTestSent"] = "Webhook test message queued. Check your Discord channel.",
                ["WebhookEnabled"]  = "Discord webhook enabled. Make sure DiscordWebhook.Url is set in config.",
                ["WebhookDisabled"] = "Discord webhook disabled.",
                ["WebhookUsage"]    = "Usage: /pdg webhook [test|on|off]. No arg shows status.",
                ["HelpEvents"]      = "/pdg events - list all currently-tracked events: entity events, RaidableBases domes, and global events (Convoy/ArmoredTrain).",
                ["HelpWebhook"]     = "/pdg webhook - show Discord webhook status. /pdg webhook test - send a test message. /pdg webhook on|off - toggle without reloading.",
                ["TimingStatus"]    = "Hook timing: Enabled={0}, samples={1}, mean={2}us, p95={3}us, max={4}us. Usage: /pdg timing [on|off|clear]",
                ["TimingEnabled"]   = "Hook timing enabled. Use /pdg timing to view stats.",
                ["TimingDisabled"]  = "Hook timing disabled.",
                ["TimingCleared"]   = "Hook timing buffer cleared.",
                ["TimingUsage"]     = "Usage: /pdg timing [on|off|clear]. No arg shows current stats.",
                ["CacheStatus"]     = "Classification cache: {0} / {1} entries. Cleared automatically on entity destroy. Use /pdg cache clear to flush.",
                ["CacheCleared"]    = "Classification cache cleared. {0} entries flushed.",
                ["HelpTiming"]      = "/pdg timing - show hook-timing stats (mean/p95/max in microseconds over the last 1000 hits). /pdg timing on|off - toggle recording. /pdg timing clear - reset the buffer.",
                ["HelpSelfTest"]    = "/pdg selftest - re-run the type-resolution self-test. Reports whether all Rust types PVEDamageGuard depends on resolve correctly under the current server build.",
                ["HelpCache"]       = "/pdg cache - show classification cache size. /pdg cache clear - flush the cache (only useful after manually changing entity classification logic).",
                ["HelpUi"]          = "/pdg ui - open the in-game CUI admin panel. Requires pvedamageguard.admin permission.",
                ["HelpClose"]       = "/pdg close - close the in-game CUI panel for you.",
                ["UiOnlyInGame"]    = "/pdg ui must be run by an in-game player.",
                ["HelpRules"]       = "/pdg rules [context] - list contexts (no arg) or print direct + inherited rules of one context.",
                ["HelpCategories"]  = "/pdg categories - list custom NPC categories registered by other plugins via API_RegisterCategory.",
                ["RulesMatrixDisabled"] = "Rule matrix is disabled (RuleMatrix.Enabled=false in config). No rules to browse.",
                ["RulesContextList"]    = "Available contexts: {0}. Default: '{1}'. Use /pdg rules <name> for details.",
                ["RulesContextNotFound"] = "Context '{0}' not found in RuleMatrix.Contexts.",
                ["RulesContextHeader"]  = "Context '{0}'. Inheritance chain: {1}",
                ["RulesDirectHeader"]   = "Direct rules:",
                ["RulesInheritedHeader"] = "Inherited rules (not overridden):",
                ["CategoriesNone"]      = "No custom NPC categories registered. Other plugins can register via API_RegisterCategory(string, Func<BaseEntity,bool>).",
                ["CategoriesList"]      = "{0} custom NPC categor(y/ies) registered: {1}",
                ["HelpStats"]       = "/pdg stats [player] - show damage stats. No arg = your stats. Admin: pass SteamID or partial name to look up another player.",
                ["StatsOnlyInGame"] = "/pdg stats with no arg must be run by an in-game player.",
                ["StatsNotFound"]   = "Player '{0}' not found.",
                ["StatsNone"]       = "No stats recorded yet for {0}.",
                ["StatsReport"]     = "Stats for {0}:\n  Damage dealt to players: {1:F1}   Taken from players: {2:F1}   Taken from NPCs: {3:F1}\n  Damage reflected back at me: {4:F1} ({5} reflects)\n  NPCs killed: {6}   PvP deaths: {7}",
                ["TestStructureAuth"] = "Your authorization on this structure: {0}. OwnerID: {1}.",
                ["StatusBlock"]     =
                    "PVE Damage Guard v{0}\n" +
                    "  Reflect: {1} ({2:F2}x), BlockPvpIfNotReflecting: {3}, Teammates: {4}\n" +
                    "  NPC->Player default: {5:F2}, NPC->Structure default: {6:F2}, Traps as PvP: {7}\n" +
                    "  Logging: {8} (file={9}), Yield to TruePVE: {10}, Discord webhook: {22}\n" +
                    "  Features: TOD={11}, VictimSubtype={12}, BuildingGrade={13}, PerAttackerStruct={14}, RuleMatrix={15}\n" +
                    "  Current hour: {16} ({17}), Events: {18} entity / {20} dome / {21} global, Config issues: {19}\n" +
                    "  Commands: /pdg reload | log | logfile | scale | hour | context | history | test | preset | import | validate | events | webhook | help"
            }, this);
        }

        #endregion
    }
}
