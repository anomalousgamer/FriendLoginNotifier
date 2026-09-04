namespace FriendLoginNotifier;

internal static class Changelog
{
    public const string Latest =
        "---------- Hotfix — Version 1.1.1.0 ----------\n\n" +
        "• Fixed all-world tracking so same-Home-World friends who move to or log in on another world can also be checked.\n\n" +
        "• A friend moving from your current world to another world is now reported as a new login, including when the change is detected during the same polling cycle.\n\n" +
        "• Login notifications now include the friend's current world whenever FFXIV provides a valid current-world value.\n\n" +
        "---------- Major Update — Version 1.1.0.0 ----------\n\n" +
        "• Added separate automatic polling modes for same-world friends and all worlds. Automatic polling remains disabled by default.\n\n" +
        "• Same-world polling refreshes the general Friend List after a newly randomized delay between 4 and 9 minutes.\n\n" +
        "• All-world polling also checks cross-world friends individually, using a newly randomized delay of 3, 4, or 5 seconds between requests. The next 4–9 minute timer begins after the final check finishes.\n\n" +
        "• Added an initial cross-world baseline to prevent false login notifications when the plugin first loads.\n\n" +
        "• Added separate buttons and commands for syncing all friends, only same-world friends, or only friends on other worlds. Command-driven cross-world syncs report progress every 10 friends.\n\n" +
        "• Replaced the old command with /friends and added help, time, changes, test, sync, syncworld, syncother, debug on, and debug off subcommands.\n\n" +
        "• Added /friends time to show the remaining time until the next automatic poll in either polling mode.\n\n" +
        "• Added optional debug messages in chat for polling activity, timers, individual requests, completed cycles, and errors.\n\n" +
        "• Expanded the settings window with polling-mode explanations, status information, synchronized checkboxes, and risk confirmation.\n\n" +
        "• Opening the Friend List manually now restarts the randomized automatic polling timer.\n\n" +
        "• Added a per-version changelog window with a Don't show again for this version option.\n\n" +
        "• Improved plugin icon support after installation.";
}
