using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace AlmanacTcm.Engine;

/// <summary>
/// The Almanac chat channel (0.4.22, RULED 2026-07-31): practice lines get their own tab,
/// built the way vanilla builds the damage log. The damage log is not a real player group;
/// the client injects a fake entry with a negative id into OwnPlayerGroupsById and the tab
/// simply exists. We do the same with our own id, then take over message delivery for that
/// id so the ticks stay quiet.
///
/// Why the takeover: vanilla's OnNewServerToClientChatLine hardcodes its quiet-group
/// exclusions to the damage log and server info. Any other group gets the full treatment on
/// every line: the tab alarm glow, the lastActivityMs bump that fades the chat HUD back in,
/// and (for Notification lines under AutoChatOpenSelected) the active-tab switch. Practice
/// ticks fire constantly, which made all three ruinous on the Info tab this replaces. Our
/// prefix appends to the channel's history and repaints when the tab is active, nothing
/// else, and skips the original. Every other group flows through vanilla untouched.
///
/// A client without this mod has no tab: lines to an unknown group land in a history nobody
/// renders, and vanilla's own tabIndex guard (-1) means no switch and no alarm. Nothing
/// errors, and on a TCM server every client has the mod anyway.
/// </summary>
public static class AlmanacChatChannel
{
    /// <summary>Far from vanilla's -1..-6 band so no future vanilla group collides.</summary>
    public const int GroupId = -7788;

    private const string HudType = "Vintagestory.Client.NoObf.HudDialogChat";

    public static void PatchClient(ICoreAPI api, Harmony harmony)
    {
        if (api is not ICoreClientAPI) return;

        var hud = AccessTools.TypeByName(HudType);
        var compose = hud == null ? null : AccessTools.DeclaredMethod(hud, "ComposeChatGuis");
        var newLine = hud == null ? null : AccessTools.DeclaredMethod(hud, "OnNewServerToClientChatLine");
        if (compose == null || newLine == null)
        {
            TcmLog.Warn(api, "chat dialog seams not found (ComposeChatGuis/OnNewServerToClientChatLine); Almanac tab inactive, practice lines invisible");
            return;
        }

        harmony.Patch(compose, prefix: new HarmonyMethod(AccessTools.Method(typeof(AlmanacChatChannel), nameof(ComposePrefix))));
        harmony.Patch(newLine, prefix: new HarmonyMethod(AccessTools.Method(typeof(AlmanacChatChannel), nameof(ChatLinePrefix))));
        TcmLog.Info(api, "Almanac chat channel live (damage-log pattern: no focus steal, no alarm, no HUD wake)");
    }

    private static Traverse Member(Traverse t, string name)
    {
        var p = t.Property(name);
        return p.PropertyExists() ? p : t.Field(name);
    }

    /// <summary>Every recompose guarantees the tab: the groups packet clears and rebuilds the
    /// dict (and purges unknown histories) before composing, so injecting here survives it.
    /// PlayerGroup is Vintagestory.API.Server.PlayerGroup (VSAPI, referenced), NOT a Lib
    /// type; 0.4.22 guessed two wrong namespaces by reflection and the injection silently
    /// no-opped, which is exactly why this is typed now: a wrong name fails the build, not
    /// the player.</summary>
    public static void ComposePrefix(object __instance)
    {
        var game = Member(Traverse.Create(__instance), "game").GetValue();
        if (game == null) return;
        if (Member(Traverse.Create(game), "OwnPlayerGroupsById").GetValue() is not System.Collections.IDictionary groups
            || groups.Contains(GroupId)) return;

        groups[GroupId] = new Vintagestory.API.Server.PlayerGroup
        {
            Uid = GroupId,
            Name = Lang.Get("almanactcm:chattab-almanac"),
        };
    }

    /// <summary>The quiet delivery: history + repaint-if-active, nothing else. Fails open to
    /// vanilla handling so a surprise in the internals costs quietness, never the line.</summary>
    public static bool ChatLinePrefix(object __instance, int groupId, string message)
    {
        if (groupId != GroupId) return true;
        try
        {
            var t = Traverse.Create(__instance);
            var game = Member(t, "game").GetValue();
            if (game == null) return true;

            var gt = Traverse.Create(game);
            if (Member(gt, "ChatHistoryByPlayerGroup").GetValue() is not System.Collections.IDictionary history)
                return true;
            if (history[GroupId] is not LimitedList<string> list)
            {
                int max = t.Field("historyMax").GetValue<int>();
                history[GroupId] = list = new LimitedList<string>(max > 0 ? max : 30);
            }
            // The muted info tone vanilla gives Notification lines; we skip that path.
            list.Add("<font color=\"#CCe0cfbb\">" + message + "</font>");

            if (Member(gt, "currentGroupid").GetValue<int>() == GroupId)
                t.Method("UpdateText").GetValue();
            return false;
        }
        catch
        {
            return true;
        }
    }
}
