namespace BlockCircuit;

public static class ControlsTests
{
    public static void Run()
    {
        int passed = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("FAIL: " + description);
            Console.WriteLine("PASS: " + description); passed++;
        }
        var controls = new PlayerControls();
        Check(!controls.Sample(0, true, false, false, false, false), "one Space press remains a jump");
        Check(controls.Sample(.2, true, false, false, false, false), "two Space presses toggle flight");
        Check(!controls.Sample(.25, true, false, false, false, false), "third Space press does not toggle flight again");
        Check(!controls.Sample(.7, true, false, false, false, false), "slow Space presses do not toggle flight");
        controls.Reset();
        controls.Sample(1, true, false, false, false, false);
        controls.Reset();
        Check(!controls.Sample(1.1, true, false, false, false, false), "opening a menu clears pending double taps");
        controls.Sample(2, false, true, true, false, false);
        Check(!controls.Sprinting, "one W press walks");
        controls.Sample(2.1, false, false, false, false, false);
        controls.Sample(2.2, false, true, true, false, false);
        Check(controls.Sprinting, "double W starts sprinting");
        controls.Sample(2.3, false, false, true, false, false);
        Check(controls.Sprinting, "sprint continues while forward is held");
        controls.Sample(2.4, false, false, true, false, true);
        Check(!controls.Sprinting, "sneaking cancels sprinting");
        controls.Sample(3, false, false, true, true, false);
        Check(controls.Sprinting, "Ctrl starts forward sprinting");
        controls.Sample(3.1, false, false, false, true, false);
        Check(!controls.Sprinting, "Ctrl without forward does not sprint");
        Check(PlayerControls.ShouldUseLever(Block.Lever, false) && !PlayerControls.ShouldUseLever(Block.Lever, true) &&
              !PlayerControls.ShouldUseLever(Block.Stone, false), "right click uses levers unless Shift bypasses interaction");
        Check(PlayerControls.ScrollSlot(0, 1, 9) == 8 && PlayerControls.ScrollSlot(8, -1, 9) == 0 &&
              PlayerControls.ScrollSlot(8, 0, 9) == 8, "mouse wheel wraps all nine hotbar slots");
        Console.WriteLine($"Controls tests: {passed} passed.");
    }
}
