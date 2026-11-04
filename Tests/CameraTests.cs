using System.Numerics;

namespace BlockCircuit;

static class CameraTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            checks++; Console.WriteLine("PASS: " + name);
        }
        var world = new World(false);
        var eye = new Vector3(10.5f, 3.62f, 10.5f);
        Check(Vector3.Distance(CameraRig.ResolvePosition(world, eye, Vector3.UnitZ), eye + new Vector3(0,0,4)) < .0001f,
            "third-person camera uses its full distance in open space");
        Check(CameraRig.ResolvePosition(world, eye, Vector3.Zero) == eye && CameraRig.ResolvePosition(world, eye, Vector3.UnitZ, 0) == eye,
            "zero direction and zero distance leave the camera at the player eye");
        world.Set(new(10, 3, 13), Block.Stone);
        var wall = CameraRig.ResolvePosition(world, eye, Vector3.UnitZ);
        Check(wall.Z < 13 - CameraRig.Radius && wall.Z > 12.7f,
            "the camera stops before a solid wall with lens clearance");
        Check(Vector3.Distance(CameraRig.ResolvePosition(world, eye, Vector3.UnitZ * 7), wall) < .0001f,
            "camera distance is independent of direction-vector magnitude");
        world.Set(new(10, 3, 13), Block.Glass);
        Check(CameraRig.ResolvePosition(world, eye, Vector3.UnitZ) == wall,
            "solid transparent blocks also obstruct the camera");
        world.Set(new(10, 3, 13), Block.Wire);
        Check(CameraRig.ResolvePosition(world, eye, Vector3.UnitZ).Z == 14.5f,
            "thin non-colliding circuit pieces do not block the camera");
        world.Set(new(12, 3, 11), Block.Stone);
        Check(Vector3.Distance(CameraRig.ResolvePosition(world, eye, new Vector3(1, 0, 1)), eye) < 2,
            "swept camera volume catches diagonal voxel corners");
        world.Set(new(10, 3, 8), Block.Stone);
        Check(CameraRig.ResolvePosition(world, eye, -Vector3.UnitZ).Z > 9 + CameraRig.Radius,
            "front and rear camera directions both respect walls");
        var edge = CameraRig.ResolvePosition(world, new(39.5f, 5, 10), Vector3.UnitX);
        Check(edge.X <= World.Size - CameraRig.Radius && edge.X >= 39.5f,
            "camera remains inside the finite world's horizontal bounds");
        bool rejected = false;
        try { CameraRig.ResolvePosition(world, eye, Vector3.UnitZ, float.NaN); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "invalid camera distances are rejected");
        Console.WriteLine($"Camera tests passed: {checks}");
    }
}
