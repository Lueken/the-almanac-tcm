using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using AlmanacTcm.Domains;
using AlmanacTcm.Leveling;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace AlmanacTcm.Gui;

/// <summary>A normalised alloy the ledger renders, from either the vanilla alloy list or
/// industrialstory's CrucibleAlloyRecipe registry. UnitsPerItem is 0 when the source does not
/// carry it (vanilla); Catalysts is empty unless the source lists required flux items.</summary>
public sealed class LedgerAlloy
{
    public string Output = "?";
    public Component[] Metals = Array.Empty<Component>();
    public Catalyst[] Catalysts = Array.Empty<Catalyst>();

    public struct Component { public string Name; public float Min; public float Max; public int UnitsPerItem; }
    public struct Catalyst { public string Name; public int Quantity; }
}

/// <summary>Attaches the Alloy Ledger to a firepit that holds a crucible (the vanilla alloying
/// window). Rides open with it, closes with it. See <see cref="AlloyLedgerBrickFurnacePatch"/>
/// for industrialstory's brick furnace, the other place a crucible is heated.</summary>
[HarmonyPatch(typeof(GuiDialogBlockEntityFirepit), nameof(GuiDialogBlockEntityFirepit.OnGuiOpened))]
public static class FirepitLedgerOpenPatch
{
    public static void Postfix(GuiDialogBlockEntity __instance)
    {
        if (!GuiDialogAlloyLedger.HasCrucible(__instance.Inventory)) return;
        AlmanacTcmModSystem.Instance?.AlloyLedger?.AttachTo(__instance);   // self-gates on config/Master
    }
}

[HarmonyPatch(typeof(GuiDialogBlockEntityFirepit), nameof(GuiDialogBlockEntityFirepit.OnGuiClosed))]
public static class FirepitLedgerClosePatch
{
    public static void Postfix(GuiDialogBlockEntity __instance) =>
        AlmanacTcmModSystem.Instance?.AlloyLedger?.Detach(__instance);
}

/// <summary>Attaches the ledger to industrialstory's brick furnace (the other window a crucible
/// is heated in). GuiDialogBrickFurnace shares GuiDialogBlockEntity as a base but does not
/// override OnGuiOpened, so we patch the base and guard to the furnace type. Conditional: only
/// registered when industrialstory is present, so vanilla installs never touch it.</summary>
public static class AlloyLedgerBrickFurnacePatch
{
    private static Type? brickFurnaceType;

    public static void Register(ICoreAPI api, Harmony harmony)
    {
        if (!api.ModLoader.IsModEnabled("industrialstory")) return;
        brickFurnaceType = AccessTools.TypeByName("IndustrialStory.GuiDialogBrickFurnace");
        if (brickFurnaceType == null)
        {
            TcmLog.Warn(api, "industrialstory present but GuiDialogBrickFurnace not found; ledger not attached to the brick furnace");
            return;
        }
        var onOpened = AccessTools.Method(typeof(GuiDialogBlockEntity), "OnGuiOpened");
        var onClosed = AccessTools.Method(typeof(GuiDialogBlockEntity), "OnGuiClosed");
        if (onOpened == null || onClosed == null) return;
        harmony.Patch(onOpened, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlloyLedgerBrickFurnacePatch), nameof(OpenPostfix))));
        harmony.Patch(onClosed, postfix: new HarmonyMethod(AccessTools.Method(typeof(AlloyLedgerBrickFurnacePatch), nameof(ClosePostfix))));
        TcmLog.Info(api, "alloy ledger attached to the industrialstory brick furnace");
    }

    public static void OpenPostfix(GuiDialogBlockEntity __instance)
    {
        if (brickFurnaceType?.IsInstanceOfType(__instance) != true) return;
        if (!GuiDialogAlloyLedger.HasCrucible(__instance.Inventory)) return;
        AlmanacTcmModSystem.Instance?.AlloyLedger?.AttachTo(__instance);
    }

    public static void ClosePostfix(GuiDialogBlockEntity __instance)
    {
        if (brickFurnaceType?.IsInstanceOfType(__instance) != true) return;
        AlmanacTcmModSystem.Instance?.AlloyLedger?.Detach(__instance);
    }
}

/// <summary>
/// The Alloy Ledger (rank-bonus-design.md §162 Axis 4, Master unlock). A read-only aid a Master
/// smith "just knows": pick an alloy and a desired ingot count and it reads out the metal amounts
/// for the ratio ranges, the leanest valid mix, and any required catalysts. Its data comes from
/// whatever alloy system is live: industrialstory's CrucibleAlloyRecipe registry when that mod is
/// present (it replaces vanilla alloys), otherwise the vanilla alloy list. It automates nothing
/// and multiplies no yield. Client-side; opens attached to a crucible-bearing firepit or brick
/// furnace, gated to Master MET (server-owned toggle).
/// </summary>
public class GuiDialogAlloyLedger : GuiDialog
{
    public override string ToggleKeyCombinationCode => "tcmalloyledger";

    // Rendered above the station window it welds to, so its OnRenderGUI reads the station's
    // freshly-positioned bounds this frame rather than last frame's.
    public override double DrawOrder => 0.3;

    private List<LedgerAlloy> alloys = new();
    private bool built;
    private int selected;
    private int ingots = 1;

    /// <summary>The floaty station window (firepit / brick furnace) this ledger is welded to,
    /// or null when nothing is attached. Drives both lifetime and position.</summary>
    private GuiDialogBlockEntity? station;

    /// <summary>Collapsed = just the tab welded to the station's left edge; expanded = the full
    /// calculator panel unfurls to the left of the tab.</summary>
    private bool expanded;

    public GuiDialogAlloyLedger(ICoreClientAPI capi) : base(capi) { }

    /// <summary>True if any slot of the given inventory holds a crucible (the vanilla smelting
    /// container). Shared by both the firepit and brick furnace hooks.</summary>
    public static bool HasCrucible(InventoryBase? inv)
    {
        if (inv == null) return false;
        foreach (ItemSlot slot in inv)
            if (slot?.Itemstack?.Collectible is BlockSmeltingContainer) return true;
        return false;
    }

    /// <summary>Weld the ledger (collapsed to its tab) to a station window that holds a crucible.
    /// Server-owned Master gate: when Master-only and the viewer is not a Master, no tab appears
    /// (silent). Called from the firepit / brick furnace open hooks.</summary>
    public void AttachTo(GuiDialogBlockEntity stationDlg)
    {
        bool masterOnly = AlmanacTcmModSystem.Instance?.AlloyLedgerMasterOnly ?? true;
        if (masterOnly && !IsMaster()) return;
        station = stationDlg;
        expanded = false;
        EnsureAlloys();
        Compose();
        if (!IsOpened()) TryOpen();
    }

    /// <summary>Unweld when that station window closes. Ignores a stale call from a different
    /// station (e.g. one closing after another already took over).</summary>
    public void Detach(GuiDialogBlockEntity stationDlg)
    {
        if (station != stationDlg) return;
        station = null;
        if (IsOpened()) TryClose();
    }

    /// <summary>Follow the floaty station each frame: weld this dialog's RIGHT edge to the
    /// station window's LEFT edge (tops aligned). The station sets its own absFixedX/Y earlier
    /// this frame; a higher DrawOrder means we read the fresh values. Closes if the station went.</summary>
    public override void OnRenderGUI(float deltaTime)
    {
        var sc = station?.SingleComposer;
        if (sc == null)
        {
            if (IsOpened()) TryClose();
            return;
        }
        ElementBounds sb = sc.Bounds;
        ElementBounds mb = SingleComposer.Bounds;
        mb.Alignment = EnumDialogArea.None;
        mb.fixedOffsetX = 0;
        mb.fixedOffsetY = 0;
        mb.absFixedX = sb.absFixedX - mb.OuterWidth;   // right edge against the station's left edge
        mb.absFixedY = sb.absFixedY;                   // tops aligned
        mb.absMarginX = 0;
        mb.absMarginY = 0;
        base.OnRenderGUI(deltaTime);
    }

    private void EnsureAlloys()
    {
        // Retry while empty: industrialstory's registry may not have synced to the client the
        // first time a station is opened, so a zero-length read is not cached.
        if (built && alloys.Count > 0) return;
        built = true;
        alloys = BuildAlloys(capi);
        if (selected >= alloys.Count) selected = 0;
    }

    // ---------------------------------------------------------------- data source

    /// <summary>Gather alloys from the live system. Industrialstory replaces the vanilla alloys
    /// with its own CrucibleAlloyRecipe registry (and clears the vanilla list), so when it is
    /// present we read that (by reflection, to keep it an optional dependency); otherwise vanilla.</summary>
    private static List<LedgerAlloy> BuildAlloys(ICoreClientAPI capi)
    {
        if (capi.ModLoader.IsModEnabled("industrialstory"))
        {
            var fromIs = TryBuildFromIndustrialStory(capi);
            if (fromIs is { Count: > 0 }) return Sort(fromIs);
        }
        return Sort(BuildFromVanilla(capi));
    }

    private static List<LedgerAlloy> Sort(List<LedgerAlloy> list)
    {
        list.Sort((x, y) => string.Compare(x.Output, y.Output, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static List<LedgerAlloy> BuildFromVanilla(ICoreClientAPI capi)
    {
        var result = new List<LedgerAlloy>();
        foreach (AlloyRecipe a in capi.GetMetalAlloys() ?? new List<AlloyRecipe>())
        {
            if (a is not { Enabled: true, Ingredients.Length: > 0 }) continue;
            var outStack = a.Output?.ResolvedItemstack;
            if (outStack?.Collectible == null) continue;
            result.Add(new LedgerAlloy
            {
                Output = outStack.GetName(),
                Metals = a.Ingredients.Select(ing => new LedgerAlloy.Component
                {
                    Name = MetalName(ing.ResolvedItemstack?.Collectible?.Code),
                    Min = ing.MinRatio,
                    Max = ing.MaxRatio,
                    UnitsPerItem = 0,
                }).ToArray(),
            });
        }
        return result;
    }

    /// <summary>Reflect industrialstory's IndustrialStoryModSystem.CrucibleAlloyRegistry.Recipes.
    /// Any shape mismatch (a mod update) fails soft and falls back to vanilla.</summary>
    private static List<LedgerAlloy>? TryBuildFromIndustrialStory(ICoreClientAPI capi)
    {
        try
        {
            var mod = capi.ModLoader.GetModSystem("IndustrialStory.IndustrialStoryModSystem");
            if (mod == null)
            {
                // Fall back to a name scan in case GetModSystem's full-name match differs.
                foreach (var s in capi.ModLoader.Systems)
                    if (s.GetType().Name == "IndustrialStoryModSystem") { mod = s; break; }
            }
            if (mod == null) { TcmLog.Warn(capi, "alloy ledger: IndustrialStoryModSystem not found"); return null; }

            var registry = AccessTools.Property(mod.GetType(), "CrucibleAlloyRegistry")?.GetValue(mod);
            if (registry == null) { TcmLog.Warn(capi, "alloy ledger: CrucibleAlloyRegistry is null (not synced yet?)"); return null; }
            if (AccessTools.Property(registry.GetType(), "Recipes")?.GetValue(registry) is not IEnumerable recipes)
            { TcmLog.Warn(capi, "alloy ledger: registry has no Recipes property"); return null; }

            var result = new List<LedgerAlloy>();
            PropertyInfo? pEnabled = null, pOutput = null, pMetals = null, pCats = null;
            PropertyInfo? pMin = null, pMax = null, pUpi = null, pMetalColl = null;
            PropertyInfo? pQty = null, pCatColl = null;

            foreach (var r in recipes)
            {
                if (r == null) continue;
                Type rt = r.GetType();
                pEnabled ??= AccessTools.Property(rt, "Enabled");
                pOutput ??= AccessTools.Property(rt, "Output");
                pMetals ??= AccessTools.Property(rt, "MetalIngredients");
                pCats ??= AccessTools.Property(rt, "Catalysts");

                if (pEnabled?.GetValue(r) is bool en && !en) continue;
                if (pOutput?.GetValue(r) is not { } outObj) continue;
                if (AccessTools.Property(outObj.GetType(), "ResolvedItemstack")?.GetValue(outObj) is not ItemStack outStack || outStack.Collectible == null) continue;

                var metals = new List<LedgerAlloy.Component>();
                if (pMetals?.GetValue(r) is Array marr)
                {
                    foreach (var m in marr)
                    {
                        if (m == null) continue;
                        Type mt = m.GetType();
                        pMin ??= AccessTools.Property(mt, "MinRatio");
                        pMax ??= AccessTools.Property(mt, "MaxRatio");
                        pUpi ??= AccessTools.Property(mt, "UnitsPerItem");
                        pMetalColl ??= AccessTools.Property(mt, "ResolvedCollectible");
                        var coll = pMetalColl?.GetValue(m) as CollectibleObject;
                        metals.Add(new LedgerAlloy.Component
                        {
                            Name = MetalName(coll?.Code),
                            Min = pMin?.GetValue(m) is float mn ? mn : 0f,
                            Max = pMax?.GetValue(m) is float mx ? mx : 0f,
                            UnitsPerItem = pUpi?.GetValue(m) is int u ? u : 0,
                        });
                    }
                }
                if (metals.Count == 0) continue;

                var cats = new List<LedgerAlloy.Catalyst>();
                if (pCats?.GetValue(r) is Array carr)
                {
                    foreach (var c in carr)
                    {
                        if (c == null) continue;
                        Type ct = c.GetType();
                        pQty ??= AccessTools.Property(ct, "Quantity");
                        pCatColl ??= AccessTools.Property(ct, "ResolvedCollectible");
                        var coll = pCatColl?.GetValue(c) as CollectibleObject;
                        cats.Add(new LedgerAlloy.Catalyst
                        {
                            Name = MetalName(coll?.Code),
                            Quantity = pQty?.GetValue(c) is int q ? q : 1,
                        });
                    }
                }

                result.Add(new LedgerAlloy { Output = outStack.GetName(), Metals = metals.ToArray(), Catalysts = cats.ToArray() });
            }
            TcmLog.Info(capi, $"alloy ledger: read {result.Count} industrialstory alloys");
            return result;
        }
        catch (Exception e)
        {
            TcmLog.Warn(capi, $"alloy ledger: industrialstory registry read failed ({e.Message}); using vanilla alloys");
            return null;
        }
    }

    // -------------------------------------------------------------------- gui

    private void ToggleExpanded()
    {
        expanded = !expanded;
        Compose();
    }

    /// <summary>Rebuild the composer for the current state. Collapsed = a lone tab welded to the
    /// station's left edge; expanded = the calculator panel unfurled to the LEFT of that tab. The
    /// tab is always the rightmost element, so OnRenderGUI (which welds the composer's right edge
    /// to the station) keeps it flush against the window in both states.</summary>
    private void Compose()
    {
        CairoFont font = CairoFont.WhiteDetailText();
        const int tabW = 58, tabH = 30, panelW = 320, gap = 6;

        var tabBounds = ElementBounds.Fixed(expanded ? panelW + gap : 0, 0, tabW, tabH);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        // Position is driven every frame by OnRenderGUI, so the composed alignment is irrelevant.
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None);

        var compo = capi.Gui.CreateCompo("tcmalloyledger", dialogBounds)
            .AddShadedDialogBG(bgBounds, false)
            .BeginChildElements(bgBounds);

        if (expanded)
        {
            var ddBounds = ElementBounds.Fixed(0, 4, panelW - 4, 28);
            var lblBounds = ElementBounds.Fixed(0, 40, 150, 28);
            var numBounds = ElementBounds.Fixed(150, 38, 90, 28);
            var txtBounds = ElementBounds.Fixed(0, 80, panelW - 4, 320);
            bgBounds.WithChildren(ddBounds, lblBounds, numBounds, txtBounds, tabBounds);

            string[] codes = alloys.Select((_, i) => i.ToString()).ToArray();
            string[] names = alloys.Select(a => a.Output).ToArray();
            if (codes.Length == 0) { codes = new[] { "0" }; names = new[] { Lang.Get("almanactcm:alloy-none") }; }

            compo.AddDropDown(codes, names, selected, OnAlloySelected, ddBounds, "alloy")
                 .AddStaticText(Lang.Get("almanactcm:alloy-ingots"), font, lblBounds)
                 .AddNumberInput(numBounds, OnCountChanged, font, "count")
                 .AddDynamicText(BuildReadout(), font, txtBounds, "readout");
        }
        else
        {
            bgBounds.WithChildren(tabBounds);
        }

        // Defer the recompose off the button's own click event to avoid disposing the element
        // that is mid-firing (reentrancy).
        compo.AddToggleButton(Lang.Get("almanactcm:alloy-tab"), font,
            _ => capi.Event.EnqueueMainThreadTask(ToggleExpanded, "tcmalloytoggle"), tabBounds, "tab");

        SingleComposer = compo.EndChildElements().Compose();
        SingleComposer.GetToggleButton("tab")?.SetValue(expanded);
        if (expanded) SingleComposer.GetNumberInput("count").SetValue(ingots.ToString());
    }

    private void OnAlloySelected(string code, bool selectedNow)
    {
        if (int.TryParse(code, out int i) && i >= 0 && i < alloys.Count) selected = i;
        RefreshReadout();
    }

    private void OnCountChanged(string text)
    {
        if (int.TryParse(text, out int n)) ingots = Math.Clamp(n, 1, 999);
        RefreshReadout();
    }

    private void RefreshReadout() => SingleComposer?.GetDynamicText("readout")?.SetNewText(BuildReadout());

    /// <summary>The ledger body: per-metal unit ranges (and piece counts when known), the leanest
    /// valid mix, any required catalysts, and the units-per-ingot reminder. No dashes (voice rule);
    /// ranges read "X to Y".</summary>
    private string BuildReadout()
    {
        if (alloys.Count == 0) return Lang.Get("almanactcm:alloy-none");
        LedgerAlloy a = alloys[Math.Clamp(selected, 0, alloys.Count - 1)];
        int total = ingots * 100;   // 100 units = 1 ingot
        int n = a.Metals.Length;

        int[] minU = new int[n], maxU = new int[n];
        int baseIdx = 0; float baseMax = -1f;
        for (int i = 0; i < n; i++)
        {
            minU[i] = (int)Math.Round(total * a.Metals[i].Min);
            maxU[i] = (int)Math.Round(total * a.Metals[i].Max);
            if (a.Metals[i].Max > baseMax) { baseMax = a.Metals[i].Max; baseIdx = i; }
        }

        var sb = new StringBuilder();
        sb.Append(a.Output).Append(',').Append(' ')
          .Append(ingots).Append(ingots == 1 ? " ingot (" : " ingots (").Append(total).Append(" units)\n\n");

        for (int i = 0; i < n; i++)
        {
            sb.Append(a.Metals[i].Name).Append(": ").Append(minU[i]).Append(" to ").Append(maxU[i]).Append(" units");
            int upi = a.Metals[i].UnitsPerItem;
            if (upi > 0)
                sb.Append(" (").Append(minU[i] / upi).Append(" to ").Append(maxU[i] / upi).Append(" pieces)");
            if (i == baseIdx) sb.Append("  (base)");
            sb.Append('\n');
        }

        // Leanest mix: every metal at its floor, the leftover poured into the base metal.
        int[] lean = (int[])minU.Clone();
        lean[baseIdx] += total - lean.Sum();
        sb.Append('\n').Append(Lang.Get("almanactcm:alloy-leanest")).Append(' ');
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(a.Metals[i].Name).Append(' ').Append(lean[i]);
        }

        if (a.Catalysts.Length > 0)
        {
            sb.Append('\n').Append('\n').Append(Lang.Get("almanactcm:alloy-catalysts")).Append(' ');
            for (int i = 0; i < a.Catalysts.Length; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(a.Catalysts[i].Name);
                if (a.Catalysts[i].Quantity > 1) sb.Append(" x").Append(a.Catalysts[i].Quantity);
            }
        }

        sb.Append("\n\n").Append(Lang.Get("almanactcm:alloy-footnote"));
        return sb.ToString();
    }

    /// <summary>Clean metal/item label from a collectible code (nugget-copper -> Copper).</summary>
    private static string MetalName(AssetLocation? code)
    {
        if (code == null) return "?";
        string path = code.Path;
        int dash = path.LastIndexOf('-');
        string metal = dash >= 0 && dash < path.Length - 1 ? path.Substring(dash + 1) : path;
        return metal.Length == 0 ? path : char.ToUpperInvariant(metal[0]) + metal.Substring(1);
    }

    private static bool IsMaster()
    {
        LevelingClient? client = AlmanacTcmModSystem.Instance?.Client;
        if (client == null) return false;
        int id = MetDomainId();
        return id >= 0 && client.Domains.TryGetValue(id, out var st) && Domain.TierOf(st.Level) >= 3;
    }

    private static int MetDomainId()
    {
        for (int i = 0; i < DomainRoster.All.Length; i++)
            if (DomainRoster.All[i].Code == MetDomain.Code) return i;
        return -1;
    }
}
