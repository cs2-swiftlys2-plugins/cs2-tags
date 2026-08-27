using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.NetMessages;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.ProtobufDefinitions;
using TagsApi;
using Tomlyn.Extensions.Configuration;
using static SwiftlyS2.Shared.Helper;
using static Tags.TagExtensions;
using static TagsApi.Tags;

namespace Tags;

[PluginMetadata(Id = "Tags", Version = "v1.3fix", Name = "Tags", Author = "schwarper")]
public sealed class Tags(ISwiftlyCore core) : BasePlugin(core)
{
    public static ISwiftlyCore Instance { get; set; } = null!;
    public static readonly Dictionary<ulong, DateTime> PlayerJoinUtc = [];
    public static readonly TagsAPI Api = new();
    public static Config Config { get; set; } = null!;
    private bool isSyncLoopRunning = false;

    public override void Load(bool hotReload)
    {
        Instance = Core;

        const string ConfigFileName = "config.toml";
        const string ConfigSection = "Tags";
        Core.Configuration
            .InitializeTomlWithModel<Config>(ConfigFileName, ConfigSection)
            .Configure(cfg => cfg.AddTomlFile(
                ConfigFileName,
                optional: false,
                reloadOnChange: true));

        ServiceCollection services = new();
        services.AddSwiftly(Core)
            .AddOptionsWithValidateOnStart<Config>()
            .BindConfiguration(ConfigSection);

        var provider = services.BuildServiceProvider();
        Config = provider.GetRequiredService<IOptions<Config>>().Value;

        foreach (string command in Config.Commands.TagsReload)
            Core.Command.RegisterCommand(command, Command_Tags_Reload, true, "tags.reload");

        foreach (string command in Config.Commands.Visibility)
            Core.Command.RegisterCommand(command, Command_Visibility, true, "tags.visibility");

        Tags.Config.Settings.Init();

        Core.Scheduler.NextWorldUpdate(() => ReloadTags());

        StartSyncLoop();

        if (hotReload)
            ReloadTags();
    }

    private void StartSyncLoop()
    {
        if (isSyncLoopRunning)
            return;

        isSyncLoopRunning = true;
        RunSyncLoopTimer();
    }

    private void RunSyncLoopTimer()
    {
        Core.Scheduler.DelayBySeconds(5.0f, () =>
        {
            Core.Scheduler.NextWorldUpdate(() =>
            {
                try
                {
                    var players = Instance.PlayerManager.GetAllPlayers();
                    foreach (var player in players)
                    {
                        if (IsPlayerInvalid(player))
                            continue;

                        // KÉNYSZERÍTett frissítés: Töröljük a cache-t és újraolvassuk a jogosultságokat,
                        // így azonnal megjelenik a tag a tabellán, ha megkaptad a flaget, vagy eltűnik, ha elvesztetted!
                        TryApplyTag(player, force: true);
                    }
                }
                catch { }

                RunSyncLoopTimer();
            });
        });
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        interfaceManager.AddSharedInterface<ITagApi, TagsAPI>("Tags.Api", Api);
    }

    public override void Unload()
    {
        PlayerJoinUtc.Clear();
    }

    public static void Command_Tags_Reload(ICommandContext context)
    {
        ReloadConfig();
        ReloadTags();
    }

    public void Command_Visibility(ICommandContext context)
    {
        if (!context.IsSentByPlayer)
            return;

        var player = context.Sender!;
        var localizer = Core.Translation.GetPlayerLocalizer(player);

        if (player.GetVisibility())
        {
            player.SetVisibility(false);
            context.Reply(Config.Settings.Tag.Colored() + localizer["Tags are now hidden"]);
        }
        else
        {
            player.SetVisibility(true);
            context.Reply(Config.Settings.Tag.Colored() + localizer["Tags are now visible"]);
        }

        TryApplyTag(player, force: true);
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerConnect(EventPlayerConnectFull @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        if (IsPlayerInvalid(player))
            return HookResult.Continue;

        PlayerJoinUtc[player.SteamID] = DateTime.UtcNow;

        Core.Scheduler.NextWorldUpdate(() => TryApplyTag(player, force: true));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        try
        {
            PlayerJoinUtc.Remove(player.SteamID);
        }
        catch {}
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is not { } player)
            return HookResult.Continue;

        if (IsPlayerInvalid(player))
            return HookResult.Continue;

        Core.Scheduler.NextWorldUpdate(() => TryApplyTag(player, force: true));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnPlayerTeam(EventPlayerTeam @event)
    {
        if (@event.UserIdPlayer is not IPlayer player)
            return HookResult.Continue;

        if (IsPlayerInvalid(player))
            return HookResult.Continue;

        Core.Scheduler.NextWorldUpdate(() => TryApplyTag(player, force: true));
        return HookResult.Continue;
    }

    [GameEventHandler(HookMode.Post)]
    public HookResult OnRoundStart(EventRoundStart @event)
    {
        var players = Instance.PlayerManager.GetAllPlayers();
        foreach (var player in players)
        {
            if (IsPlayerInvalid(player))
                continue;

            TryApplyTag(player, force: true);
        }

        return HookResult.Continue;
    }

    private static bool IsPlayerInvalid(IPlayer? player)
    {
        if (player == null || !player.IsValid || player.IsFakeClient)
            return true;

        try
        {
            return player.SteamID == 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryApplyTag(IPlayer player, bool force)
    {
        if (IsPlayerInvalid(player))
            return false;

        try
        {
            if (player.Controller == null || !player.Controller.IsValid)
                return false;

            var tag = GetOrCreatePlayerTag(player, force);
            string targetScoreTag = player.GetVisibility() ? (tag.ScoreTag ?? string.Empty) : (Tags.Config.Default.ScoreTag ?? string.Empty);

            player.SetScoreTag(targetScoreTag);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [ServerNetMessageHandler]
    public HookResult OnMessageSayText2(CUserMessageSayText2 msg)
    {
        if (Core.PlayerManager.GetPlayer(msg.Entityindex - 1) is not { } player)
            return HookResult.Continue;

        if (IsPlayerInvalid(player))
            return HookResult.Continue;

        if (string.IsNullOrEmpty(msg.Param2))
            return HookResult.Continue;

        var tag = GetOrCreatePlayerTag(player, true);

        MessageProcess messageProcess = new()
        {
            Player = player,
            Tag = !player.GetVisibility() ? Config.Default.Clone() : tag.Clone(),
            Message = msg.Param2.RemoveCurlyBraceContent(),
            PlayerName = msg.Param1,
            ChatSound = msg.Chat,
            TeamMessage = !msg.Messagename.Contains("All")
        };

        if (string.IsNullOrEmpty(messageProcess.Message))
            return HookResult.Handled;

        HookResult hookResult = Api.MessageProcessPre(messageProcess);
        if (hookResult >= HookResult.Stop)
            return hookResult;

        string prefixname =
            player.Controller.PawnIsAlive || player.Controller.Team == Team.Spectator
                ? player.Controller.Team.PrefixName()
                : Config.Settings.DeadName;

        string teamname = messageProcess.TeamMessage ? player.Controller.Team.Name() : string.Empty;

        Tag playerData = messageProcess.Tag;

        Team team = player.Controller.Team;
        messageProcess.PlayerName = FormatMessage(
            team,
            prefixname,
            teamname,
            playerData.ChatTag ?? string.Empty,
            playerData.NameColor ?? string.Empty,
            messageProcess.PlayerName
        );

        messageProcess.Message = FormatMessage(team, playerData.ChatColor ?? string.Empty, messageProcess.Message);

        hookResult = Api.MessageProcess(messageProcess);
        if (hookResult >= HookResult.Stop)
            return hookResult;

        msg.Messagename = $" {messageProcess.PlayerName}\u0001: {messageProcess.Message}";
        msg.Chat = playerData.ChatSound;

        Api.MessageProcessPost(messageProcess);
        return HookResult.Continue;
    }
}