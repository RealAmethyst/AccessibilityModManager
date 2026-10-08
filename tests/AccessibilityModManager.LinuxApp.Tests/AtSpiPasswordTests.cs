#if PATCHED_ATSPI
using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace AccessibilityModManager.LinuxApp.Tests;

public sealed class AtSpiPasswordTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [AvaloniaFact]
    public async Task Password_role_queries_and_event_values_never_expose_the_secret()
    {
        var box = new TextBox { PasswordChar = '●', Text = "test-secret" };
        var assembly = Assembly.Load("Avalonia.FreeDesktop.AtSpi");
        var server = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.AtSpiServer")!, true)!;
        var type = assembly.GetType("Avalonia.FreeDesktop.AtSpi.AtSpiNode")!;
        var peer = ControlAutomationPeer.CreatePeerForElement(box)!;
        var node = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [peer, server])!;
        type.GetMethod("Attach", Members)!.Invoke(node, [null]);
        try
        {
            var accessible = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.Handlers.AtSpiAccessibleHandler")!, Members, null, [server, node], null)!;
            var text = Activator.CreateInstance(assembly.GetType("Avalonia.FreeDesktop.AtSpi.Handlers.AtSpiTextHandler")!, Members, null, [node], null)!;
            async Task<string> Read() => await (ValueTask<string>)text.GetType().GetMethod("GetTextAsync")!.Invoke(text, [0, -1])!;
            Assert.Equal(40u, await (ValueTask<uint>)accessible.GetType().GetMethod("GetRoleAsync")!.Invoke(accessible, null)!);
            Assert.Equal("password text", await (ValueTask<string>)accessible.GetType().GetMethod("GetRoleNameAsync")!.Invoke(accessible, null)!);
            Assert.Equal(new string('•', 11), await Read());
            // This same boundary sanitizes accessible-value event payloads.
            Assert.Equal(new string('•', 11), type.GetMethod("AccessibleText", Members)!.Invoke(node, ["test-secret"]));
            box.Text = "changed";
            Assert.Equal(new string('•', 7), await Read());
            Assert.Equal((int)'•', await (ValueTask<int>)text.GetType().GetMethod("GetCharacterAtOffsetAsync")!.Invoke(text, [0])!);
            box.PasswordChar = '\0';
            Assert.Equal("changed", await Read());
            Assert.NotEqual(40u, await (ValueTask<uint>)accessible.GetType().GetMethod("GetRoleAsync")!.Invoke(accessible, null)!);
        }
        finally { type.GetMethod("Detach", Members)!.Invoke(node, null); }
    }
}
#endif
