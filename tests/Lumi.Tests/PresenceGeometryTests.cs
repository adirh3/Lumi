using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Pins the presence coordinate system. Positions are easy to get subtly wrong across window sizes — a
/// glow that sits off-centre on a wide monitor, an "island split" that separates vertically instead of
/// horizontally — so every placement rule the field relies on is asserted here, at multiple aspect ratios.
/// These are pure (no Avalonia app), so they run fast and never flake.
/// </summary>
public class PresenceGeometryTests
{
    private const double Eps = 1e-6;

    // ── Centering: focus (0.5,0.5) is dead-centre (zero offset) at ANY window size ────────────
    [Theory]
    [InlineData(800, 600)]
    [InlineData(1600, 900)]
    [InlineData(600, 1600)]
    [InlineData(2560, 1080)]
    [InlineData(400, 400)]
    public void FieldOffset_AtCenterFocus_IsZero_AtAnyAspect(double w, double h)
    {
        var (x, y) = PresenceGeometry.FieldOffset(0.5, 0.5, follow: 1.0, reach: 1.0, w, h);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.0, y, 6);
    }

    // ── Full reach/follow lands the pool centre EXACTLY on the focus point ─────────────────────
    [Theory]
    [InlineData(0.70, 0.30)]
    [InlineData(0.20, 0.85)]
    [InlineData(0.50, 0.46)]
    [InlineData(0.0, 1.0)]
    public void FieldOffset_FullReach_LandsCentreOnFocus(double fx, double fy)
    {
        const double w = 1600, h = 900;
        var (ox, oy) = PresenceGeometry.FieldOffset(fx, fy, follow: 1.0, reach: 1.0, w, h);
        var (cx, cy) = PresenceGeometry.CenterFraction(ox, oy, w, h);
        Assert.Equal(fx, cx, 6);
        Assert.Equal(fy, cy, 6);
    }

    // ── Aspect-independence: the SAME normalized focus lands at the SAME visible fraction,
    //    regardless of window size or shape. This is the core "centered regardless of window size"
    //    guarantee that kept breaking when the math mixed width and height.
    [Fact]
    public void FieldOffset_PerceivedFraction_IsAspectIndependent()
    {
        const double fx = 0.72, fy = 0.28, follow = 0.9, reach = 1.0;
        (double w, double h)[] sizes = { (800, 600), (1600, 1200), (1200, 1600), (2560, 1080), (500, 1500) };

        foreach (var (w, h) in sizes)
        {
            var (ox, oy) = PresenceGeometry.FieldOffset(fx, fy, follow, reach, w, h);
            var (cx, cy) = PresenceGeometry.CenterFraction(ox, oy, w, h);
            // follow=0.9 means the pool travels 90% of the way from centre toward the focus.
            Assert.Equal(0.5 + (fx - 0.5) * follow, cx, 6);
            Assert.Equal(0.5 + (fy - 0.5) * follow, cy, 6);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.0)]
    public void FieldOffset_FollowDampsTravelTowardCentre(double follow)
    {
        const double w = 1600, h = 900, fx = 1.0, fy = 0.0;
        var (ox, oy) = PresenceGeometry.FieldOffset(fx, fy, follow, reach: 1.0, w, h);
        var (cx, cy) = PresenceGeometry.CenterFraction(ox, oy, w, h);
        // A damped lobe sits proportionally between dead-centre (0.5) and the focus point.
        Assert.Equal(0.5 + (fx - 0.5) * follow, cx, 6);
        Assert.Equal(0.5 + (fy - 0.5) * follow, cy, 6);
    }

    [Fact]
    public void FieldOffset_ZeroReach_StaysHome()
    {
        var (ox, oy) = PresenceGeometry.FieldOffset(0.9, 0.1, follow: 1.0, reach: 0.0, 1600, 900);
        Assert.Equal(0.0, ox, 6);
        Assert.Equal(0.0, oy, 6);
    }

    // ── Island split is HORIZONTAL for a right-side island, and lands ON the island ────────────
    [Fact]
    public void CompanionOffset_RightIsland_TravelsHorizontally()
    {
        // A workspace/browser island opens on the right at mid-height.
        const double w = 1645, h = 980;
        var (ox, oy) = PresenceGeometry.CompanionOffset(islandX: 0.78, islandY: 0.5, w, h);

        Assert.True(ox > 0, "Right island must pull the companion to the RIGHT (+X).");
        Assert.Equal(0.0, oy, 6); // a mid-height island has no vertical component
        // The horizontal travel must dominate — the split reads side-by-side, not vertical.
        Assert.True(System.Math.Abs(ox) > System.Math.Abs(oy) + 0.2 * h,
            "Horizontal travel must dominate so the split does not look vertical.");
    }

    [Theory]
    [InlineData(0.78, 0.50)]
    [InlineData(0.66, 0.40)]
    [InlineData(0.85, 0.62)]
    public void CompanionOffset_LandsCentredOnIsland(double ix, double iy)
    {
        const double w = 1645, h = 980;
        var (ox, oy) = PresenceGeometry.CompanionOffset(ix, iy, w, h);
        var (cx, cy) = PresenceGeometry.CenterFraction(ox, oy, w, h);
        Assert.Equal(ix, cx, 6);
        Assert.Equal(iy, cy, 6);
    }

    // ── Lobe sizing grows with the long edge but stays capped within the short edge ─────────────
    [Theory]
    [InlineData(1600, 760)]
    [InlineData(2560, 1080)]
    [InlineData(1920, 1080)]
    public void LobeDiameter_StaysWithinShortEdge_OnWideSurface(double w, double h)
    {
        // The largest authored ambient size factor (Indigo, 0.68) on a wide window must still fit the
        // short edge — otherwise the lobe is larger than the viewport and its travel is imperceptible.
        var d = PresenceGeometry.LobeDiameter(0.68, w, h, fuller: false);
        Assert.True(d <= System.Math.Min(w, h),
            $"Lobe diameter {d} exceeds the short edge {System.Math.Min(w, h)}.");
    }

    [Fact]
    public void LobeDiameter_GrowsWithLongEdge_OnATallCanvas()
    {
        // The chat column is width-capped, so a TALLER window must yield a BIGGER pool (the fix for an
        // aura that looked small on large screens) — not a constant short-edge size.
        var shortish = PresenceGeometry.LobeDiameter(0.6, 535, 775, fuller: false);
        var taller = PresenceGeometry.LobeDiameter(0.6, 535, 1300, fuller: false);
        Assert.True(taller > shortish, $"A taller canvas should grow the pool ({taller} !> {shortish}).");
    }

    [Fact]
    public void LobeDiameter_IsAspectSwapInvariant()
    {
        // Landscape and portrait with the same pair of edges get the same size — only the short/long edge
        // lengths matter, never the orientation.
        var landscape = PresenceGeometry.LobeDiameter(0.6, 1600, 800, fuller: false);
        var portrait = PresenceGeometry.LobeDiameter(0.6, 800, 1600, fuller: false);
        Assert.Equal(landscape, portrait, 6);
    }

    [Fact]
    public void LobeDiameter_SquareCanvas_EqualsShortEdge()
    {
        // On a square canvas the long-edge growth is a no-op, so the established short-edge sizing holds.
        var d = PresenceGeometry.LobeDiameter(0.6, 900, 900, fuller: false);
        Assert.Equal(0.6 * 900, d, 6);
    }

    [Fact]
    public void LobeDiameter_CappedWithinShortEdge_OnExtremeAspect()
    {
        // Even on an extreme aspect the largest ambient factor (0.68) stays within the short edge, so a
        // focus glide still reads as travel (the cap that prevents the "lobe larger than viewport" bug).
        var d = PresenceGeometry.LobeDiameter(0.68, 535, 4000, fuller: false);
        Assert.True(d <= 535, $"Lobe diameter {d} exceeds the short edge 535.");
    }

    [Fact]
    public void LobeDiameter_FullerGivesABiggerPool()
    {
        var plain = PresenceGeometry.LobeDiameter(0.5, 900, 900, fuller: false);
        var fuller = PresenceGeometry.LobeDiameter(0.5, 900, 900, fuller: true);
        Assert.True(fuller > plain);
    }

    // ── Idle-drift anchor spread is short-edge relative and centred ────────────────────────────
    [Fact]
    public void AnchorSpread_CentreAnchor_IsZero()
    {
        var (x, y) = PresenceGeometry.AnchorSpread(0.5, 0.5, 1600, 900);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.0, y, 6);
    }

    [Fact]
    public void AnchorSpread_UsesShortEdge_NotWidth()
    {
        // On a wide window, an anchor 0.1 off-centre spreads by 0.1·shortEdge — NOT 0.1·width, which
        // would fling the hues apart into a horizontal smear.
        const double w = 1600, h = 900;
        var (x, _) = PresenceGeometry.AnchorSpread(0.6, 0.5, w, h);
        Assert.Equal(0.1 * System.Math.Min(w, h), x, 6);
    }

    [Fact]
    public void CenterFraction_IsInverseOfFieldOffset()
    {
        const double w = 1234, h = 567;
        var (ox, oy) = PresenceGeometry.FieldOffset(0.33, 0.77, follow: 1.0, reach: 1.0, w, h);
        var (cx, cy) = PresenceGeometry.CenterFraction(ox, oy, w, h);
        Assert.Equal(0.33, cx, 6);
        Assert.Equal(0.77, cy, 6);
    }

    // ── Directional light origin (position-driven luminance) ───────────────────────────────────
    [Fact]
    public void LightOrigin_Centre_IsSymmetric()
    {
        var (x, y) = PresenceGeometry.LightOrigin(0.5, 0.5);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.5, y, 6);
    }

    [Fact]
    public void LightOrigin_LowPool_DropsOriginSoLightCastsUp()
    {
        // A pool hugging the bottom (focusY→1) drops its bright core toward its base, so the soft tail —
        // the visible glow — reaches UP into the canvas.
        var (_, y) = PresenceGeometry.LightOrigin(0.5, 1.0);
        Assert.True(y > 0.5, $"Origin Y {y} should drop below centre for a low pool.");
    }

    [Fact]
    public void LightOrigin_HighPool_RaisesOriginSoLightCastsDown()
    {
        var (_, y) = PresenceGeometry.LightOrigin(0.5, 0.0);
        Assert.True(y < 0.5, $"Origin Y {y} should rise above centre for a high pool.");
    }

    [Fact]
    public void LightOrigin_LeansTowardTheFocusEdge_Horizontally()
    {
        var (xr, _) = PresenceGeometry.LightOrigin(1.0, 0.5);
        var (xl, _) = PresenceGeometry.LightOrigin(0.0, 0.5);
        Assert.True(xr > 0.5 && xl < 0.5, $"Origin should lean toward the focus edge (xr={xr}, xl={xl}).");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(-5.0, 9.0)]
    public void LightOrigin_StaysWellInsideTheLobe(double fx, double fy)
    {
        // The origin must never reach the rim (which would hard-edge the falloff): clamped to 0.5 ± 0.2.
        var (x, y) = PresenceGeometry.LightOrigin(fx, fy);
        Assert.InRange(x, 0.3, 0.7);
        Assert.InRange(y, 0.3, 0.7);
    }
}
