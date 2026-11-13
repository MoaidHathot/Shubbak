using Dalil.Core;
using Shubbak.Native;

namespace Dalil.Tests;

public sealed class WorkerScriptTests
{
    [Fact]
    public async Task AJobOwnedScriptKeepsItsOutputAndErrorSemantics()
    {
        ScriptList.Result result = await ScriptList.RunAsync(
            "cmd.exe /c \"echo one & echo two & exit /b 3\"", Win32WorkerProcess.Start).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Succeeded);
        Assert.Equal(["one ", "two "], result.Lines);
    }
}
