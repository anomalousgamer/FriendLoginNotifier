# Friend Login Notifier

Friend Login Notifier is a custom Dalamud plugin for Final Fantasy XIV that prints a bright-green chat notification when a character on your in-game Friends List changes from offline to online.

The plugin supports passive monitoring, optional automatic Friend List polling, manual synchronization, cross-world status checks, an in-game settings window, and optional debug messages.

## Features

* Monitors the Friends List information already stored by the game.
* Prints a bright-green chat message when a friend changes from offline to online.
* Supports friends on the same world and friends marked by the game as being on another world.
* Provides separate automatic polling modes for same-world friends and all worlds.
* Provides separate manual actions for syncing all friends, only same-world friends, or only cross-world friends.
* Reports the remaining time until the next automatic poll on request.
* Uses an initial cross-world baseline to prevent false login notifications when the plugin first loads.
* Includes an in-game settings window, current status information, a test notification, and a per-version changelog.
* Includes optional chat diagnostics for polling activity and errors.
* Does not permanently store friend Content IDs or account identifiers.

## Monitoring and Polling Modes

### Passive monitoring

Passive monitoring is always active while the plugin is enabled. It reads Friend List information already stored locally by the game and does not create automatic server requests.

If the game naturally updates a friend's status from offline to online, the plugin prints the login notification. Cross-world status information may not update naturally until the game requests it.

### Same-world automatic polling

This mode requests a general Friend List refresh after a newly randomized delay between 4 and 9 minutes.

It does not send individual cross-world friend status requests. After the general refresh is requested, a new 4–9 minute timer is selected.

### All-world automatic polling

This mode performs the same general Friend List refresh and then checks eligible cross-world friends individually.

* A new delay of exactly 3, 4, or 5 seconds is randomly selected between every individual cross-world request.
* After the final cross-world friend is processed, a new randomized 4–9 minute polling timer begins.
* Opening the Friend List manually restarts the automatic polling timer.

Both automatic polling modes are disabled by default and are mutually exclusive. Enabling either mode displays a confirmation warning in the settings window.

## Manual Synchronization

Manual synchronization is available regardless of which automatic polling mode is selected:

* **Sync All Friends** requests a general Friend List refresh and then individually checks friends on other worlds.
* **Sync Friends on Same World** requests only the general Friend List refresh and does not send individual cross-world checks.
* **Sync Friends on Other Worlds** individually checks only friends marked by the game as being on another world.

The same actions are available through `/friends sync`, `/friends syncworld`, and `/friends syncother`.

## Risk Warning

Automatic polling and manual synchronization send Friend List requests to the FFXIV servers. These requests may be visible in Square Enix's server logs. No one can guarantee whether automated requests will be detected or acted upon. Enable and use these features at your own risk.

Friend Login Notifier is a third-party plugin. It is not affiliated with or endorsed by Square Enix, XIVLauncher, or Dalamud.

## Commands

| Command              | Description                                                                |
| -------------------- | -------------------------------------------------------------------------- |
| `/friends`           | Opens the settings window.                                                 |
| `/friends help`      | Prints the complete command list in chat.                                  |
| `/friends time`      | Shows the time remaining until the next automatic poll.                    |
| `/friends changes`   | Opens the current version's changelog.                                     |
| `/friends test`      | Sends a test login notification.                                           |
| `/friends sync`      | Refreshes the general Friend List and then checks friends on other worlds. |
| `/friends syncworld` | Refreshes the general Friend List without individual cross-world checks.   |
| `/friends syncother` | Checks only friends on other worlds.                                       |
| `/friends debug on`  | Enables plugin activity and error messages in chat.                        |
| `/friends debug off` | Disables debug messages in chat.                                           |

Command-driven cross-world syncs report progress after every 10 friends and once more after the final friend. The settings-window buttons display their progress in the settings window unless debug chat is enabled.

## Debug Chat

Debug chat is disabled by default. It can be enabled from the settings window or with `/friends debug on`.

When enabled, it reports:

* Polling modes being enabled or disabled.
* Polling cycles starting, finishing, or being interrupted.
* General Friend List refresh results.
* Individual cross-world requests and their randomized delays.
* Newly scheduled automatic polling timers.
* Friend List reading and request errors.

Because it reports individual cross-world activity, debug mode can produce many chat messages during a large sync.

## Installation

Friend Login Notifier is distributed through a custom Dalamud repository and is not listed in the official Dalamud plugin repository.

1. Launch FFXIV through XIVLauncher.

2. Open Dalamud Settings with `/xlsettings`.

3. Open the **Experimental** section.

4. Add the following address under **Custom Plugin Repositories**:

   ```text
   https://raw.githubusercontent.com/anomalousgamer/FriendLoginNotifier/master/repo.json
   ```

5. Save and close Dalamud Settings.

6. Open the Plugin Installer with `/xlplugins`.

7. Search for **Friend Login Notifier** and install it.

Updates published through the same custom repository will appear in Dalamud's normal plugin update system.

## Settings

Open the settings window with `/friends`, the plugin installer's **Settings** button, or its **Open** button.

The settings window allows you to:

* Enable or disable debug messages in chat.
* Select same-world or all-world automatic polling.
* View current tracking and polling status.
* Send a test notification.
* Open the changelog.
* Manually sync all friends.
* Manually sync friends on the same world.
* Manually sync only friends on other worlds.

## Changelog Behavior

After an update, the changelog waits until a character is logged in and loaded before appearing. Selecting **Don't show again for this version** prevents it from opening automatically again for that version. It can always be reopened with `/friends changes`.

## Building from Source

Requirements:

* Final Fantasy XIV and XIVLauncher with Dalamud installed.
* Visual Studio with the .NET desktop development tools.
* The .NET 10 SDK.

Open the solution in Visual Studio, select either **Debug x64** for local testing or **Release x64** for packaging, and build the solution.

## License

See [LICENSE.md](LICENSE.md) for license information.


## Development disclosure

This plugin was created with substantial AI assistance and manually tested in-game as someone learning how the process works. This plugin is not intended for public use, and is for private testing while the creator learns how to build plugins.
