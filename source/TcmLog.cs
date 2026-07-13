using Vintagestory.API.Common;

namespace AlmanacTcm;

/// <summary>
/// Categorized debug logging, verbose and ON by default (Almanac convention).
/// Category lines gate on <see cref="Verbose"/> (set from global config at startup);
/// warnings and errors always emit.
/// </summary>
public static class TcmLog
{
    private const string Prefix = "almanac:tcm";

    public const string Ledger = "ledger";
    public const string Consolidation = "consolidation";
    public const string Hooks = "hooks";
    public const string Affinity = "affinity";
    public const string Config = "config";

    public static bool Verbose = true;

    public static void Cat(ICoreAPI api, string category, string message)
    {
        if (!Verbose) return;
        api.Logger.Notification($"[{Prefix}:{category}] {message}");
    }

    public static void Info(ICoreAPI api, string message)
        => api.Logger.Notification($"[{Prefix}] {message}");

    public static void Warn(ICoreAPI api, string message)
        => api.Logger.Warning($"[{Prefix}] {message}");

    public static void Error(ICoreAPI api, string message)
        => api.Logger.Error($"[{Prefix}] {message}");
}
