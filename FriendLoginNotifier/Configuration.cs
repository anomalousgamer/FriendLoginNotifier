using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FriendLoginNotifier;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool SameWorldPollingEnabled { get; set; } = false;
    public bool AllWorldPollingEnabled { get; set; } = false;
    public bool DebugChatEnabled { get; set; } = false;
    public bool AutomaticPollingEnabled { get; set; } = false;
    public string LastAcknowledgedVersion { get; set; } = string.Empty;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(
        IDalamudPluginInterface interfaceInstance)
    {
        pluginInterface = interfaceInstance;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
