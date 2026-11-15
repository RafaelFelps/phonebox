using PhoneBox.Agent;
using Xunit;

public class KeySenderTests
{
    [Fact]
    public void ParsesCombosAndSequences()
    {
        var combos = KeySender.Parse("Ctrl+Shift+M, cmd+Space,F13, play");
        Assert.Equal([["ctrl", "shift", "m"], ["win", "space"], ["f13"], ["playpause"]], combos);
    }

    [Theory]
    [InlineData("ctrl+a+b")]      // two keys besides modifiers
    [InlineData("ctrl+banana")]   // unknown key
    [InlineData("f25")]
    [InlineData(" , ")]           // nothing
    public void RejectsBadSpecs(string spec) => Assert.Throws<ArgumentException>(() => KeySender.Parse(spec));

    [Fact]
    public void ModifiersAloneAreAllowed() => Assert.Equal([["win"]], KeySender.Parse("Win"));

    [Theory]
    [InlineData("RightShift+F12", "rshift")]
    [InlineData("Right Shift + F12", "rshift")]
    [InlineData("Shift_R+F12", "rshift")]
    [InlineData("LeftShift+F12", "lshift")]
    [InlineData("RCtrl+F12", "rctrl")]
    [InlineData("AltGr+F12", "ralt")]
    public void ParsesSidedModifiers(string spec, string modifier) =>
        Assert.Equal([[modifier, "f12"]], KeySender.Parse(spec));

    [Fact]
    public void SidedModifiersCountAsModifiers() => Assert.Equal([["rshift", "lctrl", "m"]], KeySender.Parse("RightShift+LeftCtrl+M"));
}
