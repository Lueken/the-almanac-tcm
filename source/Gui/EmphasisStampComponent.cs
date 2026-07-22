using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacTcm.Gui;

/// <summary>
/// A small pressed-stamp button for the Almanac book's flow text — the CTA the ALC emphasis toggle
/// needed (a bold word doesn't read as a control). Draws a rounded box with the label inside: the
/// ACTIVE option is filled like an inked wax seal (sepia box, parchment lettering), the INACTIVE one
/// is an outlined button that shows the select cursor and fires onClick. Baked onto the page surface
/// in ComposeElements, exactly like the tier-pips and the affinity star; clicks ride the base
/// RichTextComponentBase mouse handlers (no href).
/// </summary>
public class EmphasisStampComponent : RichTextComponentBase
{
    private readonly string label;
    private readonly bool active;
    private readonly CairoFont font;
    private readonly Action? onClick;
    private double textWidth;
    private bool wasMouseDown;

    private static readonly double[] Ink = { 0.13, 0.09, 0.05, 1 };       // border + inactive lettering
    private static readonly double[] Seal = { 0.34, 0.20, 0.11, 1 };      // the inked-stamp fill (active)
    private static readonly double[] Parchment = { 0.96, 0.92, 0.80, 1 }; // lettering on the active fill

    public EmphasisStampComponent(ICoreClientAPI api, string label, bool active, CairoFont font, Action? onClick) : base(api)
    {
        this.label = label;
        this.active = active;
        this.font = font.Clone();
        this.onClick = onClick;
        MouseOverCursor = active ? null : "linkselect";   // the "this is clickable" affordance
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    private double HPad => GuiElement.scaled(11);
    private double VPad => GuiElement.scaled(4);
    private double Gap => GuiElement.scaled(12);
    private double Radius => GuiElement.scaled(3);

    private double MeasureText()
    {
        using var surf = new ImageSurface(Format.Argb32, 1, 1);
        using var ctx = new Context(surf);
        font.SetupContext(ctx);
        return ctx.TextExtents(label).Width;
    }

    public override EnumCalcBoundsResult CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double offsetX, double lineY, out double nextOffsetX)
    {
        TextFlowPath cur = GetCurrentFlowPathSection(flowPath, lineY) ?? flowPath[0];
        textWidth = MeasureText();
        double boxW = textWidth + 2 * HPad;
        double boxH = GuiElement.scaled(font.UnscaledFontsize) + 2 * VPad;

        BoundsPerLine[0].X = cur.X1 + offsetX;
        BoundsPerLine[0].Y = lineY;
        BoundsPerLine[0].Width = boxW + Gap;
        BoundsPerLine[0].Height = boxH;
        nextOffsetX = offsetX + boxW + Gap;
        return EnumCalcBoundsResult.Continue;
    }

    public override void ComposeElements(Context ctx, ImageSurface surface)
    {
        var b = BoundsPerLine[0];
        double boxW = b.Width - Gap;
        double boxH = b.Height;
        double x = b.X, y = b.Y;

        RoundRect(ctx, x, y, boxW, boxH, Radius);
        if (active)
        {
            ctx.SetSourceRGBA(Seal);
            ctx.FillPreserve();
            ctx.LineWidth = GuiElement.scaled(1.2);
            ctx.SetSourceRGBA(Ink);
            ctx.Stroke();
        }
        else
        {
            ctx.LineWidth = GuiElement.scaled(1.4);
            ctx.SetSourceRGBA(Ink);
            ctx.Stroke();
        }

        font.SetupContext(ctx);
        FontExtents fe = ctx.FontExtents;
        TextExtents te = ctx.TextExtents(label);
        double tx = x + (boxW - te.Width) / 2 - te.XBearing;
        double ty = y + (boxH - fe.Ascent - fe.Descent) / 2 + fe.Ascent;
        ctx.SetSourceRGBA(active ? Parchment : Ink);
        ctx.MoveTo(tx, ty);
        ctx.ShowText(label);
        ctx.NewPath();
    }

    private static void RoundRect(Context ctx, double x, double y, double w, double h, double r)
    {
        ctx.NewPath();
        ctx.Arc(x + w - r, y + r, r, -Math.PI / 2, 0);
        ctx.Arc(x + w - r, y + h - r, r, 0, Math.PI / 2);
        ctx.Arc(x + r, y + h - r, r, Math.PI / 2, Math.PI);
        ctx.Arc(x + r, y + r, r, Math.PI, 1.5 * Math.PI);
        ctx.ClosePath();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (active || onClick == null) return;
        wasMouseDown = HitBox(args.X, args.Y);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (active || onClick == null || !wasMouseDown) return;
        wasMouseDown = false;
        if (HitBox(args.X, args.Y)) { args.Handled = true; onClick(); }
    }

    /// <summary>Only the drawn box is live, not the trailing gap.</summary>
    private bool HitBox(double mx, double my)
    {
        var b = BoundsPerLine[0];
        return mx >= b.X && mx <= b.X + b.Width - Gap && my >= b.Y && my <= b.Y + b.Height;
    }
}
