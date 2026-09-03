using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace FriendLoginNotifier;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private enum AutomaticPollingMode
    {
        Disabled,
        SameWorld,
        AllWorlds
    }

    [PluginService]
    private static IDalamudPluginInterface PluginInterface { get; set; } = null!;

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

    [PluginService]
    private static IFramework Framework { get; set; } = null!;

    [PluginService]
    private static IPlayerState PlayerState { get; set; } = null!;

    private const string CommandName = "/friends";
    private const ushort LoginColor = 570;
    private const int MinimumPollSeconds = 4 * 60;
    private const int MaximumPollSeconds = 9 * 60;
    private const int MinimumCrossWorldRequestSeconds = 3;
    private const int MaximumCrossWorldRequestSeconds = 5;
    private const string PollingWarningPopup =
        "Automatic polling warning###FriendLoginPollingWarning";

    private static readonly TimeSpan LocalScanInterval =
        TimeSpan.FromSeconds(1);

    private static readonly TimeSpan InitialCrossWorldDelay =
        TimeSpan.FromSeconds(2);

    private static readonly TimeSpan CrossWorldBaselineSettleDelay =
        TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ChangelogLoginDelay =
        TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, bool> friendStates =
        new(StringComparer.Ordinal);

    private readonly Queue<ulong> pendingCrossWorldChecks = new();
    private readonly Random random = new();
    private readonly Configuration configuration;

    private bool baselineReady;
    private bool settingsWindowVisible;
    private bool changelogWindowVisible;
    private bool doNotShowChangelogAgain;
    private bool crossWorldCycleActive;
    private bool waitingToBuildCrossWorldQueue;
    private bool changelogPendingAfterLogin;
    private bool crossWorldBaselineReady;
    private bool crossWorldQueueBuiltFromFriendList;
    private bool reportSyncProgressToChat;
    private bool manualSyncInProgress;
    private bool currentCycleRefreshesGeneralFriendList;

    private AutomaticPollingMode pendingPollingMode =
        AutomaticPollingMode.Disabled;

    private int crossWorldChecksTotal;
    private int crossWorldChecksSent;
    private int crossWorldChecksProcessed;

    private DateTime nextPollAtUtc = DateTime.MaxValue;
    private DateTime nextLocalScanAtUtc = DateTime.MaxValue;
    private DateTime nextCrossWorldRequestAtUtc = DateTime.MaxValue;
    private DateTime changelogEligibleAtUtc = DateTime.MaxValue;
    private DateTime crossWorldBaselineReadyAtUtc = DateTime.MaxValue;

    private static string CurrentVersion =>
        typeof(Plugin).Assembly.GetName().Version?.ToString()
        ?? "Unknown";

    private AutomaticPollingMode CurrentAutomaticPollingMode =>
        configuration.AllWorldPollingEnabled
            ? AutomaticPollingMode.AllWorlds
            : configuration.SameWorldPollingEnabled
                ? AutomaticPollingMode.SameWorld
                : AutomaticPollingMode.Disabled;

    private bool IsAutomaticPollingEnabled =>
        CurrentAutomaticPollingMode !=
        AutomaticPollingMode.Disabled;

    public Plugin()
    {
        configuration =
            PluginInterface.GetPluginConfig() as Configuration
            ?? new Configuration();

        configuration.Initialize(PluginInterface);
        MigrateConfiguration();

        doNotShowChangelogAgain =
            string.Equals(
                configuration.LastAcknowledgedVersion,
                CurrentVersion,
                StringComparison.Ordinal);

        changelogPendingAfterLogin =
            !doNotShowChangelogAgain;

        changelogWindowVisible = false;

        AddonLifecycle.RegisterListener(
            AddonEvent.PostRequestedUpdate,
            "FriendList",
            OnFriendListUpdated);

        AddonLifecycle.RegisterListener(
            AddonEvent.PostOpen,
            "FriendList",
            OnFriendListOpened);

        Framework.Update += OnFrameworkUpdate;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        CommandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand)
            {
                HelpMessage =
                    "Opens settings. Subcommands: help, time, changes, test, " +
                    "sync, syncworld, syncother, debug on, debug off."
            });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettingsUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenSettingsUi;

        if (ClientState.IsLoggedIn)
        {
            nextLocalScanAtUtc = DateTime.UtcNow;

            if (changelogPendingAfterLogin)
            {
                changelogEligibleAtUtc =
                    DateTime.UtcNow + ChangelogLoginDelay;
            }

            if (IsAutomaticPollingEnabled)
            {
                ScheduleNextPoll();
            }
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettingsUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenSettingsUi;

        AddonLifecycle.UnregisterListener(
            AddonEvent.PostRequestedUpdate,
            "FriendList",
            OnFriendListUpdated);

        AddonLifecycle.UnregisterListener(
            AddonEvent.PostOpen,
            "FriendList",
            OnFriendListOpened);

        Framework.Update -= OnFrameworkUpdate;

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmedArguments = arguments.Trim();

        if (string.IsNullOrEmpty(trimmedArguments))
        {
            OpenSettingsUi();
            return;
        }

        if (trimmedArguments.Equals(
                "help",
                StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandHelp();
            return;
        }

        if (trimmedArguments.Equals(
                "time",
                StringComparison.OrdinalIgnoreCase))
        {
            PrintAutomaticPollingTime();
            return;
        }

        if (trimmedArguments.Equals(
                "changes",
                StringComparison.OrdinalIgnoreCase))
        {
            OpenChangelogWindow();
            return;
        }

        if (trimmedArguments.Equals(
                "test",
                StringComparison.OrdinalIgnoreCase))
        {
            PrintGreenMessage(
                "Test successful! Friend login notifications will appear like this.");

            return;
        }

        if (trimmedArguments.Equals(
                "sync",
                StringComparison.OrdinalIgnoreCase))
        {
            StartManualRefresh(
                true,
                true);
            return;
        }

        if (trimmedArguments.Equals(
                "syncother",
                StringComparison.OrdinalIgnoreCase))
        {
            StartManualRefresh(
                false,
                true);
            return;
        }

        if (trimmedArguments.Equals(
                "syncworld",
                StringComparison.OrdinalIgnoreCase))
        {
            StartSameWorldManualRefresh();
            return;
        }

        if (trimmedArguments.Equals(
                "debug on",
                StringComparison.OrdinalIgnoreCase))
        {
            SetDebugChatEnabled(true);
            return;
        }

        if (trimmedArguments.Equals(
                "debug off",
                StringComparison.OrdinalIgnoreCase))
        {
            SetDebugChatEnabled(false);
            return;
        }

        ChatGui.Print(
            $"Unknown command: /friends {trimmedArguments}",
            "Friend Login");

        PrintCommandHelp();
    }

    private static void PrintCommandHelp()
    {
        ChatGui.Print(
            "Friend Login Notifier commands:",
            "Friend Login");

        ChatGui.Print(
            "/friends — Opens the settings window.",
            "Friend Login");

        ChatGui.Print(
            "/friends help — Shows this command list.",
            "Friend Login");

        ChatGui.Print(
            "/friends time — Shows the time remaining until the next " +
            "automatic poll.",
            "Friend Login");

        ChatGui.Print(
            "/friends changes — Opens the changelog.",
            "Friend Login");

        ChatGui.Print(
            "/friends test — Sends a test login notification.",
            "Friend Login");

        ChatGui.Print(
            "/friends sync — Refreshes the general Friend List, then " +
            "checks friends on other worlds.",
            "Friend Login");

        ChatGui.Print(
            "/friends syncworld — Refreshes the general Friend List " +
            "without individual cross-world checks.",
            "Friend Login");

        ChatGui.Print(
            "/friends syncother — Checks only friends on other worlds.",
            "Friend Login");

        ChatGui.Print(
            "/friends debug on — Shows plugin activity and errors in chat.",
            "Friend Login");

        ChatGui.Print(
            "/friends debug off — Stops showing debug messages in chat.",
            "Friend Login");
    }

    private void PrintAutomaticPollingTime()
    {
        if (!IsAutomaticPollingEnabled)
        {
            ChatGui.Print(
                "Auto polling is not enabled. Please go to /friends and " +
                "enable if you wish to use this feature.",
                "Friend Login");

            return;
        }

        if (!ClientState.IsLoggedIn)
        {
            ChatGui.Print(
                "Auto polling is enabled, but it will begin after you log in.",
                "Friend Login");

            return;
        }

        if (crossWorldCycleActive)
        {
            ChatGui.Print(
                "A friend polling cycle is currently in progress. The next " +
                "randomized 4–9 minute timer will begin after it finishes.",
                "Friend Login");

            return;
        }

        if (nextPollAtUtc == DateTime.MaxValue)
        {
            ChatGui.Print(
                "Auto polling is enabled and waiting for its next timer " +
                "to be scheduled.",
                "Friend Login");

            return;
        }

        var secondsRemaining =
            (int)Math.Max(
                0,
                Math.Ceiling(
                    (nextPollAtUtc - DateTime.UtcNow)
                    .TotalSeconds));

        var minutes = secondsRemaining / 60;
        var seconds = secondsRemaining % 60;

        var modeName =
            CurrentAutomaticPollingMode ==
            AutomaticPollingMode.AllWorlds
                ? "All-world"
                : "Same-world";

        ChatGui.Print(
            $"{modeName} auto polling is enabled. Next automatic poll in " +
            $"approximately {minutes}m {seconds}s.",
            "Friend Login");
    }

    private void OpenSettingsUi()
    {
        settingsWindowVisible = true;
    }

    private void OpenChangelogWindow()
    {
        doNotShowChangelogAgain =
            string.Equals(
                configuration.LastAcknowledgedVersion,
                CurrentVersion,
                StringComparison.Ordinal);

        changelogPendingAfterLogin = false;
        changelogEligibleAtUtc = DateTime.MaxValue;
        changelogWindowVisible = true;
    }

    private void DrawUi()
    {
        DrawSettingsWindow();
        DrawChangelogWindow();
    }

    private void DrawSettingsWindow()
    {
        if (!settingsWindowVisible)
        {
            return;
        }

        ImGui.SetNextWindowSize(
            new Vector2(720, 650),
            ImGuiCond.Appearing);

        var shouldDraw = ImGui.Begin(
            "Friend Login Notifier Settings###FriendLoginNotifierSettings",
            ref settingsWindowVisible,
            ImGuiWindowFlags.NoCollapse);

        if (shouldDraw)
        {
            ImGui.Text("Friend Login Notifier");
            ImGui.TextDisabled($"Version {CurrentVersion}");

            ImGui.Separator();

            ImGui.TextWrapped(
                "Passive monitoring reads the friend-list information " +
                "already stored by the game.");

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.Text("Chat diagnostics");

            var debugChatEnabled =
                configuration.DebugChatEnabled;

            if (ImGui.Checkbox(
                    "Show debug messages in chat",
                    ref debugChatEnabled))
            {
                SetDebugChatEnabled(debugChatEnabled);
            }

            ImGui.TextDisabled(
                "Shows polling activity, timers, cross-world requests, " +
                "completed cycles, and errors.");

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.Text("Automatic polling modes");

            ImGui.TextWrapped(
                "Leave both options unchecked to use passive monitoring " +
                "without automatic server requests. Only one automatic " +
                "polling mode can be enabled at a time.");

            ImGui.Spacing();

            ImGui.TextWrapped(
                "Same-world mode refreshes the game's general Friend List " +
                "after each randomized 4–9 minute timer. It does not send " +
                "individual cross-world status requests.");

            ImGui.Spacing();

            ImGui.TextWrapped(
                "All-world mode performs the same general refresh, then " +
                "checks friends on other worlds individually with a new " +
                "randomized 3–5 second gap between each request. Its next " +
                "4–9 minute timer starts after the final friend is checked.");

            ImGui.Spacing();

            var sameWorldPolling =
                configuration.SameWorldPollingEnabled;

            if (ImGui.Checkbox(
                    "Enable automatic polling for same-world friends",
                    ref sameWorldPolling))
            {
                if (sameWorldPolling)
                {
                    RequestAutomaticPollingMode(
                        AutomaticPollingMode.SameWorld);
                }
                else if (CurrentAutomaticPollingMode ==
                         AutomaticPollingMode.SameWorld)
                {
                    SetAutomaticPollingMode(
                        AutomaticPollingMode.Disabled);
                }
            }

            ImGui.TextDisabled(
                "Refreshes the general Friend List without individual " +
                "cross-world checks.");

            var allWorldPolling =
                configuration.AllWorldPollingEnabled;

            if (ImGui.Checkbox(
                    "Enable automatic polling for all worlds",
                    ref allWorldPolling))
            {
                if (allWorldPolling)
                {
                    RequestAutomaticPollingMode(
                        AutomaticPollingMode.AllWorlds);
                }
                else if (CurrentAutomaticPollingMode ==
                         AutomaticPollingMode.AllWorlds)
                {
                    SetAutomaticPollingMode(
                        AutomaticPollingMode.Disabled);
                }
            }

            ImGui.TextDisabled(
                "Refreshes the general Friend List and individually checks " +
                "friends on other worlds.");

            ImGui.PushStyleColor(
                ImGuiCol.Text,
                new Vector4(1.0f, 0.65f, 0.20f, 1.0f));

            ImGui.TextWrapped(
                "Warning: Automatic polling sends friend-list refresh " +
                "requests to the FFXIV servers without manually opening " +
                "the Friend List. These requests may be visible to SE ");

            ImGui.PopStyleColor();

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.Text("Current status");
            ImGui.TextWrapped(GetTrackingStatus());
            ImGui.TextWrapped(GetPollingStatus());

            ImGui.Spacing();
            ImGui.Separator();

            if (ImGui.Button("Send test chat message"))
            {
                PrintGreenMessage(
                    "Test successful! Friend login notifications will appear like this.");
            }

            ImGui.SameLine();

            if (ImGui.Button("Show Changelog"))
            {
                OpenChangelogWindow();
            }

            ImGui.Spacing();

            if (ImGui.Button("Sync All Friends"))
            {
                StartManualRefresh(
                    true,
                    false);
            }

            ImGui.SameLine();

            if (ImGui.Button("Sync Friends on Same World"))
            {
                StartSameWorldManualRefresh();
            }

            ImGui.SameLine();

            if (ImGui.Button("Sync Friends on Other Worlds"))
            {
                StartManualRefresh(
                    false,
                    false);
            }

            DrawPollingWarningPopup();
        }

        ImGui.End();
    }

    private void DrawPollingWarningPopup()
    {
        var popupOpen = true;

        if (!ImGui.BeginPopupModal(
                PollingWarningPopup,
                ref popupOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.PushTextWrapPos(
            ImGui.GetFontSize() * 35.0f);

        ImGui.TextWrapped(
            "Automatic polling periodically asks the FFXIV servers for " +
            "updated Friend List information without requiring you to " +
            "manually open the Friend List.");

        ImGui.Spacing();

        ImGui.TextWrapped(
            "Same-world polling refreshes the general Friend List. " +
            "All-world polling also checks eligible cross-world friends " +
            "individually, with a newly randomized delay of three, four, " +
            "or five seconds between each request.");

        ImGui.Spacing();

        ImGui.PushStyleColor(
            ImGuiCol.Text,
            new Vector4(1.0f, 0.45f, 0.25f, 1.0f));

        ImGui.TextWrapped(
            "These requests may be visible in Square Enix's server logs. " +
            "No one can guarantee whether automated requests will be " +
            "detected or acted upon. Enabling this feature is at your own risk. " +
            "Also, Ima says you are gay as fuck.");

        ImGui.PopStyleColor();

        ImGui.Spacing();

        ImGui.TextWrapped(
            "When enabled, requests will occur at a newly randomized " +
            "interval between 4 and 9 minutes.");

        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("I understand — enable polling"))
        {
            SetAutomaticPollingMode(
                pendingPollingMode);

            pendingPollingMode =
                AutomaticPollingMode.Disabled;

            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel"))
        {
            pendingPollingMode =
                AutomaticPollingMode.Disabled;

            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawChangelogWindow()
    {
        if (!changelogWindowVisible)
        {
            return;
        }

        var wasVisible =
            changelogWindowVisible;

        ImGui.SetNextWindowSize(
            new Vector2(500, 370),
            ImGuiCond.FirstUseEver);

        var shouldDraw = ImGui.Begin(
            "Friend Login Notifier — What's New###FriendLoginChangelog",
            ref changelogWindowVisible,
            ImGuiWindowFlags.NoCollapse);

        if (shouldDraw)
        {
            ImGui.Text($"Version {CurrentVersion}");

            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextWrapped(Changelog.Latest);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.Checkbox(
                "Don't show again for this version",
                ref doNotShowChangelogAgain);

            ImGui.Spacing();

            if (ImGui.Button("Got it"))
            {
                SaveChangelogPreferenceAndClose();
            }
        }

        ImGui.End();

        if (wasVisible &&
            !changelogWindowVisible)
        {
            SaveChangelogPreferenceAndClose();
        }
    }

    private void SaveChangelogPreferenceAndClose()
    {
        var storedVersion =
            doNotShowChangelogAgain
                ? CurrentVersion
                : string.Empty;

        if (!string.Equals(
                configuration.LastAcknowledgedVersion,
                storedVersion,
                StringComparison.Ordinal))
        {
            configuration.LastAcknowledgedVersion =
                storedVersion;

            configuration.Save();
        }

        changelogWindowVisible = false;
    }

    private void MigrateConfiguration()
    {
        var configurationChanged = false;

        if (configuration.Version < 2)
        {
            if (configuration.AutomaticPollingEnabled)
            {
                configuration.AllWorldPollingEnabled = true;
                configuration.SameWorldPollingEnabled = false;
            }

            configuration.AutomaticPollingEnabled = false;
            configurationChanged = true;
        }

        if (configuration.AllWorldPollingEnabled &&
            configuration.SameWorldPollingEnabled)
        {
            configuration.SameWorldPollingEnabled = false;
            configurationChanged = true;
        }

        if (configuration.Version < 3)
        {
            configuration.Version = 3;
            configurationChanged = true;
        }

        if (configurationChanged)
        {
            configuration.Save();
        }
    }

    private void RequestAutomaticPollingMode(
        AutomaticPollingMode mode)
    {
        pendingPollingMode = mode;
        ImGui.OpenPopup(PollingWarningPopup);
    }

    private void SetDebugChatEnabled(bool enabled)
    {
        configuration.DebugChatEnabled = enabled;
        configuration.Save();

        ChatGui.Print(
            enabled
                ? "Debug chat messages enabled."
                : "Debug chat messages disabled.",
            "Friend Login");
    }

    private void PrintDebugChat(string message)
    {
        if (!configuration.DebugChatEnabled)
        {
            return;
        }

        ChatGui.Print(
            $"[Debug] {message}",
            "Friend Login");
    }

    private void SetAutomaticPollingMode(
        AutomaticPollingMode mode)
    {
        var cycleWasActive =
            crossWorldCycleActive;

        configuration.SameWorldPollingEnabled =
            mode == AutomaticPollingMode.SameWorld;

        configuration.AllWorldPollingEnabled =
            mode == AutomaticPollingMode.AllWorlds;

        configuration.AutomaticPollingEnabled = false;

        configuration.Save();

        CancelCrossWorldCheckCycle();

        if (cycleWasActive)
        {
            PrintDebugChat(
                "The active cross-world polling cycle was stopped.");
        }

        PrintDebugChat(
            mode switch
            {
                AutomaticPollingMode.SameWorld =>
                    "Same-world automatic polling enabled.",
                AutomaticPollingMode.AllWorlds =>
                    "All-world automatic polling enabled.",
                _ =>
                    "Automatic polling disabled."
            });

        if (mode != AutomaticPollingMode.Disabled &&
            ClientState.IsLoggedIn)
        {
            ScheduleNextPoll();
        }
        else
        {
            nextPollAtUtc =
                DateTime.MaxValue;
        }
    }

    private void StartManualRefresh(
        bool refreshGeneralFriendList,
        bool showProgressInChat)
    {
        if (!ClientState.IsLoggedIn)
        {
            ChatGui.Print(
                "You must be logged in before refreshing friends.",
                "Friend Login");

            return;
        }

        if (crossWorldCycleActive)
        {
            ChatGui.Print(
                "A friend refresh is already in progress.",
                "Friend Login");

            return;
        }

        reportSyncProgressToChat =
            showProgressInChat;

        manualSyncInProgress = true;

        if (refreshGeneralFriendList)
        {
            ChatGui.Print(
                "Syncing all friends. Refreshing the general Friend List " +
                "before checking friends on other worlds.",
                "Friend Login");
        }
        else
        {
            ChatGui.Print(
                "Syncing friends on other worlds.",
                "Friend Login");
        }

        StartRefreshCycle(
            DateTime.UtcNow,
            refreshGeneralFriendList);
    }

    private void StartSameWorldManualRefresh()
    {
        if (!ClientState.IsLoggedIn)
        {
            ChatGui.Print(
                "You must be logged in before refreshing friends.",
                "Friend Login");

            return;
        }

        if (crossWorldCycleActive)
        {
            ChatGui.Print(
                "A friend refresh is already in progress.",
                "Friend Login");

            return;
        }

        ChatGui.Print(
            "Syncing friends on the same world.",
            "Friend Login");

        var requestSent =
            RequestFriendListRefresh();

        ChatGui.Print(
            requestSent
                ? "Same-world friend sync request sent."
                : "The game did not send the same-world friend sync request.",
            "Friend Login");

        if (IsAutomaticPollingEnabled)
        {
            ScheduleNextPoll();
        }
    }

    private void OnFrameworkUpdate(
        IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
        {
            return;
        }

        var now = DateTime.UtcNow;

        TryOpenPendingChangelog(now);
        TryFinalizeCrossWorldBaseline(now);

        if (now >= nextLocalScanAtUtc)
        {
            nextLocalScanAtUtc =
                now + LocalScanInterval;

            TryScanFriendList();
        }

        if (crossWorldCycleActive)
        {
            ProcessCrossWorldCheckCycle(now);
            return;
        }

        if (!IsAutomaticPollingEnabled)
        {
            return;
        }

        if (now < nextPollAtUtc)
        {
            return;
        }

        StartAutomaticRefreshCycle(now);
    }

    private void TryFinalizeCrossWorldBaseline(
        DateTime now)
    {
        if (crossWorldBaselineReady ||
            now < crossWorldBaselineReadyAtUtc)
        {
            return;
        }

        if (!TryScanFriendList())
        {
            crossWorldBaselineReadyAtUtc =
                now + LocalScanInterval;

            return;
        }

        crossWorldBaselineReady = true;
        crossWorldBaselineReadyAtUtc = DateTime.MaxValue;

        PluginLog.Debug(
            "Initial cross-world friend baseline established.");

        PrintDebugChat(
            "Initial cross-world friend baseline established.");
    }

    private void TryOpenPendingChangelog(
        DateTime now)
    {
        if (!changelogPendingAfterLogin ||
            now < changelogEligibleAtUtc ||
            !PlayerState.IsLoaded)
        {
            return;
        }

        changelogPendingAfterLogin = false;
        changelogEligibleAtUtc = DateTime.MaxValue;
        changelogWindowVisible = true;
    }

    private void StartAutomaticRefreshCycle(
        DateTime now)
    {
        manualSyncInProgress = false;

        if (CurrentAutomaticPollingMode ==
            AutomaticPollingMode.SameWorld)
        {
            PrintDebugChat(
                "Same-world automatic polling cycle started.");

            RequestFriendListRefresh();

            PrintDebugChat(
                "Same-world automatic polling cycle finished.");

            ScheduleNextPoll();
            return;
        }

        if (CurrentAutomaticPollingMode ==
            AutomaticPollingMode.AllWorlds)
        {
            PrintDebugChat(
                "All-world automatic polling cycle started.");

            StartRefreshCycle(now, true);
        }
    }

    private void StartRefreshCycle(
        DateTime now,
        bool refreshGeneralFriendList)
    {
        currentCycleRefreshesGeneralFriendList =
            refreshGeneralFriendList;

        if (!crossWorldBaselineReady)
        {
            crossWorldBaselineReadyAtUtc = DateTime.MaxValue;
        }

        pendingCrossWorldChecks.Clear();
        crossWorldChecksTotal = 0;
        crossWorldChecksSent = 0;
        crossWorldChecksProcessed = 0;
        crossWorldQueueBuiltFromFriendList = false;
        crossWorldCycleActive = true;
        nextPollAtUtc = DateTime.MaxValue;

        if (refreshGeneralFriendList)
        {
            waitingToBuildCrossWorldQueue = true;
            nextCrossWorldRequestAtUtc =
                now + InitialCrossWorldDelay;

            RequestFriendListRefresh();
            return;
        }

        waitingToBuildCrossWorldQueue = false;
        BuildCrossWorldCheckQueue();

        if (pendingCrossWorldChecks.Count == 0)
        {
            FinishCrossWorldCheckCycle();
            return;
        }

        nextCrossWorldRequestAtUtc = now;
    }

    private void ProcessCrossWorldCheckCycle(
        DateTime now)
    {
        if (now < nextCrossWorldRequestAtUtc)
        {
            return;
        }

        if (waitingToBuildCrossWorldQueue)
        {
            waitingToBuildCrossWorldQueue = false;
            BuildCrossWorldCheckQueue();

            if (pendingCrossWorldChecks.Count == 0)
            {
                FinishCrossWorldCheckCycle();
                return;
            }
        }

        var agent = AgentFriendlist.Instance();

        if (agent == null)
        {
            PluginLog.Warning(
                "Could not request cross-world friend information: " +
                "AgentFriendlist was null.");

            PrintDebugChat(
                "ERROR: Cross-world status requests could not continue " +
                "because the Friend List agent was unavailable.");

            FinishCrossWorldCheckCycle();
            return;
        }

        if (pendingCrossWorldChecks.Count == 0)
        {
            FinishCrossWorldCheckCycle();
            return;
        }

        var contentId =
            pendingCrossWorldChecks.Dequeue();

        try
        {
            agent->RequestFriendInfo(contentId);
            crossWorldChecksSent++;

            PluginLog.Debug(
                "Cross-world friend status request sent. " +
                "{Sent}/{Total} requests completed for this cycle.",
                crossWorldChecksSent,
                crossWorldChecksTotal);

            PrintDebugChat(
                $"Cross-world status request " +
                $"{crossWorldChecksSent}/{crossWorldChecksTotal} sent.");
        }
        catch (Exception exception)
        {
            PluginLog.Error(
                exception,
                "Failed to request cross-world friend information.");

            PrintDebugChat(
                $"ERROR: Cross-world status request " +
                $"{crossWorldChecksProcessed + 1}/" +
                $"{crossWorldChecksTotal} failed.");
        }

        crossWorldChecksProcessed++;
        ReportSyncProgress();

        if (pendingCrossWorldChecks.Count == 0)
        {
            FinishCrossWorldCheckCycle();
            return;
        }

        ScheduleNextCrossWorldRequest(now);
    }

    private void ScheduleNextCrossWorldRequest(
        DateTime now)
    {
        var delaySeconds = random.Next(
            MinimumCrossWorldRequestSeconds,
            MaximumCrossWorldRequestSeconds + 1);

        nextCrossWorldRequestAtUtc =
            now.AddSeconds(delaySeconds);

        PrintDebugChat(
            $"Next cross-world status request scheduled in " +
            $"{delaySeconds} seconds.");
    }

    private void BuildCrossWorldCheckQueue()
    {
        var friendList =
            InfoProxyFriendList.Instance();

        if (friendList == null ||
            friendList->EntryCount == 0)
        {
            return;
        }

        crossWorldQueueBuiltFromFriendList = true;

        var addedContentIds =
            new HashSet<ulong>();

        foreach (ref readonly var friend
                 in friendList->CharDataSpan)
        {
            if (friend.ContentId == 0 ||
                !friend.IsOtherServer ||
                !addedContentIds.Add(friend.ContentId))
            {
                continue;
            }

            pendingCrossWorldChecks.Enqueue(
                friend.ContentId);
        }

        crossWorldChecksTotal =
            pendingCrossWorldChecks.Count;

        PluginLog.Debug(
            "Queued {Count} cross-world friends for individual status checks.",
            crossWorldChecksTotal);

        PrintDebugChat(
            $"Queued {crossWorldChecksTotal} cross-world friends " +
            "for individual status checks.");
    }

    private void FinishCrossWorldCheckCycle()
    {
        var cycleWasManual =
            manualSyncInProgress;

        var queueWasBuilt =
            crossWorldQueueBuiltFromFriendList;

        var processedCount =
            crossWorldChecksProcessed;

        var sentCount =
            crossWorldChecksSent;

        var totalCount =
            crossWorldChecksTotal;

        var baselineCycleCompleted =
            crossWorldQueueBuiltFromFriendList &&
            crossWorldChecksSent == crossWorldChecksTotal;

        var shouldReportEmptySync =
            reportSyncProgressToChat &&
            crossWorldQueueBuiltFromFriendList &&
            crossWorldChecksTotal == 0;

        var shouldReportFailedSync =
            reportSyncProgressToChat &&
            !crossWorldQueueBuiltFromFriendList;

        var shouldReportStoppedSync =
            reportSyncProgressToChat &&
            crossWorldChecksTotal > 0 &&
            crossWorldChecksProcessed < crossWorldChecksTotal;

        CancelCrossWorldCheckCycle();

        if (shouldReportEmptySync)
        {
            ChatGui.Print(
                "Friend sync complete. No eligible cross-world friends were found.",
                "Friend Login");
        }
        else if (shouldReportFailedSync)
        {
            ChatGui.Print(
                "Friend sync could not read the Friend List.",
                "Friend Login");
        }
        else if (shouldReportStoppedSync)
        {
            ChatGui.Print(
                "Friend sync stopped before all cross-world friends were processed.",
                "Friend Login");
        }

        if (!queueWasBuilt)
        {
            PrintDebugChat(
                "ERROR: The cross-world polling cycle stopped because " +
                "the Friend List could not be read.");
        }
        else if (processedCount < totalCount)
        {
            PrintDebugChat(
                $"The cross-world polling cycle stopped after " +
                $"{processedCount}/{totalCount} friends were processed.");
        }
        else
        {
            var cycleName =
                cycleWasManual
                    ? "Manual cross-world sync"
                    : "Automatic all-world polling cycle";

            PrintDebugChat(
                $"{cycleName} finished. " +
                $"{sentCount}/{totalCount} requests were sent.");
        }

        if (!crossWorldBaselineReady &&
            baselineCycleCompleted)
        {
            crossWorldBaselineReadyAtUtc =
                DateTime.UtcNow + CrossWorldBaselineSettleDelay;
        }

        if (IsAutomaticPollingEnabled &&
            ClientState.IsLoggedIn)
        {
            ScheduleNextPoll();
        }
    }

    private void ReportSyncProgress()
    {
        if (!reportSyncProgressToChat ||
            crossWorldChecksTotal == 0 ||
            (crossWorldChecksProcessed % 10 != 0 &&
             crossWorldChecksProcessed != crossWorldChecksTotal))
        {
            return;
        }

        ChatGui.Print(
            $"Friend sync progress: {crossWorldChecksProcessed}/" +
            $"{crossWorldChecksTotal} processed.",
            "Friend Login");
    }

    private void CancelCrossWorldCheckCycle()
    {
        pendingCrossWorldChecks.Clear();
        crossWorldCycleActive = false;
        waitingToBuildCrossWorldQueue = false;
        crossWorldQueueBuiltFromFriendList = false;
        reportSyncProgressToChat = false;
        manualSyncInProgress = false;
        currentCycleRefreshesGeneralFriendList = false;
        crossWorldChecksTotal = 0;
        crossWorldChecksSent = 0;
        crossWorldChecksProcessed = 0;
        nextCrossWorldRequestAtUtc =
            DateTime.MaxValue;
    }

    private void ScheduleNextPoll()
    {
        var delaySeconds = random.Next(
            MinimumPollSeconds,
            MaximumPollSeconds + 1);

        nextPollAtUtc =
            DateTime.UtcNow.AddSeconds(delaySeconds);

        PluginLog.Debug(
            "Next automatic friend-list refresh scheduled in " +
            "{Seconds} seconds ({Minutes:F2} minutes).",
            delaySeconds,
            delaySeconds / 60.0);

        PrintDebugChat(
            $"Next automatic polling cycle scheduled in " +
            $"{delaySeconds / 60}m {delaySeconds % 60}s.");
    }

    private bool RequestFriendListRefresh()
    {
        try
        {
            if (!ClientState.IsLoggedIn)
            {
                return false;
            }

            var friendList =
                InfoProxyFriendList.Instance();

            if (friendList == null)
            {
                PluginLog.Warning(
                    "Could not request friend-list refresh: " +
                    "InfoProxyFriendList was null.");

                PrintDebugChat(
                    "ERROR: The general Friend List refresh could not start " +
                    "because the Friend List was unavailable.");

                return false;
            }

            var requestSent =
                friendList->RequestData();

            if (requestSent)
            {
                PluginLog.Debug(
                    "Automatic friend-list server refresh requested.");

                PrintDebugChat(
                    "General Friend List refresh request sent.");
            }
            else
            {
                PluginLog.Debug(
                    "Automatic friend-list refresh was not sent by the game.");

                PrintDebugChat(
                    "The game did not send the general Friend List " +
                    "refresh request.");
            }

            return requestSent;
        }
        catch (Exception exception)
        {
            PluginLog.Error(
                exception,
                "Failed to request automatic friend-list refresh.");

            PrintDebugChat(
                "ERROR: The general Friend List refresh failed.");

            return false;
        }
    }

    private void OnFriendListOpened(
        AddonEvent eventType,
        AddonArgs addonArgs)
    {
        if (!ClientState.IsLoggedIn ||
            !IsAutomaticPollingEnabled)
        {
            return;
        }

        var cycleWasActive =
            crossWorldCycleActive;

        CancelCrossWorldCheckCycle();

        if (cycleWasActive)
        {
            PrintDebugChat(
                "The active cross-world polling cycle was stopped because " +
                "the Friend List was opened.");
        }

        PluginLog.Debug(
            "Automatic polling timer restarted because " +
            "the Friend List was opened.");

        PrintDebugChat(
            "Automatic polling timer restarted because the Friend List " +
            "was opened.");

        ScheduleNextPoll();
    }

    private void OnFriendListUpdated(
        AddonEvent eventType,
        AddonArgs addonArgs)
    {
        TryScanFriendList();
    }

    private bool TryScanFriendList()
    {
        try
        {
            return ScanFriendList();
        }
        catch (Exception exception)
        {
            PluginLog.Error(
                exception,
                "Failed to read the FFXIV friends list.");

            PrintDebugChat(
                "ERROR: The locally stored Friend List could not be read.");

            return false;
        }
    }

    private bool ScanFriendList()
    {
        var friendList =
            InfoProxyFriendList.Instance();

        if (friendList == null ||
            friendList->EntryCount == 0)
        {
            return false;
        }

        var foundValidEntry = false;

        foreach (ref readonly var friend
                 in friendList->CharDataSpan)
        {
            var name =
                friend.NameString;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foundValidEntry = true;

            var key =
                $"{friend.HomeWorld}:{name}";

            var isOnline =
                (friend.State &
                 InfoProxyCommonList.CharacterData
                     .OnlineStatus.Online) != 0;

            if (baselineReady &&
                (!friend.IsOtherServer ||
                 crossWorldBaselineReady) &&
                friendStates.TryGetValue(
                    key,
                    out var wasOnline) &&
                !wasOnline &&
                isOnline)
            {
                PrintGreenMessage(
                    $"{name} has logged in.");
            }

            friendStates[key] = isOnline;
        }

        if (foundValidEntry)
        {
            baselineReady = true;
        }

        return foundValidEntry;
    }

    private static void PrintGreenMessage(
        string message)
    {
        var coloredMessage =
            new SeStringBuilder()
                .AddUiForeground(
                    message,
                    LoginColor)
                .Build();

        ChatGui.Print(
            coloredMessage,
            "Friend Login",
            LoginColor);
    }

    private string GetTrackingStatus()
    {
        return baselineReady
            ? $"Tracking {friendStates.Count} friend entries."
            : "Waiting for the initial friend-list baseline.";
    }

    private string GetPollingStatus()
    {
        if (crossWorldCycleActive)
        {
            var prefix =
                manualSyncInProgress
                    ? "Manual sync in progress. "
                    : "Automatic all-world polling is in progress. ";

            if (waitingToBuildCrossWorldQueue)
            {
                return
                    prefix +
                    "Refreshing the Friend List before checking " +
                    "cross-world friends.";
            }

            return
                prefix +
                (currentCycleRefreshesGeneralFriendList
                    ? "General Friend List refresh complete. "
                    : string.Empty) +
                $"Cross-world checks: {crossWorldChecksSent}/" +
                $"{crossWorldChecksTotal} sent.";
        }

        if (!IsAutomaticPollingEnabled)
        {
            return "Automatic polling is disabled.";
        }

        if (!ClientState.IsLoggedIn)
        {
            return CurrentAutomaticPollingMode ==
                   AutomaticPollingMode.AllWorlds
                ? "All-world automatic polling is enabled and will begin after login."
                : "Same-world automatic polling is enabled and will begin after login.";
        }

        if (nextPollAtUtc == DateTime.MaxValue)
        {
            return CurrentAutomaticPollingMode ==
                   AutomaticPollingMode.AllWorlds
                ? "All-world automatic polling is enabled and waiting to be scheduled."
                : "Same-world automatic polling is enabled and waiting to be scheduled.";
        }

        var secondsRemaining =
            (int)Math.Max(
                0,
                Math.Ceiling(
                    (nextPollAtUtc - DateTime.UtcNow)
                    .TotalSeconds));

        var minutes = secondsRemaining / 60;
        var seconds = secondsRemaining % 60;

        var modeName =
            CurrentAutomaticPollingMode ==
            AutomaticPollingMode.AllWorlds
                ? "All-world"
                : "Same-world";

        return
            $"{modeName} automatic polling is enabled. " +
            $"Next refresh in approximately {minutes}m {seconds}s.";
    }

    private void OnLogin()
    {
        ResetTracking();
        CancelCrossWorldCheckCycle();

        PrintDebugChat(
            "Character login detected. Friend tracking was reset.");

        if (changelogPendingAfterLogin)
        {
            changelogEligibleAtUtc =
                DateTime.UtcNow + ChangelogLoginDelay;
        }

        nextLocalScanAtUtc =
            DateTime.UtcNow.AddSeconds(2);

        if (IsAutomaticPollingEnabled)
        {
            ScheduleNextPoll();
        }
        else
        {
            nextPollAtUtc =
                DateTime.MaxValue;
        }
    }

    private void OnLogout(
        int type,
        int code)
    {
        PrintDebugChat(
            "Logout detected. Active polling and timers were stopped.");

        ResetTracking();
        CancelCrossWorldCheckCycle();

        changelogWindowVisible = false;
        changelogEligibleAtUtc = DateTime.MaxValue;

        nextPollAtUtc =
            DateTime.MaxValue;

        nextLocalScanAtUtc =
            DateTime.MaxValue;
    }

    private void ResetTracking()
    {
        friendStates.Clear();
        baselineReady = false;
        crossWorldBaselineReady = false;
        crossWorldQueueBuiltFromFriendList = false;
        crossWorldBaselineReadyAtUtc = DateTime.MaxValue;
    }
}
