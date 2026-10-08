using XIVLauncher.Core;

public sealed class WineSpeechSetupTests
{
    [Fact]
    public void AlreadyHiddenExportsDoNotWriteOrCreateBackup()
    {
        var calls = 0;
        WineSpeechSetup.Ensure(args => { calls++; return new(0, "    HideWineExports    REG_SZ    Y\r\n"); },
            () => throw new InvalidOperationException("No backup needed"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0, "    HideWineExports    REG_SZ    N\r\n")]
    [InlineData(1, "Value missing")]
    public void VisibleOrDefaultExportsAreBackedUpThenHidden(int code, string output)
    {
        var calls = new List<string[]>();
        WineSpeechSetup.Ensure(args => { calls.Add(args); return calls.Count == 1 ? new(code, output) : new(0, ""); }, () => @"Z:\backup.reg");
        Assert.Equal("query", calls[0][1]);
        Assert.Equal(new[] { "reg", "export", @"HKCU\Software\Wine", @"Z:\backup.reg" }, calls[1]);
        Assert.Equal(new[] { "reg", "add", @"HKCU\Software\Wine", "/v", "HideWineExports", "/t", "REG_SZ", "/d", "Y", "/f" }, calls[2]);
    }

    [Fact]
    public void FailedBackupDoesNotChangeSettings()
    {
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => WineSpeechSetup.Ensure(args => { calls++; return new(1, ""); }, () => "backup.reg"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void FailedWriteStopsLaunch()
    {
        var calls = 0;
        Assert.Throws<InvalidOperationException>(() => WineSpeechSetup.Ensure(args => new(++calls == 2 ? 0 : 1, ""), () => "backup.reg"));
        Assert.Equal(3, calls);
    }
}
