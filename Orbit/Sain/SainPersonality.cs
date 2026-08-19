using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;

namespace Orbit.Sain;

/// <summary>
/// Reflective lookup against the SAIN plugin to read a bot's currently- assigned personality brain name (e.g.
/// "Rat", "GigaChad", "Wreckless"). SAIN is a hard dependency, but the brain attachment is asynchronous — the
/// BotComponent lives in <c>SAIN.Components.BotManagerComponent.Bots</c> (a dictionary keyed by Profile.Id,
/// not a UnityEngine Component on the BotOwner GameObject), and SAIN populates it 1-2 seconds after spawn.
/// Brain name resolution therefore returns null during that window — callers must defer use of the result
/// (see the deferred-personality path in <see cref="Orbit.Core.SquadRegistry"/>).
/// </summary>
public static class SainPersonality
{
    private static bool _initAttempted;
    private static bool _typesResolved;
    private static Type _botManagerComponentType;
    private static Type _ePersonalityType;
    private static PropertyInfo _botsProperty;
    private static PropertyInfo _instanceProperty;
    private static object _cachedBotManager;
    private static GameWorld _cachedBotManagerWorld;

    // PropertyInfo cache for the per-bot walk inside GetBrainName. Every element of Bots.Values is the same
    // concrete SAIN BotComponent type, and its Player/ProfileId/Info/Personality property shapes don't change
    // between bots or calls — resolving them by name via GetType().GetProperty(...) on every single element,
    // every single call, was the actual hot cost (O(bots) reflection metadata lookups per GetBrainName call,
    // on top of GetBrainName itself being called per-agent / per-squad). Cached lazily on first successful
    // resolution and re-resolved only if the observed runtime type ever changes (defensive, not expected).
    private static PropertyInfo _valuesProperty;
    private static Type _valuesPropertyOwner;
    private static PropertyInfo _playerProperty;
    private static Type _playerPropertyOwner;
    private static PropertyInfo _profileIdProperty;
    private static Type _profileIdPropertyOwner;
    private static PropertyInfo _infoProperty;
    private static Type _infoPropertyOwner;
    private static PropertyInfo _personalityProperty;
    private static Type _personalityPropertyOwner;

    private static PropertyInfo ResolveCachedProperty(
        object instance, string propertyName, ref PropertyInfo cache, ref Type cachedOwner)
    {
        if (instance == null) return null;
        var type = instance.GetType();
        if (cache != null && cachedOwner == type) return cache;
        cache = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        cachedOwner = type;
        return cache;
    }

    public static bool IsSainAvailable => _typesResolved;

    public static void InitIfNeeded()
    {
        if (_initAttempted) return;
        _initAttempted = true;

        try
        {
            _botManagerComponentType = Type.GetType("SAIN.Components.BotManagerComponent, SAIN");
            _ePersonalityType = Type.GetType("SAIN.Models.Preset.Personalities.EPersonality, SAIN");
            if (_botManagerComponentType == null)
            {
                Log.Info("SainPersonality: SAIN.Components.BotManagerComponent not found — SAIN missing or version-mismatched. Every PMC will resolve to Average.");
                return;
            }
            if (_ePersonalityType == null)
            {
                Log.Info("SainPersonality: SAIN.Models.Preset.Personalities.EPersonality not found — SAIN missing or version-mismatched. Every PMC will resolve to Average.");
                return;
            }
            _botsProperty = _botManagerComponentType.GetProperty("Bots", BindingFlags.Public | BindingFlags.Instance);
            _instanceProperty = _botManagerComponentType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            _typesResolved = _botsProperty != null;
            Log.Info($"SainPersonality: reflection ready={_typesResolved} (BotManagerComponent={_botManagerComponentType.FullName}, EPersonality={_ePersonalityType.FullName}, BotsProp={_botsProperty != null}, InstanceProp={_instanceProperty != null})");
        }
        catch (Exception ex)
        {
            Log.Warning($"SainPersonality.InitIfNeeded threw: {ex.Message} — every PMC will resolve to Average");
            _typesResolved = false;
        }
    }

    private static object ResolveBotManager()
    {
        GameWorld gameWorld = null;
        try { gameWorld = Singleton<GameWorld>.Instance; }
        catch (Exception ex) { Log.Debug($"SainPersonality.ResolveBotManager: GameWorld.Instance threw: {ex.Message}"); }

        // SainPersonality is a STATIC class, so _cachedBotManager outlives the raid. It holds a plain
        // `object`, so a `!= null` check never trips Unity's destroyed-object "fake null" — at the 2nd
        // raid of a session we'd hand back the PREVIOUS raid's BotManagerComponent, whose Bots dict is
        // keyed by last raid's profileIds → every current bot reads as (none) → Average → timeout. Tie
        // the cache to the GameWorld it came from and drop it the moment the raid changes. (raid-review
        // re-resolves from a fresh GameWorld every call, which is why it never hit this.)
        if (!ReferenceEquals(gameWorld, _cachedBotManagerWorld))
        {
            _cachedBotManager = null;
            _cachedBotManagerWorld = gameWorld;
        }

        if (_cachedBotManager != null) return _cachedBotManager;
        if (gameWorld == null) return null;

        // Prefer GameWorld.GetComponent (the canonical way SAIN attaches the manager). Fall back to the
        // static Instance property if present.
        try
        {
            var component = gameWorld.GetComponent(_botManagerComponentType);
            if (component != null)
            {
                _cachedBotManager = component;
                return _cachedBotManager;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"SainPersonality.ResolveBotManager via GameWorld.GetComponent failed: {ex.Message}");
        }
        if (_instanceProperty != null)
        {
            try
            {
                _cachedBotManager = _instanceProperty.GetValue(null);
                return _cachedBotManager;
            }
            catch (Exception ex)
            {
                Log.Debug($"SainPersonality.ResolveBotManager via Instance property failed: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the SAIN-assigned brain name for the given bot. Returns null if SAIN isn't loaded, the
    /// BotComponent hasn't been attached yet (typical in the 1-2 seconds after spawn), or the per-bot Info /
    /// Personality field couldn't be read. Caller must be defensive against the null and retry later if
    /// appropriate.
    /// </summary>
    public static string GetBrainName(BotOwner bot)
    {
        if (bot == null) return null;
        InitIfNeeded();
        if (!_typesResolved) return null;

        try
        {
            var manager = ResolveBotManager();
            if (manager == null) return null;
            var bots = _botsProperty.GetValue(manager);
            if (bots == null) return null;

            // SAIN's Bots is a Dictionary<string, BotComponent> keyed by Profile.Id (string). Use the
            // indexer.
            var profileId = bot.Profile?.Id;
            if (string.IsNullOrEmpty(profileId)) return null;

            // Always iterate Bots.Values + match against each botComponent.Player.ProfileId — same approach
            // as raid-review (Client/integrations/SAIN.cs). The previous fast-path via ContainsKey(profileId)
            // could return false EVEN WHEN the bot was in the dict — likely because SAIN changed the dict
            // keying between versions, or because the entry was added by SAIN's tick AFTER the squad lock
            // attempt of the same frame. Either way, raid-review reading the same dict at the same frame
            // succeeds with the iterate-Values approach. Slower (O(n) per lookup) but correct. The per-element
            // property lookups are cached (see ResolveCachedProperty) so this is O(n) GetValue calls, not
            // O(n) GetProperty(name) metadata searches.
            object botComponent = null;
            var valuesProp = ResolveCachedProperty(bots, "Values", ref _valuesProperty, ref _valuesPropertyOwner);
            var values = valuesProp?.GetValue(bots) as IEnumerable;
            if (values == null) return null;
            foreach (var bc in values)
            {
                if (bc == null) continue;
                var playerProp = ResolveCachedProperty(bc, "Player", ref _playerProperty, ref _playerPropertyOwner);
                var p = playerProp?.GetValue(bc);
                if (p == null) continue;
                var profileIdProp = ResolveCachedProperty(p, "ProfileId", ref _profileIdProperty, ref _profileIdPropertyOwner);
                var pid = profileIdProp?.GetValue(p) as string;
                if (pid == profileId) { botComponent = bc; break; }
            }
            if (botComponent == null) return null;

            var infoProp = ResolveCachedProperty(botComponent, "Info", ref _infoProperty, ref _infoPropertyOwner);
            var info = infoProp?.GetValue(botComponent);
            if (info == null) return null;
            var personalityProp = ResolveCachedProperty(info, "Personality", ref _personalityProperty, ref _personalityPropertyOwner);
            var personality = personalityProp?.GetValue(info);
            if (personality == null) return null;
            if (Enum.IsDefined(_ePersonalityType, personality))
                return Enum.GetName(_ePersonalityType, personality);
            return personality.ToString();
        }
        catch (Exception ex)
        {
            Log.Debug($"SainPersonality.GetBrainName({bot}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Maps a SAIN brain name to one of the five archetypes using the user's F12 mapping strings
    /// (comma-separated lists per archetype). Null / unknown brain names map to <see
    /// cref="PersonalityArchetype.Average"/>.
    /// </summary>
    public static PersonalityArchetype MapBrainToArchetype(string brainName)
    {
        if (string.IsNullOrWhiteSpace(brainName)) return PersonalityArchetype.Average;
        if (ContainsToken(Plugin.SainArchetypeTimmyBrains?.Value, brainName)) return PersonalityArchetype.Timmy;
        if (ContainsToken(Plugin.SainArchetypeCautiousBrains?.Value, brainName)) return PersonalityArchetype.Cautious;
        if (ContainsToken(Plugin.SainArchetypeAggressiveBrains?.Value, brainName)) return PersonalityArchetype.Aggressive;
        if (ContainsToken(Plugin.SainArchetypeVeryAggressiveBrains?.Value, brainName)) return PersonalityArchetype.VeryAggressive;
        // Explicit Average mapping (default "Normal") — keeps the fallback semantic but lets the user surface
        // specific brain names in the F12 list. Unknown brains still fall through to Average via the return
        // below.
        if (ContainsToken(Plugin.SainArchetypeAverageBrains?.Value, brainName)) return PersonalityArchetype.Average;
        return PersonalityArchetype.Average;
    }

    private static bool ContainsToken(string commaSeparated, string token)
    {
        if (string.IsNullOrEmpty(commaSeparated) || string.IsNullOrEmpty(token)) return false;
        foreach (var part in commaSeparated.Split(','))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
