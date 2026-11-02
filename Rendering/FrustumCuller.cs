using System.Numerics;

namespace BlockCircuit;

/// <summary>Conservative camera-frustum/AABB test with no graphics-context dependency.</summary>
public readonly struct FrustumCuller
{
    readonly Plane nearPlane, farPlane, leftPlane, rightPlane, bottomPlane, topPlane;

    public FrustumCuller(Vector3 eye, Vector3 target, Vector3 up, float verticalFovRadians,
        float aspect, float near, float far)
    {
        if (!Finite(eye) || !Finite(target) || !Finite(up))
            throw new ArgumentException("Camera vectors must contain finite values.");
        if (!float.IsFinite(verticalFovRadians) || verticalFovRadians <= 0 || verticalFovRadians >= MathF.PI)
            throw new ArgumentOutOfRangeException(nameof(verticalFovRadians));
        if (!float.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (!float.IsFinite(near) || near < 0) throw new ArgumentOutOfRangeException(nameof(near));
        if (!float.IsFinite(far) || far <= near) throw new ArgumentOutOfRangeException(nameof(far));

        var forward = target - eye;
        if (forward.LengthSquared() < 1e-12f) throw new ArgumentException("Camera target must differ from its position.");
        forward = Vector3.Normalize(forward);
        var right = Vector3.Cross(forward, up);
        if (right.LengthSquared() < 1e-12f) throw new ArgumentException("Camera up must not be parallel to its view.");
        right = Vector3.Normalize(right);
        var cameraUp = Vector3.Cross(right, forward);
        float tanY = MathF.Tan(verticalFovRadians * .5f), tanX = tanY * aspect;

        nearPlane = Through(forward, eye + forward * near);
        farPlane = Through(-forward, eye + forward * far);
        leftPlane = Through(right + forward * tanX, eye);
        rightPlane = Through(-right + forward * tanX, eye);
        bottomPlane = Through(cameraUp + forward * tanY, eye);
        topPlane = Through(-cameraUp + forward * tanY, eye);
    }

    public static FrustumCuller Create(Vector3 eye, Vector3 target, Vector3 up, float verticalFovRadians,
        float aspect, float near, float far) => new(eye, target, up, verticalFovRadians, aspect, near, far);

    public bool Intersects(Vector3 min, Vector3 max) =>
        Inside(nearPlane, min, max) && Inside(farPlane, min, max) &&
        Inside(leftPlane, min, max) && Inside(rightPlane, min, max) &&
        Inside(bottomPlane, min, max) && Inside(topPlane, min, max);

    static Plane Through(Vector3 normal, Vector3 point)
    {
        normal = Vector3.Normalize(normal);
        return new(normal, -Vector3.Dot(normal, point));
    }

    static bool Inside(Plane plane, Vector3 min, Vector3 max)
    {
        // A box is outside only if even its most positive corner is outside. Keeping
        // intersecting boxes avoids popping while crossing a plane or entering a chunk.
        var normal = plane.Normal;
        var positive = new Vector3(normal.X >= 0 ? max.X : min.X,
            normal.Y >= 0 ? max.Y : min.Y, normal.Z >= 0 ? max.Z : min.Z);
        return Vector3.Dot(normal, positive) + plane.D >= -1e-4f;
    }

    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
