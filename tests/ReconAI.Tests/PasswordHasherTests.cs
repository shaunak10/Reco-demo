using ReconAI.Core.Security;

namespace ReconAI.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Correct_password_verifies_and_wrong_password_does_not()
    {
        var hash = PasswordHasher.Hash("Correct-Horse-9");
        Assert.True(PasswordHasher.Verify("Correct-Horse-9", hash));
        Assert.False(PasswordHasher.Verify("correct-horse-9", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void Same_password_gets_a_different_salt_each_time_and_is_never_stored_in_clear()
    {
        var a = PasswordHasher.Hash("Correct-Horse-9");
        var b = PasswordHasher.Hash("Correct-Horse-9");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("Correct-Horse-9", a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("v1.abc.def.ghi")]
    [InlineData("v1.1000.!!notbase64!!.!!notbase64!!")]
    public void Malformed_stored_values_fail_closed(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything", stored));
    }
}
