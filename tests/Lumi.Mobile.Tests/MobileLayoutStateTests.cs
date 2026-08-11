using Lumi.Mobile.Layout;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>Locks down the adaptive outputs that production layout actually consumes.</summary>
public class MobileLayoutStateTests
{
    [Theory]
    [InlineData(344)]
    [InlineData(360)]
    [InlineData(393)]
    [InlineData(412)]
    public void CompactWidths_StayInThePhoneLayoutClass(double width)
    {
        var layout = MobileLayoutState.From(width, 892);

        Assert.Equal(WidthSizeClass.Compact, layout.WidthClass);
        Assert.Equal(width, layout.Width);
        Assert.Equal(0, layout.HingeSize);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(700)]
    [InlineData(834)]
    public void MediumWidths_StayInTheOverlayDrawerClass(double width)
    {
        var layout = MobileLayoutState.From(width, 900);

        Assert.Equal(WidthSizeClass.Medium, layout.WidthClass);
    }

    [Theory]
    [InlineData(840)]
    [InlineData(852)]
    [InlineData(1112)]
    public void ExpandedWidths_EnableTheDockableDrawerClass(double width)
    {
        var layout = MobileLayoutState.From(width, 900);

        Assert.Equal(WidthSizeClass.Expanded, layout.WidthClass);
    }

    [Fact]
    public void UnfoldedFoldable_ExposesTheUsableVerticalHinge()
    {
        var layout = MobileLayoutState.From(
            884,
            908,
            FoldPosture.BookVerticalHinge,
            hingeSize: 24,
            hingePosition: 430);

        Assert.Equal(430, layout.HingePosition);
        Assert.Equal(24, layout.HingeSize);
    }

    [Fact]
    public void UnfoldedFoldable_IgnoresAHingeThatWouldStarveTheDetailPane()
    {
        var layout = MobileLayoutState.From(
            884,
            908,
            FoldPosture.BookVerticalHinge,
            hingeSize: 24,
            hingePosition: 800);

        Assert.Equal(0, layout.HingePosition);
        Assert.Equal(0, layout.HingeSize);
    }

    [Fact]
    public void TabletopPosture_DoesNotCreateAHorizontalDrawerGap()
    {
        var layout = MobileLayoutState.From(
            884,
            908,
            FoldPosture.TabletopHorizontalHinge,
            hingeSize: 24,
            hingePosition: 454);

        Assert.Equal(0, layout.HingePosition);
        Assert.Equal(0, layout.HingeSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void DegenerateWidths_DoNotProduceGarbage(double width)
    {
        var layout = MobileLayoutState.From(width, 900);

        Assert.True(layout.Width > 0);
        Assert.Equal(WidthSizeClass.Compact, layout.WidthClass);
    }

    [Fact]
    public void HeightAlone_DoesNotChangeWidthClassOrHingeLayout()
    {
        var shortViewport = MobileLayoutState.From(834, 374);
        var tallViewport = MobileLayoutState.From(834, 1112);

        Assert.Equal(shortViewport.WidthClass, tallViewport.WidthClass);
        Assert.Equal(shortViewport.HingeSize, tallViewport.HingeSize);
        Assert.Equal(shortViewport.HingePosition, tallViewport.HingePosition);
        Assert.NotEqual(shortViewport.Height, tallViewport.Height);
    }
}
