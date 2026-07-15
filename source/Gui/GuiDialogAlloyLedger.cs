using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AlmanacTcm.Domains;
using AlmanacTcm.Leveling;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace AlmanacTcm.Gui;

/// <summary>
/// The Alloy Ledger (rank-bonus-design.md §162 Axis 4, Master unlock). A read-only aid a
/// Master smith "just knows": pick an alloy and a desired ingot count, and it reads out the
/// metal amounts for the alloy's ratio ranges, plus the leanest valid mix. It computes
/// against the game's own alloy recipes, so modded alloys (industrialstory, etc.) show up
/// too. It automates nothing and multiplies no yield: it only saves the arithmetic a Master
/// could do by hand. Client-side; gated to Master MET on open (the client's synced rank).
/// </summary>
public class GuiDialogAlloyLedger : GuiDialog
{
    public override string ToggleKeyCombinationCode => "tcmalloyledger";

    private List<AlloyRecipe> alloys = new();
    private bool built;
    private int selected;
    private int ingots = 1;

    public GuiDialogAlloyLedger(ICoreClientAPI capi) : base(capi) { }

    /// <summary>Master gate + lazy build. Alloys are resolved by the time a player can open
    /// this (well after asset load), so the list is gathered on first open, not construction.</summary>
    public override bool TryOpen()
    {
        if (!IsMaster())
        {
            capi.TriggerIngameError(this, "notmaster", Lang.Get("almanactcm:alloy-locked"));
            return false;
        }
        EnsureAlloys();
        Compose();
        return base.TryOpen();
    }

    private void EnsureAlloys()
    {
        if (built) return;
        built = true;
        alloys = (capi.GetMetalAlloys() ?? new List<AlloyRecipe>())
            .Where(a => a is { Enabled: true, Ingredients.Length: > 0 }
                        && a.Output?.ResolvedItemstack?.Collectible != null)
            .OrderBy(OutName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected >= alloys.Count) selected = 0;
    }

    private void Compose()
    {
        CairoFont font = CairoFont.WhiteDetailText();

        var ddBounds = ElementBounds.Fixed(0, 30, 280, 28);
        var lblBounds = ElementBounds.Fixed(0, 68, 150, 28);
        var numBounds = ElementBounds.Fixed(150, 66, 90, 28);
        var txtBounds = ElementBounds.Fixed(0, 108, 320, 320);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(ddBounds, lblBounds, numBounds, txtBounds);

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        string[] codes = alloys.Select((_, i) => i.ToString()).ToArray();
        string[] names = alloys.Select(OutName).ToArray();
        if (codes.Length == 0) { codes = new[] { "0" }; names = new[] { Lang.Get("almanactcm:alloy-none") }; }

        SingleComposer = capi.Gui.CreateCompo("tcmalloyledger", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("almanactcm:alloy-title"), () => TryClose())
            .BeginChildElements(bgBounds)
                .AddDropDown(codes, names, selected, OnAlloySelected, ddBounds, "alloy")
                .AddStaticText(Lang.Get("almanactcm:alloy-ingots"), font, lblBounds)
                .AddNumberInput(numBounds, OnCountChanged, font, "count")
                .AddDynamicText(BuildReadout(), font, txtBounds, "readout")
            .EndChildElements()
            .Compose();

        SingleComposer.GetNumberInput("count").SetValue(ingots.ToString());
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

    /// <summary>The ledger body: per-metal unit ranges for the requested output, the leanest
    /// valid mix, and the units-per-ingot reminder. No dashes (voice rule); ranges read "X to Y".</summary>
    private string BuildReadout()
    {
        if (alloys.Count == 0) return Lang.Get("almanactcm:alloy-none");
        AlloyRecipe a = alloys[Math.Clamp(selected, 0, alloys.Count - 1)];
        int total = ingots * 100;   // 100 units = 1 ingot
        int n = a.Ingredients.Length;

        int[] minU = new int[n], maxU = new int[n];
        int baseIdx = 0; float baseMax = -1f;
        for (int i = 0; i < n; i++)
        {
            minU[i] = (int)Math.Round(total * a.Ingredients[i].MinRatio);
            maxU[i] = (int)Math.Round(total * a.Ingredients[i].MaxRatio);
            if (a.Ingredients[i].MaxRatio > baseMax) { baseMax = a.Ingredients[i].MaxRatio; baseIdx = i; }
        }

        var sb = new StringBuilder();
        sb.Append(OutName(a)).Append(',').Append(' ')
          .Append(ingots).Append(ingots == 1 ? " ingot (" : " ingots (").Append(total).Append(" units)\n\n");

        for (int i = 0; i < n; i++)
        {
            sb.Append(MetalName(a.Ingredients[i])).Append(": ")
              .Append(minU[i]).Append(" to ").Append(maxU[i]).Append(" units");
            if (i == baseIdx) sb.Append("  (base)");
            sb.Append('\n');
        }

        // Leanest mix: every metal at its floor, the leftover poured into the base metal.
        int[] lean = (int[])minU.Clone();
        int assigned = lean.Sum();
        lean[baseIdx] += total - assigned;
        sb.Append('\n').Append(Lang.Get("almanactcm:alloy-leanest")).Append(' ');
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(" · ");
            sb.Append(MetalName(a.Ingredients[i])).Append(' ').Append(lean[i]);
        }
        sb.Append("\n\n").Append(Lang.Get("almanactcm:alloy-footnote"));
        return sb.ToString();
    }

    private static string OutName(AlloyRecipe a) => a.Output?.ResolvedItemstack?.GetName() ?? "?";

    /// <summary>Clean metal label from an ingredient's code (nugget-copper -> Copper).</summary>
    private static string MetalName(MetalAlloyIngredient ing)
    {
        var code = ing?.ResolvedItemstack?.Collectible?.Code;
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
