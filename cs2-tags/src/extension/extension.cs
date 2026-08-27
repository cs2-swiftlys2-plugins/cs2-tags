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
    public static readonly Dictionary<ulong, Tag> PlayerTagsList = [];

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

    public static Tag GetOrCreatePlayerTag(IPlayer player, bool force = false)
    {
        if (player == null)
            return Tags.Config.Default.Clone();

        try
        {
            // Mindig élőben lekérdjük a legfrissebb taget a jogok alapján, így azonnal reagál a flag elvesztésére
            Tag freshTag = player.GetTag();

            if (!IsDefaultTag(freshTag))
            {
                PlayerTagsList[player.SteamID] = freshTag;
                return freshTag;
            }
            else
            {
                PlayerTagsList.Remove(player.SteamID);
                return Tags.Config.Default.Clone();
            }
        }
        catch
        {
            return Tags.Config.Default.Clone();
        }
    }

    public static Tag GetTag(this IPlayer player)
    {
        if (player == null)
            return Tags.Config.Default.Clone();

        try
        {
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
        catch
        {
            return Tags.Config.Default.Clone();
        }
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
        if ((types & TagType.ChatColor) != 0) tag.ChatColor = defaultTag.ChatTag;

        Tags.Api.TagsUpdatedPost(player, tag);
    }

    public static bool GetChatSound(this IPlayer player)
    {
        try
        {
            if (PlayerTagsList.TryGetValue(player.SteamID, out Tag? tag))
                return tag.ChatSound;
        }
        catch {}

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
        try
        {
            if (PlayerTagsList.TryGetValue(player.SteamID, out Tag? tag))
                return tag.Visibility;
        }
        catch {}

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

    public static void SetScoreTag(this IPlayer player, string? tag)
    {
        if (player == null || !player.IsValid || player.Controller == null || !player.Controller.IsValid)
            return;

        string normalizedTag = tag ?? string.Empty;

        try
        {
            player.Controller.Clan = normalizedTag;
            player.Controller.ClanUpdated();
        }
        catch
        {
        }
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
            if (player == null || !player.IsValid || player.IsFakeClient)
                continue;

            try
            {
                if (player.SteamID == 0) continue;
                Tag tag = GetOrCreatePlayerTag(player, true);
                player.SetScoreTag(player.GetVisibility() ? tag.ScoreTag : Tags.Config.Default.ScoreTag);
            }
            catch {}
        }
    }
}