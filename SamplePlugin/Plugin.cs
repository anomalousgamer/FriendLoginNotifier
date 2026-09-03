using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FriendLoginNotifier;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService]
    private static IChatGui ChatGui { get; set; } = null!;

    [PluginService]
    private static IAddonLifecycle AddonLifecycle { get; set; } = null!;

    [PluginService]
    private static IClientState ClientState { get; set; } = null!;

    [PluginService]
    private static ICommandManager CommandManager { get; set; } = null!;

    [PluginService]
    private static IPluginLog PluginLog { get; set; } = null!;

    private const string CommandName = "/friendlogin";
    private const ushort LoginColor = 570;

    private readonly Dictionary<string, bool> friendStates =
        new(StringComparer.Ordinal);

    private bool baselineReady;

    public Plugin()
    {
        // Runs after FFXIV processes updated friend-list data.
        // The plugin does not request a refresh itself.
        AddonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate,
            "FriendList",
            OnFriendListUpdated);

        ClientState.Login += ResetTracking;
        ClientState.Logout += OnLogout;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Shows notifier status. Use /friendlogin test for a test message."
        });
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(
            AddonEvent.PostRequestedUpdate,
            "FriendList",
            OnFriendListUpdated);

        ClientState.Login -= ResetTracking;
        ClientState.Logout -= OnLogout;

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            PrintGreenMessage(
                "Test successful! Friend login notifications will appear like this.");

            return;
        }

        var status = baselineReady
            ? $"Tracking {friendStates.Count} friend entries. Open or refresh the normal friend list to check for changes."
            : "No baseline yet. Open the normal FFXIV friend list once.";

        ChatGui.Print(status, "Friend Login");
    }

    private void OnFriendListUpdated(
        AddonEvent eventType,
        AddonArgs addonArgs)
    {
        try
        {
            ScanFriendList();
        }
        catch (Exception exception)
        {
            PluginLog.Error(
                exception,
                "Failed to read the updated FFXIV friends list.");
        }
    }

    private void ScanFriendList()
    {
        var friendList = InfoProxyFriendList.Instance();

        if (friendList == null || friendList->EntryCount == 0)
        {
            return;
        }

        var foundValidEntry = false;

        foreach (ref readonly var friend in friendList->CharDataSpan)
        {
            var name = friend.NameString;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foundValidEntry = true;

            // Name plus Home World identifies the character without
            // retaining their permanent content or account ID.
            var key = $"{friend.HomeWorld}:{name}";

            var isOnline =
                (friend.State &
                 InfoProxyCommonList.CharacterData.OnlineStatus.Online) != 0;

            if (baselineReady &&
                friendStates.TryGetValue(key, out var wasOnline) &&
                !wasOnline &&
                isOnline)
            {
                PrintGreenMessage($"{name} has logged in.");
            }

            friendStates[key] = isOnline;
        }

        if (foundValidEntry)
        {
            baselineReady = true;
        }
    }

    private static void PrintGreenMessage(string message)
    {
        var coloredMessage = new SeStringBuilder()
            .AddUiForeground(message, LoginColor)
            .Build();

        ChatGui.Print(
            coloredMessage,
            "Friend Login",
            LoginColor);
    }

    private void ResetTracking()
    {
        friendStates.Clear();
        baselineReady = false;
    }

    private void OnLogout(int type, int code)
    {
        ResetTracking();
    }
}
