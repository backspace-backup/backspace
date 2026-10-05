using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Views;

public sealed class WindowPlacementTests
{
    [Theory]
    [InlineData(1366, 728, 1.0)]
    [InlineData(1366, 718, 1.25)]
    [InlineData(1920, 1032, 1.25)]
    [InlineData(1920, 1032, 1.5)]
    [InlineData(1280, 752, 1.0)]
    [InlineData(1536, 816, 1.5)]
    public void LaptopWorkingAreaIncludesScalingAndNativeFrame(int width, int height, double scale)
    {
        var area = new PixelRect(-width, 50, width, height);
        var frame = new Size(16, 39);
        var fit = WindowPlacement.Calculate(area, scale, new Size(1280, 840),
            new Size(920, 540), frame, new PixelPoint(9000, -9000));
        Assert.True(fit.Position.X >= area.X && fit.Position.Y >= area.Y);
        Assert.True(fit.Position.X + (fit.ClientSize.Width + frame.Width) * scale <= area.Right);
        Assert.True(fit.Position.Y + (fit.ClientSize.Height + frame.Height) * scale <= area.Bottom);
        Assert.True(fit.ClientSize.Width >= fit.MinimumSize.Width);
        Assert.True(fit.ClientSize.Height >= fit.MinimumSize.Height);
        // The old 1180×720 floor cannot pass on a 1366×768 display at125%.
        if (width == 1366 && scale == 1.25)
        {
            Assert.True(fit.ClientSize.Width < 1180);
            Assert.True(fit.ClientSize.Height < 540);
            Assert.Equal(fit.ClientSize.Height, fit.MinimumSize.Height);
        }
    }

    [Fact]
    public void SpaciousMonitorPreservesChosenSizeAndPosition()
    {
        var fit = WindowPlacement.Calculate(new PixelRect(0, 0, 3840, 2112), 2,
            new Size(1180, 760), new Size(920, 540), new Size(16, 39), new PixelPoint(120, 80));
        Assert.Equal(new Size(1180, 760), fit.ClientSize);
        Assert.Equal(new PixelPoint(120, 80), fit.Position);
        Assert.Equal(new Size(920, 540), fit.MinimumSize);
    }

    [AvaloniaFact]
    public void OpenedPreservesConfiguredClientSizeOnSpaciousScreen()
    {
        var screen = new Screen(1, new PixelRect(0, 0, 2000, 1200), new PixelRect(0, 0, 2000, 1160), true);
        var window = new Window { Width = 1000, Height = 700, MinWidth = 920, MinHeight = 540 };
        WindowPlacement.Attach(window, _ => screen);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(1000, window.Width);
            Assert.Equal(700, window.Height);
            Assert.Equal(new Size(1000, 700), window.ClientSize);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PositionHookFitsAnotherMonitorButLeavesOrdinaryDraggingAlone()
    {
        var large = new Screen(1, new PixelRect(0, 0, 2000, 1200), new PixelRect(0, 0, 2000, 1160), true);
        var small = new Screen(1, new PixelRect(2000, 0, 1024, 512), new PixelRect(2000, 0, 1024, 472), false);
        var currentScreen = large;
        var window = new Window { Width = 1000, Height = 700, MinWidth = 920, MinHeight = 540 };
        WindowPlacement.Attach(window, _ => currentScreen);
        window.Show();
        try
        {
            Assert.Equal(1000, window.Width);
            Assert.Equal(700, window.Height);
            // A partial move off this monitor must not be fought by the helper.
            window.Position = new PixelPoint(-50, 80);
            Assert.Equal(new PixelPoint(-50, 80), window.Position);
            Assert.Equal(700, window.Height);

            // Both monitors have scale 1: no ScalingChanged event will rescue
            // the second move. The real PositionChanged hook must fit it.
            currentScreen = small;
            window.Position = new PixelPoint(2100, 30);
            Assert.True(window.Height <= 456);
            Assert.True(window.MinHeight < 540);
            Assert.True(window.Position.X >= small.WorkingArea.X);
            Assert.True(window.Position.Y + window.Height <= small.WorkingArea.Bottom);

            currentScreen = large;
            window.Position = new PixelPoint(100, 100);
            Assert.Equal(540, window.MinHeight);
            Assert.True(window.Height >= 540);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RestoreHookFitsDisplayChangesThatOccurredWhileMaximized()
    {
        var currentScreen = new Screen(1, new PixelRect(0, 0, 2000, 1200), new PixelRect(0, 0, 2000, 1160), true);
        var window = new Window { Width = 1000, Height = 700, MinWidth = 920, MinHeight = 540 };
        WindowPlacement.Attach(window, _ => currentScreen);
        window.Show();
        try
        {
            window.WindowState = WindowState.Maximized;
            currentScreen = new Screen(1, new PixelRect(0, 0, 1024, 512), new PixelRect(0, 0, 1024, 472), true);
            window.Position = new PixelPoint(40, 40);
            Assert.Equal(700, window.Height);
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.Height <= 456);
            Assert.True(window.Position.Y + window.Height <= currentScreen.WorkingArea.Bottom);
        }
        finally { window.Close(); }
    }
}
