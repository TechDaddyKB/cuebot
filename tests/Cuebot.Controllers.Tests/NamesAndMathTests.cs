namespace Cuebot.Controllers.Tests;

public sealed class NamesTests
{
    [Theory]
    [InlineData("Xbox One", "South", "A")]
    [InlineData("Xbox One", "Back", "View")]
    [InlineData("Xbox 360", "Back", "Back")]
    [InlineData("PS5", "South", "Cross")]
    [InlineData("PS5", "Back", "Create")]
    [InlineData("PS4", "Back", "Share")]
    [InlineData("PS4", "Start", "Options")]
    [InlineData("Switch Pro", "South", "B")]
    [InlineData("Switch Pro", "East", "A")]
    [InlineData("Switch Pro", "Start", "Plus")]
    [InlineData("Joystick", "Button3", "Button3")]
    public void Labels_follow_the_controller_family(string type, string button, string label) =>
        Assert.Equal(label, ControllerNames.ButtonLabel(type, button));

    [Fact]
    public void Printed_labels_match_per_family_and_aliases_map_to_positions()
    {
        Assert.True(ControllerNames.ButtonMatches("A", "South", ControllerNames.ButtonLabel("Xbox One", "South")));
        Assert.False(ControllerNames.ButtonMatches("A", "South", ControllerNames.ButtonLabel("Switch Pro", "South")));
        Assert.True(ControllerNames.ButtonMatches("A", "East", ControllerNames.ButtonLabel("Switch Pro", "East")));
        Assert.True(ControllerNames.ButtonMatches("cross", "South", "Cross"));
        Assert.True(ControllerNames.ButtonMatches("Cross", "South", "A"));   // PlayStation name on an Xbox pad still means South
        Assert.True(ControllerNames.ButtonMatches("LB", "LeftShoulder", "LB"));
        Assert.True(ControllerNames.ButtonMatches("L1", "LeftShoulder", "LB"));
        Assert.True(ControllerNames.ButtonMatches("Select", "Back", "View"));
        Assert.True(ControllerNames.ButtonMatches("up", "DPadUp", "DPadUp"));
        Assert.True(ControllerNames.ButtonMatches("button3", "Button3", "Button3"));
        Assert.False(ControllerNames.ButtonMatches("", "South", "A"));
        Assert.False(ControllerNames.ButtonMatches("North", "South", "A"));
        Assert.True(ControllerNames.AxisMatches("RT", "RightTrigger"));
        Assert.True(ControllerNames.AxisMatches("lefty", "LeftY"));
        Assert.False(ControllerNames.AxisMatches("LeftX", "LeftY"));
    }

    [Fact]
    public void Indexes_name_standard_and_raw_inputs()
    {
        Assert.Equal("South", ControllerNames.Button(true, 0));
        Assert.Equal("Touchpad", ControllerNames.Button(true, 20));
        Assert.Equal("Button1", ControllerNames.Button(false, 0));
        Assert.Equal("Button30", ControllerNames.Button(true, 29));
        Assert.Equal("RightTrigger", ControllerNames.Axis(true, 5));
        Assert.Equal("Axis3", ControllerNames.Axis(false, 2));
        Assert.True(ControllerNames.IsTriggerAxis(true, 4));
        Assert.False(ControllerNames.IsTriggerAxis(false, 4));
        Assert.Equal("PS5", ControllerNames.TypeName(6));
        Assert.Equal("Unknown", ControllerNames.TypeName(99));
    }

    [Theory]
    [InlineData(0, "Centered")]
    [InlineData(1, "Up")]
    [InlineData(3, "UpRight")]
    [InlineData(12, "DownLeft")]
    [InlineData(5, "Invalid")]
    public void Hat_directions(int value, string name)
    {
        Assert.Equal(name, ControllerNames.HatDirection(value));
        if (name != "Invalid") Assert.Equal(value, ControllerNames.HatMask(name));
    }

    [Fact]
    public void DPad_buttons_form_a_hat_mask()
    {
        Assert.Equal(0, ControllerNames.DPadMask(false, false, false, false));
        Assert.Equal(9, ControllerNames.DPadMask(true, false, true, false));
        Assert.Equal(-1, ControllerNames.HatMask("sideways"));
    }
}

public sealed class AxisMathTests
{
    [Fact]
    public void Deadzone_zeroes_small_values_and_rescales_the_rest()
    {
        Assert.Equal(0, AxisMath.Normalize(3000, false, 0.15));
        Assert.Equal(0, AxisMath.Normalize(-4900, false, 0.15));
        Assert.Equal(1, AxisMath.Normalize(32767, false, 0.15));
        Assert.Equal(-1, AxisMath.Normalize(-32768, false, 0.15));
        Assert.Equal(0.5, AxisMath.Normalize(short.MaxValue, false, 0.0) / 2, 3);
        var halfway = AxisMath.Normalize((short)(32767 * 0.575), false, 0.15);
        Assert.Equal(0.5, halfway, 2);   // (0.575 - 0.15) / 0.85
    }

    [Fact]
    public void Triggers_use_zero_to_one()
    {
        Assert.Equal(0, AxisMath.Normalize(-100, true, 0.05));
        Assert.Equal(1, AxisMath.Normalize(32767, true, 0.05));
        Assert.InRange(AxisMath.Normalize(16384, true, 0.05), 0.47, 0.48);
    }

    [Fact]
    public void Only_meaningful_changes_count_but_rest_and_end_stops_always_do()
    {
        Assert.False(AxisMath.IsSignificant(0.5, 0.5, 0.02));
        Assert.False(AxisMath.IsSignificant(0.5, 0.51, 0.02));
        Assert.True(AxisMath.IsSignificant(0.5, 0.53, 0.02));
        Assert.True(AxisMath.IsSignificant(0.01, 0, 0.02));
        Assert.True(AxisMath.IsSignificant(0.99, 1, 0.02));
        Assert.True(AxisMath.IsSignificant(-0.995, -1, 0.02));
    }

    [Fact]
    public void Beyond_checks_direction_and_threshold()
    {
        Assert.True(AxisMath.Beyond(0.6, "Positive", 0.5));
        Assert.False(AxisMath.Beyond(-0.6, "Positive", 0.5));
        Assert.True(AxisMath.Beyond(-0.6, "Negative", 0.5));
        Assert.True(AxisMath.Beyond(-0.6, "Either", 0.5));
        Assert.False(AxisMath.Beyond(0.4, "Either", 0.5));
        Assert.False(AxisMath.Beyond(0, "Either", 0));
        Assert.Equal("Negative", AxisMath.Direction(-0.1));
        Assert.Equal("Centered", AxisMath.Direction(0));
    }

    [Fact]
    public void Throttle_holds_back_bursts_and_releases_the_trailing_value()
    {
        var throttle = new AxisThrottle();
        var t0 = DateTimeOffset.UnixEpoch;
        var interval = TimeSpan.FromMilliseconds(50);
        Assert.True(throttle.Offer(1, 0, t0, interval));
        Assert.False(throttle.Offer(1, 0, t0.AddMilliseconds(10), interval));
        Assert.True(throttle.Offer(1, 1, t0.AddMilliseconds(10), interval));   // other axis: own slot
        Assert.True(throttle.HasPending);
        Assert.Empty(throttle.TakeDue(t0.AddMilliseconds(30), interval));
        Assert.Equal([(1u, 0)], throttle.TakeDue(t0.AddMilliseconds(60), interval));
        Assert.False(throttle.HasPending);
        Assert.False(throttle.Offer(1, 0, t0.AddMilliseconds(70), interval));
        throttle.Forget(1);
        Assert.False(throttle.HasPending);
        Assert.True(throttle.Offer(1, 0, t0.AddMilliseconds(71), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("#00ff80", 0, 255, 128)]
    [InlineData("0080FF", 0, 128, 255)]
    [InlineData("#f0a", 255, 0, 170)]
    [InlineData("#FF112233", 17, 34, 51)]
    [InlineData("10, 20,30", 10, 20, 30)]
    public void Colours_parse(string text, int r, int g, int b) =>
        Assert.Equal(((byte)r, (byte)g, (byte)b), ControllerService.ParseColor(text));

    [Fact]
    public void Bad_colours_are_rejected() => Assert.Throws<ArgumentException>(() => ControllerService.ParseColor("blue-ish"));
}
