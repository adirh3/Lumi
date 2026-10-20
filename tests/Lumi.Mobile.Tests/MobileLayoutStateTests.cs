using Lumi.Mobile.Layout;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>Locks down the adaptive outputs that production layout actually consumes.</summary>
public class MobileLayoutStateTests
{
    /// <summary>
    /// The Galaxy Z Fold7 at its default display size (984 dp), at a larger display size (750 dp)
    /// and an upright tablet all get the list beside the content; its cover screen and phones get one pane.
    /// </summary>
    [Theory]
    [InlineData(412, 960, false)]
    [InlineData(393, 852, false)]
    [InlineData(690, 900, false)]
    [InlineData(700, 900, true)]
    [InlineData(750, 832, true)]
    [InlineData(834, 1112, true)]
    [InlineData(984, 1092, true)]
    public void TwoPanesStartWhereAListAndAReadableChatBothFit(double width, double height, bool twoPanes)
    {
        var layout = MobileLayoutState.From(width, height);

        Assert.Equal(twoPanes, layout.CanShowTwoPanes);
        Assert.False(layout.CanShowThreePanes);
    }

    [Theory]
    [InlineData(1200, 800, true)]
    [InlineData(1280, 800, true)]
    [InlineData(1112, 834, false)]
    public void ASupportingPanelFitsOnlyOnLargeTablets(double width, double height, bool threePanes)
    {
        var layout = MobileLayoutState.From(width, height);

        Assert.True(layout.CanShowTwoPanes);
        Assert.Equal(threePanes, layout.CanShowThreePanes);
    }

    /// <summary>
    /// A phone held sideways is wide but short: one pane, one-line chrome. A browser that shrinks its
    /// window above the keyboard must not cost a wide tablet its list, so width alone wins from 1000 dp.
    /// </summary>
    [Theory]
    [InlineData(852, 393, false, true)]
    [InlineData(960, 412, false, true)]
    [InlineData(1092, 420, true, false)]
    [InlineData(1280, 400, true, false)]
    public void ShortLandscapeKeepsOnePaneUnlessTheWindowIsReallyWide(
        double width, double height, bool twoPanes, bool shortHeight)
    {
        var layout = MobileLayoutState.From(width, height);

        Assert.Equal(twoPanes, layout.CanShowTwoPanes);
        Assert.Equal(shortHeight, layout.IsShortHeight);
    }

    [Theory]
    [InlineData(750, 320)]
    [InlineData(900, 324)]
    [InlineData(984, 354)]
    [InlineData(1280, 360)]
    public void TheListPaneIsAboutAThirdOfTheWindowWithinTheSidebarsRange(double width, double listWidth)
    {
        Assert.Equal(listWidth, MobileLayoutState.From(width, 900).ListPaneWidth);
    }

    [Fact]
    public void BookPostureOnASeamlessFoldSplitsAtTheCreaseWithoutReservingIt()
    {
        var layout = MobileLayoutState.From(984, 1092, FoldPosture.BookVerticalHinge, 0, 492);

        Assert.True(layout.CanShowTwoPanes);
        Assert.Equal(492, layout.FoldPosition);
        Assert.Equal(492, layout.ListPaneWidth);
        Assert.Equal(0, layout.HingeSize);
        Assert.Equal(0, layout.HingePosition);
    }

    [Fact]
    public void AnOccludingHingeStillSetsTheListPane()
    {
        var layout = MobileLayoutState.From(884, 908, FoldPosture.BookVerticalHinge, 24, 430);

        Assert.Equal(430, layout.HingePosition);
        Assert.Equal(0, layout.FoldPosition);
        Assert.Equal(430, layout.ListPaneWidth);
    }

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
    public void DrawableBookCreaseDoesNotReserveAnEmptyNavigationPane()
    {
        var layout = MobileLayoutState.From(884, 908, FoldPosture.BookVerticalHinge,
            hingeSize: 0, hingePosition: 442);

        Assert.Equal(WidthSizeClass.Expanded, layout.WidthClass);
        Assert.Equal(0, layout.HingeSize);
        Assert.Equal(0, layout.HingePosition);
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

    /// <summary>
    /// A browser page shrinks above its keyboard. Typing on a 750 dp Fold or a small tablet must not
    /// fold the layout into one pane, while a phone turned sideways still gets one.
    /// </summary>
    [Fact]
    public void AWindowThatOnlyGetsShorterKeepsItsPanes()
    {
        var open = MobileLayoutState.From(750, 676);
        Assert.True(open.CanShowTwoPanes);

        var typing = MobileLayoutState.From(750, 376).Following(open);
        Assert.True(typing.CanShowTwoPanes);
        Assert.False(typing.IsShortHeight);
        Assert.Equal(376, typing.Height);

        var stillTyping = MobileLayoutState.From(750, 420).Following(typing);
        Assert.True(stillTyping.CanShowTwoPanes);

        var done = MobileLayoutState.From(750, 676).Following(stillTyping);
        Assert.Equal(open, done);

        var sideways = MobileLayoutState.From(852, 393).Following(done);
        Assert.False(sideways.CanShowTwoPanes);
        Assert.True(sideways.IsShortHeight);

        var sidewaysTyping = MobileLayoutState.From(852, 200).Following(sideways);
        Assert.False(sidewaysTyping.CanShowTwoPanes);

        var grown = MobileLayoutState.From(852, 700).Following(sidewaysTyping);
        Assert.True(grown.CanShowTwoPanes);
    }
}
