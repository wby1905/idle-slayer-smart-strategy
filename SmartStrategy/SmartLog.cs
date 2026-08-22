namespace SmartStrategy;

internal enum SmartLogCategory
{
    Core,
    Settings,
    Quest,
    Material,
    Portal,
    Combat,
    Event,
    Endgame,
    Planning,
    Compatibility,
    Runtime
}

internal static class SmartLog
{
    internal static void Info(SmartLogCategory category, string message) =>
        Plugin.Log.Msg(Format(category, message));

    internal static void Debug(SmartLogCategory category, string message)
    {
        if (Plugin.Config?.DebugLogging.Value == true)
            Plugin.Log.Msg(Format(category, $"DEBUG: {message}"));
    }

    internal static void Warning(SmartLogCategory category, string message) =>
        Plugin.Log.Warning(Format(category, message));

    internal static void Error(SmartLogCategory category, string message) =>
        Plugin.Log.Error(Format(category, message));

    private static string Format(SmartLogCategory category, string message) =>
        $"[{category}] {message}";
}
