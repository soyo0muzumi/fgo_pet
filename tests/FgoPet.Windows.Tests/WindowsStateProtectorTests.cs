using System.Text;
using FgoPet.Platform.Secrets;
using FgoPet.Platform.Windows.Secrets;
using Xunit;

namespace FgoPet.Windows.Tests;

public sealed class WindowsStateProtectorTests
{
    [Fact]
    public void Current_user_protection_roundtrips_and_does_not_store_plaintext()
    {
        var protector = new WindowsStateProtector();
        var input = Encoding.UTF8.GetBytes("private-synthetic-state-测试");
        var encoded = protector.Protect(input, "fixture.native-run");
        Assert.Equal(input, protector.Unprotect(encoded, "fixture.native-run"));
        Assert.DoesNotContain("private-synthetic-state", Encoding.UTF8.GetString(encoded));
        Assert.Throws<StateProtectionException>(() => protector.Unprotect(encoded, "fixture.other"));
        encoded[^1] ^= 1;
        Assert.Throws<StateProtectionException>(() => protector.Unprotect(encoded, "fixture.native-run"));
    }

    [Fact]
    public void Protection_rejects_unbounded_or_invalid_envelopes()
    {
        var protector = new WindowsStateProtector();
        Assert.Throws<StateProtectionException>(() => protector.Protect(new byte[4 * 1024 * 1024 + 1], "fixture"));
        Assert.Throws<StateProtectionException>(() => protector.Protect([1], "fixture/path"));
        Assert.Throws<StateProtectionException>(() => protector.Unprotect([], "fixture"));
    }
}
