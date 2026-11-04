using System.Numerics;
using System.Text.Json;
using BlockCircuit;

sealed partial class Game
{
    readonly List<string> controlsChecks = [];

    void AdvanceControlsSmoke(int frame)
    {
        void Check(bool valid, string description)
        {
            if (!valid) throw new InvalidOperationException("FAIL: " + description);
            controlsChecks.Add(description);
        }
        if (frame == 0)
        {
            palette.SelectedSlot = 8;
            Check(palette.Hotbar.Length == 9 && palette.Selected == Block.Glass, "ninth hotbar slot is selectable");
        }
        if (frame is 5 or 15 or 25)
        {
            CyclePerspective();
            var eye = CurrentEye(); var camera = BuildCamera(eye);
            Vector3 direction = Vector3.Normalize(camera.Target - camera.Position);
            Check(perspective == (frame == 5 ? 1 : frame == 15 ? 2 : 0), "F5 perspective order at frame " + frame);
            Check(frame == 15 ? Vector3.Dot(direction, Forward) < -.99f : Vector3.Dot(direction, Forward) > .99f,
                "camera faces the expected direction at frame " + frame);
        }
        if (frame == 35) ToggleInventory();
        if (frame == 45) { ToggleInventory(); Check(palette.SelectedSlot == 8, "inventory retains ninth-slot selection"); }
        if (frame == 50) CyclePerspective();
    }

    void FinishControlsSmoke()
    {
        if (controlsChecks.Count != 8) throw new InvalidOperationException("Incomplete controls graphics smoke.");
        File.WriteAllText("controls-smoke-results.json", JsonSerializer.Serialize(new { Passed = true, Checks = controlsChecks },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 8 controls integration checks in a real graphics context.");
    }
}
