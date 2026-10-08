// SPDX-License-Identifier: GPL-3.0-or-later
namespace XIVLauncher.Core.Components.LoadingPage;

// Status written by the upstream patch/download controller and read by the accessible UI.
public sealed class LoadingPage
{
    public bool IsIndeterminate { get; set; } = true;
    public float Progress { get; set; }
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string? Line3 { get; set; }
}
