using System;
using Cairo;
using Vintagestory.API.Client;

namespace AlmanacTcm.Gui;

/// <summary>
/// A wax-seal button for the Almanac book's flow text — the CTA the ALC emphasis toggle needed (a
/// bold word doesn't read as a control). The design echoes the site's "From the Author's Hand" seal:
/// the ACTIVE option is a pressed oxblood wax lozenge — a domed radial fill, a glossy sheen, the
/// seal-die ring, and the word embossed into the wax — while the INACTIVE option is an UNPRESSED
/// seal (an oxblood ring on the parchment) that shows the select cursor and presses when clicked.
/// Baked onto the page surface in ComposeElements like the tier-pips and the affinity star; clicks
/// ride the base RichTextComponentBase mouse handlers (no href).
/// </summary>
public class EmphasisStampComponent : RichTextComponentBase
{
    private readonly string label;
    private readonly bool active;
    private readonly CairoFont font;
    private readonly Action? onClick;
    private double textWidth;
    private bool wasMouseDown;

    // Oxblood wax palette, matched to the author's-hand seal on the site.
    private static readonly double[] WaxLit = { 0.60, 0.26, 0.20 };   // domed highlight (gradient centre)
    private static readonly double[] WaxMid = { 0.46, 0.16, 0.13 };   // body of the wax
    private static readonly double[] WaxDeep = { 0.30, 0.09, 0.08 };  // rim / gradient edge
    private static readonly double[] WaxRim = { 0.24, 0.07, 0.06 };   // the hard edge stroke
    private static readonly double[] Emboss = { 0.20, 0.05, 0.04 };   // pressed-in shadow under the letters
    private static readonly double[] Relief = { 0.95, 0.84, 0.74 };   // warm-light letter face on the wax
    private static readonly double[] Oxblood = { 0.44, 0.15, 0.13 };  // the unpressed ring + its lettering

    public EmphasisStampComponent(ICoreClientAPI api, string label, bool active, CairoFont font, Action? onClick) : base(api)
    {
        this.label = label;
        this.active = active;
        this.font = font.Clone();
        this.onClick = onClick;
        MouseOverCursor = active ? null : "linkselect";   // the "this is clickable" affordance
        BoundsPerLine = new[] { new LineRectangled(0, 0, 0, 0) };
    }

    private double HPad => GuiElement.scaled(13);
    private double VPad => GuiElement.scaled(5);
    private double Gap => GuiElement.scaled(12);

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
        double r = boxH / 2.0;   // a full pill: the wax rounds off at the ends

        if (active)
        {
            // Domed molten wax: a radial fill lit from the upper-left.
            using (var grad = new RadialGradient(x + boxW * 0.40, y + boxH * 0.30, r * 0.15,
                                                 x + boxW * 0.50, y + boxH * 0.50, boxW * 0.72))
            {
                grad.AddColorStop(0, new Color(WaxLit[0], WaxLit[1], WaxLit[2]));
                grad.AddColorStop(0.55, new Color(WaxMid[0], WaxMid[1], WaxMid[2]));
                grad.AddColorStop(1, new Color(WaxDeep[0], WaxDeep[1], WaxDeep[2]));
                RoundRect(ctx, x, y, boxW, boxH, r);
                ctx.SetSource(grad);
                ctx.FillPreserve();
            }
            // The hard wax rim.
            ctx.LineWidth = GuiElement.scaled(1.6);
            ctx.SetSourceRGBA(WaxRim[0], WaxRim[1], WaxRim[2], 1);
            ctx.Stroke();

            // A glossy sheen across the top, clipped to the seal.
            ctx.Save();
            RoundRect(ctx, x, y, boxW, boxH, r);
            ctx.Clip();
            using (var gloss = new LinearGradient(x, y, x, y + boxH * 0.6))
            {
                gloss.AddColorStop(0, new Color(1, 0.96, 0.9, 0.16));
                gloss.AddColorStop(1, new Color(1, 0.96, 0.9, 0));
                ctx.SetSource(gloss);
                ctx.Rectangle(x, y, boxW, boxH);
                ctx.Fill();
            }
            ctx.Restore();

            // The seal-die ring pressed just inside the rim.
            double inset = GuiElement.scaled(3);
            RoundRect(ctx, x + inset, y + inset, boxW - 2 * inset, boxH - 2 * inset, Math.Max(1, r - inset));
            ctx.LineWidth = GuiElement.scaled(1);
            ctx.SetSourceRGBA(WaxDeep[0], WaxDeep[1], WaxDeep[2], 0.7);
            ctx.Stroke();

            DrawLabel(ctx, x, y, boxW, boxH, emboss: true);
        }
        else
        {
            // An unpressed seal: an oxblood ring on the parchment, waiting to be pressed.
            RoundRect(ctx, x, y, boxW, boxH, r);
            ctx.LineWidth = GuiElement.scaled(1.5);
            ctx.SetSourceRGBA(Oxblood[0], Oxblood[1], Oxblood[2], 0.95);
            ctx.Stroke();
            DrawLabel(ctx, x, y, boxW, boxH, emboss: false);
        }
    }

    private void DrawLabel(Context ctx, double x, double y, double boxW, double boxH, bool emboss)
    {
        font.SetupContext(ctx);
        FontExtents fe = ctx.FontExtents;
        TextExtents te = ctx.TextExtents(label);
        double tx = x + (boxW - te.Width) / 2 - te.XBearing;
        double ty = y + (boxH - fe.Ascent - fe.Descent) / 2 + fe.Ascent;
        if (emboss)
        {
            // Pressed into the wax: a dark shadow under a warm-light face.
            ctx.SetSourceRGBA(Emboss[0], Emboss[1], Emboss[2], 0.9);
            ctx.MoveTo(tx, ty + GuiElement.scaled(1));
            ctx.ShowText(label);
            ctx.SetSourceRGBA(Relief[0], Relief[1], Relief[2], 1);
            ctx.MoveTo(tx, ty);
            ctx.ShowText(label);
        }
        else
        {
            ctx.SetSourceRGBA(Oxblood[0], Oxblood[1], Oxblood[2], 1);
            ctx.MoveTo(tx, ty);
            ctx.ShowText(label);
        }
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
