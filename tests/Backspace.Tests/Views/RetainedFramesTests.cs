#if WINDOWS
using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform.Surfaces;
using Xunit;

namespace Backspace.Tests.Views;

/// <summary>
/// U239: the window's kept frame (RetainedFrames) reaches into Avalonia's
/// compositor by name. If an Avalonia upgrade renames any of it, the window
/// silently falls back to redrawing everything (a core of processor for
/// smooth motion); this test fails first, so the upgrade is noticed.
/// </summary>
public sealed class RetainedFramesTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Find(Assembly assembly, string name) => assembly.GetType(name) ?? throw new Xunit.Sdk.XunitException(name + " is gone");
    private static Assembly Base => typeof(IFramebufferRenderTarget).Assembly;

    [Fact]
    public void The_lock_delegate_takes_scene_info_and_reports_a_kept_frame()
    {
        var type = typeof(FuncFramebufferRenderTarget).GetNestedType("LockFramebufferDelegate", Any);
        Assert.NotNull(type);
        var parameters = type!.GetMethod("Invoke")!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.True(parameters[1].ParameterType.IsByRef);
        Assert.NotNull(parameters[1].ParameterType.GetElementType()!.GetConstructor([typeof(bool)]));
        Assert.NotNull(typeof(FuncFramebufferRenderTarget).GetConstructor([type, typeof(bool)]));
    }

    [Fact]
    public void The_compositor_still_has_what_the_kept_frame_reads()
    {
        Assert.NotNull(typeof(TopLevel).GetProperty("Renderer", Any));
        Assert.NotNull(Find(Base, "Avalonia.Rendering.Composition.CompositingRenderer").GetProperty("CompositionTarget", Any));
        Assert.Contains(Find(Base, "Avalonia.Rendering.Composition.CompositionTarget").GetProperties(Any), p => p.Name == "Server");
        var target = Find(Base, "Avalonia.Rendering.Composition.Server.ServerCompositionTarget");
        foreach (var field in new[] { "_renderTarget", "_fullRedrawRequested", "_overlays" })
            Assert.True(target.GetField(field, Any) is not null, "ServerCompositionTarget." + field + " is gone");
        Assert.NotNull(target.GetProperty("DirtyRects", Any));
        Assert.NotNull(target.GetField("_overlays", Any)!.FieldType.GetProperty("RequireLayer", Any));
        Assert.NotNull(Find(Base, "Avalonia.Rendering.Composition.Server.SingleDirtyRectTracker").GetField("_rect", Any));
        var skia = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Avalonia.Skia")
            ?? Assembly.Load("Avalonia.Skia");
        Assert.NotNull(Find(skia, "Avalonia.Skia.FramebufferRenderTarget").GetField("_renderTarget", Any));
    }

    /// <summary>DisplayClock paces Avalonia's frame timer by the screen
    /// (U239): the same kind of guard for what it reaches.</summary>
    [Fact]
    public void The_frame_timer_still_has_what_the_display_clock_paces()
    {
        Assert.NotNull(Find(Base, "Avalonia.Rendering.Composition.CompositingRenderer").GetField("_compositor", Any));
        Assert.NotNull(Find(Base, "Avalonia.Rendering.Composition.Compositor").GetProperty("Loop", Any));
        var loop = Find(Base, "Avalonia.Rendering.DefaultRenderLoop");
        Assert.NotNull(loop.GetField("_timer", Any));
        var timer = typeof(Avalonia.Rendering.SleepLoopRenderTimer);
        Assert.Equal(typeof(System.Threading.AutoResetEvent), timer.GetField("_wakeEvent", Any)?.FieldType);
        Assert.Equal(typeof(bool), timer.GetField("_stopped", Any)?.FieldType);
        Assert.Equal(typeof(int), timer.GetProperty("DesiredFps", Any)?.PropertyType);
    }

    [Fact]
    public void The_dirty_rectangle_has_its_four_edges()
    {
        var rect = Find(Base, "Avalonia.Platform.LtrbRect");
        foreach (var edge in new[] { "Left", "Top", "Right", "Bottom" })
            Assert.True(rect.GetField(edge, Any) is not null || rect.GetProperty(edge, Any) is not null, "LtrbRect." + edge + " is gone");
    }
}
#endif
