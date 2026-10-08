#if PATCHED_ATSPI
using System.Reflection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AccessibilityModManager.LinuxApp.Tests;

public sealed class AtSpiApplicationTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [AvaloniaFact]
    public void Application_exposes_the_desktop_parent_returned_by_the_registry()
    {
        var assembly = Assembly.Load("Avalonia.FreeDesktop.AtSpi");
        var serverType = assembly.GetType("Avalonia.FreeDesktop.AtSpi.AtSpiServer")!;
        var server = Activator.CreateInstance(serverType, true)!;
        var app = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.ApplicationAtSpiNode")!,
            Members, null, ["Manager test"], null)!;
        var handler = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.ApplicationAccessibleHandler")!,
            Members, null, [server, app], null)!;
        var parent = handler.GetType().GetProperty("Parent")!;
        var nullReference = serverType.GetMethod("GetNullReference", Members)!.Invoke(server, null);
        Assert.Equal(nullReference, parent.GetValue(handler));

        // Model the registry's Embed reply using the bridge's own wire-reference type.
        // ApplicationAccessibleHandler previously returned null even after registration.
        serverType.GetField("_uniqueName", Members)!.SetValue(server, ":1.42");
        var desktop = serverType.GetMethod("GetRootReference", Members)!.Invoke(server, null)!;
        serverType.GetField("_desktopReference", Members)!.SetValue(server, desktop);
        Assert.NotEqual(nullReference, parent.GetValue(handler));
        Assert.Equal(desktop, parent.GetValue(handler));
    }
}
#endif
