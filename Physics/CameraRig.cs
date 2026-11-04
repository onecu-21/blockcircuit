using System.Numerics;

namespace BlockCircuit;

/// <summary>Conservative camera-box sweep along the eye-to-camera segment.</summary>
public static class CameraRig
{
    public const float Radius = .18f;
    const float Clearance = .005f;

    public static Vector3 ResolvePosition(World world, Vector3 eye, Vector3 direction, float distance = 4)
    {
        ArgumentNullException.ThrowIfNull(world);
        CollisionSystem.RequireFinite(eye, nameof(eye));
        CollisionSystem.RequireFinite(direction, nameof(direction));
        if (!float.IsFinite(distance) || distance < 0 || distance > 64)
            throw new ArgumentOutOfRangeException(nameof(distance));
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
        if (length == 0 || distance == 0) return eye;
        direction = new((float)(direction.X / length), (float)(direction.Y / length), (float)(direction.Z / length));
        float allowed = distance;
        // Keep the camera inside the finite horizontal world and above the floor.
        LimitBoundary(eye.X, direction.X, Radius, World.Size - Radius, ref allowed);
        LimitBoundary(eye.Z, direction.Z, Radius, World.Size - Radius, ref allowed);
        if (direction.Y < 0) allowed = Math.Min(allowed, Math.Max(0, (Radius - eye.Y) / direction.Y));
        var end = eye + direction * allowed;
        var minimum = Vector3.Min(eye, end) - new Vector3(Radius);
        var maximum = Vector3.Max(eye, end) + new Vector3(Radius);
        int x0 = RangeStart(minimum.X, World.Size), x1 = RangeEnd(maximum.X, World.Size);
        int y0 = RangeStart(minimum.Y, World.Height), y1 = RangeEnd(maximum.Y, World.Height);
        int z0 = RangeStart(minimum.Z, World.Size), z1 = RangeEnd(maximum.Z, World.Size);
        for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
        {
            if (!world.Solid(new(x, y, z))) continue;
            var lower = new Vector3(x - Radius, y - Radius, z - Radius);
            var upper = new Vector3(x + 1 + Radius, y + 1 + Radius, z + 1 + Radius);
            if (EntryDistance(eye, direction, lower, upper, allowed, out float entry))
                allowed = Math.Max(0, entry - Clearance);
        }
        return eye + direction * allowed;
    }

    static void LimitBoundary(float position, float direction, float minimum, float maximum, ref float distance)
    {
        if (direction > 0) distance = Math.Min(distance, Math.Max(0, (maximum - position) / direction));
        else if (direction < 0) distance = Math.Min(distance, Math.Max(0, (minimum - position) / direction));
    }

    // Clamp before converting so even a finite but enormous test coordinate remains safe.
    static int RangeStart(float value, int limit) => (int)Math.Clamp(Math.Floor((double)value), 0, limit);
    static int RangeEnd(float value, int limit) => (int)Math.Clamp(Math.Floor((double)value), -1, limit - 1);

    static bool EntryDistance(Vector3 eye, Vector3 direction, Vector3 minimum, Vector3 maximum, float limit, out float entry)
    {
        float near = 0, far = limit;
        bool hit = Slab(eye.X, direction.X, minimum.X, maximum.X, ref near, ref far) &&
                   Slab(eye.Y, direction.Y, minimum.Y, maximum.Y, ref near, ref far) &&
                   Slab(eye.Z, direction.Z, minimum.Z, maximum.Z, ref near, ref far);
        entry = near;
        return hit;
    }

    static bool Slab(float origin, float direction, float minimum, float maximum, ref float near, ref float far)
    {
        if (Math.Abs(direction) < .0000001f) return origin >= minimum && origin <= maximum;
        float a = (minimum - origin) / direction, b = (maximum - origin) / direction;
        near = Math.Max(near, Math.Min(a, b));
        far = Math.Min(far, Math.Max(a, b));
        return near <= far;
    }
}
