using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using System.Text.RegularExpressions;
using static SwiftlyS2.Shared.Helper;
using static Tags.Tags;
using static TagsApi.Tags;

namespace Tags;

public static partial class TagExtensions
{
    [GeneratedRegex(@"\{.*?\}|\p{C}")]
    private static partial Regex MyRegex();

    public static string Name(this Team team)
    {
        if (Tags.Config.Settings.TeamChatNames.TryGetValue(team, out var name))
            return name;

        return string.Empty;
    }

    public static string PrefixName(this Team team)
    {
        if (Tags.Config.Settings.TeamPrefixNames.TryGetValue(team, out var name))
            return name;

        return string.Empty;
    }

    public static string RemoveCurlyBraceContent(this string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        return MyRegex().Replace(message, string.Empty);
    }

    public static string FormatMessage(Team team, params string[] args)
    {
        return ReplaceTags(string.Concat(args), team);
    }

    public static string ReplaceTags(this string message, Team team)
    {
        return message
            .Replace("[teamcolor]", ForTeam(team), StringComparison.OrdinalIgnoreCase)
            .Colored();
    }

    public static string ForTeam(Team team)
    {
        return team switch
        {
            Team.None => "\u0001",
            Team.Spectator => "\u0003",
            Team.CT => "\v",
            Team.T => "\u0010",
            _ => "\u0001"
        };
    }

    private static bool IsDefaultTag(Tag tag)
    {
        var d = Tags.Config.Default;
        return string.Equals(tag.ScoreTag ?? "", d.ScoreTag ?? "", StringComparison.Ordinal)
            && string.Equals(tag.ChatTag ?? "", d.ChatTag ?? "", StringComparison.Ordinal)
            && string.Equals(tag.NameColor ?? "", d.NameColor ?? "", StringComparison.Ordinal)
            && string.Equals(tag.ChatColor ?? "", d.ChatColor ?? "", StringComparison.Ordinal)
            && tag.ChatSound == d.ChatSound
            && tag.Visibility == d.Visibility;
    }

    private static bool TagContentEquals(Tag a, Tag b)
    {
        return string.Equals(a.ScoreTag ?? "", b.ScoreTag ?? "", StringComparison.Ordinal)
            && string.Equals(a.ChatTag ?? "", b.ChatTag ?? "", StringComparison.Ordinal)
            && string.Equals(a.NameColor ?? "", b.NameColor ?? "", StringComparison.Ordinal)
            && string.Equals(a.ChatColor ?? "", b.ChatColor ?? "", StringComparison.Ordinal);
    }

    private static Tag MergeUserPrefs(Tag baseTag, Tag? oldTag)
    {
        if (oldTag == null)
            return baseTag;

        baseTag.ChatSound = oldTag.ChatSound;
        baseTag.Visibility = oldTag.Visibility;
        return baseTag;
    }

    // NEW: called periodically from Tags.cs to instantly react to permission removal/expiry
    public static void RevalidateTagFromPermissions(this IPlayer player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
            return;

        PlayerTagsList.TryGetValue(player.SteamID, out var cached);

        // Compute current tag based on current permissions/config
        var computed = player.GetTag();
        computed = MergeUserPrefs(computed, cached);

        // If we have a cached tag and content hasn't changed, do nothing
        if (cached != null && TagContentEquals(cached, computed))
            return;

        // Update cache policy: keep non-default cached, avoid locking default (as before)
        if (IsDefaultTag(computed))
            PlayerTagsList.Remove(player.SteamID);
        else
            PlayerTagsList[player.SteamID] = computed;

        // Apply scoretag immediately (respect visibility)
        player.SetScoreTag(player.GetVisibility() ? computed.ScoreTag : Tags.Config.Default.ScoreTag);
    }

    public static Tag GetOrCreatePlayerTag(IPlayer player, bool force)
    {
        if (player == null)
            return Tags.Config.Default.Clone();

        if (!force && PlayerTagsList.TryGetValue(player.SteamID, out Tag? cachedTag) && cachedTag != null)
            return cachedTag;

        Tag newTag = player.GetTag();

        // Never cache default: allows late permissions (ShopCore async load) to flip tag later.
        if (IsDefaultTag(newTag))
        {
            PlayerTagsList.Remove(player.SteamID);
            return newTag;
        }

        PlayerTagsList[player.SteamID] = newTag;
        return newTag;
    }

    public static Tag GetTag(this IPlayer player)
    {
        if (player == null)
            return Tags.Config.Default.Clone();

        string steamId = player.SteamID.ToString();

        Tag? steamIdTag = Tags.Config.Tags
            .FirstOrDefault(t => string.Equals(t.Role, steamId, StringComparison.Ordinal))?.Clone();

        if (steamIdTag != null)
            return steamIdTag;

        if (Tags.Instance?.Permission == null)
            return Tags.Config.Default.Clone();

        Tag? permTag = Tags.Config.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t.Role)
                        && Tags.Instance.Permission.PlayerHasPermission(player.SteamID, t.Role))
            .Select(t => t.Clone())
            .FirstOrDefault();

        return permTag ?? Tags.Config.Default.Clone();
    }

    public static string GetPrePostValue(TagPrePost prePost, string? oldValue, string newValue)
    {
        string old = oldValue ?? string.Empty;
        return prePost switch
        {
            TagPrePost.Pre => newValue + old,
            TagPrePost.Post => old + newValue,
            _ => newValue
        };
    }

    public static void AddAttribute(this IPlayer player, TagType types, TagPrePost prePost, string newValue)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            string value = GetPrePostValue(prePost, tag.ScoreTag, newValue);
            tag.ScoreTag = value;
            player.SetScoreTag(value);
        }
        if ((types & TagType.ChatTag) != 0)
            tag.ChatTag = GetPrePostValue(prePost, tag.ChatTag, newValue);

        if ((types & TagType.NameColor) != 0)
            tag.NameColor = GetPrePostValue(prePost, tag.NameColor, newValue);

        if ((types & TagType.ChatColor) != 0)
            tag.ChatColor = GetPrePostValue(prePost, tag.ChatColor, newValue);

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static void SetAttribute(this IPlayer player, TagType types, string newValue)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            tag.ScoreTag = newValue;
            player.SetScoreTag(newValue);
        }
        if ((types & TagType.ChatTag) != 0) tag.ChatTag = newValue;
        if ((types & TagType.NameColor) != 0) tag.NameColor = newValue;
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = newValue;

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static string? GetAttribute(this IPlayer player, TagType type)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);

        return type switch
        {
            TagType.ScoreTag => tag.ScoreTag,
            TagType.ChatTag => tag.ChatTag,
            TagType.NameColor => tag.NameColor,
            TagType.ChatColor => tag.ChatColor,
            _ => null
        };
    }

    public static void ResetAttribute(this IPlayer player, TagType types)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);
        Tag defaultTag = player.GetTag();

        Tags.Api.TagsUpdatedPre(player, tag);

        if ((types & TagType.ScoreTag) != 0)
        {
            tag.ScoreTag = defaultTag.ScoreTag;
            player.SetScoreTag(defaultTag.ScoreTag);
        }
        if ((types & TagType.ChatTag) != 0) tag.ChatTag = defaultTag.ChatTag;
        if ((types & TagType.NameColor) != 0) tag.NameColor = defaultTag.NameColor;
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = defaultTag.ChatColor;

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static bool GetChatSound(this IPlayer player)
    {
        if (PlayerTagsList.TryGetValue(player.SteamID, out Tag? tag))
            return tag.ChatSound;

        return GetOrCreatePlayerTag(player, true).ChatSound;
    }

    public static void SetChatSound(this IPlayer player, bool value)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);
        tag.ChatSound = value;
        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static bool GetVisibility(this IPlayer player)
    {
        if (PlayerTagsList.TryGetValue(player.SteamID, out Tag? tag))
            return tag.Visibility;

        return GetOrCreatePlayerTag(player, true).Visibility;
    }

    public static void SetVisibility(this IPlayer player, bool value)
    {
        Tag tag = GetOrCreatePlayerTag(player, false);
        Tags.Api.TagsUpdatedPre(player, tag);

        tag.Visibility = value;
        player.SetScoreTag(value ? player.GetAttribute(TagType.ScoreTag) : Tags.Config.Default.ScoreTag);

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    // Scoreboard refresh fix (immediate)
    public static void SetScoreTag(this IPlayer player, string? tag)
    {
        if (player == null || !player.IsValid)
            return;

        string normalizedTag = tag ?? string.Empty;
        if (normalizedTag.Length == 0)
        {
            ClearScoreTag(player);
            return;
        }

        if (player.Controller.Clan != normalizedTag)
            player.Controller.Clan = normalizedTag;

        player.Controller.ClanUpdated();
        FireScoreTagRefreshEvent(player);
    }

    private static void ClearScoreTag(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return;

        if (player.Controller.Clan != string.Empty)
            player.Controller.Clan = string.Empty;

        player.Controller.ClanUpdated();
        FireScoreTagRefreshEvent(player);
    }

    private static void FireScoreTagRefreshEvent(IPlayer player)
    {
        if (Instance == null || player == null || !player.IsValid)
            return;

        Instance.Scheduler.NextWorldUpdate(() =>
        {
            if (player == null || !player.IsValid)
                return;

            Instance.GameEvent.Fire<EventNextlevelChanged>();
        });
    }

    public static void ReloadConfig()
    {
        Tags.Config.Settings.Init();
    }

    public static void ReloadTags()
    {
        var players = Instance.PlayerManager.GetAllPlayers();
        foreach (IPlayer player in players)
        {
            if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0)
                continue;

            Tag tag = GetOrCreatePlayerTag(player, true);
            player.SetScoreTag(player.GetVisibility() ? tag.ScoreTag : Tags.Config.Default.ScoreTag);
        }
    }
}
